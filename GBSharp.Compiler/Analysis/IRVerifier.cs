using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;
using GBSharp.Rules;

namespace GBSharp.Compiler.Analysis;

/// <summary>
/// Checks that every call in a module agrees with the function it calls.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a specific failure. A call to a struct's instance
/// method passed the struct by value while the emitted function declared
/// <c>Point* self</c>; the C was well formed, so nothing in GB# objected, and
/// the developer was shown SDCC complaining about a generated file they did not
/// write. Section 6 asks for GB# diagnostics rather than leaked backend errors,
/// and the only way to keep that promise for codegen bugs is to check the IR
/// before the backend ever sees it.
/// </para>
/// <para>
/// Deliberately narrow. It compares arity and the one distinction that
/// mattered: pointer against value, rather than attempting a full type
/// system for the IR. A check that tried to verify everything would need to
/// model C's conversions, and a verifier that reports false positives gets
/// switched off, which is worse than one that catches a single real class of
/// bug every time.
/// </para>
/// <para>
/// Anything it reports is a bug in GB#, not in the developer's code, so it
/// reports <c>GBS9001</c> and names the caller, which is the only part of the
/// picture a bug report can be reconstructed from.
/// </para>
/// </remarks>
public static class IRVerifier
{
    public static void Verify(IRModule module, DiagnosticBag diagnostics)
    {
        Dictionary<string, IRFunction> functions = [];
        foreach (IRFunction function in module.Functions)
        {
            functions[function.Name] = function;
        }

        foreach (IRFunction caller in module.Functions)
        {
            // Expressions() already descends through nested statements, so the
            // body goes in whole. Walking the statements as well would visit
            // every call once per enclosing block and report it that many times.
            foreach (IRExpression expression in IRWalk.Expressions(caller.Body))
            {
                if (expression is IRCall call)
                {
                    VerifyCall(caller, call, functions, diagnostics);
                }
            }
        }
    }

    private static void VerifyCall(
        IRFunction caller,
        IRCall call,
        IReadOnlyDictionary<string, IRFunction> functions,
        DiagnosticBag diagnostics)
    {
        // A name with no function behind it is a [Native] symbol or a helper the
        // backend supplies, and there is no signature here to check it against.
        if (!functions.TryGetValue(call.FunctionName, out IRFunction? callee))
        {
            return;
        }

        if (call.Arguments.Count != callee.Parameters.Count)
        {
            Report(
                diagnostics,
                caller,
                $"'{call.FunctionName}' takes {callee.Parameters.Count} argument(s) but is " +
                $"called with {call.Arguments.Count}");
            return;
        }

        for (int i = 0; i < call.Arguments.Count; i++)
        {
            IRType parameter = callee.Parameters[i].Type;
            IRType argument = call.Arguments[i].Type;

            if (IsAddress(parameter) == IsAddress(argument))
            {
                continue;
            }

            Report(
                diagnostics,
                caller,
                $"'{call.FunctionName}' parameter {i} ('{callee.Parameters[i].Name}') is " +
                $"{Describe(parameter)} but the argument is {Describe(argument)}");
        }
    }

    /// <summary>
    /// Whether a type is passed as an address.
    /// </summary>
    /// <remarks>
    /// An array counts, because C passes one as a pointer to its first element
    /// and the lowerer relies on that rather than emitting a conversion.
    /// </remarks>
    private static bool IsAddress(IRType type) => type is IRPointerType or IRArrayType;

    private static string Describe(IRType type) => IsAddress(type) ? $"an address ({type})" : $"a value ({type})";

    private static void Report(DiagnosticBag diagnostics, IRFunction caller, string what) =>
        diagnostics.Report(
            GBDiagnostics.InternalError,
            caller.Span,
            $"{what}, in {caller.SourceName ?? caller.Name}");
}
