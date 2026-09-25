using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>Hands out label numbers for one method, so regions structured on their own never share one.</summary>
internal sealed class JavaLabels
{
    private int _next;

    public int Next() => ++_next;
}

/// <summary>Thrown when a graph is not one the Java structurer can express without a <c>goto</c>: the caller falls back.</summary>
internal sealed class NotStructurableException(string reason) : Exception(reason);

/// <summary>
/// Structures a method's block graph into Java with no <c>goto</c>, by the dominator-tree translation of Ramsey's
/// "Beyond Relooper" (ICFP 2022), with Java's labelled blocks standing in for WebAssembly's.
///
/// Java has no <c>goto</c>, so javac's graphs are reducible, and a reducible graph needs none: every block is
/// emitted once, at the place of its immediate dominator. A block reached from several places (a join) gets a
/// labelled block around the code of its dominator, ending just before it, so every jump to it is a
/// <c>break</c>; a loop header gets a labelled loop, so every jump back is a <c>continue</c>; any other block is
/// written in place where its one predecessor jumps to it. Blocks that leave a loop go after it, not inside it,
/// so the loop can later read as <c>while (c)</c>. The output is full of labels and redundant jumps; the shaping
/// passes remove them, and what remains is what the code needs.
///
/// A region collapsed into one block (a try with its handlers) carries <see cref="JExit"/> placeholders for its
/// ways out; they are resolved here like any other jump, and every block they reach is given a labelled block so
/// that code outside the try is never written inside it. An exit to a block outside this graph stays a
/// <see cref="JExit"/> for the graph around it to resolve.
///
/// An irreducible graph — only obfuscators make them — or one with unreachable blocks raises
/// <see cref="NotStructurableException"/>, and the method is structured the old way, with gotos.
/// </summary>
internal sealed class JavaStructurer
{
    /// <summary>Dominator-tree depth past which the method is given to the old structurer, whose own caps bound it.</summary>
    private const int MaxDepth = 4000;

    private readonly Cfg _cfg;
    private readonly Dominance _dom;
    private readonly JavaLabels _labels;
    private readonly List<int>[] _children;
    private readonly bool[] _merge;
    private readonly Dictionary<int, HashSet<int>> _loops = new();
    private readonly Dictionary<int, HashSet<int>> _fallInto = new();
    private int _depth;

    private enum FrameKind
    {
        /// <summary>A loop headed by the node: <c>continue</c> reaches it.</summary>
        Loop,

        /// <summary>A block followed by the node: <c>break</c> reaches it.</summary>
        Block,
    }

    private sealed record Frame(FrameKind Kind, int Node, int Label, Frame? Next);

    private JavaStructurer(IrFunction function, JavaLabels labels)
    {
        _labels = labels;
        _cfg = Cfg.Build(function);
        for (int i = 0; i < _cfg.Count; i++)
        {
            if (!_cfg.IsReachable(i))
            {
                throw new NotStructurableException($"block {_cfg.Va(i):X4} is not reached from the entry");
            }
        }

        _dom = Dominance.Compute(_cfg.Successors, _cfg.Predecessors, 0, _cfg.ReversePostOrder, _cfg.RpoNumber);
        _children = new List<int>[_cfg.Count];
        for (int i = 0; i < _cfg.Count; i++)
        {
            _children[i] = [];
        }

        foreach (int node in _cfg.ReversePostOrder)
        {
            if (node != 0)
            {
                _children[_dom.Idom(node)].Add(node);
            }
        }

        // Reducible means every edge that goes back in reverse post-order goes to a block that dominates its source.
        var forwardPredecessors = new int[_cfg.Count];
        _merge = new bool[_cfg.Count];
        for (int u = 0; u < _cfg.Count; u++)
        {
            bool region = IsRegion(u);
            foreach (int v in _cfg.Successors[u])
            {
                if (IsBackward(u, v))
                {
                    if (!_dom.Dominates(v, u))
                    {
                        throw new NotStructurableException($"the edge {_cfg.Va(u):X4} → {_cfg.Va(v):X4} enters a loop other than at its head");
                    }

                    AddToLoop(v, u);
                    continue;
                }

                forwardPredecessors[v]++;

                // A try's exits are always jumps: code after a try is never written inside it.
                if (region)
                {
                    _merge[v] = true;
                }
            }
        }

        for (int v = 0; v < _cfg.Count; v++)
        {
            _merge[v] |= forwardPredecessors[v] >= 2;
        }

        for (int x = 0; x < _cfg.Count; x++)
        {
            if (FallThroughTargets(x) is { Count: > 0 } targets)
            {
                _fallInto[x] = targets;
            }
        }
    }

