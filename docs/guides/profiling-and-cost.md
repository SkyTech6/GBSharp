# Profiling and the cost model

A Game Boy gives you 70,224 cycles between frames, at 59.7 frames a second. GB# estimates what your code spends against that at every build, from the IR, with no toolchain and no emulator, and when an estimate is not enough, `gbsharp profile` measures.

## The static estimates

```text
Program.cs(69,9): performance GBS0410: This loop runs up to 8 times at an estimated 3,500 cycles each, about 28,000 in total.
            for (byte i = 0; i < enemies.Count; i++)
            ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
```

That loop's bound is not in the source. `enemies.Count` is a runtime field, but a `FixedList` refuses to grow past its capacity, so the capacity is a ceiling the compiler can prove. Without that rule the estimate would have had nothing to say about the most ordinary loop in a GB# program. A `break` makes the count an upper bound rather than an exact one, which is what a worst-case estimate wants. See [GBS0410](../reference/diagnostics/cycle-cost.md#gbs0410-loop-cost).

**These are estimates, and the wording never pretends otherwise.** GB# emits C and SDCC decides what actually runs, so the model cannot see the register allocator, the peephole pass, or which way a branch goes. That is honest to within about ±30–50% for straight-line 8-bit work, and a factor of two to five once calls and 16-bit arithmetic are involved. Figures are rounded to two significant figures for the same reason. What survives a systematic error is the comparison: is this loop dearer than that one, did this change make things worse, and that is what the numbers are for.

## Frame loops and spin loops

The model refuses more than it answers. `while (true)` is the canonical GB# game loop, and there is no code path that can give an unbounded loop a total:

```csharp
while (true)
{
    if (Input.Right) x++;
    Sprites[0].X = x;
    Game.WaitVBlank();
}
```

A `while (true)` that waits for VBlank is the *frame* loop, so its per-iteration cost gets measured against a frame instead. One that does not wait is a spin loop with nothing to do with a frame, and gets nothing said about it at all.

```text
Program.cs(20,9): performance GBS0401: This frame loop costs an estimated 45,000 cycles an iteration, about 64% of a frame.
    The hardware gives 70,224 cycles between frames, at 59.7 frames a second. Everything else comes
    out of the same budget: the VBlank handler, any audio driver, and whatever the loaders copy,
    none of which GB# can see from the source. So 100% is well past too late.
```

[GBS0401](../reference/diagnostics/cycle-cost.md#gbs0401-frame-loop-is-close-to-a-frame) fires early on purpose, and the build report ranks the functions the loop reaches, which is usually where the time has gone.

## Recursion

Recursion is not an estimate: it is a graph property, and it was legal and undetected until now:

```text
Program.cs(31,24): warning GBS0058: 'Enemies.Cascade()' is part of a recursive call cycle: Enemies.Cascade calls itself.
    SM83 has no stack limit check. The stack starts at the top of work RAM and grows down through
    the same 8 KB the static fields grow up through, so a recursion that goes one level too deep
    overwrites them: the failure looks like a variable changing value on its own rather than like
    a crash.
```

See [GBS0058](../reference/diagnostics/language.md#gbs0058-recursive-call).

## Banked calls on the hot path

The call graph also knows something about [banking](banking.md) that a single call site cannot. [GBS0301](../reference/diagnostics/banking.md#gbs0301-banked-call) already says a call switches banks; [GBS0440](../reference/diagnostics/cycle-cost.md#gbs0440-banked-call-every-frame) says it switches banks *on the path that runs sixty times a second*:

```text
Program.cs(22,13): performance GBS0440: 'ForestLevel.Load()' is reached from the frame loop and
                   switches to ROM bank 2, which costs an estimated 100 cycles more than a local
                   call every time.
```

Its sibling [GBS0441](../reference/diagnostics/cycle-cost.md#gbs0441-callee-could-share-its-callers-bank) notices when a callee's callers all sit in one other bank. Neither fires on setup code, and neither will ever suggest moving banked code *into* bank 0, because that would be advising you to undo the `[Bank]` you wrote, and bank 0 is the 16 KB banking exists to protect.

## Call depth, in calls rather than bytes

Call depth is reported in calls rather than bytes, deliberately. GB# rejects delegates and has no function pointers, so the call graph is the complete account of what can reach what and the depth is exact. A byte figure would not be, since GB# never sees SDCC's frame layout or its spills, and would be wrong by a factor of two to four in the optimistic direction, which is the one that lets a ROM ship and then corrupt memory. The measured byte figure comes from the linker instead, in the build report's [memory figures](memory-and-budgets.md).

The frame budget is printed exactly and every other cycle figure is rounded, because only one of them is a fact. `--report-json` carries the same numbers unrounded, plus the call depth ([GBS0420](../reference/diagnostics/cycle-cost.md#gbs0420-call-depth)), which is the one worth a CI check, since it is exact and a growing stack is a real regression.

## Measuring with `gbsharp profile`

```bash
gbsharp profile MyGame
```

This builds the ROM, runs it headlessly on the instrumented flavour of the bundled emulator for 600 frames (ten seconds of emulated time; `--frames` changes it), and attributes real cycles to C# methods. No window opens and no input is played; it measures what the game does on its own, which makes it repeatable enough to compare across changes.

The attribution goes through the same symbol chain every build writes: the linker's `.sym` maps a program counter to a C symbol, and `<rom>.functions.json` maps that symbol back to the C# method it was lowered from. The estimate says what a method should cost from a walk over the IR; this says what it did cost, and the two disagreeing is information rather than a bug in either.

The same run reports **coverage**: which methods never executed at all. The two are complementary: the profile says what the expensive code was, coverage says what code this run never reached and so proved nothing about.

Costs land on the code that paid them, not on its callers. Cross-call attribution (folding a callee's cycles into whoever called it) is deliberately absent rather than approximated, because GBDK's banked-call trampolines rewrite return addresses and a wrong attribution is worse than a missing one.

`gbsharp profile` needs the instrumented emulator runtime; if it is missing, the command says so and `gbsharp doctor --fix` fetches it (`tools/get-emulator.ps1` in a checkout). See the [CLI reference](../reference/cli.md) for the full option list, and [Configuring diagnostics](diagnostics-configuration.md) for turning the estimates down once you have accepted them.
