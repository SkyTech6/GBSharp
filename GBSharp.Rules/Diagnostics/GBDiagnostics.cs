namespace GBSharp.Compiler.Diagnostics;

/// <summary>
/// Every diagnostic GB# can report.
/// </summary>
/// <remarks>
/// Ids are permanent once released. See <see cref="GBDiagnosticCategory"/> for
/// the range each band owns.
/// </remarks>
public static class GBDiagnostics
{
    // A rejection is not suppressible: lowering answers "cannot represent this"
    // by returning null and relies on the build stopping before that null is
    // used. A cost note is, because nothing downstream reads it.
    private static GBDiagnosticDescriptor Language(string id, string title, string message, string? help = null) =>
        new(id, title, message, GBDiagnosticCategory.Language, GBSeverity.Error, help);

    private static GBDiagnosticDescriptor Perf(string id, string title, string message, string? help = null) =>
        new(id, title, message, GBDiagnosticCategory.Performance, GBSeverity.Performance, help, isSuppressible: true);

    private static GBDiagnosticDescriptor Memory(string id, string title, string message, string? help = null) =>
        new(id, title, message, GBDiagnosticCategory.Memory, GBSeverity.Resource, help, isSuppressible: true);

    private static GBDiagnosticDescriptor Banking(string id, string title, string message, string? help = null) =>
        new(id, title, message, GBDiagnosticCategory.Banking, GBSeverity.Error, help);

    private static GBDiagnosticDescriptor Toolchain(string id, string title, string message, string? help = null) =>
        new(id, title, message, GBDiagnosticCategory.Toolchain, GBSeverity.Error, help);

    private static GBDiagnosticDescriptor Asset(string id, string title, string message, string? help = null) =>
        new(id, title, message, GBDiagnosticCategory.Assets, GBSeverity.Error, help);

    // ---------------------------------------------------------------------
    // GBS0001-GBS0099 - language subset
    // ---------------------------------------------------------------------

    public static readonly GBDiagnosticDescriptor UnsupportedConstruct = Language(
        "GBS0001",
        "Unsupported construct",
        "{0} is not supported in GB#.",
        "GB# supports a subset of C# that lowers predictably to an 8-bit target. " +
        "See the GB# language reference for the supported constructs.");

    public static readonly GBDiagnosticDescriptor UnsupportedType = Language(
        "GBS0002",
        "Unsupported type",
        "Type '{0}' is not supported in GB#.",
        "GB# supports byte, sbyte, ushort, short, bool, enums, structs and fixed-size arrays.");

    public static readonly GBDiagnosticDescriptor MissingEntryPoint = Language(
        "GBS0003",
        "No entry point",
        "No entry point found.",
        "GB# needs one 'public static void Main()' method with no parameters.");

    public static readonly GBDiagnosticDescriptor MultipleEntryPoints = Language(
        "GBS0004",
        "Multiple entry points",
        "Found {0} candidate entry points; GB# needs exactly one.",
        "Remove or rename the extra 'static void Main()' methods.");

    public static readonly GBDiagnosticDescriptor UnresolvedCall = Language(
        "GBS0005",
        "Call cannot be lowered",
        "'{0}' has no body in this compilation and is not marked [Native].",
        "GB# can only call methods it can see the source of, or methods mapped to a C symbol " +
        "with [Native(\"symbol\")].");

    public static readonly GBDiagnosticDescriptor Int32Arithmetic = new(
        "GBS0007",
        "32-bit arithmetic",
        "'{0}' requires 32-bit arithmetic on SM83.",
        GBDiagnosticCategory.Language,
        GBSeverity.Performance,
        "Consider ushort if values cannot exceed 65,535, or byte if they cannot exceed 255.");

    public static readonly GBDiagnosticDescriptor DynamicCollection = Language(
        "GBS0042",
        "Dynamic collection",
        "{0} requires dynamic allocation.",
        "Use FixedList<T, N> or FixedArray<T, N> instead. Capacity stays visible in the source, " +
        "and the storage is reserved at compile time.");

    public static readonly GBDiagnosticDescriptor StringType = Language(
        "GBS0043",
        "System.String is unavailable",
        "System.String requires heap allocation and is not available in GB#.",
        "Use a fixed byte array for text, and the tile-based text APIs to draw it.");

    public static readonly GBDiagnosticDescriptor Exceptions = Language(
        "GBS0044",
        "Exceptions are unavailable",
        "Exception handling is not supported in GB#.",
        "There is no unwinding machinery on the target. Return a status value instead.");

    public static readonly GBDiagnosticDescriptor DelegatesAndEvents = Language(
        "GBS0045",
        "Delegates and events are unavailable",
        "{0} requires runtime dispatch and is not supported in GB#.",
        "Call the target directly, or switch on an enum to choose between behaviours.");

