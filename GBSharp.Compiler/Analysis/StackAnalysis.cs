namespace GBSharp.Compiler.Analysis;

/// <summary>
/// How deep the call stack can get.
/// </summary>
/// <remarks>
/// <para>
/// Depth in calls, not bytes. The depth is exact: GB# rejects delegates and has
/// no function pointers, so the call graph is the complete account of what can
/// reach what. A byte figure would not be exact: GB# never sees SDCC's frame
/// layout, its register allocation or its spills, and would be wrong by a
/// factor of two to four and biased low, which is the direction that lets a ROM
/// ship and then quietly corrupt memory. The measured byte figure comes from the
/// linker map instead, where the toolchain is present, and is reported as the
/// measurement it is.
/// </para>
/// <para>
/// Why this matters on this hardware: the stack starts at the top of work RAM
/// and grows down through the same 8 KB the static fields grow up through, and
/// nothing checks. Going one frame too deep is not a crash; it is a static field
/// changing value for no visible reason.
/// </para>
/// </remarks>
public static class StackAnalysis
{
    /// <summary>
    /// The longest chain of calls reachable from the entry point.
    /// </summary>
    /// <remarks>
    /// Refuses to produce a maximum when the program can recurse. The depth is
    /// then whatever the data makes it, and a number derived from the acyclic
    /// part would be quoted as a ceiling it is not. The recursion itself is
    /// reported instead, which is the more useful fact anyway.
    /// </remarks>
    public static StackDepth Measure(CallGraph graph)
    {
        if (graph.Functions.Count == 0 || graph.EntryPoint.Length == 0)
        {
            return StackDepth.Unknown;
        }

        var recursive = new HashSet<string>(graph.Cycles.SelectMany(c => c), StringComparer.Ordinal);
        var deepest = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<string> path = Longest(graph.EntryPoint);

        return new StackDepth(path.Count, !ReachesRecursion(graph, recursive), path);

        IReadOnlyList<string> Longest(string function)
        {
            if (deepest.TryGetValue(function, out IReadOnlyList<string>? known))
            {
                return known;
            }

            // A cycle is not descended into. Its depth is unbounded and the
            // caller reports that rather than a number.
            if (!visiting.Add(function))
            {
                return [graph.DisplayName(function)];
            }

            IReadOnlyList<string> best = [];

            foreach (string callee in graph.From(function).Select(s => s.Callee).Distinct(StringComparer.Ordinal))
            {
                IReadOnlyList<string> candidate = Longest(callee);

                if (candidate.Count > best.Count)
                {
                    best = candidate;
                }
            }

            visiting.Remove(function);

            IReadOnlyList<string> result = [graph.DisplayName(function), .. best];
            deepest[function] = result;

            return result;
        }
    }

    /// <summary>
    /// True if the entry point can reach a cycle, which is the only case where
    /// recursion actually threatens this program's stack.
    /// </summary>
    /// <remarks>
    /// A recursive function nothing calls still gets its own diagnostic, because
    /// it is a hazard waiting for its first caller. But it does not make the
    /// depth of the program that never calls it unbounded.
    /// </remarks>
    private static bool ReachesRecursion(CallGraph graph, IReadOnlySet<string> recursive)
    {
        if (recursive.Count == 0)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { graph.EntryPoint };
        var pending = new Stack<string>();
        pending.Push(graph.EntryPoint);

        while (pending.Count > 0)
        {
            string function = pending.Pop();

            if (recursive.Contains(function))
            {
                return true;
            }

            foreach (CallSite site in graph.From(function))
            {
                if (seen.Add(site.Callee))
                {
                    pending.Push(site.Callee);
                }
            }
        }

        return false;
    }
}
