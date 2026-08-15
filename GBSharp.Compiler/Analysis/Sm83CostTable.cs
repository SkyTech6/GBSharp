using GBSharp.Compiler.IR;

namespace GBSharp.Compiler.Analysis;

// What an IR operation costs on an SM83, approximately.
//
// Unit: T-cycles, ticks of the 4.194304 MHz clock. Every figure in the cycle
// cost band is in these, including the ones quoted in diagnostic help text.
// Machine cycles (T/4) are the other convention in circulation and mixing the
// two makes every number incomparable, so GB# picks one and says so.
//
// WHAT THIS MODEL CANNOT SEE
//
// GB# emits C. SDCC then decides what the machine actually executes, and the
// gap between those two things is this file's entire error bar:
//
//   1. Register allocation. SDCC may keep a loop counter in a register for the
//      loop's whole life where this model charges a frame access every touch.
//      This is the largest single source of overestimate.
//   2. The peephole optimiser. Whole sequences vanish that this model charges
//      for, and the amount varies with the shape of the surrounding code.
//   3. Constant folding after GB# hands over. GB# folds what Roslyn folded;
//      SDCC folds more.
//   4. Inlining decisions, both SDCC's and GBDK's. gbs_runtime.h's own header
//      records how large this effect is: changing one keyword from `inline` to
//      `static inline` turns all 22 framework wrappers into real calls.
//   5. Branch outcomes. Both arms of a conditional are considered and the
//      worse one charged, so a program that always takes the cheap arm is
//      overcharged by the difference.
//   6. Interrupt time. The VBlank handler and any audio driver take cycles out
//      of the same frame and appear nowhere in the IR.
//   7. Loaders whose length is a runtime value. See BulkCopy below.
//
// The honest accuracy band that leaves: roughly +/-30-50% for straight-line
// 8-bit work on locals, and a factor of two to five in either direction once
// calls, struct copies or 16-bit arithmetic are involved.
//
// WHAT FOLLOWS FROM THAT
//
// The model is worth having anyway, because the questions it answers are
// comparative (is this loop more expensive than that one, did this change make
// things worse, does one iteration plausibly fit in a frame), and a systematic
// error cancels out of a comparison. What it must not do is present itself as a
// measurement. So: every diagnostic derived from these numbers says
// "estimated", figures are passed through RoundForDisplay first, and a cost
// that includes something unmeasurable is marked partial and says so.
//
// This is a worst-case model. Where a choice arises, it charges the more
// expensive path; stating that once here means no individual case has to repeat
// it.
//
// REFERENCE MEASUREMENTS
//
// Samples/Enemies, GBDK-2020 4.5.0, 2026-08-13. Bytes are the gaps between
// symbol addresses in the linker's .sym, in bank 0:
//
//   function                 estimated cycles   emitted bytes
//   Program.Main()                        186              30
//   EnemySystem.Update()                  440              59
//   Program.Setup()                     2,930             105
//   Program.UpdateEnemies()             5,338              31
//   Program.Draw()                     28,314              70
//
// Recorded rather than asserted. A test pinning these would fail on every
// refinement of the table and pass forever if the table were simply wrong; a
// dated note next to the version it was taken against ages honestly instead.
//
// Read the bottom two rows before trusting any of this: UpdateEnemies and Draw
// are almost all loop, so their cycles are large while their code is small.
// Bytes and cycles only track each other for straight-line code, which is why
// the rank-agreement test in CostRankingTests compares nothing else.

/// <summary>
/// The per-operation cycle figures the cost model is built from.
/// </summary>
/// <remarks>
/// Constants rather than a lookup table, each named for the instruction
/// sequence it stands for, because a bare number in a cost table is a claim
/// nobody can check. See the file header for the model's limits.
/// </remarks>
public static class Sm83CostTable
{
    /// <summary>
    /// T-cycles in one frame: 154 scanlines of 456 cycles each.
    /// </summary>
    /// <remarks>
    /// The only exact figure in this file, and the only one that is a property
    /// of the hardware rather than an estimate of what SDCC will emit. At the
    /// 4.194304 MHz clock this is 59.7275 frames a second. It is what gives
    /// every other number here a denominator worth quoting.
    /// </remarks>
    public const int FrameCycles = 70_224;

    /// <summary>Frames per second, for the report's header row.</summary>
    public const double FramesPerSecond = 4_194_304.0 / FrameCycles;

    // -----------------------------------------------------------------------
    // Data access
    // -----------------------------------------------------------------------

    /// <summary>A register-to-register 8-bit ALU operation: <c>add a, b</c>.</summary>
    public const int ByteRegisterOp = 4;

