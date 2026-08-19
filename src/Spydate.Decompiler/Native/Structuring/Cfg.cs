using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Structuring;

/// <summary>
/// The block graph of an <see cref="IrFunction"/> in a form the structurer can index: blocks numbered
/// with the entry first, edges that leave the function dropped, and predecessors recomputed from the
/// successor lists so the two always agree.
/// </summary>
internal sealed class Cfg
{
    private readonly Dictionary<ulong, int> _index;

    private Cfg(IrBlock[] blocks, Dictionary<ulong, int> index, int[][] successors, int[][] predecessors, int[] reversePostOrder, int[] rpoNumber)
    {
        Blocks = blocks;
        _index = index;
        Successors = successors;
        Predecessors = predecessors;
        ReversePostOrder = reversePostOrder;
        RpoNumber = rpoNumber;
    }

    public IrBlock[] Blocks { get; }

    public int Count => Blocks.Length;

    /// <summary>Successor indices, in the order the block lists them.</summary>
    public int[][] Successors { get; }

    public int[][] Predecessors { get; }

    /// <summary>Reachable nodes in reverse post-order; unreachable blocks are absent.</summary>
    public int[] ReversePostOrder { get; }

    /// <summary>Position in <see cref="ReversePostOrder"/>, or -1 when the block is unreachable.</summary>
    public int[] RpoNumber { get; }

    public ulong Va(int index) => Blocks[index].StartVa;

    /// <summary>Index of the block starting at <paramref name="va"/>, or -1 when it is outside the function.</summary>
    public int IndexOf(ulong va) => _index.TryGetValue(va, out int i) ? i : -1;

    public bool IsReachable(int index) => RpoNumber[index] >= 0;

    public static Cfg Build(IrFunction function)
    {
        // Entry first so index 0 is the dominator root; the rest in address order for a stable layout.
        var blocks = new List<IrBlock>(function.Blocks.Count);
        var entry = function.Blocks.FirstOrDefault(b => b.StartVa == function.EntryVa);
        if (entry is not null)
        {
            blocks.Add(entry);
        }

        blocks.AddRange(function.Blocks.Where(b => !ReferenceEquals(b, entry)).OrderBy(b => b.StartVa));

        var index = new Dictionary<ulong, int>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            index.TryAdd(blocks[i].StartVa, i);
        }

        var successors = new int[blocks.Count][];
        var predecessors = new List<int>[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            predecessors[i] = new List<int>();
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            var targets = new List<int>();
            foreach (ulong va in blocks[i].Successors)
            {
                if (index.TryGetValue(va, out int t) && !targets.Contains(t))
                {
                    targets.Add(t);
                }
            }

            successors[i] = targets.ToArray();
            foreach (int t in targets)
            {
                if (!predecessors[t].Contains(i))
                {
                    predecessors[t].Add(i);
                }
            }
        }

        var (rpo, rpoNumber) = ComputeReversePostOrder(successors, blocks.Count);
        return new Cfg(blocks.ToArray(), index, successors, predecessors.Select(p => p.ToArray()).ToArray(), rpo, rpoNumber);
    }

    private static (int[] Order, int[] Number) ComputeReversePostOrder(int[][] successors, int count)
    {
        var order = new List<int>(count);
        var number = new int[count];
        Array.Fill(number, -1);
        if (count == 0)
        {
            return (Array.Empty<int>(), number);
        }

        // Iterative post-order: the cursor records how many successors of each stacked node were taken.
        var visited = new bool[count];
        var stack = new Stack<(int Node, int Cursor)>();
        stack.Push((0, 0));
        visited[0] = true;
        while (stack.Count > 0)
        {
            var (node, cursor) = stack.Pop();
            if (cursor < successors[node].Length)
            {
                stack.Push((node, cursor + 1));
                int next = successors[node][cursor];
                if (!visited[next])
                {
                    visited[next] = true;
                    stack.Push((next, 0));
                }
            }
            else
            {
                order.Add(node);
            }
        }

        order.Reverse();
        for (int i = 0; i < order.Count; i++)
        {
            number[order[i]] = i;
        }

        return (order.ToArray(), number);
    }
}

/// <summary>
/// Immediate dominators by the Cooper-Harvey-Kennedy iteration. Used forwards for dominance and, over the
/// reversed graph, for the post-dominator that gives a two-way branch its join point.
/// </summary>
internal sealed class Dominance
{
    private readonly int[] _idom;
    private readonly int[] _rpoNumber;

