using Spydate.Core.Jvm;
using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Structures a lifted method, try blocks included, with the native <see cref="Structurer"/> unchanged.
///
/// The structurer knows only ordinary edges; a handler is reached by none. So each try is handled as a region of
/// its own, innermost first: its protected blocks are structured as one small graph, each handler's blocks as
/// another, both with the block they rejoin at added as an empty exit so leaving the region reads as falling out
/// of it. The region then collapses into a single block holding the result, and the enclosing graph — the next
/// try out, and finally the method — is structured with it in place. Handlers are found the way a compiler lays
/// them out: after the protected range, each running until the next handler or the join.
///
/// What cannot be made a region — a handler that is also inside its own range, a range with no block of its own,
/// regions nested past a sane depth — is left as it is; its handler code is still printed, as blocks the method
/// never reaches by an ordinary edge, so nothing is dropped.
/// </summary>
internal static class JavaRegions
{
    private const int MaxTries = 256;

    private sealed record TryRegion(int Start, int End, List<(string? Type, int Handler)> Catches)
    {
        public Dictionary<int, List<string>> Alternatives { get; init; } = [];
    }

    /// <summary>
    /// Structures the method. Unless <paramref name="legacy"/>, with the <see cref="JavaStructurer"/>, which throws
    /// <see cref="NotStructurableException"/> when the graph needs a goto; legacy is the native structurer, which
    /// always succeeds and keeps a goto for what it cannot express.
    /// </summary>
    public static CStmt Structure(LiftedMethod lifted, bool legacy = false)
    {
        var function = lifted.Function;
        var labels = legacy ? null : new JavaLabels();
        foreach (var region in Regions(lifted.Handlers).Take(MaxTries))
        {
            Collapse(function, region, lifted, labels);
        }

        if (labels is null)
        {
            return Structurer.Structure(function);
        }

        var body = JavaStructurer.Structure(function, labels);
        if (JavaTree.Descendants(body).OfType<JExit>().FirstOrDefault() is { } stray)
        {
            throw new NotStructurableException($"a try leaves to {stray.Target:X4}, which is inside another region");
        }

        return body;
    }

    /// <summary>
    /// The exception table as try regions. A handler's entries are joined into one range — a <c>finally</c>
    /// handler's entries skip the copies of its own code between them — and handlers with the same range share a
    /// try, in table order. An entry whose handler lies inside its own range guards the handler itself, which
    /// javac emits for <c>finally</c>; it adds nothing to the structure and is skipped. Latest start first, the
    /// shorter of two with one start first, so an inner try collapses before the one around it — whether it sits in
    /// that try's body or in one of its handlers, which come after the body.
    /// </summary>
    private static List<TryRegion> Regions(IReadOnlyList<ExceptionHandler> handlers)
    {
        var byHandler = new Dictionary<int, (int Start, int End, string? Type)>();
        var alternatives = new Dictionary<int, List<string>>();
        var order = new List<int>();
        foreach (var handler in handlers)
        {
            if (handler.EndPc <= handler.StartPc || (handler.HandlerPc >= handler.StartPc && handler.HandlerPc < handler.EndPc))
            {
                continue;
            }

            if (byHandler.TryGetValue(handler.HandlerPc, out var range))
            {
                byHandler[handler.HandlerPc] = (Math.Min(range.Start, handler.StartPc), Math.Max(range.End, handler.EndPc), range.Type);

                // Another type for the same handler is a multi-catch.
                if (range.Type is not null && handler.CatchType is { } other && other != range.Type)
                {
                    var list = alternatives.TryGetValue(handler.HandlerPc, out var known) ? known : alternatives[handler.HandlerPc] = [];
                    if (!list.Contains(other))
                    {
                        list.Add(other);
                    }
                }
            }
            else
            {
                byHandler[handler.HandlerPc] = (handler.StartPc, handler.EndPc, handler.CatchType);
                order.Add(handler.HandlerPc);
            }
        }

        var regions = new List<TryRegion>();
        foreach (int handler in order)
        {
            var (start, end, type) = byHandler[handler];
            var existing = regions.FirstOrDefault(r => r.Start == start && r.End == end);
            if (existing is null)
            {
                regions.Add(new TryRegion(start, end, [(type, handler)]) { Alternatives = alternatives });
            }
            else
            {
                existing.Catches.Add((type, handler));
            }
        }

        return regions.OrderByDescending(r => r.Start).ThenBy(r => r.End).ToList();
    }

