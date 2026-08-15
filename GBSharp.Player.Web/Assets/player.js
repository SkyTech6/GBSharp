/*
 * The GB# web player.
 *
 * The browser counterpart of the native Player, and deliberately the same
 * program: canvas instead of a window, WebAudio instead of a sound device,
 * IndexedDB instead of an application data directory, and the identical ABI
 * underneath through gbsharp-runtime.js.
 *
 * Everything here is a host decision. The emulator paces nothing, opens
 * nothing, and stores nothing.
 */

import { GameBoy, Button, Screen, Audio } from './gbsharp-runtime.js';

/*
 * 70224 ticks at 4194304Hz. Not requestAnimationFrame's cadence, which is the
 * display's and is 60Hz, or 120, or whatever the machine happens to have: a
 * game paced by the display runs fast on half the laptops sold.
 */
const FRAME_PERIOD_MS = 16.742706;

/* How far behind we are willing to catch up before giving up on the missed
 * time. A backgrounded tab can be seconds behind, and replaying those seconds
 * at once would fast forward through the game. */
const MAX_CATCHUP_FRAMES = 4;

/* Roughly this much audio stays queued: enough not to click, little enough
 * that sound does not lag visibly behind the picture. */
const AUDIO_TARGET_SECONDS = 0.08;
const AUDIO_MAX_SECONDS = 0.25;

const KEY_BINDINGS = {
  ArrowRight: Button.Right,
  ArrowLeft: Button.Left,
  ArrowUp: Button.Up,
  ArrowDown: Button.Down,
  KeyX: Button.A,
  KeyZ: Button.B,
  Enter: Button.Start,
  Backspace: Button.Select,
  ShiftRight: Button.Select,
};

/* Standard gamepad mapping, which is what every controller reports through the
 * Gamepad API regardless of what is printed on its buttons. */
const PAD_BINDINGS = {
  0: Button.A,
  1: Button.B,
  8: Button.Select,
  9: Button.Start,
  12: Button.Up,
  13: Button.Down,
  14: Button.Left,
  15: Button.Right,
};

export class WebPlayer {
  constructor(canvas, config = {}) {
    this.canvas = canvas;
    this.config = {
      title: 'GB# Player',
      integerScaling: true,
      volume: 100,
      ...config,
    };

    this.context = canvas.getContext('2d', { alpha: false });
    this.context.imageSmoothingEnabled = false;

    this.canvas.width = Screen.width;
    this.canvas.height = Screen.height;

    this.image = this.context.createImageData(Screen.width, Screen.height);
    this.pixels = new Uint32Array(this.image.data.buffer);

    this.game = null;
    this.audio = null;
    this.audioTime = 0;
    this.gain = null;
    this.running = false;
    this.owedMs = 0;
    this.previous = 0;
    this.padButtons = new Set();
    this.saveKey = null;
    this.saveSignature = 0;
  }

  async start(factory, romBytes, saveKey) {
    this.game = await GameBoy.create(factory);

    if (!this.game.loadRom(romBytes)) {
      throw new Error(
        'This game could not be started: its cartridge data is not something ' +
        'the emulator can run.');
    }

    this.saveKey = saveKey || this.config.title;
    await this.loadSave();

    this.bindInput();
    this.applyScaling();

    this.running = true;
    this.previous = performance.now();
    requestAnimationFrame((now) => this.tick(now));
  }

  /*
   * Audio cannot start until a gesture, by every browser's rules, so this is
   * called from the first click or key press rather than at load.
   */
  ensureAudio() {
    if (this.audio !== null) {
      if (this.audio.state === 'suspended') {
        this.audio.resume();
      }
      return;
    }

    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass) {
      return;
    }