    /// <summary>
    /// The case targets of a switch that the arm before them runs into: joins reached only from the switch and from
    /// code the previous arm (in address order) dominates. Each is written as its own arm, which the previous arm
    /// falls into, instead of after the switch behind a labelled block.
    /// </summary>
    private HashSet<int> FallThroughTargets(int x)
    {
        var fall = new HashSet<int>();
        var statements = _cfg.Blocks[x].Statements;
        if (statements.LastOrDefault(st => st is not IrNop) is not IrSwitch dispatch)
        {
            return fall;
        }

        var targets = dispatch.Targets.Select(_cfg.IndexOf).Where(t => t >= 0).Distinct().OrderBy(_cfg.Va).ToList();
        for (int i = 1; i < targets.Count; i++)
        {
            int target = targets[i];
            int previous = targets[i - 1];
            if (!_merge[target] || _dom.Idom(target) != x || (_loops.TryGetValue(x, out var body) && !body.Contains(target)))
            {
                continue;
            }

            if (_cfg.Predecessors[target].All(p => p == x || IsBackward(p, target) || _dom.Dominates(previous, p)))
            {
                fall.Add(target);
            }
        }

        return fall;
    }

    /// <summary>The method (or region) as structured Java, jumps out of it left as <see cref="JExit"/>.</summary>
    public static CStmt Structure(IrFunction function, JavaLabels labels)
    {
        if (function.Blocks.Count == 0)
        {
            return CSeq.Empty;
        }

        // Kotlin's coroutines resume inside loops; copying the code up to the loop's head gives each loop one way in.
        JavaSplitter.MakeReducible(function);
        var structurer = new JavaStructurer(function, labels);
        var items = new List<CStmt>();
        structurer.DoTree(0, null, items);
        return JavaTree.Sequence(items);
    }

    private bool IsBackward(int from, int to) => _cfg.RpoNumber[to] <= _cfg.RpoNumber[from];

    private bool IsRegion(int node) => _cfg.Blocks[node].Statements is [JRegion];

    private void AddToLoop(int header, int latch)
    {
        if (!_loops.TryGetValue(header, out var body))
        {
            body = [header];
            _loops[header] = body;
        }

        var stack = new Stack<int>();
        if (body.Add(latch))
        {
            stack.Push(latch);
        }

        while (stack.Count > 0)
        {
            foreach (int pred in _cfg.Predecessors[stack.Pop()])
            {
                if (body.Add(pred))
                {
                    stack.Push(pred);
                }
            }
        }
    }

    /// <summary>A block and everything it dominates, placed as its own code followed by the joins it dominates.</summary>
    private void DoTree(int x, Frame? context, List<CStmt> into)
    {
        if (++_depth > MaxDepth)
        {
            throw new NotStructurableException("the method nests too deeply to structure");
        }

        // Children in decreasing reverse post-order: the first is the outermost block, whose join comes last.
        if (_loops.TryGetValue(x, out var body))
        {
            // What leaves the loop goes after it, each in a block the loop breaks out of.
            var outside = _children[x].Where(c => !body.Contains(c)).OrderByDescending(c => _cfg.RpoNumber[c]).ToList();
            var inside = _children[x].Where(c => body.Contains(c) && _merge[c] && !FallsInto(x, c)).OrderByDescending(c => _cfg.RpoNumber[c]).ToList();
            Within(outside, 0, context, into, (outer, list) =>
            {
                int label = _labels.Next();
                var loopBody = new List<CStmt>();
                Within(inside, 0, new Frame(FrameKind.Loop, x, label, outer), loopBody, (ctx, l) => Code(x, ctx, l));
                list.Add(new JLoop(label, CLoopKind.Forever, null, JavaTree.Sequence(loopBody), _cfg.Va(x)));
            });
        }
        else
        {
            var joins = _children[x].Where(c => _merge[c] && !FallsInto(x, c)).OrderByDescending(c => _cfg.RpoNumber[c]).ToList();
            Within(joins, 0, context, into, (ctx, l) => Code(x, ctx, l));
        }

        _depth--;
    }

    private bool FallsInto(int x, int target) => _fallInto.TryGetValue(x, out var fall) && fall.Contains(target);

    /// <summary><c>B: { ...core... } join-code</c> for each join, outermost first.</summary>
    private void Within(List<int> joins, int index, Frame? context, List<CStmt> into, Action<Frame?, List<CStmt>> core)
    {
        if (index == joins.Count)
        {
            core(context, into);
            return;
        }

        int join = joins[index];
        int label = _labels.Next();
        var inner = new List<CStmt>();
        Within(joins, index + 1, new Frame(FrameKind.Block, join, label, context), inner, core);
        into.Add(new JBlock(label, JavaTree.Sequence(inner)));
        DoTree(join, context, into);
    }