    /// <summary>The same operation reaching memory through <c>(hl)</c>.</summary>
    public const int ByteMemoryOp = 8;

    /// <summary>
    /// One byte of a local or parameter, amortised.
    /// </summary>
    /// <remarks>
    /// A frame slot is reached with <c>ldhl sp, #n</c> then <c>ld a, (hl)</c>,
    /// which is 20 cycles for the first byte and 8 for each after it, but SDCC
    /// keeps many locals in registers, where the access is free. 10 sits between
    /// those, and the file header names this as the model's largest overestimate.
    /// <para>
    /// Parameters cost the same as locals. SDCC passes the first argument in
    /// registers, so this overstates slightly, in the direction a worst-case
    /// model should.
    /// </para>
    /// </remarks>
    public const int LocalByte = 10;

    /// <summary>
    /// One byte of a global: <c>ld a, (nn)</c>.
    /// </summary>
    /// <remarks>
    /// Exact, and deliberately more than <see cref="LocalByte"/>. An absolute
    /// load cannot fold into a shorter form and cannot live in a register across
    /// a call, so "a static field costs more to touch than a local" is a real
    /// fact a developer can act on rather than an artefact of the model.
    /// </remarks>
    public const int GlobalByte = 16;

    /// <summary>Loading a pointer and reading through it.</summary>
    public const int PointerDeref = 16;

    /// <summary>Adding a constant field offset to a pointer already in hand.</summary>
    /// <remarks>Waived when the offset is zero, which costs nothing at all.</remarks>
    public const int FieldOffsetAdd = 8;

    /// <summary>Forming the address of an array element, before index scaling.</summary>
    public const int IndexBase = 20;

    // -----------------------------------------------------------------------
    // Arithmetic
    // -----------------------------------------------------------------------

    /// <summary>
    /// One byte of a multi-byte arithmetic chain, including the carry plumbing
    /// SDCC emits between bytes.
    /// </summary>
    /// <remarks>
    /// Charged per byte of the operand width, so 8-bit work costs about 10,
    /// 16-bit about 20 and 32-bit about 40. The ordering is the point, and it is
    /// the same ordering the frontend already warns about in GBS0101 and
    /// GBS0007; the two agree by construction rather than by coincidence.
    /// </remarks>
    public const int PerByteArithmetic = 10;

    /// <summary>One step of a shift: <c>sla a</c> and friends are CB-prefixed.</summary>
    public const int ShiftStep = 8;

    /// <summary>
    /// Steps assumed for a shift whose distance is not a constant.
    /// </summary>
    /// <remarks>
    /// A guess, and named so it reads as one. A variable shift becomes a loop
    /// whose length GB# cannot know; assuming four steps is wrong in a bounded
    /// way, whereas assuming zero would be wrong in an unbounded one.
    /// </remarks>
    public const int AssumedVariableShift = 4;

    /// <summary>An 8-bit multiply, through SDCC's <c>__muluchar</c>.</summary>
    /// <remarks>
    /// SM83 has no multiply instruction. Every one of these is a call to a
    /// library routine that shifts and adds, so the figure is that loop's
    /// length rather than an instruction timing.
    /// </remarks>
    public const int MultiplyHelper8 = 100;

    /// <summary>A 16-bit multiply, through SDCC's <c>__mulint</c>.</summary>
    public const int MultiplyHelper16 = 240;

    /// <summary>An 8-bit divide or remainder, through SDCC's <c>__divuchar</c>.</summary>
    /// <remarks>
    /// Remainder costs what division costs: the same routine computes both, and
    /// which of the two results is used does not change the work done.
    /// </remarks>
    public const int DivideHelper8 = 250;

    /// <summary>A 16-bit divide or remainder, through SDCC's <c>__divuint</c>.</summary>
    public const int DivideHelper16 = 700;

    /// <summary>Added when a multiply or divide helper has to handle signs.</summary>
    public const int SignedHelperPenalty = 60;

    /// <summary>Widening a value: zeroing the high byte.</summary>
    public const int Widen = 8;

    /// <summary>Sign-extending a value, which has to test and propagate the sign bit.</summary>
    public const int SignExtend = 20;

    /// <summary>
    /// Narrowing a value, which is not an instruction.
    /// </summary>
    /// <remarks>
    /// Dropping the high byte costs nothing; the cast a developer writes to get
    /// back to <c>byte</c> is free. What was not free was the widening it
    /// undoes, which is exactly what GBS0101 is trying to teach and why this is
    /// worth a named zero rather than a silent omission.
    /// </remarks>
    public const int Narrow = 0;

    // -----------------------------------------------------------------------
    // Control flow and calls
    // -----------------------------------------------------------------------

