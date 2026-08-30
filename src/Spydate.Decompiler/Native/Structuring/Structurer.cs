using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Structuring;

/// <summary>
/// Turns the block graph of a lifted function into <c>if</c> / <c>else</c> / loop structure.
///
/// The shape comes from two dominance relations: a two-way branch is joined at its immediate
/// post-dominator, and a loop is a back edge to a block that dominates its source. Everything else
/// - irreducible regions, jumps that leave a loop sideways, tail jumps out of the function - keeps a
/// <c>goto</c>. That fallback is what makes the result trustworthy: a block is emitted in exactly one
/// place, control never falls out of a region except through the follow node the region was built
/// around, and blocks that no structure reached are appended at the end rather than dropped.
/// </summary>
public sealed class Structurer
{
    /// <summary>Nesting depth after which further regions are emitted as gotos, so deep graphs cannot blow the stack.</summary>
    private const int MaxDepth = 96;

    private readonly Cfg _cfg;
    private readonly Dominance _dom;
    private readonly Dominance _pdom;
    private readonly Dictionary<int, LoopInfo> _loops = new();
    private readonly bool[] _emitted;
    private int _depth;

    private Structurer(IrFunction function)
    {
        _cfg = Cfg.Build(function);
        _dom = Dominance.Compute(_cfg.Successors, _cfg.Predecessors, 0, _cfg.ReversePostOrder, _cfg.RpoNumber);
        _pdom = Dominance.ComputePost(_cfg);
        _emitted = new bool[_cfg.Count];
        FindLoops();
        ClassifyLoops();
    }

    public static CStmt Structure(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return function.Blocks.Count == 0 ? CSeq.Empty : new Structurer(function).Run();
    }

    private sealed class LoopInfo
    {
        public required int Header;
        public HashSet<int> Body { get; } = new();
        public List<int> Latches { get; } = new();
        public CLoopKind Kind { get; set; } = CLoopKind.Forever;
        public IrExpr? Condition { get; set; }
        /// <summary>Block after the loop, or -1 when the loop is left only by returns and gotos.</summary>
        public int Follow { get; set; } = -1;
        /// <summary>For <see cref="CLoopKind.DoWhile"/>, the block whose trailing branch became the test.</summary>
        public int ConditionBlock { get; set; } = -1;
    }

    /// <summary>
    /// What the region currently being emitted is nested in. <paramref name="Follow"/> is the block the
    /// region falls out to; <paramref name="BreakTarget"/> is set inside a switch arm, where <c>break</c>
    /// leaves the switch rather than the enclosing loop.
    /// </summary>
    private readonly record struct Ctx(LoopInfo? Loop, int Follow, int BreakTarget = -1);

    private enum EdgeKind
    {
        Inline,
        Fallout,
        Break,
        Continue,
        Goto,
    }

    // ------------------------------------------------------------------
    // Loops
    // ------------------------------------------------------------------

    private void FindLoops()
    {
        foreach (int node in _cfg.ReversePostOrder)
        {
            foreach (int succ in _cfg.Successors[node])
            {
                // A back edge is one whose target dominates its source; its target heads a natural loop.
                if (!_dom.Dominates(succ, node))
                {
                    continue;
                }

                if (!_loops.TryGetValue(succ, out var loop))
                {
                    loop = new LoopInfo { Header = succ };
                    loop.Body.Add(succ);
                    _loops[succ] = loop;
                }

                loop.Latches.Add(node);
                AddNaturalLoopBody(loop, node);
            }
        }
    }

    /// <summary>Everything that reaches <paramref name="latch"/> without passing the header belongs to the loop.</summary>
    private void AddNaturalLoopBody(LoopInfo loop, int latch)
    {
        var stack = new Stack<int>();
        if (loop.Body.Add(latch))
        {
            stack.Push(latch);
        }

        while (stack.Count > 0)
        {
            foreach (int pred in _cfg.Predecessors[stack.Pop()])
            {
                if (loop.Body.Add(pred))
                {
                    stack.Push(pred);
                }
            }
        }
    }

    private void ClassifyLoops()
    {
        foreach (var loop in _loops.Values)
        {
            if (TryClassifyWhile(loop) || TryClassifyDoWhile(loop))
            {
                continue;
            }

            loop.Kind = CLoopKind.Forever;
            loop.Follow = PickExit(loop);
        }
    }