    public static readonly GBDiagnosticDescriptor Interfaces = Language(
        "GBS0046",
        "Interfaces are unavailable",
        "Interface '{0}' requires virtual dispatch and is not supported in GB#.",
        "Use a struct with an enum tag and a switch, which lowers to a jump the developer can see.");

    public static readonly GBDiagnosticDescriptor AsyncAwait = Language(
        "GBS0047",
        "async/await is unavailable",
        "'async' and 'await' are not supported in GB#.",
        "There is no scheduler on the target. Drive work from the frame loop instead.");

    public static readonly GBDiagnosticDescriptor Boxing = Language(
        "GBS0048",
        "Boxing",
        "Converting '{0}' to '{1}' boxes the value onto a heap GB# does not have.",
        "Keep the value in its own type. GB# has no object header and no allocator.");

    public static readonly GBDiagnosticDescriptor Linq = Language(
        "GBS0049",
        "LINQ is unavailable",
        "LINQ is not supported in GB#.",
        "Write the loop. On an 8-bit CPU the loop is what you want to be able to read anyway.");

    public static readonly GBDiagnosticDescriptor ReferenceTypeAllocation = Language(
        "GBS0050",
        "Reference type allocation",
        "Allocating '{0}' requires a heap GB# does not have.",
        "Declare it as a struct, or make the type static if it holds no per-instance state.");

    public static readonly GBDiagnosticDescriptor UnsupportedOperator = Language(
        "GBS0051",
        "Unsupported operator",
        "Operator '{0}' is not supported in GB#.");

    public static readonly GBDiagnosticDescriptor UnsizedArray = Language(
        "GBS0052",
        "Array size must be constant",
        "The length of '{0}' must be a compile-time constant.",
        "GB# reserves array storage at compile time, so the length has to be known then.");

    public static readonly GBDiagnosticDescriptor NativeSignatureInvalid = Language(
        "GBS0053",
        "Invalid [Native] declaration",
        "[Native] member '{0}' is invalid: {1}");

    public static readonly GBDiagnosticDescriptor CapacityRequired = Language(
        "GBS0054",
        "Capacity required",
        "'{0}' needs a [Capacity(n)] attribute on its declaration.",
        "GB# reserves the storage at compile time, so the capacity has to be written where the " +
        "collection is declared. For example: [Capacity(8)] static FixedList<Enemy> enemies;");

    public static readonly GBDiagnosticDescriptor CapacityInvalid = Language(
        "GBS0055",
        "Invalid capacity",
        "Capacity {0} on '{1}' is out of range.",
        "A fixed collection must hold between 1 and 255 items.");

    public static readonly GBDiagnosticDescriptor WriteToReadOnlyData = Language(
        "GBS0056",
        "Write to read-only data",
        "'{0}' is read-only data in ROM and cannot be assigned.",
        "A cartridge cannot be written to. Copy the value into a mutable array or a local " +
        "if it has to change while the game runs, or drop 'readonly' to move the whole array " +
        "into WRAM and pay for it there.");

    public static readonly GBDiagnosticDescriptor NonConstantInitializer = Language(
        "GBS0057",
        "Initializer is not constant",
        "Element {0} of '{1}' is not a compile-time constant.",
        "GB# writes static data into the ROM image at build time, so every element has to be " +
        "known then. Assign the value in Main if it can only be computed at runtime.");

    // A graph property, not a cost estimate, which is why it sits in this band
    // rather than with the cycle costs: the answer is exact and needs no hedging.
    // A warning rather than an error because bounded recursion over a small fixed
    // depth does work, and refusing it outright would be the compiler overruling
    // a developer who has done the arithmetic.
    public static readonly GBDiagnosticDescriptor RecursiveCall = new(
        "GBS0058",
        "Recursive call",
        "'{0}' is part of a recursive call cycle: {1}.",
        GBDiagnosticCategory.Language,
        GBSeverity.Warning,
        "SM83 has no stack limit check. The stack starts at the top of work RAM and grows down " +
        "through the same 8 KB the static fields grow up through, so a recursion that goes one " +
        "level too deep overwrites them: the failure looks like a variable changing value on its " +
        "own rather than like a crash. This is also why GB# reports no call depth for this " +
        "program, since the depth is whatever the data makes it. Rewriting the recursion as a " +
        "loop over a FixedList is the usual fix.",
        isSuppressible: true);

    public static readonly GBDiagnosticDescriptor ConstructorPosition = Language(
        "GBS0059",
        "Constructor needs somewhere to construct",
        "A '{0}' constructor cannot be used here.",
        "A GB# constructor writes through a pointer to storage that already exists, so it needs " +
        "a variable to fill. Assign it to one first - 'Point p = new Point(3, 4);' or " +
        "'p = new Point(3, 4);' - and pass that. Constructing straight into an argument or a " +
        "return would need a temporary GB# invented, which is stack the developer cannot see.");

    // ---------------------------------------------------------------------
    // GBS0100-GBS0199 - performance
    // ---------------------------------------------------------------------

