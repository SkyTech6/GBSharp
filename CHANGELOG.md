# Changelog

Notable changes to GBSharp are recorded here. Keep pending changes under
`Unreleased`; when publishing, move them into a section with the package version
and release date. The version-bump workflow does this automatically using
`vX.X.X - YYYY-MM-DD` and the UTC release date, leaving a new empty `Unreleased`
section. The CLI, SDK, framework, and analyzer packages version together.

## Unreleased

### Added

- Explicit tile and CGB palette placement for converted background, window,
  sprite, and metasprite assets. Metasprite drawing supports matching palette
  offsets for every flip variant.
- Background and window `LoadGraphics` and `CopyRegion` APIs to upload graphics
  once and update map regions without re-uploading tiles or palettes. Existing
  zero-based convenience APIs retain their behaviour.
- Compiler diagnostic `GBS0629` for statically invalid asset placements, source
  rectangles, sprite alignment, frames, and OAM ranges.
- The `ComposedScrolling` sample and [composition tutorial](docs/tutorials/composed-scrolling.md),
  demonstrating a banked world, fixed window HUD, independent character assets,
  and exposed-row/column updates on DMG and CGB.
- Isolated installed-package validation for all three templates, SDK and analyzer
  binding, sample builds, and web exports from the exact staged release packages.
- Pinned Chromium regression coverage for HTTP folder and local single-file web
  exports, keyboard input, rendering, audio initialization, save/reopen behaviour,
  and unavailable-storage fallback.

### Changed

- The version-bump workflow moves pending changelog notes into a dated release
  section and includes the changelog in the version-bump commit.
- NuGet publication now requires installed-package and real-browser delivery
  checks against the same package set that will be published, with retained
  validation and failure artifacts.

### Fixed

- Scaffolded projects preserve prerelease suffixes in SDK, framework, and analyzer
  package versions so their dependencies match the installed CLI.
- Starting a web game with a click followed by keyboard input no longer starts
  a second emulator instance.
- Explicit web saves can be awaited until the IndexedDB transaction commits;
  database connections close after loading and writing saves.
