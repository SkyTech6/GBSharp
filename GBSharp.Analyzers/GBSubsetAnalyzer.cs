using System.Collections.Immutable;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace GBSharp.Analyzers;

/// <summary>
/// Reports GB# subset violations in the editor, before a build.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here is answerable from a symbol or a single operation, and every
/// verdict comes from <see cref="SubsetRules"/>, which the compiler also
/// consults. That is what keeps a GBS id meaning one thing: the analyzer and the
/// build cannot drift because there is only one copy of the rule.
/// </para>
/// <para>
/// <b>This analyzer never touches disk.</b> That single constraint explains what
/// is missing from it. The asset diagnostics need the image decoded, the cycle
/// costs need the lowerer's width inference, and the toolchain ones need GBDK,
/// none of which belong on a keystroke. Those stay build-time only, and the
/// parity test asserts this reports a subset of what a build does, never more.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GBSubsetAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Derived from the catalog rather than written out, so a rule cannot be
    /// implemented without being declared.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.CreateRange(GBRuleCatalog.IdeReportable.Select(GBRuleCatalog.ToRoslyn));

    public override void Initialize(AnalysisContext context)
    {
        // Generated code is nobody's to fix, and running concurrently is what
        // keeps this off the typing path.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
        context.RegisterSymbolAction(AnalyzeParameter, SymbolKind.Parameter);

        context.RegisterOperationAction(AnalyzeVariableDeclarator, OperationKind.VariableDeclarator);
        context.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeThrow, OperationKind.Throw);
        context.RegisterOperationAction(AnalyzeAwait, OperationKind.Await);
        context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
    }

    // -----------------------------------------------------------------------
    // Declarations
    // -----------------------------------------------------------------------

    private static void AnalyzeField(SymbolAnalysisContext context)
    {
        var field = (IFieldSymbol)context.Symbol;

        // Constants fold at compile time and cost nothing, so neither the type
        // rules nor the resource notes apply to them.
        if (field.IsConst || field.IsImplicitlyDeclared)
        {
            return;
        }

        Location location = field.Locations.FirstOrDefault() ?? Location.None;

        if (Report(context.ReportDiagnostic, SubsetRules.ClassifyType(field.Type), location))
        {
            return;
        }

        ReportStorage(context, field, location);
    }

    private static void AnalyzeParameter(SymbolAnalysisContext context)
    {
        var parameter = (IParameterSymbol)context.Symbol;

        Report(
            context.ReportDiagnostic,
            SubsetRules.ClassifyType(parameter.Type),
            parameter.Locations.FirstOrDefault() ?? Location.None);
    }

    private static void AnalyzeVariableDeclarator(OperationAnalysisContext context)
    {
        var declarator = (IVariableDeclaratorOperation)context.Operation;

        Report(
            context.ReportDiagnostic,
            SubsetRules.ClassifyType(declarator.Symbol.Type),
            declarator.Syntax.GetLocation());
    }

    /// <summary>
    /// What a static field costs, reported as you declare it.
    /// </summary>
    /// <remarks>
    /// Thesis sections 3.2 and 25: if a developer reserves a quarter of the work
    /// RAM, GB# should make that obvious. Seeing it under the declaration while
    /// typing is that promise kept, rather than something learned after a build.
    /// <para>
    /// Read-only data is in the cartridge, not work RAM, and the two are
    /// reported separately for the same reason the build report separates them.
    /// </para>
    /// </remarks>
    private static void ReportStorage(SymbolAnalysisContext context, IFieldSymbol field, Location location)
    {
        if (!field.IsStatic || SizeOf(field.Type) is not { } size || size == 0)
        {
            return;
        }

        // An array's length lives on the initializer rather than the type, and
        // the analyzer does not evaluate initializers, so only single values are
        // costed here. The build report is the complete account.
        if (field.Type is IArrayTypeSymbol)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            GBRuleCatalog.ToRoslyn(field.IsReadOnly ? GBDiagnostics.RomAllocation : GBDiagnostics.StaticAllocation),
            location,
            field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            size));
    }

    // -----------------------------------------------------------------------
    // Operations
    // -----------------------------------------------------------------------

    private static void AnalyzeObjectCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;

        // Roslyn also raises an ObjectCreation operation for the implicit
        // constructor call behind an attribute application ([Asset("x")] is
        // modeled the same as `new AssetAttribute("x")` here). That call
        // compiles to metadata, not a heap allocation, so it is not this
        // rule's business, the same reason the compiler never routes an
        // attribute's constructor through TypeMapper/ClassifyType either.
        if (creation.Syntax is AttributeSyntax)
        {
            return;
        }

        Report(
            context.ReportDiagnostic,
            SubsetRules.ClassifyType(creation.Type),
            creation.Syntax.GetLocation());
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (SubsetRules.IsLinq(invocation.TargetMethod))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                GBRuleCatalog.ToRoslyn(GBDiagnostics.Linq),
                invocation.Syntax.GetLocation()));
        }
    }

    private static void AnalyzeThrow(OperationAnalysisContext context) =>
        context.ReportDiagnostic(Diagnostic.Create(
            GBRuleCatalog.ToRoslyn(GBDiagnostics.Exceptions),
            context.Operation.Syntax.GetLocation()));

    private static void AnalyzeAwait(OperationAnalysisContext context) =>
        context.ReportDiagnostic(Diagnostic.Create(
            GBRuleCatalog.ToRoslyn(GBDiagnostics.AsyncAwait),
            context.Operation.Syntax.GetLocation()));

    private static void AnalyzeConversion(OperationAnalysisContext context)
    {
        var conversion = (IConversionOperation)context.Operation;

        // Only the widening that costs something: an 8-bit machine doing 32-bit
        // arithmetic. Reported at the conversion, where the type change happens.
        if (conversion.Type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32 &&
            conversion.Operand.Type?.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_UInt16 or SpecialType.System_Int16)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                GBRuleCatalog.ToRoslyn(GBDiagnostics.Int32Arithmetic),
                conversion.Syntax.GetLocation(),
                conversion.Type.ToDisplayString()));
        }
    }

    // -----------------------------------------------------------------------

    /// <summary>Reports a verdict if it is a rejection. Returns true if it did.</summary>
    private static bool Report(Action<Diagnostic> report, RuleVerdict verdict, Location location)
    {
        if (!verdict.IsRejection)
        {
            return false;
        }

        report(Diagnostic.Create(
            GBRuleCatalog.ToRoslyn(verdict.Descriptor!),
            location,
            verdict.Arguments));

        return true;
    }

    /// <summary>The bytes a value of this type occupies, or null if unknown.</summary>
    private static int? SizeOf(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
        SpecialType.System_UInt16 or SpecialType.System_Int16 => 2,
        SpecialType.System_Int32 or SpecialType.System_UInt32 => 4,
        _ => type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying }
            ? SizeOf(underlying)
            : null,
    };
}
