using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Makes an irreducible graph reducible by copying code, so it can be written without <c>goto</c>.
///
/// javac never makes one, but Kotlin's coroutines do: the state machine's <c>switch</c> resumes a suspended call
/// in the middle of a loop, so the loop has a second way in. Java cannot jump into a loop, but the code from that
/// entry up to the loop's own head can be written twice: once as part of the loop, and once for the resume path,
/// which then enters the loop at its head like any other code before it. That is node splitting (the classic cure
/// for irreducible flow): each strongly connected region with more than one entry keeps the entry reached first
/// as its head, and every other entry's part of the region — the blocks reachable from it without passing the head
/// — is copied for the edges from outside the region. The copies get addresses past the method's code.
///
/// Copying grows the method, so it is bounded; a graph that would need more is left as it is, and the structurer
/// falls back to showing it with gotos. Blocks are copied whole, try regions included, their exits renamed with them.
/// </summary>
internal static class JavaSplitter
{
    /// <summary>First address given to a copy: far past any JVM method's code, which is at most 64 KB.</summary>
    private const ulong CopyBase = 0x1000_0000;

    private const int MaxRounds = 64;

    /// <summary>Splits until the graph is reducible or the copies would pass the budget; true when it is reducible.</summary>
    public static bool MakeReducible(IrFunction function)
    {
        int original = function.Blocks.Sum(b => b.Statements.Count);
        int budget = Math.Max(400, original * 2);
        int copied = 0;
        ulong next = Math.Max(CopyBase, function.Blocks.Count == 0 ? 0 : function.Blocks.Max(b => b.StartVa) + 1);
        for (int round = 0; round < MaxRounds; round++)
        {
            var cfg = Cfg.Build(function);
            if (Reducible(cfg) || FirstIrreducible(cfg) is not { } region)
            {
                return true;
            }

            var (head, entry, members) = region;

            // The entry's part of the region: what it reaches inside it without passing the head.
            var part = new HashSet<int>();
            var work = new Stack<int>();
            work.Push(entry);
            part.Add(entry);
            while (work.Count > 0)
            {
                foreach (int s in cfg.Successors[work.Pop()])
                {
                    if (s != head && members.Contains(s) && part.Add(s))
                    {
                        work.Push(s);
                    }
                }
            }

            copied += part.Sum(p => cfg.Blocks[p].Statements.Count);
            if (copied > budget)
            {
                return false;
            }

            var renamed = new Dictionary<ulong, ulong>();
            foreach (int p in part.OrderBy(p => cfg.Va(p)))
            {
                renamed[cfg.Va(p)] = next++;
            }

            foreach (int p in part)
            {
                var block = cfg.Blocks[p];
                var copy = new IrBlock(renamed[block.StartVa]);
                copy.Statements.AddRange(block.Statements.Select(s => Retarget(s, renamed) is var moved && ReferenceEquals(moved, s) ? s with { } : moved));
                copy.Successors.AddRange(block.Successors.Select(s => renamed.GetValueOrDefault(s, s)));
                function.Blocks.Add(copy);
            }

            // Every edge into the entry from outside the region now goes to its copy.
            ulong entryVa = cfg.Va(entry);
            var only = new Dictionary<ulong, ulong> { [entryVa] = renamed[entryVa] };
            foreach (int predecessor in cfg.Predecessors[entry].Where(p => !members.Contains(p)))
            {
                var block = cfg.Blocks[predecessor];
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    block.Statements[i] = Retarget(block.Statements[i], only);
                }

                for (int i = 0; i < block.Successors.Count; i++)
                {
                    if (block.Successors[i] == entryVa)
                    {
                        block.Successors[i] = renamed[entryVa];
                    }
                }
            }

            // An original the copy replaced for every way in is dead now: it goes, so the structurer sees none.
            var before = Enumerable.Range(0, cfg.Count).Where(cfg.IsReachable).Select(cfg.Va).ToHashSet();
            var after = Cfg.Build(function);
            function.Blocks.RemoveAll(b => before.Contains(b.StartVa) && after.IndexOf(b.StartVa) is var i && i >= 0 && !after.IsReachable(i));
        }

