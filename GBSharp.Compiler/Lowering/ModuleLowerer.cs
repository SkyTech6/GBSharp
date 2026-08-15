using System.Collections.Immutable;
using System.Globalization;
using GBSharp.Compiler.Analysis;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.Frontend;
using GBSharp.Compiler.IR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace GBSharp.Compiler.Lowering;

/// <summary>
/// Walks a compilation and produces one <see cref="IRModule"/>.
/// </summary>
/// <remarks>
/// Runs in passes so that names resolve before bodies are lowered: struct
/// layouts first (a body may need a struct's size), then globals, then the set
/// of function names, then the bodies themselves.
/// </remarks>
public sealed class ModuleLowerer(
    CSharpCompilation compilation,
    DiagnosticBag diagnostics,
    CompilationRequest? request = null)
{
    private readonly TypeMapper _types = new(diagnostics);
    private readonly FrameworkSymbols _framework = FrameworkSymbols.Resolve(compilation);
    private readonly Dictionary<ISymbol, IRGlobal> _globals = new(SymbolEqualityComparer.Default);

    private FixedCollections? _collectionsCache;
    private AssetBindings? _assetsCache;

    // Both need the resolved framework symbols, which a field initializer cannot
    // see. Resolving a second set for each would let two collaborators disagree
    // about what the framework is, so they share the one field instead.
    private FixedCollections _collections =>
        _collectionsCache ??= new FixedCollections(_framework, diagnostics);

    private AssetBindings _assets =>
        _assetsCache ??= new AssetBindings(
            _framework,
            request?.AssetCompiler ?? Assets.NullAssetCompiler.Instance,
            request?.AssetSearchPaths ?? [],
            request?.AssetProfile ?? Assets.AssetTargetProfile.GameBoy,
            diagnostics);

    public IRModule? Lower()
    {
        _types.Collections = _collections;

        List<INamedTypeSymbol> declaredTypes = CollectDeclaredTypes().ToList();

        foreach (INamedTypeSymbol type in declaredTypes.Where(t => t.TypeKind == TypeKind.Struct))
        {
            _types.DeclareStruct(type, type.Locations.FirstOrDefault());
        }

        var globals = new List<IRGlobal>();
        foreach (INamedTypeSymbol type in declaredTypes)
        {
            CollectGlobals(type, globals);
        }

        List<IMethodSymbol> methods = declaredTypes
            .SelectMany(CollectLowerableMethods)
            .ToList();

        IMethodSymbol? entryPointSymbol = ResolveEntryPoint(methods);
        if (entryPointSymbol is null)
        {
            return null;
        }

        var knownFunctions = methods
            .Select(NameMangler.ForMethod)
            .ToHashSet(StringComparer.Ordinal);

        var functions = new List<IRFunction>();
        IRFunction? entryPoint = null;

        // Banks are resolved for every method before any body is lowered, so a
        // call can be told what it costs to reach its target without the
        // lowerer depending on the order the methods happen to be visited in.
        var functionBanks = new Dictionary<string, IRBank>(StringComparer.Ordinal);
        foreach (IMethodSymbol method in methods)
        {
            functionBanks[NameMangler.ForMethod(method)] = ResolveCodeBank(method, entryPointSymbol);
        }

        foreach (IMethodSymbol method in methods)
        {
            IRFunction? function = LowerMethod(method, knownFunctions, functionBanks);
            if (function is null)
            {
                continue;
            }

            function = function with { Bank = functionBanks[NameMangler.ForMethod(method)] };

            functions.Add(function);

            if (SymbolEqualityComparer.Default.Equals(method, entryPointSymbol))
            {
                entryPoint = function;
            }
        }

        if (entryPoint is null)
        {
            return null;
        }

        // Specialised collection structs must precede user structs that contain
        // them, and their generated operations join the module's functions.
        var structs = _collections.Structs.Concat(_types.Structs).ToList();
        functions.InsertRange(0, _collections.Helpers);

        // Asset data leads, so the ROM tables read before the code that uses them.
        globals.InsertRange(0, _assets.Globals);

        ReportBankPlacement(globals, functions);
        ReportVramPressure();

        var module = new IRModule(
            compilation.AssemblyName ?? "Game",
            structs,
            globals,
            functions,
            entryPoint)
        {
            Assets = _assets.Assets,
            Budgets = BudgetSymbols.Resolve(compilation),
        };

        // Before anything reads the module for real: a call that disagrees with
        // its callee is a GB# bug, and catching it here is what stops it
        // reaching the developer as an SDCC error about generated code.
        IRVerifier.Verify(module, diagnostics);

        // Last, and over the finished module rather than over the lists above:
        // the analysis walks a call graph, and the generated collection
        // operations have to already be among the functions or it silently misses
        // them. Nothing it reports needs a toolchain, which is what lets
        // 'gbsharp analyze' produce the same notes a full build does.
        return module with { Costs = ModuleAnalysis.Analyse(module, diagnostics) };
    }

    private IRFunction? LowerMethod(
        IMethodSymbol method,
        IReadOnlySet<string> knownFunctions,
        IReadOnlyDictionary<string, IRBank> functionBanks)
    {
        SyntaxReference? reference = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
        {
            return null;
        }

        SyntaxNode node = reference.GetSyntax();
        SemanticModel model = compilation.GetSemanticModel(node.SyntaxTree);

        IOperation? body = model.GetOperation(node);
        if (body is null)
        {
            diagnostics.Report(
                GBDiagnostics.InternalError,
                method.Locations.FirstOrDefault(),
                $"no operation tree for '{method.Name}'");
            return null;
        }

        var lowerer = new FunctionLowerer(
            _types, _framework, _collections, diagnostics, _globals, knownFunctions, _assets, functionBanks);
        return lowerer.Lower(method, body);
    }

    /// <summary>Every named type declared in this compilation's own source.</summary>
    private IEnumerable<INamedTypeSymbol> CollectDeclaredTypes()
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(compilation.Assembly.GlobalNamespace);

        while (stack.Count > 0)
        {
            INamespaceOrTypeSymbol current = stack.Pop();

            foreach (ISymbol member in current.GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol ns:
                        stack.Push(ns);
                        break;

                    case INamedTypeSymbol type when type.DeclaringSyntaxReferences.Length > 0:
                        stack.Push(type);
                        yield return type;
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Static fields become C globals, which is the whole of a GB# program's
    /// WRAM footprint. Constants are excluded: they fold at compile time and
    /// cost nothing.
    /// </summary>
    private void CollectGlobals(INamedTypeSymbol type, List<IRGlobal> globals)
    {
        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsConst || field.IsImplicitlyDeclared)
            {
                continue;
            }

            Location? location = field.Locations.FirstOrDefault();

            // An asset field names data in ROM produced from an image. It is not
            // a WRAM global and must not be reported as one, so it never reaches
            // the allocation path below.
            if (_assets.TryCollect(field, location))
            {
                continue;
            }

            if (!field.IsStatic)
            {
                continue;
            }
            IRType? fieldType = _types.MapDeclaration(field.Type, field, location);
            if (fieldType is null)
            {
                continue;
            }

            IOperation? initializer = GetFieldInitializer(field);

            if (fieldType is IRArrayType arrayType)
            {
                int? length = TryGetArrayLength(initializer);
                if (length is null)
                {
                    diagnostics.Report(GBDiagnostics.UnsizedArray, location, field.Name);
                    continue;
                }

                fieldType = arrayType with { Length = length.Value };
            }

            IRExpression? initialValue = fieldType is IRArrayType array
                ? LowerArrayInitializer(field, array, initializer, location)
                : initializer?.ConstantValue is { HasValue: true, Value: { } constant }
                    ? new IRConstant(fieldType, IntegerWidth.Normalize(constant, fieldType))
                    : null;

            var global = new IRGlobal(
                NameMangler.ForGlobal(field),
                fieldType,
                initialValue,
                SourceSpan.FromLocation(location))
            {
                IsReadOnly = field.IsReadOnly,
                Bank = ResolveDataBank(field, location),
            };

            _globals[field] = global;
            globals.Add(global);

            // Read-only data is const in the generated C, so it stays in the
            // cartridge and never spends a byte of the 8 KB of work RAM. The two
            // costs are different enough that reporting them as one would make
            // the resource report useless.
            diagnostics.Report(
                global.IsReadOnly ? GBDiagnostics.RomAllocation : GBDiagnostics.StaticAllocation,
                location,
                field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                fieldType.SizeInBytes);
        }
    }

    /// <summary>
    /// The bank a data declaration lands in, rejecting the ones that cannot work.
    /// </summary>
    /// <remarks>
    /// Mutable statics are rejected outright rather than quietly kept resident:
    /// a developer who writes <c>[Bank(2)]</c> on a mutable array has a mental
    /// model worth correcting, and silently ignoring it would leave them
    /// believing they had freed work RAM they had not.
    /// </remarks>
    private IRBank ResolveDataBank(IFieldSymbol field, Location? location)
    {
        IRBank bank = BankResolver.Resolve(_framework, field);

        if (bank.IsResident)
        {
            return bank;
        }

        if (!field.IsReadOnly)
        {
            // Only complain when the field asked for a bank itself. Inheriting
            // one from a banked class is how a class holds both banked data and
            // ordinary state, and that should not be an error.
            if (BankResolver.Read(_framework, field) is not null)
            {
                diagnostics.Report(
                    GBDiagnostics.BankedMutableData,
                    location,
                    field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            }

            return IRBank.Resident;
        }

        return ValidBankOrResident(bank, field, location);
    }

    /// <summary>Bytes in one ROM bank.</summary>
    private const int BankSizeInBytes = 16 * 1024;

    /// <summary>
    /// Tiles the background and window share.
    /// </summary>
    /// <remarks>
    /// Sprites address their own 256 entries, so they are counted separately,
    /// which is why the flat per-asset limit of 255 was never the real budget.
    /// </remarks>
    private const int BackgroundTileRegion = 256;

    /// <summary>
    /// Sums the tiles every background asset needs, against what VRAM holds.
    /// </summary>
    /// <remarks>
    /// The per-asset tile check catches one image that is too detailed. This
    /// catches the more common problem: four images that are each fine and
    /// cannot coexist. GB# can do it because it sees the whole program at once,
    /// which a per-file asset tool cannot.
    /// <para>
    /// It is a resource note rather than an error until the region is genuinely
    /// over-subscribed, because loading assets in turn as levels start is a
    /// legitimate design, and a common one.
    /// </para>
    /// </remarks>
    private void ReportVramPressure()
    {
        IRAsset[] backgrounds =
        [
            .. _assets.Assets.Where(a => a.Stats.WidthTiles > 0 && a.Stats.UniqueTiles > 0),
        ];

        if (backgrounds.Length == 0)
        {
            return;
        }

        int tiles = backgrounds.Sum(a => a.Stats.UniqueTiles);

        // Only a single asset that cannot fit by itself is a hard error: the
        // sum across assets exceeding the region is legal when screens replace
        // each other at runtime, which is exactly what this check's own help
        // recommends. GB# cannot see load order, so the sum stays a note.
        IRAsset? oversized = backgrounds.FirstOrDefault(a => a.Stats.UniqueTiles > BackgroundTileRegion);

        if (oversized is not null)
        {
            diagnostics.Report(
                GBDiagnostics.VramBudgetExceeded,
                SourceSpan.None,
                oversized.Name,
                oversized.Stats.UniqueTiles,
                BackgroundTileRegion);
        }
        else if (backgrounds.Length > 1)
        {
            // One asset already has its own per-image budget; the sum only says
            // something new once there is more than one.
            diagnostics.Report(GBDiagnostics.VramBudget, SourceSpan.None, tiles, BackgroundTileRegion);
        }
    }

    /// <summary>
    /// Reports a bank whose declared data alone cannot fit.
    /// </summary>
    /// <remarks>
    /// Only data GB# placed is counted, so this catches the unambiguous case
    /// early, at a source location, rather than as a linker error naming an
    /// address. The linker still has the last word (code shares these banks),
    /// which is why the diagnostic says so rather than implying the remaining
    /// space is available.
    /// <para>
    /// Bank 0 is excluded: what it really holds includes the runtime and the
    /// interrupt vectors, so a declared-bytes total would be misleading. The
    /// build report covers it from the map instead.
    /// </para>
    /// </remarks>
    private void ReportBankPlacement(List<IRGlobal> globals, List<IRFunction> functions)
    {
        foreach (IGrouping<int, IRGlobal> bank in globals
                     .Where(g => g.Bank.Kind == IRBankKind.Fixed)
                     .GroupBy(g => g.Bank.Number))
        {
            int bytes = bank.Sum(g => g.Type.SizeInBytes);
            if (bytes <= BankSizeInBytes)
            {
                continue;
            }

            diagnostics.Report(
                GBDiagnostics.BankOverflow,
                bank.OrderByDescending(g => g.Type.SizeInBytes).First().Span,
                bank.Key,
                bytes);
        }

        // What went where, one line per bank. This is the "inspect" half of the
        // banking contract: a layout nobody can see is the thing thesis section
        // 15 says not to build. Bank 0 is left to the build report, which reads
        // it from the map and so knows about the runtime too.
        foreach (IGrouping<int, IRGlobal> bank in globals
                     .Where(g => g.Bank.Kind == IRBankKind.Fixed)
                     .GroupBy(g => g.Bank.Number)
                     .OrderBy(g => g.Key))
        {
            int symbols = bank.Count() + functions.Count(f => f.Bank == IRBank.Fixed(bank.Key));
            int bytes = bank.Sum(g => g.Type.SizeInBytes);

            diagnostics.Report(
                GBDiagnostics.BankPlacement,
                SourceSpan.None,
                bank.Key,
                symbols,
                symbols == 1 ? string.Empty : "s",
                bytes);
        }
    }

    /// <summary>
    /// The bank a method's code lands in.
    /// </summary>
    /// <remarks>
    /// The entry point is always resident: execution starts there, before any
    /// bank has been switched in. Asking for that directly is an error, but
    /// inheriting a bank from a containing class is not: that is how a class
    /// holds Main alongside code worth banking, and the rest of it still moves.
    /// </remarks>
    private IRBank ResolveCodeBank(IMethodSymbol method, IMethodSymbol? entryPoint)
    {
        IRBank bank = BankResolver.Resolve(_framework, method);
        Location? location = method.Locations.FirstOrDefault();

        if (SymbolEqualityComparer.Default.Equals(method, entryPoint))
        {
            if (BankResolver.Read(_framework, method) is { IsResident: false })
            {
                diagnostics.Report(
                    GBDiagnostics.EntryPointCannotBeBanked,
                    location,
                    method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            }

            return IRBank.Resident;
        }

        return ValidBankOrResident(bank, method, location);
    }

    /// <summary>Rejects a bank number outside the 1-255 a cartridge can map.</summary>
    private IRBank ValidBankOrResident(IRBank bank, ISymbol symbol, Location? location)
    {
        if (bank.Kind == IRBankKind.Fixed && bank.Number is < 1 or > 255)
        {
            diagnostics.Report(
                GBDiagnostics.InvalidBank,
                location,
                bank.Number,
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));

            return IRBank.Resident;
        }

        return bank;
    }

    /// <summary>
    /// Lowers <c>= { 1, 2, 3 }</c> into an aggregate the backend can print.
    /// </summary>
    /// <remarks>
    /// Without this every array global would emit as a bare declaration and the
    /// data would have to be written element by element at runtime, which is
    /// both slower and, for anything the size of a tileset, impossible. Elements
    /// must fold to constants: GB# reserves and fills this storage at compile
    /// time, so there is no point at which a runtime expression could run.
    /// </remarks>
    private IRExpression? LowerArrayInitializer(
        IFieldSymbol field,
        IRArrayType type,
        IOperation? initializer,
        Location? location)
    {
        if (ArrayElements(initializer) is not { Length: > 0 } values)
        {
            // 'new byte[4]' with no values. C zero-initializes globals already.
            return null;
        }

        var elements = new List<IRExpression>(values.Length);

        foreach (IOperation value in values)
        {
            if (value.ConstantValue is not { HasValue: true, Value: { } element })
            {
                diagnostics.Report(
                    GBDiagnostics.NonConstantInitializer,
                    value.Syntax.GetLocation(),
                    elements.Count,
                    field.Name);
                return null;
            }

            elements.Add(new IRConstant(type.ElementType, IntegerWidth.Normalize(element, type.ElementType)));
        }

        return new IRAggregate(type, elements);
    }

    private IOperation? GetFieldInitializer(IFieldSymbol field)
    {
        SyntaxReference? reference = field.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference?.GetSyntax() is not VariableDeclaratorSyntax { Initializer: { } equalsValue })
        {
            return null;
        }

        SemanticModel model = compilation.GetSemanticModel(equalsValue.SyntaxTree);
        return model.GetOperation(equalsValue.Value);
    }

    private static int? TryGetArrayLength(IOperation? initializer)
    {
        if (initializer is IArrayCreationOperation { DimensionSizes.Length: 1 } creation &&
            creation.DimensionSizes[0].ConstantValue is { HasValue: true, Value: { } size })
        {
            return Convert.ToInt32(size, CultureInfo.InvariantCulture);
        }

        return ArrayElements(initializer)?.Length;
    }

    /// <summary>
    /// The element values of an array initializer.
    /// </summary>
    /// <remarks>
    /// Handles both spellings, because tile and map data is nearly always
    /// written with the shorthand: <c>new byte[] { 1, 2 }</c> gives an array
    /// creation wrapping an initializer, while <c>= { 1, 2 }</c> gives a bare
    /// initializer with no creation around it at all.
    /// </remarks>
    private static ImmutableArray<IOperation>? ArrayElements(IOperation? initializer) => initializer switch
    {
        IArrayCreationOperation { Initializer: { } nested } => nested.ElementValues,
        IArrayInitializerOperation direct => direct.ElementValues,
        _ => null,
    };

    /// <summary>Methods that have a body GB# should lower.</summary>
    private IEnumerable<IMethodSymbol> CollectLowerableMethods(INamedTypeSymbol type)
    {
        foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.IsImplicitlyDeclared ||
                method.IsAbstract ||
                method.IsExtern ||
                method.DeclaringSyntaxReferences.Length == 0)
            {
                continue;
            }

            // Property accessors are lowered through their property, and
            // [Native] members have no body worth looking at.
            if (_framework.GetNativeSymbol(method) is not null || _framework.IsNativeIdentity(method))
            {
                continue;
            }

            // A struct constructor is lowered like any other instance member:
            // a function taking the struct by pointer. Nothing is inlined, so
            // what it costs stays visible at the call site.
            if (method.MethodKind is MethodKind.Constructor)
            {
                if (type.TypeKind is TypeKind.Struct && !ReportIfAmbiguousConstructor(type, method))
                {
                    yield return method;
                }

                continue;
            }

            if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet))
            {
                continue;
            }

            yield return method;
        }
    }

    /// <summary>
    /// Refuses a second constructor on the same struct, and says why.
    /// </summary>
    /// <remarks>
    /// <see cref="NameMangler"/> names a function after its type and member, so
    /// two constructors on one struct would both be <c>Point__ctor</c> and the
    /// second would silently win. Overloads are refused here rather than
    /// renamed, because a generated suffix is exactly the sort of name a
    /// developer cannot find again in a linker map (thesis section 3.3).
    /// </remarks>
    private bool ReportIfAmbiguousConstructor(INamedTypeSymbol type, IMethodSymbol constructor)
    {
        bool first = type.InstanceConstructors
            .Where(c => !c.IsImplicitlyDeclared)
            .Take(1)
            .Any(c => SymbolEqualityComparer.Default.Equals(c, constructor));

        if (first)
        {
            return false;
        }

        diagnostics.Report(
            GBDiagnostics.UnsupportedConstruct,
            constructor.Locations.FirstOrDefault(),
            $"More than one constructor on '{type.Name}'");
        return true;
    }

    /// <summary>
    /// Finds the single <c>static void Main()</c>.
    /// </summary>
    /// <remarks>
    /// Roslyn's own entry point resolution is not used because it requires an
    /// executable output kind and would report CS5001 for a missing Main, which
    /// says nothing about GB#. GBS0003 explains what GB# actually needs.
    /// </remarks>
    private IMethodSymbol? ResolveEntryPoint(IReadOnlyList<IMethodSymbol> methods)
    {
        List<IMethodSymbol> candidates = methods
            .Where(m => m is { IsStatic: true, Name: "Main", Parameters.Length: 0 } &&
                        m.ReturnType.SpecialType == SpecialType.System_Void)
            .ToList();

        switch (candidates.Count)
        {
            case 1:
                return candidates[0];

            case 0:
                diagnostics.Report(GBDiagnostics.MissingEntryPoint, (Location?)null);
                return null;

            default:
                diagnostics.Report(
                    GBDiagnostics.MultipleEntryPoints,
                    candidates[0].Locations.FirstOrDefault(),
                    candidates.Count);
                return null;
        }
    }
}
