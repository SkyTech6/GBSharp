using System.Globalization;
using System.Text;
using GBSharp.Backend.GBDK.Reporting;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

// Lives directly in the backend namespace rather than a CEmitter one, so the
// type name and the folder name from thesis section 18 can both stay as they are.
namespace GBSharp.Backend.GBDK;

/// <summary>
/// Emits C for GBDK from GB# IR.
/// </summary>
/// <remarks>
/// <para>
/// The output is meant to be read. A GBDK developer should be able to open it
/// and recognise their own program: real <c>if</c> and <c>while</c>, the
/// original names, one statement per line (thesis sections 3.3 and 9). That is
/// a product requirement, not formatting preference: it is how a developer
/// answers "what did this cost?" without a disassembler.
/// </para>
/// <para>
/// Nothing here is clever. No expression reordering, no strength reduction, no
/// inlining. SDCC is the optimiser; this emitter's job is to be predictable.
/// </para>
/// </remarks>
/// <param name="annotateSource">
/// When true, every emitted statement that carries a real
/// <see cref="IRStatement.Span"/> gets a trailing comment naming the C# line it
/// came from, and <see cref="SourceMap"/> is populated with the same
/// information for <c>build/c/sourcemap.json</c>. False reproduces today's
/// output byte-for-byte: no caller that does not ask for this changes at all.
/// </param>
/// <param name="userIncludes">
/// Bare file names of user headers to include in the shared generated header,
/// after the runtime shim. This is how a [Native] declaration reaches a
/// function the framework does not wrap: SDCC rejects calls to undeclared
/// functions, and the emitter cannot know a foreign symbol's signature, so the
/// developer states it in a header of their own. Empty or null emits exactly
/// today's includes.
/// </param>
public sealed class CEmitter(bool annotateSource = false, IReadOnlyList<string>? userIncludes = null)
{
    /// <summary>Initializer lists up to this length stay on one line.</summary>
    private const int InlineAggregateElements = 8;

    private readonly StringBuilder _output = new();
    private readonly List<SourceMapEntry> _sourceMap = [];
    private int _indent;

    /// <summary>The 1-based line about to be written in the current file.</summary>
    private int _lineNumber = 1;

    /// <summary>The bare name of the file <see cref="_output"/> is building.</summary>
    private string _currentFile = string.Empty;

    /// <summary>
    /// Every annotated statement, traced back to its C# source. Empty unless
    /// <paramref name="annotateSource"/> was true and <see cref="Emit"/> has run.
    /// </summary>
    public IReadOnlyList<SourceMapEntry> SourceMap => _sourceMap;

    /// <summary>The shared declaration header every translation unit includes.</summary>
    public const string HeaderFileName = "game.h";

    /// <summary>The translation unit holding the program itself.</summary>
    public const string ProgramFileName = "game.c";

    /// <summary>
    /// Emits the whole program as a set of files.
    /// </summary>
    /// <remarks>
    /// Declarations are split into a header rather than repeated per unit
    /// because a program is about to stop being one <c>.c</c> file: asset data
    /// and banked code each need their own translation unit, and both need to
    /// see the same structs and the same globals.
    /// </remarks>
    public IReadOnlyList<EmittedFile> Emit(IRModule module)
    {
        IReadOnlyList<IRStruct> structs = SortByDependency(module.Structs);
        HashSet<string> zeroed = CollectZeroInitializedStructs(module);
        IReadOnlyList<BankGroup> banks = Partition(module);

        var files = new List<EmittedFile>(banks.Count + 1)
        {
            new(HeaderFileName, EmitHeaderFile(module, structs, zeroed), EmittedFileKind.Header),
            new(ProgramFileName, EmitProgramFile(module, structs, zeroed), EmittedFileKind.TranslationUnit),
        };

        // Only the resident group shares a file with main and the zero
        // instances; everything else gets its own unit, because GBDK selects a
        // bank per translation unit rather than per function.
        foreach (BankGroup group in banks)
        {
            files.Add(new EmittedFile(group.FileName, EmitBankFile(module, group), EmittedFileKind.TranslationUnit)
            {
                RomBank = group.Bank.Kind == IRBankKind.Fixed ? group.Bank.Number : AutoBankSentinel,
            });
        }

        return files;
    }

    /// <summary>
    /// bankpack's "place this anywhere" sentinel.
    /// </summary>
    /// <remarks>
    /// GBDK's own examples write <c>#pragma bank 255</c> for a unit whose bank
    /// the packer should choose, so this is its convention rather than ours.
    /// </remarks>
    public const int AutoBankSentinel = 255;