    public static readonly GBDiagnosticDescriptor WideningArithmetic = Perf(
        "GBS0101",
        "Arithmetic widened",
        "This {0} operation runs at {1} bits because its result is used as {2}.",
        "Cast the result back to the narrower type to keep the arithmetic 8-bit.");

    public static readonly GBDiagnosticDescriptor ExpensiveMultiplication = Perf(
        "GBS0102",
        "Expensive multiplication",
        "Multiplication of {0} values generates expensive code on SM83.",
        "SM83 has no multiply instruction. Consider a shift if one operand is a power of two, " +
        "or a lookup table if the range is small.");

    public static readonly GBDiagnosticDescriptor ExpensiveDivision = Perf(
        "GBS0103",
        "Expensive division",
        "{0} of {1} values generates expensive code on SM83.",
        "SM83 has no divide instruction. Consider a shift if the divisor is a power of two.");

    // ---------------------------------------------------------------------
    // GBS0200-GBS0299 - memory
    // ---------------------------------------------------------------------

    public static readonly GBDiagnosticDescriptor StaticAllocation = Memory(
        "GBS0201",
        "Static allocation",
        "{0} reserves {1} bytes of WRAM.");

    public static readonly GBDiagnosticDescriptor LargeStruct = Memory(
        "GBS0202",
        "Large struct passed by value",
        "'{0}' is {1} bytes and is passed by value, which copies it through the stack.",
        "Pass it by 'ref' to copy a 2-byte pointer instead.");

    public static readonly GBDiagnosticDescriptor RomAllocation = Memory(
        "GBS0203",
        "ROM allocation",
        "{0} reserves {1} bytes of ROM.");

    public static readonly GBDiagnosticDescriptor VramBudget = Memory(
        "GBS0204",
        "VRAM tile budget",
        "Background and window assets need {0} tiles of the {1} that region holds.",
        "Background and window share one 256-tile region of video memory; sprites have their own. " +
        "Every asset loaded at the same time has to fit together, and GB# counts them all because it " +
        "can see them all. A total above the region is fine when screens replace each other at " +
        "runtime - GB# cannot see load order, so it reports the sum rather than failing the build.");

    public static readonly GBDiagnosticDescriptor VramBudgetExceeded = new(
        "GBS0205",
        "VRAM tile budget exceeded",
        "{0} needs {1} tiles by itself; the background/window region holds {2}.",
        GBDiagnosticCategory.Memory,
        GBSeverity.Error,
        "No load order can make a single asset larger than the region work. Reduce its unique tile " +
        "count - the asset table in the build report shows where the tiles are.");

    // Budgets are errors, because failing the build is the only thing that makes
    // a declared budget different from a comment. They are checked against what
    // the linker placed rather than what the code declared: the real WRAM figure
    // includes the stack, shadow OAM and GBDK's own state.

    public static readonly GBDiagnosticDescriptor WramBudgetExceeded = new(
        "GBS0210",
        "WRAM budget exceeded",
        "This game uses {0} bytes of work RAM; the declared budget is {1}.",
        GBDiagnosticCategory.Memory,
        GBSeverity.Error,
        "The figure is what the linker placed, which includes the stack, shadow OAM and GBDK's own " +
        "state as well as your static fields. Raise [assembly: MaxWRAM] if the budget was optimistic, " +
        "or move data into ROM by making it 'static readonly'.");

    public static readonly GBDiagnosticDescriptor RomBudgetExceeded = new(
        "GBS0211",
        "ROM budget exceeded",
        "The ROM is {0} bytes; the declared budget is {1}.",
        GBDiagnosticCategory.Memory,
        GBSeverity.Error,
        "Raise [assembly: MaxROM], or reduce what the cartridge holds. The build report's asset " +
        "table is usually where the bytes are.");

    public static readonly GBDiagnosticDescriptor BankBudgetExceeded = new(
        "GBS0212",
        "ROM bank budget exceeded",
        "The cartridge declares {0} banks; the budget is {1}.",
        GBDiagnosticCategory.Memory,
        GBSeverity.Error,
        "Raise [assembly: MaxROMBanks], or pack the existing banks more tightly. The build report " +
        "shows how full each one is.");

    // ---------------------------------------------------------------------
    // GBS0300-GBS0399 - banking
    //
    // Banking is the one place where GB# decides where the developer's code
    // physically goes, so these lean towards saying what was placed and where
    // rather than only refusing. The informational ones are not noise: a layout
    // nobody can see is exactly what thesis section 15 says not to build.
    // ---------------------------------------------------------------------

    public static readonly GBDiagnosticDescriptor EntryPointCannotBeBanked = Banking(
        "GBS0300",
        "The entry point cannot be banked",
        "'{0}' is the entry point and must stay in the resident bank.",
        "Execution starts here, before any bank has been switched in, so this code has to be mapped " +
        "already. Move the work into a [Bank] method and call it from Main.");

