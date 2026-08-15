using GBSharp.Compiler.Analysis;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Compiler;

/// <summary>
/// The check that stops a codegen bug reaching the developer as an SDCC error.
/// </summary>
/// <remarks>
/// Written against hand-built IR rather than against C# source, because the
/// bugs it guards against are ones the lowerer no longer produces. Feeding it
/// real source could only ever assert that correct IR passes, which is the half
/// that was never in doubt, so the mismatches are constructed directly.
/// </remarks>
public sealed class IRVerifierTests
{
    private static readonly IRStructType Point = new("Point", 1);

    /// <summary>A module of one caller and one callee taking <c>Point*</c>.</summary>
    private static IRModule Module(IRExpression argument)
    {
        var callee = new IRFunction(
            "Point_Update",
            IRPrimitiveType.Void,
            [new IRParameter("self", new IRPointerType(Point))],
            [],
            IRBlock.Empty,
            SourceSpan.None);

        var caller = new IRFunction(
            "Program_Main",
            IRPrimitiveType.Void,
            [],
            [],
            new IRBlock([
                new IRExpressionStatement(new IRCall("Point_Update", [argument], IRPrimitiveType.Void)),
            ]),
            SourceSpan.None)
        {
            SourceName = "Program.Main()",
        };

        return new IRModule("TestGame", [], [], [callee, caller], caller);
    }

    private static IReadOnlyList<GBDiagnostic> Verify(IRModule module)
    {
        var diagnostics = new DiagnosticBag();
        IRVerifier.Verify(module, diagnostics);
        return diagnostics.Diagnostics;
    }

    private static IRExpression Local() => new IRLocalRef(new IRLocal("p", Point));

    [Fact]
    public void AStructPassedWhereAPointerIsExpectedIsCaught()
    {
        // Exactly the bug this exists for: the declaration says Point*, the call
        // site passes the struct itself, and the C compiler is the only thing
        // that used to object.
        IReadOnlyList<GBDiagnostic> diagnostics = Verify(Module(Local()));

        GBDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("GBS9001", diagnostic.Id);
        Assert.Equal(GBSeverity.Error, diagnostic.Severity);

        // Names the caller, because that is what a bug report is rebuilt from.
        Assert.Contains("Program.Main()", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("self", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAddressOfAStructIsAccepted()
    {
        Assert.Empty(Verify(Module(new IRAddressOf(Local()))));
    }

    [Fact]
    public void AWrongArgumentCountIsCaught()
    {
        IRModule module = Module(new IRAddressOf(Local()));

        var caller = new IRFunction(
            "Program_Main",
            IRPrimitiveType.Void,
            [],
            [],
            new IRBlock([
                new IRExpressionStatement(new IRCall("Point_Update", [], IRPrimitiveType.Void)),
            ]),
            SourceSpan.None);

        module = module with { Functions = [module.Functions[0], caller], EntryPoint = caller };

        GBDiagnostic diagnostic = Assert.Single(Verify(module));
        Assert.Equal("GBS9001", diagnostic.Id);
        Assert.Contains("1 argument(s) but is called with 0", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name with no function behind it is a runtime helper the backend
    /// supplies, so there is no signature here to check and nothing to say.
    /// </summary>
    [Fact]
    public void ACallToSomethingOutsideTheModuleIsLeftAlone()
    {
        var caller = new IRFunction(
            "Program_Main",
            IRPrimitiveType.Void,
            [],
            [],
            new IRBlock([
                new IRExpressionStatement(new IRCall("gbs_helper", [Local()], IRPrimitiveType.Void)),
            ]),
            SourceSpan.None);

        Assert.Empty(Verify(new IRModule("TestGame", [], [], [caller], caller)));
    }
}
