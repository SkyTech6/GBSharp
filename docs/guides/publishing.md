# Publishing a game

A ROM needs an emulator, and most people do not have one. `gbsharp publish` produces something they can just run:

```bash
gbsharp publish win-x64
```

```text
Published: MyGame/publish/win-x64/MyGame.exe
  124.1 KB, opens straight into the game
```

## What publish produces

Six targets. Five are native platforms and one is the browser:

| Rid | Output |
|---|---|
| `win-x64` | `MyGame.exe`, with `SDL2.dll` beside it |
| `linux-x64` | An executable ELF |
| `linux-arm64` | An executable ELF |
| `osx-x64` | An executable Mach-O |
| `osx-arm64` | An executable Mach-O |
| `web` | One `.html`, or a folder for a static host |

Any of the five native platforms can be published for from any of them, since the player stub is fetched from the same checksum-pinned release as the emulator runtime. Output lands in `<project>/publish/<rid>/` unless `--out` says otherwise.

## How it works

The published executable is the prebuilt GB# Player with the ROM and the window settings appended to it. The Player reads them out of its own file at startup, so nothing is unpacked to disk and nothing is relinked, which is why publishing needs no C toolchain and takes about as long as copying a file. The same mechanism works on every platform, because PE, ELF and Mach-O loaders all ignore bytes past the end of the image they describe.

## Player settings

How the game presents itself is the game's decision, not the Player's, so it lives in [gbsharp.json](../reference/gbsharp-json.md):

```jsonc
{
  "name": "MyGame",
  "player": {
    "title": "My Game",
    "scale": 4,
    "fullscreen": false,
    "resizable": true,
    "integerScaling": true,
    "volume": 80
  }
}
```

`scale` is 1 to 8 times the Game Boy's 160×144 screen and `volume` runs 0 to 100; both are validated when you publish, because the person who can fix a typo in the project file is the one running the command, not the one running the game.

The Player has no settings screen, no ROM browser and no menu, because a player that could disagree with the game would be an emulator wearing the game's name. Saves go to the per-user application data directory, so a game installed somewhere read-only still works and two people on one machine keep separate progress.

## Before shipping

Two things to know. The executable is unsigned, and signing has to happen **after** publishing, because the appended bytes are part of what a signature covers: sign first and the append invalidates the signature. And on Windows the game ships with `SDL2.dll` beside it, so it is a small folder rather than a single file; making it one file needs a statically linked SDL in the runtime's release build, which is on the roadmap rather than done.

## The browser

```bash
gbsharp publish web --single-file
```

```text
Published: MyGame/publish/web/My Game.html
  188.5 KB, one file, opens without a server
```

One `.html` with the emulator and the ROM inlined into it. It opens from a file manager with nothing else installed, which makes it the thing to send somebody who asked to try your game.

Without `--single-file` you get a folder to upload to any static host. That layout has to be served over http rather than opened from disk, because browsers block module scripts and wasm fetches under `file://`, which is exactly the constraint the single file mode exists to sidestep.

The web player is the native one in a different room: canvas instead of a window, WebAudio instead of a sound device, IndexedDB instead of an application data directory, and the same emulator ABI underneath. Both are paced by the Game Boy's own 59.7275 Hz rather than by the display, so a game does not run 2.4 times too fast on a 144 Hz monitor. Keyboard and gamepad both work; saves survive a reload.

## Related

- [Running your game](emulators.md): the same Player is what `gbsharp run` launches during development.
- [CLI reference](../reference/cli.md): every `publish` option.