    public static readonly GBDiagnosticDescriptor BankedCall = new(
        "GBS0301",
        "Banked call",
        "Calling '{0}' switches to ROM bank {1}.",
        GBDiagnosticCategory.Banking,
        GBSeverity.Performance,
        // "Cycles" means T-cycles everywhere in GB#, including here. This used to
        // read "roughly thirty", which was the same claim in machine cycles, and
        // two units in the compiler's prose makes every number incomparable.
        "A banked call goes through a trampoline that saves the current bank, switches, calls, and " +
        "switches back, which costs roughly a hundred cycles more than a local call. The caller's " +
        "own bank is unmapped for the duration. Mark the callee [Bank(0)] to keep it resident if " +
        "it runs every frame.");

    public static readonly GBDiagnosticDescriptor BankOverflow = Banking(
        "GBS0302",
        "Bank overflow",
        "Bank {0} holds {1} bytes of declared data; a ROM bank is 16,384.",
        "This counts only the data GB# placed here. The code in this bank is measured by the linker " +
        "and reported after the build, so the real budget is tighter than this number. Move an asset " +
        "to another bank, or split the type across two.");

    public static readonly GBDiagnosticDescriptor BankedDataAccess = Banking(
        "GBS0303",
        "Banked data read directly",
        "'{0}' is in ROM bank {1} and cannot be read directly.",
        "Data outside bank 0 is only mapped while its bank is switched in, so a plain read gets " +
        "whatever happens to be at that address instead. Pass it to a loader that takes its bank, " +
        "such as Background.Load, or switch explicitly with Banking.Switch and take responsibility " +
        "for restoring the previous bank.");

    public static readonly GBDiagnosticDescriptor InvalidBank = Banking(
        "GBS0304",
        "Invalid ROM bank",
        "Bank {0} on '{1}' is not a valid ROM bank.",
        "Bank 0 is the resident bank and is always mapped, so [Bank(0)] means \"keep this resident\". " +
        "Banked code and data go in banks 1 to 255.");

    public static readonly GBDiagnosticDescriptor ConflictingBanks = Banking(
        "GBS0305",
        "Conflicting banks for shared data",
        "'{0}' and '{1}' name the same image but ask for different banks ({2} and {3}).",
        "Assets with identical contents share one copy in ROM, and one copy can only live in one bank. " +
        "Give both fields the same bank, or make the images differ so each gets its own copy.");

    public static readonly GBDiagnosticDescriptor BankedMutableData = Banking(
        "GBS0306",
        "Mutable data cannot be banked",
        "'{0}' is not read-only and cannot be placed in a ROM bank.",
        "A ROM bank holds cartridge data, which cannot be written to. Mutable statics live in the 8 KB " +
        "of work RAM, which is always mapped and is not banked on a Game Boy. Add 'readonly' to move " +
        "the data into the cartridge.");

    public static readonly GBDiagnosticDescriptor ResidentBankFull = new(
        "GBS0307",
        "Bank 0 nearly full",
        "Bank 0 is {0}% full ({1} of {2} bytes).",
        GBDiagnosticCategory.Banking,
        GBSeverity.Resource,
        "Bank 0 is always mapped and holds the interrupt vectors, the GBDK runtime, and everything " +
        "not marked [Bank]. When it fills, nothing more fits at any address, however large the " +
        "cartridge is. Move code or data out with [Bank].");

    public static readonly GBDiagnosticDescriptor ResidentBankOverflow = Banking(
        "GBS0310",
        "Bank 0 overflowed",
        "Bank 0 overflowed by {0} bytes: '{1}' runs from {2} to {3}, past the 0x4000 boundary.",
        "Bank 0 is the 16 KB at 0x0000-0x3FFF, and the linker placed this area straight through the " +
        "end of it. Everything above 0x4000 is where the switchable bank appears, so at run time it " +
        "is overlaid by whichever bank is mapped: the ROM builds, and then fails as soon as it " +
        "reaches the part that moved. Neither lcc nor sdld reports this, and the bank usage above " +
        "cannot show it, because the spilled bytes are counted against the bank whose addresses they " +
        "landed on. Move code or data out of the resident bank with [Bank].");

    public static readonly GBDiagnosticDescriptor BankPlacement = new(
        "GBS0308",
        "Bank placement",
        "Bank {0} holds {1} symbol{2} and {3} bytes of declared data.",
        GBDiagnosticCategory.Banking,
        GBSeverity.Info,
        null);

    public static readonly GBDiagnosticDescriptor AutomaticPlacement = new(
        "GBS0309",
        "Automatic placement",
        "'{0}' was placed automatically in bank {1}.",
        GBDiagnosticCategory.Banking,
        GBSeverity.Info,
        // No placeholder here: help text is shown verbatim, never formatted, so
        // a {1} would reach the developer as literal braces.
        "GB# left this to GBDK's bankpack rather than choosing itself. Write [Bank(n)] on the " +
        "declaration, with the bank named above, to pin it there instead.");

