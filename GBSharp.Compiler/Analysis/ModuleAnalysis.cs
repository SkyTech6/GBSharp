using System.Globalization;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Compiler.Analysis;

/// <summary>
/// Runs the cost, call-graph and stack analyses over a lowered module and
/// reports what they found.
/// </summary>
/// <remarks>
/// <para>
/// Advice only. Nothing here rewrites the IR, and the C emitter stays
/// deliberately non-optimising: SDCC is the optimiser, and this folder's job is
/// to make what the developer wrote visible, not to change it (thesis sections
/// 3.3 and 9).
/// </para>
/// <para>
/// The reporting rule this follows, which is what keeps the band usable: a
/// diagnostic is for something that scales with the <em>program</em>, and the
/// build report is for anything that scales with its <em>size</em>. GB# already
/// reports a note per global, per asset and per bank, and a fourth such family
/// would bury the errors among them. So the whole-program facts get ids and the
/// per-function ranking goes in the report, where it has no severity to argue
/// about and nothing to suppress.
/// </para>
/// <para>
/// What was considered and left out, because the first question about a hint is
/// what else was on the list:
/// </para>
/// <list type="bullet">
/// <item>
/// Dead-code hints. The framework's lifecycle is entered without a call the IR
/// can see, and interrupt handlers will be too. A hint that accuses live code of
/// being dead is worse than no hint.
/// </item>
/// <item>
/// Per-expression costs. Expressions carry no source span, so a note could only
/// anchor at the enclosing statement, and a cycle figure per statement claims a
/// precision the model does not have.
/// </item>
/// <item>
/// "This multiply could be a shift." GBS0102's help already says it, and knowing
/// when a range is small enough for a lookup table needs value-range analysis
/// GB# does not do.
/// </item>
/// <item>
/// Loop-invariant hoisting advice, which needs real dataflow over a control-flow
/// graph the IR deliberately is not.
/// </item>
/// <item>
/// "Inline this." GB# does not inline and SDCC's decision is invisible here, so
/// the advice would be about a transformation neither party admits to.
/// </item>
/// <item>
/// A cycle figure bolted onto GBS0102 and GBS0103. Those state a fact (SM83 has
/// no multiply instruction) at a source location, with a fix. An estimate is a
/// guess, and folding a guess into a fact downgrades the better diagnostic.
/// </item>
/// </list>
/// </remarks>
public static class ModuleAnalysis
{
    /// <summary>
    /// The share of a frame at which a frame loop is worth mentioning.
    /// </summary>
    /// <remarks>
    /// Deliberately well under 100%. The estimate excludes the VBlank handler,
    /// any audio driver and the loaders' copies, so a loop measuring at the
    /// budget has already exceeded it. Sixty leaves room to act.
    /// </remarks>
    private const int FramePressurePercent = 60;

    /// <summary>
    /// The estimate below which a loop is not worth a note.
    /// </summary>
    /// <remarks>
    /// About 6% of a frame. A cost note on a three-instruction loop is noise, and
    /// a band that fires everywhere gets silenced wholesale, which costs the
    /// developer the notes that mattered.
    /// </remarks>
    private const int LoopReportThreshold = 4_000;

    /// <summary>
    /// The call depth at which the stack is worth mentioning.
    /// </summary>
    /// <remarks>
    /// Shallow call chains are the normal state of a GB# program and saying so
    /// every build would be noise. This fires where the depth starts to be a
    /// number a developer might not have expected.
    /// </remarks>
    private const int StackDepthThreshold = 6;

