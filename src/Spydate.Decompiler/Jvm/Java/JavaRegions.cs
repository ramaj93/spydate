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

    private sealed record TryRegion(int Start, int End, List<(string? Type, int Handler)> Catches);

    public static CStmt Structure(LiftedMethod lifted)
    {
        var function = lifted.Function;
        foreach (var region in Regions(lifted.Handlers).Take(MaxTries))
        {
            Collapse(function, region, lifted);
        }

        return Structurer.Structure(function);
    }

    /// <summary>
    /// The exception table as try regions. A handler's entries are joined into one range — a <c>finally</c>
    /// handler's entries skip the copies of its own code between them — and handlers with the same range share a
    /// try, in table order. An entry whose handler lies inside its own range guards the handler itself, which
    /// javac emits for <c>finally</c>; it adds nothing to the structure and is skipped. Smallest first, so an
    /// inner try collapses before the one around it.
    /// </summary>
    private static List<TryRegion> Regions(IReadOnlyList<ExceptionHandler> handlers)
    {
        var byHandler = new Dictionary<int, (int Start, int End, string? Type)>();
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
                regions.Add(new TryRegion(start, end, [(type, handler)]));
            }
            else
            {
                existing.Catches.Add((type, handler));
            }
        }

        return regions.OrderBy(r => r.End - r.Start).ThenByDescending(r => r.Start).ToList();
    }

    private static bool Collapse(IrFunction function, TryRegion region, LiftedMethod lifted)
    {
        var byStart = function.Blocks.ToDictionary(b => (int)b.StartVa);
        if (!byStart.TryGetValue(region.Start, out var entry) || region.Catches.Any(c => !byStart.ContainsKey(c.Handler)))
        {
            function.Warnings.Add($"the try at {region.Start:X4} has no block of its own and is shown unstructured");
            return false;
        }

        var body = function.Blocks.Where(b => (int)b.StartVa >= region.Start && (int)b.StartVa < region.End).ToHashSet();
        var handlerEntries = region.Catches.Select(c => byStart[c.Handler]).ToHashSet();
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
        foreach (var (type, handler) in region.Catches)
        {
            int next = handlerStarts.Where(h => h > handler).DefaultIfEmpty(int.MaxValue).Min();
            int limit = join > handler ? Math.Min(next, join) : next;
            var blocks = Reach(byStart, byStart[handler], b => (int)b.StartVa >= handler && (int)b.StartVa < limit, taken);
            foreach (var block in blocks)
            {
                taken.Add(block);
            }

            string? variable = BindCatch(byStart[handler], lifted);
            catches.Add(new JCatch(type, variable, StructureSubgraph(blocks, byStart[handler], join)));
        }

        var tryBody = StructureSubgraph(body.OrderBy(b => b.StartVa).ToList(), entry, join);

        // The region becomes one block at its start, which falls out to the join.
        var replacement = new IrBlock(entry.StartVa);
        replacement.Statements.Add(new JRegion(new JTry(tryBody, catches)) { Va = entry.StartVa });
        if (join >= 0)
        {
            replacement.Successors.Add((ulong)join);
        }

        var removed = taken;
        int position = function.Blocks.IndexOf(entry);
        function.Blocks.RemoveAll(removed.Contains);
        function.Blocks.Insert(Math.Min(position, function.Blocks.Count), replacement);

        return true;
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
    private static CStmt StructureSubgraph(List<IrBlock> blocks, IrBlock entry, int join)
    {
        var sub = new IrFunction(entry.StartVa, "region", 32);
        sub.Blocks.Add(entry);
        sub.Blocks.AddRange(blocks.Where(b => !ReferenceEquals(b, entry)));
        if (join >= 0 && !blocks.Any(b => (int)b.StartVa == join))
        {
            sub.Blocks.Add(new IrBlock((ulong)join));
        }

        // The structurer builds its own edges from the successor lists; the blocks are only read.
        return Structurer.Structure(sub);
    }
}
