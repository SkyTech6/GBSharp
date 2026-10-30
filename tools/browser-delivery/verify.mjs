import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, mkdir, writeFile, cp, stat } from 'node:fs/promises';
import { dirname, resolve, join, relative, extname, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright';

// Input is produced by test-package-consumer.ps1 using the installed tool.
// No checkout CLI assemblies, fake runtime, or server for file-mode exports.
const manifestPath = resolve(process.argv[2] ?? 'artifacts/package-consumer/manifest.json');
const artifacts = resolve(process.argv[3] ?? 'artifacts/browser-delivery');
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
await mkdir(artifacts, { recursive: true });
for (const name of ['sample', 'save']) {
  for (const mode of ['folder', 'single']) {
    assert.equal(typeof manifest[name]?.[mode], 'string', `Missing manifest.${name}.${mode}`);
    assert.ok((await stat(manifest[name][mode])).isFile());
  }
}

const roots = new Map(['sample', 'save'].map(name => [name, dirname(resolve(manifest[name].folder))]));
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.wasm': 'application/wasm', '.gb': 'application/octet-stream' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://127.0.0.1');
    if (url.pathname === '/favicon.ico') { response.writeHead(204).end(); return; }
    const [, name, ...parts] = decodeURIComponent(url.pathname).split('/');
    const root = roots.get(name);
    if (!root) { response.writeHead(404).end(); return; }
    const path = resolve(root, parts.join('/') || 'index.html');
    if (relative(root, path).startsWith(`..${sep}`) || relative(root, path) === '..') {
      response.writeHead(403).end(); return;
    }
    const bytes = await readFile(path);
    response.writeHead(200, { 'Content-Type': mime[extname(path)] ?? 'application/octet-stream' });
    response.end(bytes);
  } catch { if (!response.headersSent) response.writeHead(404); response.end(); }
});
await new Promise((ok, fail) => { server.once('error', fail); server.listen(0, '127.0.0.1', ok); });
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
const results = [];
const log = [];
let currentPage;
let currentCase = 'startup';
const watchdog = setTimeout(() => {
  log.push({ case: currentCase, type: 'timeout', text: 'Browser delivery exceeded three minutes' });
  browser?.close().catch(() => {});
}, 180000);
watchdog.unref();

function observe(page, mode) {
  page.on('console', message => {
    log.push({ case: currentCase, type: 'console', level: message.type(), text: message.text() });
  });
  page.on('pageerror', error => log.push({ case: currentCase, type: 'exception', text: error.message }));
  page.on('requestfailed', request => log.push({ case: currentCase, type: 'requestfailed', url: request.url(), text: request.failure()?.errorText }));
  page.on('response', response => {
    if (response.status() >= 400) log.push({ case: currentCase, type: 'http-error', url: response.url(), status: response.status() });
  });
  page.on('request', request => {
    if (mode === 'single' && /^https?:/.test(request.url())) {
      log.push({ case: currentCase, type: 'unexpected-network', url: request.url() });
    }
  });
}

async function open(context, fixture, mode, blockedStorage = false) {
  const page = currentPage = await context.newPage();
  page.setDefaultTimeout(20000);
  observe(page, mode);
  if (blockedStorage) {
    await page.addInitScript(() => {
      Object.defineProperty(window, 'indexedDB', { get() { throw new DOMException('Storage denied by test', 'SecurityError'); } });
    });
  }
  const url = mode === 'single' ? pathToFileURL(resolve(manifest[fixture].single)).href : `${origin}/${fixture}/index.html`;
  await page.goto(url, { waitUntil: 'load', timeout: 30000 });
  assert.equal(await page.evaluate(() => window.gbsharpPlayer.audio), null, 'Audio must wait for a gesture');
  await page.locator('#start').click();
  await page.waitForFunction(() => window.gbsharpPlayer?.running || !document.querySelector('#error').hidden);
  const error = await page.locator('#error').textContent();
  assert.equal(error, '', `Player boot failed at ${url}: ${error}`);
  await page.evaluate(() => {
    window.gbsharpPlayer.running = false;
    window.__deliveryInitialGame = window.gbsharpPlayer.game;
  });
  await frames(page, 120);
  await page.waitForFunction(() => window.gbsharpPlayer.audio?.state === 'running');
  const audio = await page.evaluate(() => {
    const p = window.gbsharpPlayer;
    return { state: p.audio.state, queuedUntil: p.audioTime, now: p.audio.currentTime, gain: p.gain.gain.value };
  });
  assert.ok(audio.queuedUntil > audio.now, 'Actual runtime samples should be queued to WebAudio');
  return page;
}