    // ---------------------------------------------------------------------
    // GBS0400-GBS0499 - cycle cost
    //
    // Every number in this band is a static estimate from the IR, never a
    // measurement. GB# emits C and SDCC decides what actually runs: this band
    // cannot see the register allocator, the peephole pass, or which way a
    // branch goes. So the wording is "estimated" throughout, figures are rounded
    // to the precision the model can support, and a note whose cost includes
    // something unknowable says so rather than quietly omitting it.
    //
    // The questions these answer are comparative -- is this loop dearer than
    // that one, did this change make things worse, does one iteration plausibly
    // fit in a frame -- and a systematic error cancels out of a comparison. An
    // estimate presented as a measurement would be worse than no estimate.
    // ---------------------------------------------------------------------

    private static GBDiagnosticDescriptor Cost(string id, string title, string message, string? help = null) =>
        new(id, title, message, GBDiagnosticCategory.CycleCost, GBSeverity.Performance, help, isSuppressible: true);

    public static readonly GBDiagnosticDescriptor FrameBudgetPressure = Cost(
        "GBS0401",
        "Frame loop is close to a frame",
        "This frame loop costs an estimated {0} cycles an iteration, about {1}% of a frame.{2}",
        "The hardware gives 70,224 cycles between frames, at 59.7 frames a second. Everything " +
        "else comes out of the same budget: the VBlank handler, any audio driver, and whatever " +
        "the loaders copy, none of which GB# can see from the source. So 100% is well past too " +
        "late, and this fires early on purpose. The build report ranks the functions this loop " +
        "reaches, which is usually where the time has gone.");

    public static readonly GBDiagnosticDescriptor LoopCycleCost = Cost(
        "GBS0410",
        "Loop cost",
        "This loop runs up to {0} times at an estimated {1} cycles each, about {2} in total.{3}",
        "The trip count comes from the loop's own bounds, or from the capacity of the collection " +
        "it walks, which its count can never exceed. A 'break' makes the count an upper bound " +
        "rather than an exact one, which is what a worst-case estimate wants. The per-iteration " +
        "figure does not account for what SDCC keeps in registers, so read the total as a ceiling " +
        "and as a way of comparing two versions of the same loop.");

    public static readonly GBDiagnosticDescriptor StackDepthNote = new(
        "GBS0420",
        "Call depth",
        "The deepest call path is {0} calls: {1}.",
        GBDiagnosticCategory.CycleCost,
        GBSeverity.Resource,
        "Every call costs two bytes of stack for its return address before any argument or local, " +
        "and a banked call costs more again for the trampoline. The stack grows down from the top " +
        "of work RAM into the same 8 KB the static fields grow up through, and nothing checks. " +
        "This depth is exact rather than estimated: GB# has no delegates and no function " +
        "pointers, so the call graph is the whole account of what can reach what.",
        isSuppressible: true);

    // Both bank hints are whole-program facts. GBS0301 already reports that a
    // call is banked, from the call site, which is the part a single site can
    // know; these two say what only the call graph can, and that is the whole
    // justification for the extra noise.

    public static readonly GBDiagnosticDescriptor BankedCallInFrameLoop = Cost(
        "GBS0440",
        "Banked call every frame",
        "'{0}' is reached from the frame loop and switches to ROM bank {1}, which costs an "
        + "estimated {2} cycles more than a local call every time.",
        "GBS0301 says this call is banked. This says it is banked on the path that runs sixty "
        + "times a second, which is the version worth doing something about. Mark the callee "
        + "[Bank(0)] to keep it resident, or hoist the call out of the loop and keep what it "
        + "returned. Bank 0 is only 16 KB and the build report shows how much of it is left.");

    public static readonly GBDiagnosticDescriptor BankGrouping = new(
        "GBS0441",
        "Callee could share its caller's bank",
        "'{0}' is in bank {1} and every call to it comes from bank {2}; moving it would remove "
        + "{3} bank switches.",
        GBDiagnosticCategory.CycleCost,
        GBSeverity.Info,
        "A bank switch is paid per call rather than per function, so putting a callee in the same "
        + "bank as its only caller removes all of them at once. This is a consequence, not a "
        + "recommendation: the callee may well be where it is because the other bank is full, and "
        + "the build report is what says whether it would fit.",
        isSuppressible: true);

    // ---------------------------------------------------------------------
    // GBS0500-GBS0599 - toolchain
    // ---------------------------------------------------------------------

    public static readonly GBDiagnosticDescriptor GbdkNotFound = Toolchain(
        "GBS0501",
        "GBDK not found",
        "Could not locate a GBDK-2020 installation. Looked in: {0}",
        "Run 'gbsharp doctor --fix' (or 'pwsh tools/get-gbdk.ps1' in a checkout) to fetch the " +
        "pinned toolchain, set the GBDK_HOME environment variable, or pass --gbdk-path.");