    /// <summary>
    /// Analyses a module and reports what is worth reporting.
    /// </summary>
    /// <remarks>
    /// Needs no toolchain, which is what lets <c>gbsharp analyze</c> produce the
    /// same notes a full build does.
    /// </remarks>
    public static ModuleCostReport Analyse(IRModule module, DiagnosticBag diagnostics)
    {
        IReadOnlyDictionary<string, int> capacities = LoopBounds.CollectionCapacities(module);

        List<FunctionCost> costs = [.. module.Functions.Select(f => CostModel.Measure(f, capacities))];

        CallGraph graph = CallGraph.Build(module);

        ReportRecursion(graph, diagnostics);

        StackDepth stack = StackAnalysis.Measure(graph);

        ReportStackDepth(stack, module, diagnostics);
        ReportLoops(costs, module, diagnostics);

        LoopCost? frameLoop = costs.Select(c => c.FrameLoop).FirstOrDefault(l => l is not null);

        ReportFramePressure(frameLoop, module, diagnostics);
        ReportBankedCallsInFrameLoop(graph, diagnostics);
        ReportBankGrouping(graph, diagnostics);

        return new ModuleCostReport(
            [.. costs.OrderByDescending(c => c.Cycles).ThenBy(c => c.DisplayName, StringComparer.Ordinal)],
            frameLoop,
            stack,
            module.Globals.Where(g => !g.IsReadOnly).Sum(g => g.Type.SizeInBytes),
            module.Globals.Where(g => g.IsReadOnly).Sum(g => g.Type.SizeInBytes));
    }

    /// <summary>
    /// One note per cycle, at the declaration of the first function in it.
    /// </summary>
    /// <remarks>
    /// Per cycle rather than per function in the cycle: a two-function recursion
    /// is one hazard, and reporting it twice would suggest two.
    /// </remarks>
    private static void ReportRecursion(CallGraph graph, DiagnosticBag diagnostics)
    {
        foreach (IReadOnlyList<string> cycle in graph.Cycles)
        {
            string[] names = [.. cycle.Select(graph.DisplayName)];

            // A self-call reads better as "A calls itself" than as "A -> A".
            string chain = names.Length == 1
                ? names[0] + " calls itself"
                : string.Join(" -> ", names.Append(names[0]));

            diagnostics.Report(
                GBDiagnostics.RecursiveCall,
                graph.Functions.TryGetValue(cycle[0], out IRFunction? function) ? function.Span : SourceSpan.None,
                names[0],
                chain);
        }
    }

    private static void ReportStackDepth(StackDepth stack, IRModule module, DiagnosticBag diagnostics)
    {
        // A recursive program has no ceiling to report. GBS0058 has already said
        // why, and a depth taken from the acyclic part would be read as a maximum.
        if (!stack.Bounded || stack.Calls < StackDepthThreshold)
        {
            return;
        }

        diagnostics.Report(
            GBDiagnostics.StackDepthNote,
            module.EntryPoint.Span,
            stack.Calls,
            string.Join(" -> ", stack.DeepestPath));
    }

    private static void ReportLoops(
        IReadOnlyList<FunctionCost> costs,
        IRModule module,
        DiagnosticBag diagnostics)
    {
        foreach (LoopCost loop in costs.SelectMany(c => c.Loops))
        {
            // An unbounded loop has no total, and there is deliberately no code
            // path here that could invent one. The frame loop is reported on its
            // own terms by GBS0401; any other unbounded loop says nothing at all.
            if (loop.TotalCycles is not { } total || total < LoopReportThreshold)
            {
                continue;
            }

            diagnostics.Report(
                GBDiagnostics.LoopCycleCost,
                loop.Span.IsNone ? module.EntryPoint.Span : loop.Span,
                loop.TripCount!.Value,
                Format(loop.PerIterationCycles),
                Format(total),
                Caveat(loop.IsPartial));
        }
    }

    private static void ReportFramePressure(LoopCost? frameLoop, IRModule module, DiagnosticBag diagnostics)
    {
        if (frameLoop is null)
        {
            return;
        }

        int percent = Sm83CostTable.PercentOfFrame(frameLoop.PerIterationCycles);

        if (percent < FramePressurePercent)
        {
            return;
        }

        diagnostics.Report(
            GBDiagnostics.FrameBudgetPressure,
            frameLoop.Span.IsNone ? module.EntryPoint.Span : frameLoop.Span,
            Format(frameLoop.PerIterationCycles),
            percent,
            Caveat(frameLoop.IsPartial));
    }