async function frames(page, count) {
  await page.evaluate(count => {
    const p = window.gbsharpPlayer;
    for (let i = 0; i < count; i++) { p.game.runFrame(); p.pumpAudio(); }
    p.present();
  }, count);
}

async function assertSameSession(page) {
  const same = await page.evaluate(async () => {
    await new Promise(resolve => requestAnimationFrame(resolve));
    return window.gbsharpPlayer.game === window.__deliveryInitialGame && !window.gbsharpPlayer.running;
  });
  assert.ok(same, 'Keyboard input after clicking play must not start a second emulator');
}

async function snapshot(page) {
  return page.evaluate(() => {
    const p = window.gbsharpPlayer;
    const pixels = Array.from(p.game.framebuffer);
    const memory = (start, count) => Array.from({ length: count }, (_, i) => p.game.readMemory(start + i));
    const oam = memory(0xfe00, 32);
    const characterInk = [];
    for (let sprite = 0; sprite < 8; sprite++) {
      const [screenY, screenX, tile, flags] = oam.slice(sprite * 4, sprite * 4 + 4);
      for (let row = 0; row < 8; row++) {
        const sourceRow = flags & 0x40 ? 7 - row : row;
        const low = p.game.readMemory(0x8000 + tile * 16 + sourceRow * 2);
        const high = p.game.readMemory(0x8001 + tile * 16 + sourceRow * 2);
        for (let column = 0; column < 8; column++) {
          const bit = flags & 0x20 ? column : 7 - column;
          if (((low | high) & (1 << bit)) === 0) continue; // Transparent OBJ color zero.
          const x = screenX - 8 + column;
          const y = screenY - 16 + row;
          if (x >= 0 && x < 160 && y >= 0 && y < 128) {
            characterInk.push([sprite, x, y, pixels[y * 160 + x]]);
          }
        }
      }
    }
    const canvas = p.context.getImageData(0, 0, 160, 144).data;
    return {
      pixels, hud: pixels.slice(128 * 160),
      // Compare tile and property bytes, allowing position changes.
      sprites: oam.filter((_, i) => i % 4 >= 2), characterInk,
      scroll: memory(0xff42, 2),
      canvasHasInk: new Set(new Uint32Array(canvas.buffer)).size > 1,
    };
  });
}

function assertNoErrors(start) {
  const errors = log.slice(start).filter(item => item.type !== 'console' || item.level === 'error');
  assert.deepEqual(errors, [], 'Unexpected browser console, exception, or network failure');
}

