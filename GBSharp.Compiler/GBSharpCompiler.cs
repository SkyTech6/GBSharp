using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.Frontend;
using GBSharp.Compiler.IR;
using GBSharp.Compiler.Lowering;
using Microsoft.CodeAnalysis.CSharp;

namespace GBSharp.Compiler;

/// <summary>The result of a frontend run.</summary>
/// <param name="Diagnostics">Everything reported, at every severity.</param>
/// <param name="Module">The lowered module, or null if compilation failed.</param>
public sealed record CompilationResult(IReadOnlyList<GBDiagnostic> Diagnostics, IRModule? Module)
{
    public bool Succeeded => Module is not null && !Diagnostics.Any(d => d.IsError);
}

/// <summary>
/// C# source in, GB# IR out.
/// </summary>
/// <remarks>
/// Knows nothing about C, GBDK or ROMs. Keeping the frontend backend-agnostic
/// is what makes a second backend a new project rather than a rewrite
/// (thesis section 2).
/// </remarks>
public sealed class GBSharpCompiler
{
    public CompilationResult Compile(CompilationRequest request)
    {
        var diagnostics = new DiagnosticBag(request.DiagnosticOptions);

        // Said once, up front: a setting that could not be honoured has to be
        // visible before the diagnostics it was meant to affect would have been.
        diagnostics.ReportRefusedSuppressions(GBDiagnostics.All);

        CSharpCompilation? compilation = RoslynFrontend.Create(request, diagnostics);
        if (compilation is null)
        {
            return new CompilationResult(diagnostics.Diagnostics, null);
        }

        // If it is not valid C#, say so in Roslyn's words and stop. GB# has
        // nothing useful to add about code that does not parse or bind.
        RoslynFrontend.ReportRoslynDiagnostics(compilation, diagnostics);
        if (diagnostics.HasErrors)
        {
            return new CompilationResult(diagnostics.Diagnostics, null);
        }

        IRModule? module = new ModuleLowerer(compilation, diagnostics, request).Lower();

        return new CompilationResult(diagnostics.Diagnostics, diagnostics.HasErrors ? null : module);
    }
}