    private static bool Collapse(IrFunction function, TryRegion region, LiftedMethod lifted, JavaLabels? labels)
    {
        var byStart = function.Blocks.ToDictionary(b => (int)b.StartVa);
        if (!byStart.TryGetValue(region.Start, out var entry) || region.Catches.Any(c => !byStart.ContainsKey(c.Handler)))
        {
            function.Warnings.Add($"the try at {region.Start:X4} has no block of its own and is shown unstructured");
            return false;
        }

        var body = function.Blocks.Where(b => (int)b.StartVa >= region.Start && (int)b.StartVa < region.End).ToHashSet();
        var handlerEntries = region.Catches.Select(c => byStart[c.Handler]).ToHashSet();
        if (labels is not null)
        {
            // Only what the try's own entry reaches is its body; anything else in the range belongs to the code around it.
            body = Reach(byStart, entry, b => body.Contains(b), []).ToHashSet();
            Adopt(body, entry, handlerEntries, byStart);
            Evict(body, entry, function);
        }

        if (body.Overlaps(handlerEntries))
        {
            return false;
        }

        // The join: where the protected code goes when it is done, which is past the handlers in javac's layout —
        // the exit the body takes most, preferring one after the range.
        int join = PickJoin(body.SelectMany(b => b.Successors).Select(s => (int)s).Where(s => s >= region.End && !handlerEntries.Any(h => (int)h.StartVa == s)), region);

        var handlerStarts = region.Catches.Select(c => c.Handler).OrderBy(h => h).ToList();
        if (join < 0)
        {
            // A body that only returns or throws: the handlers say where they go instead.
            var exits = new List<int>();
            foreach (var (_, handler) in region.Catches)
            {
                exits.AddRange(Reach(byStart, byStart[handler], b => (int)b.StartVa >= handler, body).SelectMany(b => b.Successors).Select(s => (int)s)
                    .Where(s => s > handlerStarts.Max()));
            }

            join = PickJoin(exits, region);
        }

        var catches = new List<JCatch>();
        var taken = new HashSet<IrBlock>(body);
        var dominates = labels is null ? null : Dominators(function, lifted.Handlers);
        foreach (var (type, handler) in region.Catches)
        {
            // A handler is what its entry dominates once exceptions count as edges: code the normal path also
            // reaches — the method's shared return — is where it rejoins, not part of it. The native structurer
            // still takes the layout's word for it: up to the next handler or the join.
            int next = handlerStarts.Where(h => h > handler).DefaultIfEmpty(int.MaxValue).Min();
            int limit = join > handler ? Math.Min(next, join) : next;
            var start = byStart[handler];
            var blocks = dominates is null
                ? Reach(byStart, start, b => (int)b.StartVa >= handler && (int)b.StartVa < limit, taken)
                : Reach(byStart, start, b => dominates(start, b), taken);
            foreach (var block in blocks)
            {
                taken.Add(block);
            }

            string? variable = BindCatch(byStart[handler], lifted);
            catches.Add(new JCatch(type, variable, StructureSubgraph(blocks, byStart[handler], join, labels))
            {
                Alternatives = region.Alternatives.TryGetValue(handler, out var more) ? more : [],
            });
        }

        var tryBody = StructureSubgraph(body.OrderBy(b => b.StartVa).ToList(), entry, join, labels);

        // The region becomes one block at its start. The native structurer sees it fall out to the join; the Java
        // structurer sees every way out, each a placeholder inside it.
        var replacement = new IrBlock(entry.StartVa);
        replacement.Statements.Add(new JRegion(new JTry(tryBody, catches)) { Va = entry.StartVa });
        if (labels is not null)
        {
            var starts = taken.Select(b => b.StartVa).ToHashSet();
            var exits = taken.SelectMany(b => b.Successors).Where(s => !starts.Contains(s)).Distinct().OrderBy(s => (long)s == join ? 0 : 1).ThenBy(s => s);
            replacement.Successors.AddRange(exits);
        }
        else if (join >= 0)
        {
            replacement.Successors.Add((ulong)join);
        }

        var removed = taken;
        int position = function.Blocks.IndexOf(entry);
        function.Blocks.RemoveAll(removed.Contains);
        function.Blocks.Insert(Math.Min(position, function.Blocks.Count), replacement);

        return true;
    }