    /// <summary>One translation unit's worth of banked declarations.</summary>
    private sealed record BankGroup(
        IRBank Bank,
        string FileName,
        IReadOnlyList<IRFunction> Functions,
        IReadOnlyList<IRGlobal> Globals);

    /// <summary>
    /// Splits the non-resident parts of the module into translation units.
    /// </summary>
    /// <remarks>
    /// Returns nothing at all for a program that never says <c>[Bank]</c>, so
    /// <see cref="Emit"/> produces exactly the two files it always has. Explicit
    /// banks group by number. Automatic placements group by the prefix their
    /// names share (the declaring C# type), so a class's methods and its data
    /// travel together and the packer sees one unit to place rather than several
    /// it might scatter.
    /// </remarks>
    private static IReadOnlyList<BankGroup> Partition(IRModule module)
    {
        var groups = new Dictionary<string, (IRBank Bank, List<IRFunction> Functions, List<IRGlobal> Globals)>(
            StringComparer.Ordinal);

        foreach (IRFunction function in module.Functions.Where(f => !f.Bank.IsResident))
        {
            Group(function.Bank, function.Name).Functions.Add(function);
        }

        foreach (IRGlobal global in module.Globals.Where(g => !g.Bank.IsResident))
        {
            Group(global.Bank, global.Name).Globals.Add(global);
        }

        return
        [
            .. groups
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new BankGroup(g.Value.Bank, g.Key, g.Value.Functions, g.Value.Globals)),
        ];