        return false;
    }

    /// <summary>Every edge going back in reverse post-order goes to a block that dominates its source: nothing to split.</summary>
    private static bool Reducible(Cfg cfg)
    {
        var dominance = Dominance.Compute(cfg.Successors, cfg.Predecessors, 0, cfg.ReversePostOrder, cfg.RpoNumber);
        for (int u = 0; u < cfg.Count; u++)
        {
            if (!cfg.IsReachable(u))
            {
                continue;
            }

            foreach (int v in cfg.Successors[u])
            {
                if (cfg.RpoNumber[v] <= cfg.RpoNumber[u] && !dominance.Dominates(v, u))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// A strongly connected region entered at more than one block: its head (the entry first in reverse
    /// post-order), one other entry, and its members. Null when every region has one entry.
    /// </summary>
    private static (int Head, int Entry, HashSet<int> Members)? FirstIrreducible(Cfg cfg)
    {
        foreach (var component in Components(cfg))
        {
            if (component.Count < 2 && !cfg.Successors[component.First()].Contains(component.First()))
            {
                continue;
            }

            var entries = component
                .Where(n => n == 0 || cfg.Predecessors[n].Any(p => !component.Contains(p) && cfg.IsReachable(p)))
                .Where(cfg.IsReachable)
                .OrderBy(n => cfg.RpoNumber[n])
                .ToList();
            if (entries.Count > 1)
            {
                return (entries[0], entries[1], component);
            }

            // One entry, but a region inside it may still be entered twice once the head is taken out.
            if (entries.Count == 1)
            {
                var inner = new HashSet<int>(component);
                inner.Remove(entries[0]);
                if (Irreducible(cfg, inner) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static (int Head, int Entry, HashSet<int> Members)? Irreducible(Cfg cfg, HashSet<int> within)
    {
        foreach (var component in Components(cfg, within))
        {
            if (component.Count < 2 && !cfg.Successors[component.First()].Contains(component.First()))
            {
                continue;
            }

            var entries = component
                .Where(n => cfg.Predecessors[n].Any(p => !component.Contains(p) && cfg.IsReachable(p)))
                .OrderBy(n => cfg.RpoNumber[n])
                .ToList();
            if (entries.Count > 1)
            {
                return (entries[0], entries[1], component);
            }

            if (entries.Count == 1)
            {
                var inner = new HashSet<int>(component);
                inner.Remove(entries[0]);
                if (Irreducible(cfg, inner) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    /// <summary>Strongly connected components of the reachable graph (restricted to <paramref name="within"/>), iteratively.</summary>
    private static List<HashSet<int>> Components(Cfg cfg, HashSet<int>? within = null)
    {
        var components = new List<HashSet<int>>();
        var index = new int[cfg.Count];
        var low = new int[cfg.Count];
        var onStack = new bool[cfg.Count];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        int counter = 0;
        bool Inside(int n) => cfg.IsReachable(n) && (within is null || within.Contains(n));

        foreach (int root in Enumerable.Range(0, cfg.Count).Where(Inside))
        {
            if (index[root] >= 0)
            {
                continue;
            }

            var calls = new Stack<(int Node, int Next)>();
            calls.Push((root, 0));
            index[root] = low[root] = counter++;
            stack.Push(root);
            onStack[root] = true;
            while (calls.Count > 0)
            {
                var (node, next) = calls.Pop();
                var successors = cfg.Successors[node];
                if (next < successors.Length)
                {
                    calls.Push((node, next + 1));
                    int s = successors[next];
                    if (!Inside(s))
                    {
                        continue;
                    }

                    if (index[s] < 0)
                    {
                        index[s] = low[s] = counter++;
                        stack.Push(s);
                        onStack[s] = true;
                        calls.Push((s, 0));
                    }
                    else if (onStack[s])
                    {
                        low[node] = Math.Min(low[node], index[s]);
                    }

                    continue;
                }

                if (calls.Count > 0)
                {
                    int parent = calls.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }

                if (low[node] == index[node])
                {
                    var component = new HashSet<int>();
                    int member;
                    do
                    {
                        member = stack.Pop();
                        onStack[member] = false;
                        component.Add(member);
                    }
                    while (member != node);
                    components.Add(component);
                }
            }
        }

        return components;
    }

    /// <summary>
    /// A statement with its jump targets renamed; a try region's exits inside it too. A copy is always a new
    /// statement, since declarations are placed by the statement itself.
    /// </summary>
    private static IrStmt Retarget(IrStmt statement, IReadOnlyDictionary<ulong, ulong> renamed)
    {
        ulong Map(ulong va) => renamed.GetValueOrDefault(va, va);
        return statement switch
        {
            IrGoto jump => jump with { TargetVa = Map(jump.TargetVa) },
            IrBranch branch => branch with { TargetVa = Map(branch.TargetVa), FallthroughVa = Map(branch.FallthroughVa) },
            IrSwitch dispatch => dispatch with { Targets = dispatch.Targets.Select(Map).ToList() },
            JRegion region => region with { Body = JavaTree.Rewrite(region.Body, s => s is JExit exit ? new JExit(Map(exit.Target)) : s) },
            _ => statement,
        };
    }
}