    public static readonly GBDiagnosticDescriptor BackendCompileFailed = Toolchain(
        "GBS0502",
        "Backend compilation failed",
        "GBDK failed to compile the generated C (exit code {0}). Generated source: {1}",
        "This is usually a GB# code generation bug. The generated C has been kept so it can be inspected.");

    public static readonly GBDiagnosticDescriptor ProjectFileInvalid = Toolchain(
        "GBS0503",
        "Invalid project file",
        "{0}: {1}");

    public static readonly GBDiagnosticDescriptor NoSourceFiles = Toolchain(
        "GBS0504",
        "No source files",
        "No .cs files found under '{0}'.");

    public static readonly GBDiagnosticDescriptor BankUsageUnavailable = new(
        "GBS0510",
        "Bank usage unavailable",
        "Per-bank ROM and WRAM usage could not be read ({0}).",
        GBDiagnosticCategory.Toolchain,
        GBSeverity.Info,
        "The ROM was still built. GB# reads what the linker actually placed using GBDK's " +
        "'romusage' tool; check that the toolchain install is complete.");

    public static readonly GBDiagnosticDescriptor EmulatorLaunchFailed = new(
        "GBS0506",
        "Emulator could not be launched",
        "Could not launch '{0}': {1}",
        GBDiagnosticCategory.Toolchain,
        GBSeverity.Error,
        "The ROM was built and is still on disk. Check the path, or set \"emulator\" in the project " +
        "file to the emulator you want.");

    public static readonly GBDiagnosticDescriptor DiagnosticNotSuppressible = new(
        "GBS0508",
        "Diagnostic cannot be suppressed",
        "{0} cannot be suppressed or downgraded, and the setting for it was ignored.",
        GBDiagnosticCategory.Toolchain,
        GBSeverity.Warning,
        "GB# stops the build on this diagnostic because it cannot generate code for what it " +
        "describes; letting it through would produce C that compiles and does the wrong thing. " +
        "Costs and resource notes can be configured freely.");

    public static readonly GBDiagnosticDescriptor EmulatorNotConfigured = new(
        "GBS0505",
        "No emulator configured",
        "No emulator is configured for this project.",
        GBDiagnosticCategory.Toolchain,
        GBSeverity.Warning,
        "Set \"emulator\" in the project file to the path of SameBoy, BGB or Emulicious.");

    public static readonly GBDiagnosticDescriptor LibraryNotFound = Toolchain(
        "GBS0511",
        "Library not found",
        "{0}: \"libraries\" names '{1}', which does not exist.",
        "GB# links whatever is declared under \"libraries\" without inspecting it - check the " +
        "path is correct and relative to the project directory, and that the file has been built.");

    public static readonly GBDiagnosticDescriptor IncludeNotFound = Toolchain(
        "GBS0512",
        "Include not found",
        "{0}: \"includes\" names '{1}', which does not exist.",
        "Headers declared under \"includes\" are added to the generated C, which is how a " +
        "[Native] method reaches a function the framework does not wrap - check the path is " +
        "correct and relative to the project directory.");

    // Byte 0x147 (the mapper) and byte 0x149 (the RAM size) are written by two
    // separate linker flags, so nothing but a check makes them agree. Left
    // unset, "ramBanks" now follows the mapper, so the only way to reach this
    // is to write both and write them contradicting each other.
    public static readonly GBDiagnosticDescriptor CartridgeRamMismatch = new(
        "GBS0513",
        "Cartridge RAM does not match the mapper",
        "{0}: {1}",
        GBDiagnosticCategory.Toolchain,
        GBSeverity.Warning,
        "The ROM still builds; its header just describes a cartridge that was never made. " +
        "Leave \"ramBanks\" unset to let the mapper decide, or name a mapper that matches " +
        "the save RAM you want.",
        isSuppressible: true);

    // GB# always builds from GameProject.EnumerateSourceFiles, never from the
    // .csproj a 'gbsharp new' project carries for its editor, so a mismatch here
    // cannot produce a wrong ROM. It exists only because an editor's view and
    // GB#'s own view of "the files in this project" can silently disagree.
    public static readonly GBDiagnosticDescriptor ProjectDrift = new(
        "GBS0507",
        "Project file drift",
        "'{0}' would let an editor compile a different set of files than GB# builds: {1}.",
        GBDiagnosticCategory.Toolchain,
        GBSeverity.Warning,
        "GB# always builds from gbsharp.json's own file list, never from the .csproj. Update the " +
        "csproj, or gbsharp.json's \"exclude\", so an editor sees the same files GB# does.",
        isSuppressible: true);

    // ---------------------------------------------------------------------
    // GBS0600-GBS0699 - assets
    //
    // These all report at the C# field that declared the asset, never at the
    // image. A developer reads their own source with a caret under it, which is
    // the whole point of doing conversion inside the compiler.
    // ---------------------------------------------------------------------