try {
  browser = await chromium.launch({ headless: true, timeout: 30000 });
  for (const mode of ['folder', 'single']) {
    const context = await browser.newContext();
    try {
      currentCase = `sample-${mode}`;
      let start = log.length;
      let page = await open(context, 'sample', mode);
      const before = await snapshot(page);
      assert.ok(before.canvasHasInk, 'Visible canvas must contain multiple colors');
      assert.ok(new Set(before.hud).size > 1, 'The window HUD must contain visible text');
      assert.ok(before.characterInk.length > 32, 'Both character assets must contain opaque visible pixels');
      await page.keyboard.down('ArrowRight');
      await frames(page, 80);
      await page.keyboard.up('ArrowRight');
      await assertSameSession(page);
      const after = await snapshot(page);
      assert.notDeepEqual(after.scroll, before.scroll, 'Real keyboard input should scroll the world');
      assert.notDeepEqual(after.pixels, before.pixels, 'Scrolling should change the framebuffer');
      assert.deepEqual(after.hud, before.hud, 'Window HUD must survive scrolling');
      assert.deepEqual(after.sprites, before.sprites, 'Both independently loaded characters retain tiles/palettes');
      assert.deepEqual(after.characterInk, before.characterInk, 'Opaque character pixels must survive scrolling');
      assert.ok(new Set(after.sprites.filter((_, i) => i % 2 === 0)).size >= 2, 'Scene must have distinct character tiles');
      await page.screenshot({ path: join(artifacts, `${currentCase}.png`) });
      assertNoErrors(start);
      results.push({ case: currentCase, passed: true, scrollBefore: before.scroll, scrollAfter: after.scroll });
      await page.close();

      currentCase = `save-${mode}`;
      start = log.length;
      page = await open(context, 'save', mode);
      const initial = await page.evaluate(() => Array.from(window.gbsharpPlayer.game.readSaveRam().slice(0, 4)));
      assert.deepEqual(initial.slice(0, 3), [0x47, 0x42, 0x53], 'Fixture must initialize actual cartridge SRAM');
      await page.keyboard.down('KeyX');
      await frames(page, 2);
      await page.keyboard.up('KeyX');
      await assertSameSession(page);
      await frames(page, 2);
      const changed = await page.evaluate(async () => {
        const p = window.gbsharpPlayer;
        await p.persistSave(true);
        return p.game.readSaveRam()[3];
      });
      assert.equal(changed, (initial[3] + 1) & 255, 'Game CPU must change SRAM after keyboard input');
      await page.close();
      page = await open(context, 'save', mode);
      assert.equal(await page.evaluate(() => window.gbsharpPlayer.game.readSaveRam()[3]), changed,
        'Changed SRAM value must survive reopening at the identical origin/path');
      assertNoErrors(start);
      results.push({ case: currentCase, passed: true, initial: initial[3], changed, reopened: changed });
      await page.close();

      currentCase = `storage-unavailable-${mode}`;
      start = log.length;
      page = await open(context, 'save', mode, true);
      await page.evaluate(async () => { await window.gbsharpPlayer.persistSave(true); });
      assert.ok((await snapshot(page)).canvasHasInk, 'Unavailable persistence must still allow visible gameplay');
      assertNoErrors(start);
      results.push({ case: currentCase, passed: true });
      await page.close();
    } finally { await context.close(); }
  }
} catch (error) {
  if (currentPage && !currentPage.isClosed()) {
    await currentPage.screenshot({ path: join(artifacts, 'failure.png') }).catch(() => {});
  }
  // Keep published artifacts with the evidence, including embedded single-file ROM/wasm.
  for (const [name, paths] of Object.entries(manifest)) {
    if (!paths?.folder || !paths?.single) continue;
    await cp(dirname(paths.folder), join(artifacts, 'published', name, 'folder'), { recursive: true }).catch(() => {});
    await mkdir(join(artifacts, 'published', name, 'single'), { recursive: true });
    await cp(paths.single, join(artifacts, 'published', name, 'single', 'game.html')).catch(() => {});
  }
  results.push({ case: currentCase, passed: false, error: error.stack });
  process.exitCode = 1;
} finally {
  clearTimeout(watchdog);
  await browser?.close();
  await new Promise(ok => server.close(ok));
  await writeFile(join(artifacts, 'console-network.json'), JSON.stringify(log, null, 2));
  await writeFile(join(artifacts, 'results.json'), JSON.stringify(results, null, 2));
  console.log(JSON.stringify(results, null, 2));
}