    /// <summary>A taken conditional jump.</summary>
    public const int Branch = 12;

    /// <summary>
    /// A call to another function in this module: <c>call</c> plus <c>ret</c>.
    /// </summary>
    /// <remarks>
    /// Exact, and excludes the callee's body: a call site is charged for the
    /// call, not for what it reaches. See <c>FunctionCost.Cycles</c>.
    /// </remarks>
    public const int LocalCall = 24 + 16;

    /// <summary>
    /// Mapping a ROM bank: remembering the current one and writing the MBC register.
    /// </summary>
    public const int BankSwitch = 40;

    /// <summary>Saving and restoring the caller's bank around a banked call.</summary>
    public const int TrampolineOverhead = 20;

    /// <summary>
    /// A call to a function in another bank, through GBDK's trampoline.
    /// </summary>
    /// <remarks>
    /// The trampoline saves the current bank, switches, calls, and switches
    /// back. The difference from <see cref="LocalCall"/> is the figure GBS0301's
    /// help text quotes, and it is read from here so the two cannot disagree.
    /// </remarks>
    public const int BankedCall = LocalCall + (2 * BankSwitch) + TrampolineOverhead;

    /// <summary>What a banked call costs over a local one.</summary>
    public static int BankedCallOverhead => BankedCall - LocalCall;

    /// <summary>
    /// A framework wrapper from <c>gbs_runtime.h</c>: the hardware access itself.
    /// </summary>
    /// <remarks>
    /// Everything in that header is bare <c>inline</c> and compiles to the
    /// register read or write it wraps, with no call at all. That "costs nothing
    /// beyond the operation it wraps" is a promise the framework's design rests
    /// on, and the header says so at length.
    /// </remarks>
    public const int InlineShimCall = 8;

    /// <summary>
    /// Sampling the joypad, which is what every <c>Input</c> member does.
    /// </summary>
    /// <remarks>
    /// GBDK's <c>joypad()</c> selects each half of the pad in turn and waits for
    /// the lines to settle before reading. <c>gbs_runtime.h</c> already warns in
    /// prose that "testing several buttons therefore costs several reads"; this
    /// constant is what turns that sentence into a number in the build report.
    /// </remarks>
    public const int JoypadRead = 120;

    /// <summary>
    /// A <c>gbs_runtime.c</c> entry point, which maps a bank and restores it.
    /// </summary>
    /// <remarks>
    /// The split between this and <see cref="InlineShimCall"/> is not a
    /// modelling convenience: it is the actual division of the runtime. The
    /// header holds everything that inlines away, and the <c>.c</c> holds only
    /// what has to switch banks to do its job. A test asserts every function
    /// defined in the <c>.c</c> is classified here, so the two cannot drift.
    /// </remarks>
    public const int RuntimeCall = LocalCall + (2 * BankSwitch);

    /// <summary>How a <c>[Native]</c> symbol is reached.</summary>
    public enum NativeKind
    {
        /// <summary>An inlined wrapper from <c>gbs_runtime.h</c>.</summary>
        InlineShim,

        /// <summary>An <c>Input</c> member, which samples the joypad.</summary>
        Joypad,

        /// <summary>A <c>gbs_runtime.c</c> entry point, which switches banks.</summary>
        Runtime,

        /// <summary>
        /// A <c>gbs_runtime.c</c> entry point that also copies a buffer whose
        /// length is only known at runtime.
        /// </summary>
        BulkCopy,
    }

    /// <summary>
    /// Everything defined in <c>gbs_runtime.c</c>, and which of those also copies
    /// a runtime-length buffer.
    /// </summary>
    /// <remarks>
    /// Anything not named here is assumed to be an inlined header wrapper, which
    /// is the right default: the header is where a symbol goes unless it needs to
    /// switch banks, so the exceptions are the short list.
    /// </remarks>
    private static readonly Dictionary<string, NativeKind> NativeKinds = new(StringComparer.Ordinal)
    {
        ["gbs_bank_switch"] = NativeKind.Runtime,
        ["gbs_data_read"] = NativeKind.Runtime,
        ["gbs_metasprite_move"] = NativeKind.Runtime,
        ["gbs_metasprite_move_flip_x"] = NativeKind.Runtime,
        ["gbs_metasprite_move_flip_y"] = NativeKind.Runtime,
        ["gbs_metasprite_move_flip_xy"] = NativeKind.Runtime,

        ["gbs_background_load"] = NativeKind.BulkCopy,
        ["gbs_window_load"] = NativeKind.BulkCopy,
        ["gbs_background_draw_region"] = NativeKind.BulkCopy,
        ["gbs_font_load"] = NativeKind.BulkCopy,
        ["gbs_font_draw"] = NativeKind.BulkCopy,
        ["gbs_win_font_draw"] = NativeKind.BulkCopy,
        ["gbs_sprite_load"] = NativeKind.BulkCopy,
        ["gbs_metasprite_load"] = NativeKind.BulkCopy,
    };