        (IRBank Bank, List<IRFunction> Functions, List<IRGlobal> Globals) Group(IRBank bank, string symbolName)
        {
            string name = bank.Kind == IRBankKind.Fixed
                ? $"game_bank{bank.Number}.c"
                : $"game_auto_{OwnerOf(symbolName)}.c";

            if (!groups.TryGetValue(name, out var group))
            {
                group = (bank, [], []);
                groups[name] = group;
            }

            return group;
        }
    }

    /// <summary>
    /// The declaring type's mangled prefix, from a mangled symbol name.
    /// </summary>
    /// <remarks>
    /// Names are <c>Type_Member</c>, so everything before the last underscore is
    /// the type. A name with no underscore is its own owner.
    /// </remarks>
    private static string OwnerOf(string symbolName)
    {
        int lastUnderscore = symbolName.LastIndexOf('_');
        return lastUnderscore <= 0 ? symbolName : symbolName[..lastUnderscore];
    }

    private string EmitBankFile(IRModule module, BankGroup group)
    {
        Reset(group.FileName);

        string where = group.Bank.Kind == IRBankKind.Fixed
            ? $"ROM bank {group.Bank.Number}"
            : "a ROM bank chosen at link time";

        WriteFileComment(module, $"the code and data in {where}");

        // First line of the file on purpose. A developer opening this should not
        // have to consult the build report to learn where it lands, and the file
        // still builds by hand if they copy it out.
        Line($"#pragma bank {(group.Bank.Kind == IRBankKind.Fixed ? group.Bank.Number : AutoBankSentinel)}");
        Blank();
        Line($"#include \"{HeaderFileName}\"");
        Blank();

        if (group.Globals.Count > 0)
        {
            WriteSectionComment("Static data");

            foreach (IRGlobal global in group.Globals)
            {
                EmitGlobal(global);

                // BANKREF defines the companion symbol BANK(x) reads, which is
                // how a loader in another bank is told where this data lives.
                Line($"BANKREF({global.Name})");
            }

            Blank();
        }

        foreach (IRFunction function in group.Functions)
        {
            EmitFunction(function);
        }

        return _output.ToString();
    }

    private string EmitHeaderFile(IRModule module, IReadOnlyList<IRStruct> structs, HashSet<string> zeroed)
    {
        Reset(HeaderFileName);

        WriteFileComment(module, "the declarations every generated file shares");
        Line("#ifndef GBS_GAME_H");
        Line("#define GBS_GAME_H");
        Blank();
        Line("#include <gb/gb.h>");
        Line("#include <stdint.h>");
        Line("#include \"gbs_runtime.h\"");

        foreach (string include in userIncludes ?? [])
        {
            Line($"#include \"{include}\"");
        }

        Blank();

        if (structs.Count > 0)
        {
            WriteSectionComment("Structs");

            foreach (IRStruct declaration in structs)
            {
                EmitStruct(declaration);
            }

            foreach (IRStruct declaration in structs.Where(s => zeroed.Contains(s.Name)))
            {
                Line($"extern const {declaration.Name} {ZeroName(declaration.Name)};");
            }

            Blank();
        }

        if (module.Globals.Count > 0)
        {
            WriteSectionComment("Static data");

            foreach (IRGlobal global in module.Globals)
            {
                Line($"extern {Qualifier(global)}{Declare(global.Type, global.Name)};");

                // Declares the companion symbol BANK(x) reads. Every unit sees
                // it, because any of them may need to name this data's bank.
                if (!global.Bank.IsResident)
                {
                    Line($"BANKREF_EXTERN({global.Name})");
                }
            }

            Blank();
        }

        WriteSectionComment("Functions");

        foreach (IRFunction function in module.Functions)
        {
            Line($"{Signature(function)};");
        }

        Blank();
        Line("#endif");

        return _output.ToString();
    }

    private string EmitProgramFile(IRModule module, IReadOnlyList<IRStruct> structs, HashSet<string> zeroed)
    {
        Reset(ProgramFileName);

        WriteFileComment(module, "the program");
        Line($"#include \"{HeaderFileName}\"");
        Blank();

        // C cannot assign a scalar zero to a struct, so a struct that is
        // zero-initialised anywhere gets one read-only zero instance in ROM to
        // copy from. 'new Enemy()' then costs exactly one visible struct copy,
        // and structs never zeroed cost nothing at all. These are defined once
        // here rather than 'static' per unit, which would duplicate the bytes.
        if (zeroed.Count > 0)
        {
            WriteSectionComment("Zero instances");

            var byName = structs.ToDictionary(s => s.Name, StringComparer.Ordinal);

            foreach (IRStruct declaration in structs.Where(s => zeroed.Contains(s.Name)))
            {
                Line($"const {declaration.Name} {ZeroName(declaration.Name)} = " +
                     $"{ZeroInitializer(declaration.Name, byName)};");
            }

            Blank();
        }

        // Banked declarations are defined in their own translation unit, so this
        // file carries only what stays mapped: main, the resident code, every
        // WRAM global, and the shared zero instances.
        IRGlobal[] resident = [.. module.Globals.Where(g => g.Bank.IsResident)];

        if (resident.Length > 0)
        {
            WriteSectionComment("Static data");

            foreach (IRGlobal global in resident)
            {
                EmitGlobal(global);
            }

            Blank();
        }

        foreach (IRFunction function in module.Functions.Where(f => f.Bank.IsResident))
        {
            EmitFunction(function);
        }

        WriteSectionComment("Entry point");
        Line("void main(void)");
        Line("{");
        _indent++;
        Line($"{module.EntryPoint.Name}();");
        _indent--;
        Line("}");

        return _output.ToString();
    }

    private void Reset(string fileName)
    {
        _output.Clear();
        _indent = 0;
        _lineNumber = 1;
        _currentFile = fileName;
    }

    private static string Qualifier(IRGlobal global) => global.IsReadOnly ? "const " : string.Empty;

    /// <summary>
    /// Where in the developer's C# this came from, or nothing if it is synthesised.
    /// </summary>
    /// <remarks>
    /// Generated helpers (the fixed-collection operations, the zero instances)
    /// have no source position, and saying so would be worse than saying nothing.
    /// </remarks>
    private static string Origin(SourceSpan span) =>
        span.IsNone ? string.Empty : $" - {Path.GetFileName(span.FilePath)}({span.Line})";

    /// <summary>
    /// Writes a statement's line, and (only under <c>--annotate-source</c> and
    /// only when the statement carries a real span) a trailing comment naming
    /// where it came from, reusing <see cref="Origin"/> so the format matches
    /// the one function and global comments already use.
    /// </summary>
    /// <remarks>
    /// With the flag off this is exactly <see cref="Line"/>, so default output
    /// does not change by a single byte. This is also where
    /// <see cref="SourceMap"/> gets its entries, one per annotated line, so the
    /// comment in the C and the row in <c>sourcemap.json</c> can never disagree
    /// about which line they mean.
    /// </remarks>
    private void AnnotatedLine(string text, SourceSpan span)
    {
        if (!annotateSource || span.IsNone)
        {
            Line(text);
            return;
        }

        _sourceMap.Add(new SourceMapEntry(span.FilePath, span.Line, _currentFile, _lineNumber));
        Line($"{text}   /*{Origin(span)} */");
    }

    private void WriteFileComment(IRModule module, string what)
    {
        Line("/*");
        Line($" * Generated by GB# from '{module.Name}': {what}.");
        Line(" *");
        Line(" * This file is written to be read: it is the answer to \"what does my");
        Line(" * C# actually compile to?\". Editing it has no effect, because it is");
        Line(" * regenerated on every build.");
        Line(" */");
        Blank();
    }

    private void WriteSectionComment(string title)
    {
        Line($"/* --- {title} {new string('-', Math.Max(0, 60 - title.Length))} */");
        Blank();
    }

    /// <summary>
    /// Orders structs so each is declared after the ones it contains.
    /// </summary>
    /// <remarks>
    /// C needs a complete type before it can embed it by value, and a
    /// specialised <c>FixedList&lt;Enemy&gt;</c> embeds <c>Enemy</c>. Declaration
    /// order in the IR follows the source, which carries no such requirement, so
    /// the emitter imposes one here. Cycles are impossible in valid C#, but a
    /// visiting set keeps a malformed module from recursing forever.
    /// </remarks>
    private static IReadOnlyList<IRStruct> SortByDependency(IReadOnlyList<IRStruct> structs)
    {
        var byName = structs.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var ordered = new List<IRStruct>(structs.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (IRStruct declaration in structs)
        {
            Visit(declaration);
        }

        return ordered;

        void Visit(IRStruct declaration)
        {
            if (!emitted.Add(declaration.Name) || !visiting.Add(declaration.Name))
            {
                return;
            }

            foreach (IRField field in declaration.Fields)
            {
                if (DependencyName(field.Type) is { } dependency &&
                    byName.TryGetValue(dependency, out IRStruct? nested))
                {
                    Visit(nested);
                }
            }

            visiting.Remove(declaration.Name);
            ordered.Add(declaration);
        }

        static string? DependencyName(IRType type) => type switch
        {
            IRStructType structType => structType.Name,
            IRArrayType array => DependencyName(array.ElementType),

            // A pointer only needs the tag, not the full definition.
            _ => null,
        };
    }

    private void EmitStruct(IRStruct declaration)
    {
        Line($"typedef struct {declaration.Name} {{");
        _indent++;
        foreach (IRField field in declaration.Fields)
        {
            Line($"{Declare(field.Type, field.Name)};");
        }

        _indent--;
        Line($"}} {declaration.Name};   /* {declaration.SizeInBytes} bytes */");
        Blank();
    }

    private void EmitGlobal(IRGlobal global)
    {
        // 'const' is what puts the bytes in the cartridge rather than in work
        // RAM: SDCC places const-qualified data in a CODE area.
        string qualifier = Qualifier(global);
        string declaration = Declare(global.Type, global.Name);
        string initializer = global.Initializer is null ? string.Empty : $" = {Expression(global.Initializer)}";
        string where = global.IsReadOnly
            ? global.Bank.IsResident ? "ROM" : $"ROM bank {global.Bank}"
            : "WRAM";
        Line($"{qualifier}{declaration}{initializer};   /* {global.Type.SizeInBytes} bytes, {where}{Origin(global.Span)} */");
    }

    private void EmitFunction(IRFunction function)
    {
        if (function.SourceName is not null)
        {
            // Naming the C# file and line costs one comment and answers "which
            // of my methods is this?" without a source map or a debugger. Comments
            // rather than #line directives: SDCC would carry those into its debug
            // output pointing at a file it cannot read, and they would wreck the
            // readability this emitter exists to preserve.
            Line($"/* {function.SourceName}{Origin(function.Span)} */");
        }

        Line(Signature(function));
        EmitBlock(function.Body, declareLocals: function.Locals);
        Blank();
    }

    /// <summary>
    /// The C signature, including the banking qualifier.
    /// </summary>
    /// <remarks>
    /// The qualifier belongs here rather than at the two call sites because this
    /// builds both the prototype in the header and the definition in the bank
    /// file. SDCC accepts a prototype and a definition that disagree about
    /// <c>BANKED</c> and generates a call that returns into the wrong bank, so
    /// the two are made incapable of disagreeing rather than checked.
    /// </remarks>
    private static string Signature(IRFunction function)
    {
        string parameters = function.Parameters.Count == 0
            ? "void"
            : string.Join(", ", function.Parameters.Select(p => Declare(p.Type, p.Name)));

        // BANKED and NONBANKED are GBDK's portable spellings, reachable from the
        // generated C through <gb/gb.h>. Resident functions say nothing at all,
        // which is what keeps an unbanked program's output byte-identical.
        string qualifier = function.Bank.IsResident ? string.Empty : " BANKED";

        return $"{TypeName(function.ReturnType)} {function.Name}({parameters}){qualifier}";
    }

    // -----------------------------------------------------------------------
    // Statements
    // -----------------------------------------------------------------------

    private void EmitBlock(IRBlock block, IReadOnlyList<IRLocal>? declareLocals = null)
    {
        Line("{");
        _indent++;

        // Locals are declared at the top of the function rather than at first
        // use, so the frame's size is visible in one place.
        if (declareLocals is { Count: > 0 })
        {
            foreach (IRLocal local in declareLocals)
            {
                Line($"{Declare(local.Type, local.Name)};");
            }

            Blank();
        }

        foreach (IRStatement statement in block.Statements)
        {
            EmitStatement(statement);
        }

        _indent--;
        Line("}");
    }

    private void EmitStatement(IRStatement statement)
    {
        switch (statement)
        {
            case IRBlock block:
                EmitBlock(block);
                break;

            case IRLocalDeclaration declaration:
                // The declaration itself was hoisted; only the assignment remains.
                if (declaration.Initializer is not null)
                {
                    AnnotatedLine(
                        $"{declaration.Local.Name} = {Expression(declaration.Initializer)};",
                        declaration.Span);
                }

                break;

            case IRAssign assign:
                AnnotatedLine($"{Expression(assign.Target)} = {Expression(assign.Value)};", assign.Span);
                break;

            case IRCompoundAssign compound:
                AnnotatedLine(
                    $"{Expression(compound.Target)} {BinaryOperator(compound.Operator)}= {Expression(compound.Value)};",
                    compound.Span);
                break;

            case IRExpressionStatement expression:
                AnnotatedLine($"{Expression(expression.Expression)};", expression.Span);
                break;

            case IRIf ifStatement:
                AnnotatedLine($"if ({Expression(ifStatement.Condition)})", ifStatement.Span);
                EmitNested(ifStatement.Then);
                if (ifStatement.Else is not null)
                {
                    Line("else");
                    EmitNested(ifStatement.Else);
                }

                break;

            case IRWhile loop:
                AnnotatedLine($"while ({Expression(loop.Condition)})", loop.Span);
                EmitNested(loop.Body);
                break;

            case IRDoWhile loop:
                AnnotatedLine("do", loop.Span);
                EmitNested(loop.Body);
                Line($"while ({Expression(loop.Condition)});");
                break;

            case IRFor loop:
            {
                string initializers = string.Join(", ", loop.Initializers.Select(InlineStatement));
                string condition = loop.Condition is null ? string.Empty : Expression(loop.Condition);
                string updates = string.Join(", ", loop.Updates.Select(InlineStatement));
                AnnotatedLine($"for ({initializers}; {condition}; {updates})", loop.Span);
                EmitNested(loop.Body);
                break;
            }

            case IRSwitch switchStatement:
                EmitSwitch(switchStatement);
                break;

            case IRBreak breakStatement:
                AnnotatedLine("break;", breakStatement.Span);
                break;

            case IRContinue continueStatement:
                AnnotatedLine("continue;", continueStatement.Span);
                break;

            case IRReturn returnStatement:
                AnnotatedLine(
                    returnStatement.Value is null
                        ? "return;"
                        : $"return {Expression(returnStatement.Value)};",
                    returnStatement.Span);
                break;

            default:
                Line($"/* unhandled statement: {statement.GetType().Name} */");
                break;
        }
    }

    private void EmitSwitch(IRSwitch switchStatement)
    {
        AnnotatedLine($"switch ({Expression(switchStatement.Value)})", switchStatement.Span);
        Line("{");
        _indent++;

        foreach (IRSwitchSection section in switchStatement.Sections)
        {
            foreach (IRExpression value in section.Values)
            {
                Line($"case {Expression(value)}:");
            }

            _indent++;
            EmitStatementsOf(section.Body);
            _indent--;
        }

        if (switchStatement.Default is not null)
        {
            Line("default:");
            _indent++;
            EmitStatementsOf(switchStatement.Default);
            _indent--;
        }

        _indent--;
        Line("}");
    }

    /// <summary>Emits a body without its own braces, for switch sections.</summary>
    private void EmitStatementsOf(IRStatement statement)
    {
        if (statement is IRBlock block)
        {
            foreach (IRStatement child in block.Statements)
            {
                EmitStatement(child);
            }

            return;
        }

        EmitStatement(statement);
    }

    private void EmitNested(IRStatement statement)
    {
        if (statement is IRBlock block)
        {
            EmitBlock(block);
            return;
        }

        _indent++;
        EmitStatement(statement);
        _indent--;
    }

    private string InlineStatement(IRStatement statement) => statement switch
    {
        IRLocalDeclaration { Initializer: not null } d => $"{d.Local.Name} = {Expression(d.Initializer)}",
        IRLocalDeclaration d => d.Local.Name,
        IRAssign a => $"{Expression(a.Target)} = {Expression(a.Value)}",
        IRCompoundAssign c => $"{Expression(c.Target)} {BinaryOperator(c.Operator)}= {Expression(c.Value)}",
        IRExpressionStatement e => Expression(e.Expression),
        _ => string.Empty,
    };

    // -----------------------------------------------------------------------
    // Expressions
    // -----------------------------------------------------------------------

    private string Expression(IRExpression expression) => expression switch
    {
        IRUnit => string.Empty,
        IRDefaultValue { Type: IRPrimitiveType } => "0",
        IRDefaultValue { Type: IRPointerType } => "0",
        IRDefaultValue { Type: IRStructType structType } => ZeroName(structType.Name),
        IRDefaultValue => "{ 0 }",
        IRConstant constant => Constant(constant),
        IRAggregate aggregate => Aggregate(aggregate),
        IRDataBlob blob => DataBlob(blob),
        IRLocalRef local => local.Local.Name,
        IRParameterRef parameter => parameter.Parameter.Name,
        IRGlobalRef global => global.Global.Name,

        // A dereferenced pointer parameter is the 'self' of a struct method.
        IRDereference { Operand: IRParameterRef p } => $"(*{p.Parameter.Name})",
        IRDereference dereference => $"(*{Expression(dereference.Operand)})",

        IRFieldAccess field => FieldAccess(field),
        IRElementAccess element => $"{Expression(element.Target)}[{Expression(element.Index)}]",
        IRBinary binary => $"({Expression(binary.Left)} {BinaryOperator(binary.Operator)} {Expression(binary.Right)})",
        IRUnary unary => $"({UnaryOperator(unary.Operator)}{Expression(unary.Operand)})",
        IRIncrement increment => $"{Expression(increment.Target)}{(increment.IsDecrement ? "--" : "++")}",
        IRConditional conditional =>
            $"({Expression(conditional.Condition)} ? {Expression(conditional.WhenTrue)} : {Expression(conditional.WhenFalse)})",
        IRCall call => $"{call.FunctionName}({Arguments(call.Arguments)})",
        IRNativeCall native => $"{native.Symbol}({Arguments(native.Arguments)})",
        IRConvert convert => $"(({TypeName(convert.Type)}){Expression(convert.Operand)})",
        IRAddressOf address => $"(&{Expression(address.Operand)})",
        _ => $"/* unhandled expression: {expression.GetType().Name} */ 0",
    };

    /// <summary>
    /// Field access through the implicit <c>self</c> pointer uses <c>-&gt;</c>,
    /// which is what a C developer expects to read.
    /// </summary>
    private string FieldAccess(IRFieldAccess field) => field.Target switch
    {
        IRDereference { Operand: IRParameterRef p } => $"{p.Parameter.Name}->{field.FieldName}",
        _ => $"{Expression(field.Target)}.{field.FieldName}",
    };

    private static string ZeroName(string structName) => $"{structName}__zero";

    /// <summary>
    /// Builds a zero initializer whose braces match the type's shape.
    /// </summary>
    /// <remarks>
    /// SDCC will not accept a flat <c>{ 0 }</c> for an aggregate containing an
    /// aggregate, so the nesting is generated explicitly. One element is enough
    /// per array: C zero-fills the remainder.
    /// </remarks>
    private static string ZeroInitializer(string structName, IReadOnlyDictionary<string, IRStruct> structs)
    {
        if (!structs.TryGetValue(structName, out IRStruct? declaration) || declaration.Fields.Count == 0)
        {
            return "{ 0 }";
        }

        return "{ " + string.Join(", ", declaration.Fields.Select(f => ZeroFor(f.Type, structs))) + " }";
    }

    private static string ZeroFor(IRType type, IReadOnlyDictionary<string, IRStruct> structs) => type switch
    {
        IRStructType structType => ZeroInitializer(structType.Name, structs),
        IRArrayType array => "{ " + ZeroFor(array.ElementType, structs) + " }",
        _ => "0",
    };

    /// <summary>
    /// Finds every struct that is zero-initialised somewhere in the module, so
    /// only those pay for a zero instance.
    /// </summary>
    private static HashSet<string> CollectZeroInitializedStructs(IRModule module)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (IRGlobal global in module.Globals)
        {
            VisitExpression(global.Initializer);
        }

        foreach (IRFunction function in module.Functions)
        {
            VisitStatement(function.Body);
        }

        return found;

        void VisitStatement(IRStatement? statement)
        {
            switch (statement)
            {
                case null:
                    break;
                case IRBlock block:
                    foreach (IRStatement child in block.Statements)
                    {
                        VisitStatement(child);
                    }

                    break;
                case IRLocalDeclaration declaration:
                    VisitExpression(declaration.Initializer);
                    break;
                case IRAssign assign:
                    VisitExpression(assign.Target);
                    VisitExpression(assign.Value);
                    break;
                case IRCompoundAssign compound:
                    VisitExpression(compound.Target);
                    VisitExpression(compound.Value);
                    break;
                case IRExpressionStatement expression:
                    VisitExpression(expression.Expression);
                    break;
                case IRIf ifStatement:
                    VisitExpression(ifStatement.Condition);
                    VisitStatement(ifStatement.Then);
                    VisitStatement(ifStatement.Else);
                    break;
                case IRWhile loop:
                    VisitExpression(loop.Condition);
                    VisitStatement(loop.Body);
                    break;
                case IRDoWhile loop:
                    VisitStatement(loop.Body);
                    VisitExpression(loop.Condition);
                    break;
                case IRFor loop:
                    foreach (IRStatement initializer in loop.Initializers)
                    {
                        VisitStatement(initializer);
                    }

                    VisitExpression(loop.Condition);
                    foreach (IRStatement update in loop.Updates)
                    {
                        VisitStatement(update);
                    }

                    VisitStatement(loop.Body);
                    break;
                case IRSwitch switchStatement:
                    VisitExpression(switchStatement.Value);
                    foreach (IRSwitchSection section in switchStatement.Sections)
                    {
                        VisitStatement(section.Body);
                    }

                    VisitStatement(switchStatement.Default);
                    break;
                case IRReturn returnStatement:
                    VisitExpression(returnStatement.Value);
                    break;
            }
        }

        void VisitExpression(IRExpression? expression)
        {
            switch (expression)
            {
                case null:
                    break;
                case IRDefaultValue { Type: IRStructType structType }:
                    found.Add(structType.Name);
                    break;
                case IRFieldAccess field:
                    VisitExpression(field.Target);
                    break;
                case IRElementAccess element:
                    VisitExpression(element.Target);
                    VisitExpression(element.Index);
                    break;
                case IRBinary binary:
                    VisitExpression(binary.Left);
                    VisitExpression(binary.Right);
                    break;
                case IRUnary unary:
                    VisitExpression(unary.Operand);
                    break;
                case IRIncrement increment:
                    VisitExpression(increment.Target);
                    break;
                case IRConditional conditional:
                    VisitExpression(conditional.Condition);
                    VisitExpression(conditional.WhenTrue);
                    VisitExpression(conditional.WhenFalse);
                    break;
                case IRCall call:
                    foreach (IRExpression argument in call.Arguments)
                    {
                        VisitExpression(argument);
                    }

                    break;
                case IRNativeCall native:
                    foreach (IRExpression argument in native.Arguments)
                    {
                        VisitExpression(argument);
                    }

                    break;
                case IRConvert convert:
                    VisitExpression(convert.Operand);
                    break;
                case IRAddressOf address:
                    VisitExpression(address.Operand);
                    break;
                case IRDereference dereference:
                    VisitExpression(dereference.Operand);
                    break;
            }
        }
    }

    private string Arguments(IEnumerable<IRExpression> arguments) =>
        string.Join(", ", arguments.Where(a => a is not IRUnit).Select(Expression));

    /// <summary>
    /// A braced initializer list.
    /// </summary>
    /// <remarks>
    /// Long lists wrap, because tile and map data is the main thing that ends up
    /// here and a tileset on one line would be several thousand characters wide.
    /// The row width is chosen so the wrapped output stays under 80 columns.
    /// </remarks>
    private string Aggregate(IRAggregate aggregate)
    {
        string[] values = [.. aggregate.Elements.Select(Expression)];

        if (values.Length <= InlineAggregateElements)
        {
            return $"{{ {string.Join(", ", values)} }}";
        }

        int perLine = aggregate.Type is IRArrayType { ElementType.SizeInBytes: >= 2 } ? 8 : 16;
        string rowIndent = new(' ', (_indent + 1) * 4);

        var builder = new StringBuilder("{\n");

        for (int i = 0; i < values.Length; i += perLine)
        {
            builder.Append(rowIndent);
            builder.AppendJoin(", ", values.Skip(i).Take(perLine));
            builder.Append(i + perLine < values.Length ? ",\n" : "\n");
        }

        return builder.Append(' ', _indent * 4).Append('}').ToString();
    }

    /// <summary>
    /// Bulk data from the asset pipeline, in hex.
    /// </summary>
    /// <remarks>
    /// Hex rather than decimal because this is tile and map data: a reader
    /// comparing it against an image or a tile viewer is reading bit patterns,
    /// and 0x3C means something that 60 does not.
    /// </remarks>
    private string DataBlob(IRDataBlob blob)
    {
        ReadOnlySpan<byte> bytes = blob.Bytes.Span;
        int perLine = blob.ElementWidth == 2 ? 8 : 16;
        string rowIndent = new(' ', (_indent + 1) * 4);

        var builder = new StringBuilder("{\n");

        for (int i = 0; i < blob.ElementCount; i++)
        {
            if (i % perLine == 0)
            {
                builder.Append(rowIndent);
            }

            int value = blob.ElementWidth == 2
                ? bytes[i * 2] | (bytes[(i * 2) + 1] << 8)
                : bytes[i];

            builder.Append("0x").Append(value.ToString(blob.ElementWidth == 2 ? "X4" : "X2", CultureInfo.InvariantCulture));

            if (i < blob.ElementCount - 1)
            {
                builder.Append(',');
            }

            builder.Append((i + 1) % perLine == 0 || i == blob.ElementCount - 1 ? "\n" : " ");
        }

        return builder.Append(' ', _indent * 4).Append('}').ToString();
    }

    private static string Constant(IRConstant constant) => constant.Value switch
    {
        bool b => b ? "1" : "0",
        byte b => b.ToString(CultureInfo.InvariantCulture) + "U",
        ushort u => u.ToString(CultureInfo.InvariantCulture) + "U",
        uint u => u.ToString(CultureInfo.InvariantCulture) + "UL",
        sbyte s => s.ToString(CultureInfo.InvariantCulture),
        short s => s.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => "0",
    };

    // -----------------------------------------------------------------------
    // Types
    // -----------------------------------------------------------------------

    /// <summary>
    /// Declares <paramref name="name"/> with <paramref name="type"/>, handling
    /// C's inside-out array and pointer syntax.
    /// </summary>
    private static string Declare(IRType type, string name) => type switch
    {
        // A zero length means no declaration site fixed one, which C cannot
        // express as an array. Lowering turns array parameters into pointers, so
        // this is a backstop against a malformed module producing invalid C
        // rather than a shape that is expected to arrive here.
        IRArrayType { Length: 0 } array => $"{TypeName(array.ElementType)}* {name}",
        IRArrayType array => $"{TypeName(array.ElementType)} {name}[{array.Length}]",
        _ => $"{TypeName(type)} {name}",
    };

    private static string TypeName(IRType type) => type switch
    {
        IRPrimitiveType primitive => primitive.Kind switch
        {
            IRPrimitiveKind.Void => "void",

            // GB# bools are byte-sized on the target; uint8_t keeps that explicit.
            IRPrimitiveKind.Bool => "uint8_t",
            IRPrimitiveKind.U8 => "uint8_t",
            IRPrimitiveKind.I8 => "int8_t",
            IRPrimitiveKind.U16 => "uint16_t",
            IRPrimitiveKind.I16 => "int16_t",
            IRPrimitiveKind.U32 => "uint32_t",
            _ => "int32_t",
        },
        IRStructType structType => structType.Name,
        IRPointerType pointer => $"{TypeName(pointer.PointeeType)}*",
        IRArrayType array => $"{TypeName(array.ElementType)}*",
        _ => "uint8_t",
    };

    private static string BinaryOperator(IRBinaryOperator op) => op switch
    {
        IRBinaryOperator.Add => "+",
        IRBinaryOperator.Subtract => "-",
        IRBinaryOperator.Multiply => "*",
        IRBinaryOperator.Divide => "/",
        IRBinaryOperator.Remainder => "%",
        IRBinaryOperator.BitwiseAnd => "&",
        IRBinaryOperator.BitwiseOr => "|",
        IRBinaryOperator.BitwiseXor => "^",
        IRBinaryOperator.ShiftLeft => "<<",
        IRBinaryOperator.ShiftRight => ">>",
        IRBinaryOperator.Equal => "==",
        IRBinaryOperator.NotEqual => "!=",
        IRBinaryOperator.LessThan => "<",
        IRBinaryOperator.LessThanOrEqual => "<=",
        IRBinaryOperator.GreaterThan => ">",
        IRBinaryOperator.GreaterThanOrEqual => ">=",
        IRBinaryOperator.LogicalAnd => "&&",
        IRBinaryOperator.LogicalOr => "||",
        _ => "+",
    };

    private static string UnaryOperator(IRUnaryOperator op) => op switch
    {
        IRUnaryOperator.Negate => "-",
        IRUnaryOperator.LogicalNot => "!",
        IRUnaryOperator.BitwiseNot => "~",
        _ => string.Empty,
    };

    // -----------------------------------------------------------------------
    // Output
    // -----------------------------------------------------------------------

    private void Line(string text)
    {
        if (text.Length > 0)
        {
            _output.Append(' ', _indent * 4);
            _output.Append(text);
        }

        _output.Append('\n');
        _lineNumber++;
    }

    private void Blank()
    {
        _output.Append('\n');
        _lineNumber++;
    }
}
