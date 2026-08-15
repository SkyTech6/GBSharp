# GB# Roadmap

What is open, what is deferred with a reason, and what was decided against. Everything else is shipped: the [README](README.md) is the status document.

*Last reconciled against the tree at commit `70661b9` (August 2026).*

## Committed next

**Fast iteration (emulator milestone M6).** The only unstarted milestone. Deterministic restart is the v1 answer: the primitives already ship (`gbsharp_reset` boots to a byte-identical state, `gbsharp_step` exists), so what is missing is the loop around them: watch the source, rebuild, restart the running game without losing the window. The fork's `rewind.c` is the noted substrate if selective state preservation is ever wanted beyond a full restart.

**Publishing polish.** The mechanism is done; the shipping checklist is not:

- **A single Windows file.** The published game is a small folder because `SDL2.dll` sits beside the exe. One file needs a statically linked SDL in the runtime's release build.
- **Code signing.** Not wired up. It must happen *after* publishing, because the appended ROM and settings are part of what a signature covers.
- **SmartScreen** behaviour is untested, and can't meaningfully be tested until signing exists.
- **Icon and version stamping.** The Windows icon and version metadata are baked into the prebuilt stub; per-game values need PE resource editing at publish time.
- **The macOS release job does not smoke-test the Player.** It checks the archive contains one, but the "does it start" step is guarded with `runner.os != 'macOS'`.

**Small correctness items.**

- The Player's menu-visibility setting was sketched in the original strategy and never built. Decide: build `showMenu`, or strike it. The Player currently has no menu at all, which the README defends on purpose; striking it is the likely answer.

## Deferred, with the reason recorded

These are not queued work. Each failed a test the project applies deliberately, and stays deferred until the test's answer changes.

- **Profiler caller attribution.** Costs land on the instruction that paid them, so a slow function called from three places is one row, not three. Call counts close most of the gap for free (the count at a function's first address is its invocation count). *Who called whom* needs a stack the core does not track, and GBDK's banked-call trampolines rewrite return addresses, so naive CALL/RET pairing would mis-attribute exactly the code banking exists for. A wrong attribution is worse than an absent one.
- **Video-shaped inspection APIs** (named tile-map / sprite / scanline accessors) and two once-planned metrics (sprites on the busiest scanline, VRAM transfers per frame). Declined on the standing rule that an ABI entry point should answer a question somebody actually has; `gbsharp_read_memory` already reaches VRAM, OAM and palettes. Same rule currently declines disassembly, opcode histograms, and SGB/CGB palette reads.
- **Player UX beyond the six settings** (pause, reset, save backup, user-facing screenshot, key rebinding). The Player deliberately has no surface that could disagree with the game; anything added must survive that principle.
- **Upstreaming fork patches.** Nine of the fifteen divergences in the emulator fork's `DIVERGENCE.md` are marked as upstream candidates; none has been sent. Worth clearing on binjgb's account, not only ours.

## Long term

- **GBA backend (thesis Phase 7).** The reason the IR exists in its backend-agnostic shape: so a port would be a backend, not a rewrite. Untouched, and explicitly out of scope for v1.
- **Music and sound-effect drivers.** `Audio` is register-level on purpose; a driver is a library concern layered on top, not a framework change.
- **`RingBuffer` / `BitSet` / `Pool`** alongside `FixedArray<T>` / `FixedList<T>`, if real games demand them.
- **An external editor / scene-view play mode.** The emulator ABI and the framebuffer/input/save entry points were shaped so an editor could embed the real compiled ROM: architecturally enabled, never yet exercised by a real consumer.

## Decided against

Recorded so they are not re-litigated. Reasons live in `ModuleAnalysis`, the framework doc comments, and the thesis's anti-goals.

- A .NET runtime, GC, or JIT on the target: the founding constraint.
- A `Game` lifecycle base class. `Game` is a static class; data-oriented style is the house style.
- `[assembly: MaxSprites(n)]`: sprite indices are runtime values, so the budget could not be checked honestly.
- `gbsharp banks` / `gbsharp size` commands: folded into the build report, which every build prints anyway.
- Absolute cycle counts: a confidently wrong number hides the hardware more effectively than silence, because it is quotable. Estimates stay banded, rounded and comparative.
- A coverage *percentage*: a `.sym` gives no symbol its length, so a byte-based percentage would improve when you add code.