    /// <summary>A block's own statements and the transfer that ends it.</summary>
    private void Code(int x, Frame? context, List<CStmt> into)
    {
        var block = _cfg.Blocks[x];
        if (block.Statements is [JRegion region])
        {
            into.Add(ResolveExits(x, region.Body, context));
            return;
        }

        var statements = block.Statements;
        int last = statements.FindLastIndex(s => s is not IrNop);
        var terminator = last >= 0 ? statements[last] : null;
        bool transfers = terminator is IrGoto or IrBranch or IrSwitch;
        int limit = transfers ? last : statements.Count;
        for (int i = 0; i < limit; i++)
        {
            if (statements[i] is not IrNop)
            {
                into.Add(new CRaw(statements[i]));
            }
        }

        switch (terminator)
        {
            case IrGoto jump:
                DoBranch(x, jump.TargetVa, context, into);
                return;
            case IrBranch branch when branch.TargetVa == branch.FallthroughVa:
                DoBranch(x, branch.TargetVa, context, into);
                return;
            case IrBranch branch:
            {
                // javac jumps past the then-part on the negated test, so the fall-through is the then-part and the
                // negation of the jump's test is the source's own — exactly, NaN included, since the compiler picked
                // the comparison that makes the jump right.
                var then = new List<CStmt>();
                var otherwise = new List<CStmt>();
                DoBranch(x, branch.FallthroughVa, context, then);
                DoBranch(x, branch.TargetVa, context, otherwise);
                into.Add(new CIf(JavaShaping.Not(branch.Condition), JavaTree.Sequence(then), JavaTree.Sequence(otherwise), branch.Va));
                return;
            }

            case IrSwitch dispatch:
                into.Add(Switch(x, dispatch, context));
                return;
            case IrReturn or JThrow:
                return;
        }

        // Runs into the next block, or ends where the code could not be followed.
        if (block.Successors.Count == 1)
        {
            DoBranch(x, block.Successors[0], context, into);
        }
    }

    private JSwitch Switch(int x, IrSwitch dispatch, Frame? context)
    {
        var groups = new List<(ulong Target, List<int> Labels)>();
        for (int i = 0; i < dispatch.Targets.Count; i++)
        {
            int at = groups.FindIndex(g => g.Target == dispatch.Targets[i]);
            if (at < 0)
            {
                groups.Add((dispatch.Targets[i], [i]));
            }
            else
            {
                groups[at].Labels.Add(i);
            }
        }

        // Arms in the order their code comes, so the layout reads like the source did.
        groups.Sort((a, b) => a.Target.CompareTo(b.Target));
        int label = _labels.Next();
        var cases = new List<CCase>(groups.Count);
        for (int g = 0; g < groups.Count; g++)
        {
            var (target, labels) = groups[g];
            var arm = new List<CStmt>();

            // An arm the next one's code is run into from: a block whose break is falling off the arm.
            int next = g + 1 < groups.Count ? _cfg.IndexOf(groups[g + 1].Target) : -1;
            int fallLabel = next >= 0 && FallsInto(x, next) ? _labels.Next() : 0;
            var armContext = fallLabel != 0 ? new Frame(FrameKind.Block, next, fallLabel, context) : context;

            int index = _cfg.IndexOf(target);
            if (index >= 0 && FallsInto(x, index))
            {
                DoTree(index, armContext, arm);
            }
            else
            {
                DoBranch(x, target, armContext, arm);
            }

            if (fallLabel != 0)
            {
                arm = [new JBlock(fallLabel, JavaTree.Sequence(arm))];
            }

            cases.Add(new CCase(labels, JavaTree.Sequence(arm)));
        }

        return new JSwitch(label, dispatch.Value, cases, dispatch.Va) { DefaultLabel = dispatch.Targets.Count - 1 };
    }

    /// <summary>How control gets from <paramref name="x"/> to the block at <paramref name="targetVa"/>.</summary>
    private void DoBranch(int x, ulong targetVa, Frame? context, List<CStmt> into)
    {
        if (Jump(x, targetVa, context) is { } jump)
        {
            into.Add(jump);
            return;
        }

        int target = _cfg.IndexOf(targetVa);
        if (_merge[target])
        {
            throw new NotStructurableException($"the join at {targetVa:X4} has no block around its jumps");
        }

        // Reached only from here: its code goes here.
        DoTree(target, context, into);
    }

    /// <summary>The jump that reaches a block, or null when it is written in place.</summary>
    private CStmt? Jump(int x, ulong targetVa, Frame? context)
    {
        int target = _cfg.IndexOf(targetVa);
        if (target < 0)
        {
            return new JExit(targetVa);
        }

        if (IsBackward(x, target))
        {
            return Find(context, FrameKind.Loop, target) is { } loop
                ? new JContinue(loop.Label)
                : throw new NotStructurableException($"the jump back to {targetVa:X4} is not inside its loop");
        }

        return Find(context, FrameKind.Block, target) is { } block ? new JBreak(block.Label) : null;
    }

    /// <summary>A try's placeholders for its ways out, each made the jump that leaves it from here.</summary>
    private CStmt ResolveExits(int x, CStmt body, Frame? context) => JavaTree.Rewrite(body, statement =>
    {
        if (statement is not JExit exit)
        {
            return statement;
        }

        return Jump(x, exit.Target, context) ?? throw new NotStructurableException($"the exit to {exit.Target:X4} has no block around it");
    });

    private static Frame? Find(Frame? context, FrameKind kind, int node)
    {
        for (var frame = context; frame is not null; frame = frame.Next)
        {
            if (frame.Kind == kind && frame.Node == node)
            {
                return frame;
            }
        }

        return null;
    }
}