    /// <summary>
    /// A block past the range that leads back into the middle of it — a loop's closing jump, which the compiler (or
    /// a shrinker) left outside the protected range — is part of the body: without it the try would have a second
    /// way in. Only blocks that cannot throw are taken, so protecting them changes nothing.
    /// </summary>
    private static void Adopt(HashSet<IrBlock> body, IrBlock entry, HashSet<IrBlock> handlers, Dictionary<int, IrBlock> byStart)
    {
        bool grew = true;
        while (grew)
        {
            grew = false;
            var candidates = body.SelectMany(b => b.Successors).Distinct()
                .Select(v => byStart.GetValueOrDefault((int)v))
                .Where(b => b is not null && !body.Contains(b) && !handlers.Contains(b))
                .ToList();
            foreach (var block in candidates)
            {
                if (block!.Statements.All(CannotThrow)
                    && block.Successors.Any(s => byStart.TryGetValue((int)s, out var target) && body.Contains(target) && !ReferenceEquals(target, entry)))
                {
                    body.Add(block);
                    grew = true;
                }
            }
        }
    }

    /// <summary>
    /// A block of the body that code outside the try also reaches — the shared return a Kotlin coroutine's resume
    /// path jumps to — is a second way in, which a try cannot have. When it cannot throw, protecting it changes
    /// nothing, so it moves out of the body and the try leaves to it instead.
    /// </summary>
    private static void Evict(HashSet<IrBlock> body, IrBlock entry, IrFunction function)
    {
        bool shrank = true;
        while (shrank)
        {
            shrank = false;
            var enteredFromOutside = function.Blocks
                .Where(b => !body.Contains(b))
                .SelectMany(b => b.Successors)
                .ToHashSet();
            foreach (var block in body.Where(b => !ReferenceEquals(b, entry) && enteredFromOutside.Contains(b.StartVa)).ToList())
            {
                if (block.Statements.All(CannotThrow))
                {
                    body.Remove(block);
                    shrank = true;
                }
            }
        }
    }

    private static bool CannotThrow(IrStmt statement) => statement switch
    {
        IrNop or IrComment or IrGoto => true,
        IrReturn { Value: null } => true,
        IrReturn { Value: { } value } => Plain(value),
        IrAssign { Dst: JLocal, Src: var value } => Plain(value),
        IrBranch branch => Plain(branch.Condition),
        _ => false,
    };

    /// <summary>An expression of locals, constants and comparisons between them: evaluating it cannot throw.</summary>
    private static bool Plain(IrExpr expression) => JavaRewrite.PostOrder(expression).All(e => e is JLocal or JConst or IrCondition or IrUnary or JLogical);

    /// <summary>Dominance over the method's blocks with an edge from every protected block to each of its handlers.</summary>
    private static Func<IrBlock, IrBlock, bool> Dominators(IrFunction function, IReadOnlyList<ExceptionHandler> handlers)
    {
        var shadow = new IrFunction(function.EntryVa, "exceptional", 32);
        foreach (var block in function.Blocks)
        {
            var copy = new IrBlock(block.StartVa);
            copy.Successors.AddRange(block.Successors);
            foreach (var handler in handlers)
            {
                if ((int)block.StartVa >= handler.StartPc && (int)block.StartVa < handler.EndPc)
                {
                    copy.Successors.Add((ulong)handler.HandlerPc);
                }
            }

            shadow.Blocks.Add(copy);
        }

        var cfg = Cfg.Build(shadow);
        var dominance = Dominance.Compute(cfg.Successors, cfg.Predecessors, 0, cfg.ReversePostOrder, cfg.RpoNumber);
        return (dominator, block) =>
        {
            int a = cfg.IndexOf(dominator.StartVa);
            int b = cfg.IndexOf(block.StartVa);
            return a >= 0 && b >= 0 && dominance.Dominates(a, b);
        };
    }

