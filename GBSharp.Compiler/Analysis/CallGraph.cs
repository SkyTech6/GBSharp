using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Compiler.Analysis;

/// <summary>One call, from the statement that makes it.</summary>
/// <param name="Caller">The mangled name of the calling function.</param>
/// <param name="Callee">The mangled name of the called function.</param>
/// <param name="Span">
/// The enclosing statement's source position. Expressions carry no span of their
/// own, so this is statement-granular; the per-call-site diagnostic that needs
/// finer than that is GBS0301, which reports from the Roslyn operation instead.
/// </param>
/// <param name="CrossesBank">True if the call switches ROM banks.</param>
/// <param name="InFrameLoop">True if the call site is inside the frame loop.</param>
public readonly record struct CallSite(
    string Caller,
    string Callee,
    SourceSpan Span,
    bool CrossesBank,
    bool InFrameLoop);

/// <summary>
/// Which functions can reach which, for the whole module.
/// </summary>
/// <remarks>
/// <para>
/// Built once and shared. Stack depth and the bank advice both need it, and two
/// separately built graphs are two chances to disagree about the same program.
/// </para>
/// <para>
/// The graph is the whole truth about what can reach what, because GB# has
/// neither delegates nor function pointers: both are rejected by the subset
/// (GBS0045). That is what makes call depth an exact figure rather than an
/// estimate, and it is the reason the stack analysis reports depth rather than
/// bytes.
/// </para>
/// </remarks>
public sealed record CallGraph(
    IReadOnlyDictionary<string, IRFunction> Functions,
    IReadOnlyDictionary<string, IReadOnlyList<CallSite>> Edges,
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    string EntryPoint)
{
    /// <summary>Nothing to analyse.</summary>
    public static CallGraph Empty { get; } = new(
        new Dictionary<string, IRFunction>(),
        new Dictionary<string, IReadOnlyList<CallSite>>(),
        [],
        string.Empty);

    /// <summary>True if the program can call itself, directly or through a chain.</summary>
    public bool HasRecursion => Cycles.Count > 0;

    /// <summary>Calls made by one function, or nothing.</summary>
    public IReadOnlyList<CallSite> From(string function) =>
        Edges.TryGetValue(function, out IReadOnlyList<CallSite>? sites) ? sites : [];

    /// <summary>A function's C# name, for anything a developer reads.</summary>
    public string DisplayName(string function) =>
        Functions.TryGetValue(function, out IRFunction? declaration)
            ? declaration.SourceName ?? declaration.Name
            : function;

    public static CallGraph Build(IRModule module)
    {
        var functions = new Dictionary<string, IRFunction>(StringComparer.Ordinal);

        foreach (IRFunction function in module.Functions)
        {
            functions[function.Name] = function;
        }

        var edges = new Dictionary<string, IReadOnlyList<CallSite>>(StringComparer.Ordinal);

        foreach (IRFunction function in module.Functions)
        {
            edges[function.Name] = CollectCalls(function, functions);
        }

        return new CallGraph(functions, edges, FindCycles(functions.Keys, edges), module.EntryPoint.Name);
    }

    /// <summary>
    /// Every call one function makes, with the context each was made in.
    /// </summary>
    /// <remarks>
    /// A native call produces no edge: there is no IR function behind it, so
    /// there is nothing for the graph to reach. Its cost and its bank switch are
    /// charged by the cost model instead.
    /// </remarks>
    private static IReadOnlyList<CallSite> CollectCalls(
        IRFunction caller,
        IReadOnlyDictionary<string, IRFunction> functions)
    {
        var sites = new List<CallSite>();

        Visit(caller.Body, inFrameLoop: false);

        return sites;

        void Visit(IRStatement statement, bool inFrameLoop)
        {
            bool insideFrameLoop = inFrameLoop
                || (statement is IRWhile loop && IsFrameLoop(loop));

            foreach (IRStatement child in Children(statement))
            {
                Visit(child, insideFrameLoop);
            }

            foreach (IRExpression expression in Own(statement).SelectMany(IRWalk.Descend))
            {
                // An unresolved name is skipped rather than thrown on. Lowering
                // only emits a call for a function it knows, so this cannot
                // happen, and a graph is not worth an internal compiler error.
                if (expression is not IRCall call
                    || !functions.TryGetValue(call.FunctionName, out IRFunction? callee))
                {
                    continue;
                }

                sites.Add(new CallSite(
                    caller.Name,
                    callee.Name,
                    statement.Span,
                    Crosses(caller.Bank, callee.Bank),
                    insideFrameLoop));
            }
        }
    }

    /// <summary>
    /// True if reaching the callee from the caller pays for a bank switch.
    /// </summary>
    /// <remarks>
    /// An automatically placed callee is assumed to cross, because the bank it
    /// lands in is not known until the linker has run and assuming otherwise
    /// would understate the cost of exactly the code a developer has not pinned.
    /// </remarks>
    private static bool Crosses(IRBank caller, IRBank callee) => callee.Kind switch
    {
        IRBankKind.Resident => false,
        IRBankKind.Automatic => true,
        _ => caller.Kind != IRBankKind.Fixed || caller.Number != callee.Number,
    };

    private static bool IsFrameLoop(IRWhile loop) =>
        loop.Condition is IRConstant { Value: true }
        && IRWalk.Expressions(loop.Body).Any(e => e is IRNativeCall { Symbol: CostModel.FrameBarrier });

    /// <summary>The statements directly under one statement.</summary>
    private static IEnumerable<IRStatement> Children(IRStatement statement) => statement switch
    {
        IRBlock block => block.Statements,
        IRIf conditional => conditional.Else is { } otherwise
            ? [conditional.Then, otherwise]
            : [conditional.Then],
        IRWhile loop => [loop.Body],
        IRDoWhile loop => [loop.Body],
        IRFor loop => [.. loop.Initializers, .. loop.Updates, loop.Body],
        IRSwitch selection => selection.Default is { } fallback
            ? [.. selection.Sections.Select(s => s.Body), fallback]
            : [.. selection.Sections.Select(s => s.Body)],
        _ => [],
    };

    /// <summary>The expressions a statement holds directly, not counting its children.</summary>
    private static IEnumerable<IRExpression> Own(IRStatement statement) => statement switch
    {
        IRLocalDeclaration { Initializer: { } value } => [value],
        IRAssign assign => [assign.Target, assign.Value],
        IRCompoundAssign compound => [compound.Target, compound.Value],
        IRExpressionStatement expression => [expression.Expression],
        IRIf conditional => [conditional.Condition],
        IRWhile loop => [loop.Condition],
        IRDoWhile loop => [loop.Condition],
        IRFor { Condition: { } condition } => [condition],
        IRSwitch selection => [selection.Value],
        IRReturn { Value: { } result } => [result],
        _ => [],
    };

    /// <summary>
    /// Every group of functions that can reach each other, and every function
    /// that calls itself.
    /// </summary>
    /// <remarks>
    /// Tarjan's algorithm, written iteratively. A recursive implementation would
    /// overflow this compiler's own stack on a deep enough call chain, which
    /// would be a poor way for a stack analysis to fail.
    /// </remarks>
    private static IReadOnlyList<IReadOnlyList<string>> FindCycles(
        IEnumerable<string> functions,
        IReadOnlyDictionary<string, IReadOnlyList<CallSite>> edges)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var component = new Stack<string>();
        var cycles = new List<IReadOnlyList<string>>();

        int next = 0;

        foreach (string root in functions)
        {
            if (index.ContainsKey(root))
            {
                continue;
            }

            var work = new Stack<(string Node, int Edge)>();
            work.Push((root, 0));
            index[root] = low[root] = next++;
            component.Push(root);
            onStack.Add(root);

            while (work.Count > 0)
            {
                (string node, int edge) = work.Pop();
                IReadOnlyList<CallSite> outgoing = edges.TryGetValue(node, out IReadOnlyList<CallSite>? sites)
                    ? sites
                    : [];

                bool descended = false;

                while (edge < outgoing.Count)
                {
                    string callee = outgoing[edge].Callee;
                    edge++;

                    if (!index.ContainsKey(callee))
                    {
                        work.Push((node, edge));
                        index[callee] = low[callee] = next++;
                        component.Push(callee);
                        onStack.Add(callee);
                        work.Push((callee, 0));
                        descended = true;
                        break;
                    }

                    if (onStack.Contains(callee))
                    {
                        low[node] = Math.Min(low[node], index[callee]);
                    }
                }

                if (descended)
                {
                    continue;
                }

                // Finished this node: fold its low link into its parent's.
                if (work.Count > 0)
                {
                    (string parent, _) = work.Peek();
                    low[parent] = Math.Min(low[parent], low[node]);
                }

                if (low[node] != index[node])
                {
                    continue;
                }

                var members = new List<string>();

                while (true)
                {
                    string member = component.Pop();
                    onStack.Remove(member);
                    members.Add(member);

                    if (string.Equals(member, node, StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                bool selfCall = members.Count == 1
                    && edges.TryGetValue(node, out IReadOnlyList<CallSite>? own)
                    && own.Any(s => string.Equals(s.Callee, node, StringComparison.Ordinal));

                if (members.Count > 1 || selfCall)
                {
                    members.Reverse();
                    cycles.Add(members);
                }
            }
        }

        return cycles;
    }
}