    public static readonly GBDiagnosticDescriptor TooManyColors = Asset(
        "GBS0601",
        "Too many colours",
        "'{0}' contains {1} colours. A 2bpp palette holds 4.",
        "Reduce the image to 4 colours, or target Game Boy Color, where each 8x8 tile can use " +
        "its own 4-colour palette out of 8.");

    public static readonly GBDiagnosticDescriptor TileTooManyColors = Asset(
        "GBS0602",
        "Tile uses too many colours",
        "The tile at ({0},{1}) in '{2}' uses {3} colours.",
        "On Game Boy Color every 8x8 tile draws from one 4-colour palette. Recolour this tile, " +
        "or align the artwork to the 8-pixel grid so colour changes fall on tile boundaries.");

    public static readonly GBDiagnosticDescriptor TooManyPalettes = Asset(
        "GBS0603",
        "Too many palettes",
        "'{0}' needs {1} background palettes; the Game Boy Color has 8.",
        "Share colours between tiles so their palettes merge. Colours that appear in the same " +
        "tile must live in the same palette, so moving one colour can free a whole palette.");

    public static readonly GBDiagnosticDescriptor TileBudgetExceeded = Asset(
        "GBS0604",
        "Tile budget exceeded",
        "'{0}' has {1} unique tiles after deduplication. A tileset holds at most {2}.",
        "Repeat more of the artwork so tiles deduplicate, or split the image into two assets " +
        "and load them into different halves of the screen.");

    public static readonly GBDiagnosticDescriptor DimensionsNotTileAligned = Asset(
        "GBS0605",
        "Dimensions not tile-aligned",
        "'{0}' is {1}x{2} pixels. Tiles are 8x8, so both dimensions must be multiples of 8.",
        "Pad or crop the image so its width and height are multiples of 8.");

    public static readonly GBDiagnosticDescriptor AssetNotFound = Asset(
        "GBS0606",
        "Asset not found",
        "Asset '{0}' was not found. Looked in: {1}",
        "Asset paths are relative to the file that declares them, then to the project's Assets " +
        "folder. Check the name and the extension.");

    public static readonly GBDiagnosticDescriptor UnsupportedImageFeature = Asset(
        "GBS0607",
        "Unsupported image feature",
        "'{0}' uses {1}, which GB# cannot read.",
        "Re-export the image as a non-interlaced PNG with 8 bits per channel. Most editors " +
        "call this \"PNG-24\" or \"PNG-8\".");

    public static readonly GBDiagnosticDescriptor MalformedImage = Asset(
        "GBS0608",
        "Malformed image",
        "'{0}' is not a readable PNG: {1}",
        "The file may be truncated or may not be a PNG at all. Opening it in an image editor " +
        "is the fastest way to confirm.");

    public static readonly GBDiagnosticDescriptor InvalidAssetDeclaration = Asset(
        "GBS0609",
        "Invalid asset declaration",
        "'{0}' is marked [Asset] but {1}.",
        "An asset field must be static and typed TileMap, TileSet, SpriteAsset, MetaspriteAsset or " +
        "FontAsset. " +
        "For example: [Asset(\"forest.png\")] static TileMap Forest;");

    public static readonly GBDiagnosticDescriptor AssetPipelineUnavailable = Asset(
        "GBS0610",
        "Asset support unavailable",
        "'{0}' declares an asset, but this compiler host has no asset pipeline.",
        "This is a GB# configuration problem, not a problem with your code. Build with 'gbsharp build'.");

    public static readonly GBDiagnosticDescriptor MapTooLarge = Asset(
        "GBS0611",
        "Map too large",
        "'{0}' is {1}x{2} tiles. GB# converts maps up to {3}x{3}.",
        "The limit is what fits in one ROM bank: a 128x128 map is 16 KB. Crop the image, or split it " +
        "into several assets and load them as the camera moves.");

    public static readonly GBDiagnosticDescriptor MapLargerThanHardware = new(
        "GBS0623",
        "Map larger than the hardware map",
        "'{0}' is {1}x{2} tiles; the hardware background map is 32x32.",
        GBDiagnosticCategory.Assets,
        GBSeverity.Info,
        "Background.Load copies as much as fits. Use Background.DrawRegion to copy a window of the " +
        "map instead, which is how a world larger than one screen scrolls.",
        isSuppressible: true);

    public static readonly GBDiagnosticDescriptor NonGreyscaleOnGameBoy = new(
        "GBS0612",
        "Colours on an original Game Boy",
        "'{0}' uses colours the original Game Boy cannot show.",
        GBDiagnosticCategory.Assets,
        GBSeverity.Warning,
        "The four colours will be assigned to the four shades by brightness. Set \"target\": " +
        "\"gbc\" in gbsharp.json to keep the colours.");