    private Dominance(int[] idom, int[] rpoNumber)
    {
        _idom = idom;
        _rpoNumber = rpoNumber;
    }

    /// <summary>Immediate dominator of <paramref name="node"/>; the root dominates itself, -1 when unreachable.</summary>
    public int Idom(int node) => _idom[node];

    /// <summary>True when every path from the root to <paramref name="node"/> passes through <paramref name="dominator"/>.</summary>
    public bool Dominates(int dominator, int node)
    {
        if (dominator == node)
        {
            return _idom[node] >= 0;
        }

        for (int i = node; i >= 0 && _idom[i] != i; i = _idom[i])
        {
            if (_idom[i] == dominator)
            {
                return true;
            }
        }

        return false;
    }

    public static Dominance Compute(int[][] successors, int[][] predecessors, int root, int[] reversePostOrder, int[] rpoNumber)
    {
        int count = successors.Length;
        var idom = new int[count];
        Array.Fill(idom, -1);
        if (count == 0)
        {
            return new Dominance(idom, rpoNumber);
        }

        idom[root] = root;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int node in reversePostOrder)
            {
                if (node == root)
                {
                    continue;
                }

                int candidate = -1;
                foreach (int pred in predecessors[node])
                {
                    if (idom[pred] < 0)
                    {
                        continue; // not processed yet on this round
                    }

                    candidate = candidate < 0 ? pred : Intersect(pred, candidate, idom, rpoNumber);
                }

                if (candidate >= 0 && idom[node] != candidate)
                {
                    idom[node] = candidate;
                    changed = true;
                }
            }
        }

        return new Dominance(idom, rpoNumber);
    }

    /// <summary>Walks two nodes up the dominator tree until they meet.</summary>
    private static int Intersect(int a, int b, int[] idom, int[] rpoNumber)
    {
        while (a != b)
        {
            while (a >= 0 && b >= 0 && rpoNumber[a] > rpoNumber[b])
            {
                a = idom[a];
            }

            while (a >= 0 && b >= 0 && rpoNumber[b] > rpoNumber[a])
            {
                b = idom[b];
            }

            if (a < 0 || b < 0)
            {
                return a < 0 ? b : a;
            }

            if (rpoNumber[a] == rpoNumber[b] && a != b)
            {
                return a; // unreachable nodes share number -1; stop rather than loop
            }
        }

        return a;
    }

    /// <summary>
    /// Post-dominators, computed on the reversed graph from a virtual exit that every block without an
    /// in-function successor flows to. The exit node has index <c>count</c>.
    /// </summary>
    public static Dominance ComputePost(Cfg cfg)
    {
        int count = cfg.Count;
        int exit = count;
        var succR = new int[count + 1][];
        var predR = new int[count + 1][];
        var exits = new List<int>();

        for (int i = 0; i < count; i++)
        {
            // Reversed graph: predecessors become successors.
            succR[i] = cfg.Predecessors[i];
            if (cfg.Successors[i].Length == 0)
            {
                exits.Add(i);
                predR[i] = cfg.Successors[i].Append(exit).ToArray();
            }
            else
            {
                predR[i] = cfg.Successors[i];
            }
        }

        succR[exit] = exits.ToArray();
        predR[exit] = Array.Empty<int>();

        var (rpo, number) = ReversePostOrderFrom(succR, exit);
        return Compute(succR, predR, exit, rpo, number);
    }

    private static (int[] Order, int[] Number) ReversePostOrderFrom(int[][] successors, int root)
    {
        int count = successors.Length;
        var order = new List<int>(count);
        var number = new int[count];
        Array.Fill(number, -1);

        var visited = new bool[count];
        var stack = new Stack<(int Node, int Cursor)>();
        stack.Push((root, 0));
        visited[root] = true;
        while (stack.Count > 0)
        {
            var (node, cursor) = stack.Pop();
            if (cursor < successors[node].Length)
            {
                stack.Push((node, cursor + 1));
                int next = successors[node][cursor];
                if (!visited[next])
                {
                    visited[next] = true;
                    stack.Push((next, 0));
                }
            }
            else
            {
                order.Add(node);
            }
        }

        order.Reverse();
        for (int i = 0; i < order.Count; i++)
        {
            number[order[i]] = i;
        }

        return (order.ToArray(), number);
    }
}