    this.audio = new AudioContextClass({ sampleRate: Audio.frequency });
    this.gain = this.audio.createGain();
    this.gain.gain.value = Math.max(0, Math.min(100, this.config.volume)) / 100;
    this.gain.connect(this.audio.destination);
    this.audioTime = this.audio.currentTime;
  }

  tick(now) {
    if (!this.running) {
      return;
    }

    const elapsed = now - this.previous;
    this.previous = now;

    this.owedMs = Math.min(
      this.owedMs + elapsed, FRAME_PERIOD_MS * MAX_CATCHUP_FRAMES);

    this.pollGamepad();

    let drew = false;
    while (this.owedMs >= FRAME_PERIOD_MS) {
      this.owedMs -= FRAME_PERIOD_MS;
      this.game.runFrame();
      this.pumpAudio();
      drew = true;
    }

    if (drew) {
      this.present();
      this.persistSaveIfChanged();
    }

    requestAnimationFrame((next) => this.tick(next));
  }

  present() {
    const framebuffer = this.game.framebuffer;
    if (framebuffer === null) {
      return;
    }

    /*
     * The ABI's pixels are 0xAABBGGRR, which is R,G,B,A in ascending byte
     * order, and that is exactly what ImageData wants on a little endian
     * machine. So this is a copy and not a conversion.
     */
    this.pixels.set(framebuffer);
    this.context.putImageData(this.image, 0, 0);
  }

  pumpAudio() {
    const samples = this.game.readAudio();
    if (samples === null || this.audio === null) {
      return;
    }

    const queued = this.audioTime - this.audio.currentTime;

    /* Already further ahead than the target, so this frame's audio is late by
     * definition and queueing it would push everything after it later still. */
    if (queued > AUDIO_MAX_SECONDS) {
      return;
    }

    const frames = samples.length / Audio.channels;
    const buffer = this.audio.createBuffer(Audio.channels, frames, Audio.frequency);

    for (let channel = 0; channel < Audio.channels; channel++) {
      const output = buffer.getChannelData(channel);
      for (let i = 0; i < frames; i++) {
        output[i] = samples[(i * Audio.channels) + channel] / 32768;
      }
    }

    const source = this.audio.createBufferSource();
    source.buffer = buffer;
    source.connect(this.gain);

    /* Scheduled against the audio clock, not the frame clock, so the gaps
     * between buffers do not accumulate into a drift. */
    const startAt = Math.max(this.audio.currentTime + AUDIO_TARGET_SECONDS, this.audioTime);
    source.start(startAt);
    this.audioTime = startAt + buffer.duration;
  }

  bindInput() {
    window.addEventListener('keydown', (event) => {
      this.ensureAudio();

      if (event.code === 'F11' || (event.key === 'Enter' && event.altKey)) {
        this.toggleFullscreen();
        event.preventDefault();
        return;
      }

      const button = KEY_BINDINGS[event.code];
      if (button !== undefined) {
        this.game.setButton(button, true);
        /* Arrows scroll the page and Backspace navigates back, neither of
         * which is what somebody holding a d-pad meant. */
        event.preventDefault();
      }
    });

    window.addEventListener('keyup', (event) => {
      const button = KEY_BINDINGS[event.code];
      if (button !== undefined) {
        this.game.setButton(button, false);
        event.preventDefault();
      }
    });

    this.canvas.addEventListener('pointerdown', () => this.ensureAudio());
    window.addEventListener('resize', () => this.applyScaling());

    /* Progress is worth more than a clean shutdown. */
    window.addEventListener('pagehide', () => this.persistSave(true));
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') {
        this.persistSave(true);
      }
    });
  }

  pollGamepad() {
    if (!navigator.getGamepads) {
      return;
    }

    const pad = [...navigator.getGamepads()].find((p) => p !== null);
    if (!pad) {
      return;
    }

    this.ensureAudio();

    for (const [index, button] of Object.entries(PAD_BINDINGS)) {
      const pressed = pad.buttons[index]?.pressed === true;
      const was = this.padButtons.has(button);

      if (pressed !== was) {
        this.game.setButton(button, pressed);
        if (pressed) {
          this.padButtons.add(button);
        } else {
          this.padButtons.delete(button);
        }
      }
    }
  }

  /*
   * Integer scaling keeps every pixel the same size as every other pixel, which
   * is the difference between a Game Boy screen and a photograph of one. The
   * canvas stays 160x144; CSS does the scaling, so the browser never resamples
   * a bitmap we already know the size of.
   */
  applyScaling() {
    const available = this.canvas.parentElement ?? document.body;
    const width = available.clientWidth;
    const height = available.clientHeight;

    let scale;
    if (this.config.integerScaling) {
      scale = Math.max(1, Math.floor(
        Math.min(width / Screen.width, height / Screen.height)));
    } else {
      scale = Math.min(width / Screen.width, height / Screen.height);
    }

    this.canvas.style.width = `${Screen.width * scale}px`;
    this.canvas.style.height = `${Screen.height * scale}px`;
  }

  toggleFullscreen() {
    const target = this.canvas.parentElement ?? this.canvas;

    if (document.fullscreenElement) {
      document.exitFullscreen();
    } else if (target.requestFullscreen) {
      target.requestFullscreen();
    }
  }

  /* ----------------------------------------------------------------------- */
  /* Saves                                                                    */
  /*                                                                          */
  /* IndexedDB rather than localStorage: cartridge RAM is up to 128KB of       */
  /* binary, and localStorage is a string store with a quota measured in a few */
  /* megabytes across a whole origin.                                         */
  /* ----------------------------------------------------------------------- */

  openDatabase() {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open('gbsharp-saves', 1);

      request.onupgradeneeded = () => request.result.createObjectStore('saves');
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }

  async loadSave() {
    if (this.game.saveRamSize === 0) {
      return;
    }

    try {
      const database = await this.openDatabase();
      const bytes = await new Promise((resolve, reject) => {
        const request = database
          .transaction('saves', 'readonly')
          .objectStore('saves')
          .get(this.saveKey);

        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });

      if (bytes) {
        const view = new Uint8Array(bytes);
        this.game.writeSaveRam(view);
        this.saveSignature = signature(view);
      }
    } catch {
      /* A browser with storage blocked still plays the game; it just forgets. */
    }
  }

  persistSaveIfChanged() {
    if (this.game.saveRamSize === 0) {
      return;
    }

    const bytes = this.game.readSaveRam();
    const current = signature(bytes);

    if (current !== this.saveSignature) {
      this.saveSignature = current;
      this.writeSave(bytes);
    }
  }

  persistSave(force) {
    if (this.game === null || this.game.saveRamSize === 0) {
      return;
    }

    const bytes = this.game.readSaveRam();
    if (force || signature(bytes) !== this.saveSignature) {
      this.writeSave(bytes);
    }
  }

  async writeSave(bytes) {
    try {
      const database = await this.openDatabase();
      database
        .transaction('saves', 'readwrite')
        .objectStore('saves')
        .put(bytes.buffer, this.saveKey);
    } catch {
      /* As above: storage is a convenience, not a requirement to play. */
    }
  }
}

/* Adler-32, the same check the native player uses to notice a save changed. */
function signature(bytes) {
  let a = 1;
  let b = 0;

  for (let i = 0; i < bytes.length; i++) {
    a = (a + bytes[i]) % 65521;
    b = (b + a) % 65521;
  }

  return ((b << 16) | a) >>> 0;
}