    /// <summary>A header that holds nothing but its test becomes <c>while (cond)</c>.</summary>
    private bool TryClassifyWhile(LoopInfo loop)
    {
        var statements = _cfg.Blocks[loop.Header].Statements;
        int term = TerminatorIndex(statements);
        if (term < 0 || statements[term] is not IrBranch branch)
        {
            return false;
        }

        for (int i = 0; i < term; i++)
        {
            if (statements[i] is not (IrNop or IrComment))
            {
                return false; // work before the test would only run on the first iteration
            }
        }

        int target = _cfg.IndexOf(branch.TargetVa);
        int fallthrough = _cfg.IndexOf(branch.FallthroughVa);
        if (target < 0 || fallthrough < 0)
        {
            return false;
        }

        if (loop.Body.Contains(target) && !loop.Body.Contains(fallthrough))
        {
            loop.Kind = CLoopKind.While;
            loop.Condition = branch.Condition;
            loop.Follow = fallthrough;
            return true;
        }

        if (loop.Body.Contains(fallthrough) && !loop.Body.Contains(target))
        {
            loop.Kind = CLoopKind.While;
            loop.Condition = CStmts.Invert(branch.Condition);
            loop.Follow = target;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A latch that post-dominates the header ends every iteration, so its trailing branch can become a
    /// <c>do { } while (cond)</c> test. A latch nested in an inner loop is rejected: the test would then
    /// sit outside the code it belongs to.
    /// </summary>
    private bool TryClassifyDoWhile(LoopInfo loop)
    {
        foreach (int latch in loop.Latches)
        {
            if (!_pdom.Dominates(latch, loop.Header) || IsInsideNestedLoop(loop, latch))
            {
                continue;
            }

            var statements = _cfg.Blocks[latch].Statements;
            int term = TerminatorIndex(statements);
            if (term < 0 || statements[term] is not IrBranch branch)
            {
                continue;
            }

            int target = _cfg.IndexOf(branch.TargetVa);
            int fallthrough = _cfg.IndexOf(branch.FallthroughVa);
            if (target < 0 || fallthrough < 0)
            {
                continue;
            }

            if (target == loop.Header && !loop.Body.Contains(fallthrough))
            {
                loop.Kind = CLoopKind.DoWhile;
                loop.Condition = branch.Condition;
                loop.Follow = fallthrough;
                loop.ConditionBlock = latch;
                return true;
            }

            if (fallthrough == loop.Header && !loop.Body.Contains(target))
            {
                loop.Kind = CLoopKind.DoWhile;
                loop.Condition = CStmts.Invert(branch.Condition);
                loop.Follow = target;
                loop.ConditionBlock = latch;
                return true;
            }
        }

        return false;
    }

    private bool IsInsideNestedLoop(LoopInfo loop, int node)
        => _loops.Values.Any(other => !ReferenceEquals(other, loop) && other.Header != loop.Header
                                      && loop.Body.Contains(other.Header) && other.Body.Contains(node));

    /// <summary>The block the loop leaves to: the most-used exit target, earliest in layout on a tie.</summary>
    private int PickExit(LoopInfo loop)
    {
        var counts = new Dictionary<int, int>();
        foreach (int node in loop.Body)
        {
            foreach (int succ in _cfg.Successors[node])
            {
                if (!loop.Body.Contains(succ))
                {
                    counts[succ] = counts.GetValueOrDefault(succ) + 1;
                }
            }
        }

        int best = -1;
        int bestCount = 0;
        foreach (var (node, count) in counts)
        {
            if (count > bestCount || (count == bestCount && best >= 0 && _cfg.Va(node) < _cfg.Va(best)))
            {
                best = node;
                bestCount = count;
            }
        }

        return best;
    }

    // ------------------------------------------------------------------
    // Emission
    // ------------------------------------------------------------------

    private CStmt Run()
    {
        var items = new List<CStmt>();
        var top = new Ctx(null, -1);
        EmitInto(items, 0, top);

        // Blocks no structure reached (unreachable code, or targets of a goto we could not fold in) are
        // appended in address order so nothing is silently dropped.
        for (int i = 0; i < _cfg.Count; i++)
        {
            if (!_emitted[i])
            {
                EmitInto(items, i, top);
            }
        }

        return CStmts.Sequence(items);
    }

    /// <summary>Emits <paramref name="start"/> and everything that follows it inline, until the region ends.</summary>
    private void EmitInto(List<CStmt> items, int start, Ctx ctx)
    {
        int node = start;
        while (node >= 0)
        {
            if (_loops.TryGetValue(node, out var loop) && !_emitted[node])
            {
                items.Add(new CLabel(_cfg.Va(node)));
                items.Add(EmitLoop(loop));
                node = ResolveNext(items, loop.Follow, ctx);
                continue;
            }

            _emitted[node] = true;
            items.Add(new CLabel(_cfg.Va(node)));
            node = EmitBlock(items, node, ctx);
        }
    }

    private CStmt EmitLoop(LoopInfo loop)
    {
        _emitted[loop.Header] = true;
        var inner = new Ctx(loop, -1);
        var items = new List<CStmt>();

        if (loop.Kind == CLoopKind.While)
        {
            // The header is nothing but the test, so the body starts at whichever successor stays inside.
            var branch = (IrBranch)_cfg.Blocks[loop.Header].Statements[TerminatorIndex(_cfg.Blocks[loop.Header].Statements)];
            int target = _cfg.IndexOf(branch.TargetVa);
            int body = loop.Body.Contains(target) ? target : _cfg.IndexOf(branch.FallthroughVa);
            int next = ResolveNext(items, body, inner);
            if (next >= 0)
            {
                EmitInto(items, next, inner);
            }
        }
        else
        {
            int next = EmitBlock(items, loop.Header, inner);
            if (next >= 0)
            {
                EmitInto(items, next, inner);
            }
        }

        return new CLoop(loop.Kind, loop.Condition, CStmts.Sequence(Trim(items)), ConditionVa(loop));
    }

    /// <summary>Where the loop's test lives, so the <c>while</c> line carries an address like any other.</summary>
    private ulong ConditionVa(LoopInfo loop)
    {
        int block = loop.Kind switch
        {
            CLoopKind.While => loop.Header,
            CLoopKind.DoWhile => loop.ConditionBlock,
            _ => -1,
        };

        if (block < 0)
        {
            return 0;
        }

        var statements = _cfg.Blocks[block].Statements;
        int term = TerminatorIndex(statements);
        return term >= 0 ? statements[term].Va : 0;
    }

    /// <summary>Drops a <c>continue</c> that only says "go round again" at the end of a loop body.</summary>
    private static List<CStmt> Trim(List<CStmt> items)
    {
        while (items.Count > 0 && items[^1] is CContinue)
        {
            items.RemoveAt(items.Count - 1);
        }

        return items;
    }

    /// <summary>
    /// Emits one block's statements and resolves the edge that leaves it. Returns the next block to emit
    /// inline, or -1 when the region ends here.
    /// </summary>
    private int EmitBlock(List<CStmt> items, int node, Ctx ctx)
    {
        var statements = _cfg.Blocks[node].Statements;
        int term = TerminatorIndex(statements);
        var terminator = term >= 0 ? statements[term] : null;
        int limit = terminator is IrBranch or IrGoto or IrSwitch ? term : statements.Count;

        for (int i = 0; i < limit; i++)
        {
            if (statements[i] is not IrNop)
            {
                items.Add(new CRaw(statements[i]));
            }
        }

        switch (terminator)
        {
            case IrReturn:
                return -1;

            case IrBranch when ctx.Loop is { ConditionBlock: var test } && test == node:
                return -1; // consumed as the do-while test

            case IrGoto jump:
                return ResolveNext(items, _cfg.IndexOf(jump.TargetVa), ctx, jump.TargetVa);

            case IrBranch branch:
                return EmitIf(items, node, branch, ctx);

            case IrSwitch dispatch:
                return EmitSwitch(items, node, dispatch, ctx);
        }

        // No terminator: either a fallthrough, or a block whose exit the disassembler could not follow.
        var successors = _cfg.Successors[node];
        return successors.Length == 1 ? ResolveNext(items, successors[0], ctx) : -1;
    }

    private int EmitIf(List<CStmt> items, int node, IrBranch branch, Ctx ctx)
    {
        int follow = _pdom.Idom(node);
        if (follow >= _cfg.Count || follow == node)
        {
            follow = -1; // every path from here leaves the function
        }

        var armCtx = ctx with { Follow = follow };
        _depth++;
        var thenArm = EmitRegion(_cfg.IndexOf(branch.TargetVa), armCtx, branch.TargetVa);
        var elseArm = EmitRegion(_cfg.IndexOf(branch.FallthroughVa), armCtx, branch.FallthroughVa);
        _depth--;

        if (CStmts.IsEmpty(thenArm) && CStmts.IsEmpty(elseArm))
        {
            // Both sides fall straight through to the join; the test decides nothing.
        }
        else if (CStmts.IsEmpty(thenArm))
        {
            items.Add(new CIf(CStmts.Invert(branch.Condition), elseArm, null, branch.Va));
        }
        else
        {
            items.Add(new CIf(branch.Condition, thenArm, CStmts.IsEmpty(elseArm) ? null : elseArm, branch.Va));
        }

        // An arm that prints nothing is still a block, and a goto elsewhere may target it. Its label
        // moves ahead of the join, where control would have arrived anyway.
        if (CStmts.IsEmpty(thenArm))
        {
            HoistLabels(thenArm, items);
        }

        if (CStmts.IsEmpty(elseArm))
        {
            HoistLabels(elseArm, items);
        }

        return ResolveNext(items, follow, ctx);
    }

    private static void HoistLabels(CStmt arm, List<CStmt> items)
    {
        foreach (var label in CStmts.Descendants(arm).OfType<CLabel>())
        {
            items.Add(label);
        }
    }

    /// <summary>
    /// A recovered switch. Indices that share a target share an arm, arms are emitted in address order so
    /// that falling off the end of one lands in the next, and the join point becomes what <c>break</c>
    /// means inside them.
    /// </summary>
    private int EmitSwitch(List<CStmt> items, int node, IrSwitch dispatch, Ctx ctx)
    {
        int follow = _pdom.Idom(node);
        if (follow >= _cfg.Count || follow == node)
        {
            follow = -1;
        }

        var groups = new List<(int Target, List<int> Labels)>();
        var byTarget = new Dictionary<int, List<int>>();
        for (int i = 0; i < dispatch.Targets.Count; i++)
        {
            int target = _cfg.IndexOf(dispatch.Targets[i]);
            if (target < 0)
            {
                continue; // an entry that left the function; the table read stops there anyway
            }

            if (!byTarget.TryGetValue(target, out var labels))
            {
                labels = new List<int>();
                byTarget[target] = labels;
                groups.Add((target, labels));
            }

            labels.Add(i);
        }

        if (groups.Count == 0)
        {
            return ResolveNext(items, follow, ctx);
        }

        groups.Sort((a, b) => _cfg.Va(a.Target).CompareTo(_cfg.Va(b.Target)));

        _depth++;
        var cases = new List<CCase>(groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            // The next arm is this one's follow, which is what makes a run-off-the-end fall through.
            int next = i + 1 < groups.Count ? groups[i + 1].Target : -1;
            var armCtx = ctx with { Follow = next, BreakTarget = follow };
            cases.Add(new CCase(groups[i].Labels, EmitRegion(groups[i].Target, armCtx, _cfg.Va(groups[i].Target))));
        }

        _depth--;

        items.Add(new CSwitch(dispatch.Value, cases, dispatch.Va));
        return ResolveNext(items, follow, ctx);
    }

    /// <summary>One arm of a conditional: everything reachable from <paramref name="target"/> up to the join.</summary>
    private CStmt EmitRegion(int target, Ctx ctx, ulong targetVa)
    {
        var items = new List<CStmt>();
        int next = ResolveNext(items, target, ctx, targetVa);
        if (next >= 0)
        {
            EmitInto(items, next, ctx);
        }

        return CStmts.Sequence(items);
    }

    /// <summary>
    /// Decides how control gets to <paramref name="target"/>: inline, by falling out of the region, or with
    /// a keyword. Returns the block to keep emitting, or -1 when the region ends.
    /// </summary>
    private int ResolveNext(List<CStmt> items, int target, Ctx ctx, ulong externalVa = 0)
    {
        var (kind, statement) = Resolve(target, ctx, externalVa);
        if (statement is not null)
        {
            items.Add(statement);
        }

        return kind == EdgeKind.Inline ? target : -1;
    }

    private (EdgeKind Kind, CStmt? Statement) Resolve(int target, Ctx ctx, ulong externalVa)
    {
        if (target < 0)
        {
            // A jump out of the function: a tail call, or a target discovery never decoded.
            return externalVa == 0
                ? (EdgeKind.Fallout, null)
                : (EdgeKind.Goto, new CGoto(externalVa, External: true));
        }

        if (target == ctx.Follow)
        {
            return (EdgeKind.Fallout, null);
        }

        if (ctx.BreakTarget >= 0 && target == ctx.BreakTarget)
        {
            return (EdgeKind.Break, new CBreak());
        }

        if (ctx.Loop is { } loop)
        {
            if (target == loop.Header)
            {
                return (EdgeKind.Continue, new CContinue());
            }

            if (target == loop.Follow)
            {
                // Inside a switch arm, `break` would only leave the switch.
                return ctx.BreakTarget >= 0
                    ? (EdgeKind.Goto, new CGoto(_cfg.Va(target)))
                    : (EdgeKind.Break, new CBreak());
            }

            if (!loop.Body.Contains(target))
            {
                // Leaving the loop sideways: inlining here would put the target inside the body.
                return (EdgeKind.Goto, new CGoto(_cfg.Va(target)));
            }
        }

        if (_emitted[target] || _depth >= MaxDepth)
        {
            return (EdgeKind.Goto, new CGoto(_cfg.Va(target)));
        }

        return (EdgeKind.Inline, null);
    }

    /// <summary>Index of the branch, jump or return that ends the block, ignoring trailing nops.</summary>
    private static int TerminatorIndex(List<IrStmt> statements)
    {
        for (int i = statements.Count - 1; i >= 0; i--)
        {
            switch (statements[i])
            {
                case IrNop:
                    continue;
                case IrBranch or IrGoto or IrReturn or IrSwitch:
                    return i;
                default:
                    return -1;
            }
        }

        return -1;
    }
}