    /// <summary>The prefix every joypad-sampling shim shares.</summary>
    private const string InputPrefix = "gbs_input_";

    /// <summary>How a native symbol is reached, and therefore what it costs.</summary>
    public static NativeKind KindOf(string symbol)
    {
        if (NativeKinds.TryGetValue(symbol, out NativeKind kind))
        {
            return kind;
        }

        return symbol.StartsWith(InputPrefix, StringComparison.Ordinal)
            ? NativeKind.Joypad
            : NativeKind.InlineShim;
    }

    /// <summary>What one call to a native symbol costs, excluding its arguments.</summary>
    public static int NativeCallCost(string symbol) => KindOf(symbol) switch
    {
        NativeKind.Joypad => JoypadRead,
        NativeKind.Runtime or NativeKind.BulkCopy => RuntimeCall,
        _ => InlineShimCall,
    };

    /// <summary>
    /// The cost of a binary operation on operands of the given type, excluding
    /// the operands themselves.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="operandType">
    /// The width the operation runs at. For a comparison this is the operands'
    /// type, not <c>bool</c>: comparing two 16-bit values is 16-bit work.
    /// </param>
    /// <param name="shiftBy">
    /// The constant shift distance, or null if the distance is not a constant.
    /// </param>
    public static int BinaryCost(IRBinaryOperator op, IRType operandType, int? shiftBy = null)
    {
        int width = Math.Max(1, operandType.SizeInBytes);
        bool signed = operandType is IRPrimitiveType { IsSigned: true };

        switch (op)
        {
            case IRBinaryOperator.Multiply:
                return HelperCost(width <= 1 ? MultiplyHelper8 : MultiplyHelper16, width, signed);

            case IRBinaryOperator.Divide:
            case IRBinaryOperator.Remainder:
                return HelperCost(width <= 1 ? DivideHelper8 : DivideHelper16, width, signed);

            case IRBinaryOperator.ShiftLeft:
            case IRBinaryOperator.ShiftRight:
                return (shiftBy ?? AssumedVariableShift) * width * ShiftStep;

            // A short-circuiting operator is a branch, not arithmetic: the
            // right-hand side may not run at all, and its own cost is charged
            // where it appears.
            case IRBinaryOperator.LogicalAnd:
            case IRBinaryOperator.LogicalOr:
                return Branch;

            default:
                return width * PerByteArithmetic;
        }
    }

    /// <summary>
    /// A helper call's cost, scaled for 32-bit operands and for sign handling.
    /// </summary>
    /// <remarks>
    /// 32-bit multiply and divide are roughly two and a half times their 16-bit
    /// forms: the same routine over twice the bytes, plus the extra carry
    /// plumbing. GB# already reports 32-bit arithmetic as a subset warning
    /// (GBS0007), so this only has to be the right order of magnitude.
    /// </remarks>
    private static int HelperCost(int baseCost, int width, bool signed)
    {
        int cost = width >= 4 ? (baseCost * 5) / 2 : baseCost;
        return signed ? cost + SignedHelperPenalty : cost;
    }

    /// <summary>The cost of converting between two widths.</summary>
    public static int ConvertCost(IRType from, IRType to)
    {
        if (to.SizeInBytes <= from.SizeInBytes)
        {
            return Narrow;
        }

        return from is IRPrimitiveType { IsSigned: true } ? SignExtend : Widen;
    }

    /// <summary>
    /// Rounds an estimate to the precision the model can actually support.
    /// </summary>
    /// <remarks>
    /// Two significant figures above a thousand. Printing "2,141 cycles" claims
    /// a precision this file does not have; printing "2,100" claims what it
    /// does. Small numbers are left alone, because rounding 40 to 0 would lose
    /// the only thing the figure was saying.
    /// </remarks>
    public static int RoundForDisplay(int cycles)
    {
        if (cycles < 1_000)
        {
            return cycles;
        }

        int magnitude = 1;

        while (cycles / magnitude >= 100)
        {
            magnitude *= 10;
        }

        return (cycles + (magnitude / 2)) / magnitude * magnitude;
    }

    /// <summary>An estimate as a percentage of one frame, rounded to a whole number.</summary>
    public static int PercentOfFrame(int cycles) =>
        (int)Math.Round(cycles * 100.0 / FrameCycles, MidpointRounding.AwayFromZero);
}