    private static int PickJoin(IEnumerable<int> exits, TryRegion region)
    {
        var counts = exits.GroupBy(e => e).Select(g => (Target: g.Key, Count: g.Count())).ToList();
        if (counts.Count == 0)
        {
            return -1;
        }

        return counts.OrderByDescending(c => c.Count).ThenBy(c => c.Target < region.End ? 1 : 0).ThenBy(c => c.Target).First().Target;
    }

    /// <summary>Blocks reachable from <paramref name="from"/> through blocks that pass <paramref name="inside"/> and are not <paramref name="excluded"/>.</summary>
    private static List<IrBlock> Reach(Dictionary<int, IrBlock> byStart, IrBlock from, Func<IrBlock, bool> inside, HashSet<IrBlock> excluded)
    {
        var found = new List<IrBlock>();
        var seen = new HashSet<IrBlock> { from };
        var work = new Stack<IrBlock>();
        work.Push(from);
        while (work.Count > 0)
        {
            var block = work.Pop();
            found.Add(block);
            foreach (ulong succ in block.Successors)
            {
                if (byStart.TryGetValue((int)succ, out var next) && inside(next) && !excluded.Contains(next) && seen.Add(next))
                {
                    work.Push(next);
                }
            }
        }

        return found.OrderBy(b => b.StartVa).ToList();
    }

    /// <summary>
    /// The handler's first statement stores the caught exception, or drops it: that becomes the catch clause's
    /// variable (or none), and the statement goes.
    /// </summary>
    private static string? BindCatch(IrBlock handler, LiftedMethod lifted)
    {
        int first = handler.Statements.FindIndex(s => s is not (IrNop or IrComment));
        if (first < 0)
        {
            return null;
        }

        switch (handler.Statements[first])
        {
            // The slot is only the catch variable: the clause declares it.
            case IrAssign { Dst: JLocal variable, Src: JCaught } when !AssignedElsewhere(lifted.Function, variable.Name, handler.Statements[first]):
                handler.Statements.RemoveAt(first);
                lifted.Locals.Caught(variable.Name);
                return variable.Name;

            // The slot also holds other values — without a variable table, one name covers a slot's every use — so
            // the clause gets a name of its own, handed to the slot by the assignment that was already there.
            case IrAssign { Dst: JLocal, Src: JCaught caught } shared:
                var own = lifted.Locals.Fresh("ex", caught.Type);
                lifted.Locals.Caught(own.Name);
                handler.Statements[first] = shared with { Src = own };
                return own.Name;
            case JExprStmt { Expression: JCaught }:
                handler.Statements.RemoveAt(first);
                return null;
            default:
                return null;
        }
    }

    private static bool AssignedElsewhere(IrFunction function, string name, IrStmt except)
        => function.Blocks.SelectMany(b => b.Statements).Any(s => !ReferenceEquals(s, except) && s is IrAssign { Dst: JLocal l } && l.Name == name);

    /// <summary>A few blocks structured as a graph of their own, the join added as an empty exit so reaching it is falling out.</summary>
    private static CStmt StructureSubgraph(List<IrBlock> blocks, IrBlock entry, int join, JavaLabels? labels)
    {
        var sub = new IrFunction(entry.StartVa, "region", 32);
        sub.Blocks.Add(entry);
        sub.Blocks.AddRange(blocks.Where(b => !ReferenceEquals(b, entry)));
        if (labels is not null)
        {
            return JavaStructurer.Structure(sub, labels);
        }

        if (join >= 0 && !blocks.Any(b => (int)b.StartVa == join))
        {
            sub.Blocks.Add(new IrBlock((ulong)join));
        }

        // The structurer builds its own edges from the successor lists; the blocks are only read.
        return Structurer.Structure(sub);
    }
}