    /// <summary>
    /// Banked calls on the path that runs sixty times a second.
    /// </summary>
    /// <remarks>
    /// One note per callee rather than per call site: the fact is about where the
    /// callee lives, and repeating it at every site would be the per-site
    /// diagnostic GBS0301 already is.
    /// </remarks>
    private static void ReportBankedCallsInFrameLoop(CallGraph graph, DiagnosticBag diagnostics)
    {
        IReadOnlySet<string> onFramePath = FrameReachable(graph);

        if (onFramePath.Count == 0)
        {
            return;
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (CallSite site in graph.Functions.Keys
                     .Where(onFramePath.Contains)
                     .SelectMany(graph.From)
                     .Where(s => s.CrossesBank))
        {
            if (!reported.Add(site.Callee) || !graph.Functions.TryGetValue(site.Callee, out IRFunction? callee))
            {
                continue;
            }

            diagnostics.Report(
                GBDiagnostics.BankedCallInFrameLoop,
                site.Span,
                graph.DisplayName(site.Callee),
                callee.Bank.ToString(),
                Format(Sm83CostTable.BankedCallOverhead));
        }
    }

    /// <summary>
    /// Everything the frame loop can reach, transitively.
    /// </summary>
    /// <remarks>
    /// Transitive rather than just the direct call sites, because the cost is
    /// paid wherever it sits on that path: a banked call three frames down from
    /// the loop still runs sixty times a second. The seed is the callees of call
    /// sites the graph marked as being inside a frame loop.
    /// </remarks>
    private static IReadOnlySet<string> FrameReachable(CallGraph graph)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();

        foreach (CallSite site in graph.Functions.Keys.SelectMany(graph.From).Where(s => s.InFrameLoop))
        {
            // The caller is on the path too: it is the one running the loop.
            if (reachable.Add(site.Caller))
            {
                pending.Push(site.Caller);
            }
        }

        while (pending.Count > 0)
        {
            foreach (CallSite site in graph.From(pending.Pop()))
            {
                if (reachable.Add(site.Callee))
                {
                    pending.Push(site.Callee);
                }
            }
        }

        return reachable;
    }

    /// <summary>
    /// Callees whose every caller sits in one other bank.
    /// </summary>
    /// <remarks>
    /// Stated as a consequence rather than as advice. The callee may well be
    /// where it is because the bank it would move to is full, and GB# does not
    /// know that until the linker has run, so this says what moving it would
    /// save and leaves the decision where it belongs.
    /// </remarks>
    private static void ReportBankGrouping(CallGraph graph, DiagnosticBag diagnostics)
    {
        foreach ((string name, IRFunction callee) in graph.Functions)
        {
            // Only a pinned bank can be reasoned about. A resident callee costs
            // nothing to reach, and an automatic one has no bank to compare
            // against until bankpack has chosen it.
            if (callee.Bank.Kind != IRBankKind.Fixed)
            {
                continue;
            }

            List<CallSite> callers = [.. graph.Functions.Keys
                .SelectMany(graph.From)
                .Where(s => string.Equals(s.Callee, name, StringComparison.Ordinal))];

            if (callers.Count == 0)
            {
                continue;
            }

            var callerBanks = callers
                .Select(s => graph.Functions[s.Caller].Bank)
                .Distinct()
                .ToList();

            // Only a single pinned caller bank is worth reporting, and it must not
            // be bank 0: suggesting that banked code move into the resident bank
            // would be advising a developer to undo the [Bank] they wrote, and
            // bank 0 is the 16 KB banking exists to protect. An automatic caller
            // has no bank to name yet.
            if (callerBanks.Count != 1
                || callerBanks[0] == callee.Bank
                || callerBanks[0].Kind != IRBankKind.Fixed)
            {
                continue;
            }

            diagnostics.Report(
                GBDiagnostics.BankGrouping,
                callee.Span,
                graph.DisplayName(name),
                callee.Bank.ToString(),
                callerBanks[0].ToString(),
                callers.Count);
        }
    }

    /// <summary>
    /// What the estimate could not account for, appended to the message.
    /// </summary>
    /// <remarks>
    /// A loader whose length is a runtime value cannot be costed. Saying so is
    /// the difference between an estimate and a fiction, and it belongs in the
    /// message rather than the help because it is a fact about this particular
    /// number rather than about the diagnostic.
    /// </remarks>
    private static string Caveat(bool partial) =>
        partial ? " That excludes the copying inside the loaders it calls." : string.Empty;

    private static string Format(int cycles) =>
        Sm83CostTable.RoundForDisplay(cycles).ToString("N0", CultureInfo.InvariantCulture);
}
