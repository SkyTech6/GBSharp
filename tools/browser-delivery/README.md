# Browser delivery regression checks

Run from the checkout after packing and validating the installed consumer:

```powershell
npm --prefix tools/browser-delivery ci
npx --prefix tools/browser-delivery playwright install --with-deps chromium
node tools/browser-delivery/verify.mjs artifacts/package-consumer/manifest.json artifacts/browser-delivery
```

Node 20 or newer is recommended. Playwright is pinned in `package-lock.json`;
its package pins the Chromium revision. The test does not select a host browser.
The manifest comes from `tools/test-package-consumer.ps1`. It contains absolute
HTML paths for `sample.folder`, `sample.single`, `save.folder` and `save.single`.
The consumer publishes `Samples/ComposedScrolling` and `save-fixture` using the
freshly installed CLI. Both use the real SHA256-pinned emulator WASM and player
assets. The SRAM fixture uses existing native interop and a battery cartridge;
it does not introduce a framework save API.

The harness serves folder exports with an ephemeral loopback HTTP server and
opens single-file exports directly with `file://`. Any HTTP request from the
single-file page fails the check. It uses Chromium keyboard input and checks
scroll registers, logical framebuffer changes, the fixed bottom HUD, and both
characters' OAM tile/property entries. Canvas rendering must contain multiple
colors. After boot it pauses animation scheduling and advances the real emulator
in deterministic batches, avoiding dependence on CI display cadence.

Audio must remain uninitialized before the start click, then enter the running
state and queue actual emulator samples into WebAudio. No host speaker output
is required. The save ROM initializes a magic signature, increments its fourth
SRAM byte only on a real X/A press, commits persistence, and must recover that
changed byte after closing and reopening at the same origin/path. The same checks
run for both delivery modes. A separate page denies IndexedDB and must continue
rendering without an unhandled failure.

Every page has bounded waits; the entire browser phase has a three minute
watchdog. Browser contexts and the HTTP listener close in `finally` blocks.
Results and console/network logs are always written to the artifact directory.
Failures retain a screenshot and copies of published outputs there; successful
scene cases also retain screenshots. Upload this directory and the consumer
artifacts when a CI check fails. Screenshots are diagnostic artifacts, not
cross-engine pixel baselines.