    public static readonly GBDiagnosticDescriptor AssetUsedAsValue = Asset(
        "GBS0613",
        "Asset used as a value",
        "'{0}' is an asset and can only be passed to a loader such as Background.Load.",
        "An asset is data in ROM, not a value you can copy. Pass it to the framework member " +
        "that loads it.");

    public static readonly GBDiagnosticDescriptor FlipDeduplicationUnavailable = Asset(
        "GBS0614",
        "Flip deduplication unavailable",
        "DedupeFlips is set on '{0}', but a Game Boy background map has no flip bits.",
        "Remove DedupeFlips, or target Game Boy Color, where each map cell carries X and Y " +
        "flip flags.");

    public static readonly GBDiagnosticDescriptor BinaryTooLarge = Asset(
        "GBS0615",
        "Binary asset too large",
        "'{0}' is {1} bytes; a binary asset holds at most {2}.",
        "The length is handed to your code as a 16-bit value, so it cannot describe more than 64 KB. " +
        "Split the file, or address it in chunks with separate [Binary] fields.");

    public static readonly GBDiagnosticDescriptor BinaryRomCost = new(
        "GBS0622",
        "Binary asset ROM cost",
        "{0} places {1} bytes in ROM.",
        GBDiagnosticCategory.Assets,
        GBSeverity.Resource,
        null,
        isSuppressible: true);

    public static readonly GBDiagnosticDescriptor AssetRomCost = new(
        "GBS0620",
        "Asset ROM cost",
        "{0} places {1} bytes in ROM: {2} tiles ({3} unique), {4}x{5} map.",
        GBDiagnosticCategory.Assets,
        GBSeverity.Resource,
        null);

    public static readonly GBDiagnosticDescriptor SharedAsset = new(
        "GBS0621",
        "Shared asset",
        "'{0}' and '{1}' name the same image and share one copy in ROM.",
        GBDiagnosticCategory.Assets,
        GBSeverity.Info,
        null);

    public static readonly GBDiagnosticDescriptor TallSpriteHeightNotAligned = Asset(
        "GBS0624",
        "Tall sprite height not aligned",
        "'{0}' is {1} pixels tall; TallSprites needs a multiple of 16.",
        "Each 8x16 sprite is one 16-pixel-tall column on the sheet. Pad or crop the image so its " +
        "height is a multiple of 16.");

    public static readonly GBDiagnosticDescriptor MetaspriteSheetNotDivisible = Asset(
        "GBS0625",
        "Metasprite sheet not divisible into frames",
        "'{0}' is {1}x{2} tiles; that does not divide evenly into {3}x{4}-tile frames.",
        "The sheet is a grid of same-sized frames, read left-to-right then top-to-bottom. Pad the " +
        "image, or change FrameWidth/FrameHeight so they divide the sheet evenly.");

    public static readonly GBDiagnosticDescriptor MetaspriteTooManySubSprites = Asset(
        "GBS0626",
        "Metasprite has too many sub-sprites",
        "'{0}' needs {1} sub-sprite records across its frames; that does not fit a uint8_t offset table.",
        "Use fewer frames, smaller frames, or drop more of each frame's fully-transparent tiles so " +
        "fewer sub-sprites are needed.");

    public static readonly GBDiagnosticDescriptor FontSheetShapeMismatch = Asset(
        "GBS0627",
        "Font sheet has the wrong shape",
        "'{0}' is {1}x{2} tiles; a font with {3} characters needs a sheet {3}x1 tiles.",
        "Font sheets are one row of 8x8 glyphs, one tile per character in Characters, left to " +
        "right. Pad or crop the image so its width is Characters.Length tiles and its height is " +
        "exactly one tile.");

    public static readonly GBDiagnosticDescriptor FontCharactersRequired = Asset(
        "GBS0628",
        "Font characters required",
        "'{0}' is marked [Font] but Characters is empty.",
        "Set Characters to the character set in the order the glyphs appear on the sheet, for " +
        "example [Font(\"font.png\", Characters = \"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,!?\")].");

    // ---------------------------------------------------------------------
    // GBS9000+ - internal
    // ---------------------------------------------------------------------

    public static readonly GBDiagnosticDescriptor InternalError = new(
        "GBS9001",
        "Internal compiler error",
        "Internal GB# error: {0}",
        GBDiagnosticCategory.Internal,
        GBSeverity.Error,
        "This is a bug in GB#. Please report it with the source that triggered it.");

    /// <summary>
    /// Every descriptor declared here.
    /// </summary>
    /// <remarks>
    /// Reflected over the fields rather than maintained as a list, because a list
    /// is a second place to forget to add something. Used to validate
    /// configuration against ids that actually exist.
    /// </remarks>
    public static IReadOnlyList<GBDiagnosticDescriptor> All { get; } =
    [
        .. typeof(GBDiagnostics)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(GBDiagnosticDescriptor))
            .Select(f => (GBDiagnosticDescriptor)f.GetValue(null)!),
    ];
}
