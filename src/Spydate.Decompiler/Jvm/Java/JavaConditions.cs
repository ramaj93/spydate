using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Rebuilds <c>&amp;&amp;</c> and <c>||</c> from the ladders of branches a compiler turns them into, before the
/// structurer sees the graph. Two branches that share a target, where the second block is nothing but its branch
/// and is reached only from the first, are one condition:
/// <c>if (a) goto T; if (b) goto T;</c> is <c>if (a || b) goto T</c>, and <c>if (a) { if (b) goto T }</c> is
/// <c>if (a &amp;&amp; b) goto T</c>, with the negations the fall-throughs imply. Repeated until nothing merges, which
/// folds a chain of any length. The structurer cannot nest such a ladder — the shared target is reached from
/// several depths — so without this every <c>&amp;&amp;</c> would leave a <c>goto</c>.
/// </summary>
internal static class JavaConditions
{
    /// <summary>A merged condition is kept below this height, like every tree the lifter builds.</summary>
    private const int MaxDepth = 48;

    public static void Merge(IrFunction function, IReadOnlySet<ulong> pinned)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            var byStart = function.Blocks.ToDictionary(b => b.StartVa);
            var predecessors = new Dictionary<ulong, int>();
            foreach (var block in function.Blocks)
            {
                foreach (ulong succ in block.Successors.Distinct())
                {
                    predecessors[succ] = predecessors.GetValueOrDefault(succ) + 1;
                }
            }

            foreach (var first in function.Blocks)
            {
                if (first.Statements.Count == 0 || first.Statements[^1] is not IrBranch a)
                {
                    continue;
                }

                foreach (ulong candidate in new[] { a.FallthroughVa, a.TargetVa })
                {
                    if (candidate == first.StartVa || pinned.Contains(candidate) || predecessors.GetValueOrDefault(candidate) != 1
                        || !byStart.TryGetValue(candidate, out var second) || second.Statements.Count != 1 || second.Statements[0] is not IrBranch b)
                    {
                        continue;
                    }

                    if (Combine(a, b, candidate) is not { } merged || Height(merged.Condition) > MaxDepth)
                    {
                        continue;
                    }

                    first.Statements[^1] = merged with { Va = a.Va };
                    first.Successors.Clear();
                    first.Successors.Add(merged.TargetVa);
                    if (merged.FallthroughVa != merged.TargetVa)
                    {
                        first.Successors.Add(merged.FallthroughVa);
                    }

                    function.Blocks.Remove(second);
                    changed = true;
                    break;
                }

                if (changed)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The one branch two branches make, when they share a target. <paramref name="second"/> is where
    /// <paramref name="a"/> goes on one side.
    /// </summary>
    private static IrBranch? Combine(IrBranch a, IrBranch b, ulong second)
    {
        if (second == a.FallthroughVa)
        {
            // a false leads to b: taken to a.Target when a, or when b says so too.
            if (b.TargetVa == a.TargetVa)
            {
                return new IrBranch(new JLogical(false, a.Condition, b.Condition), a.TargetVa, b.FallthroughVa);
            }

            if (b.FallthroughVa == a.TargetVa)
            {
                return new IrBranch(new JLogical(false, a.Condition, JLogical.Not(b.Condition)), a.TargetVa, b.TargetVa);
            }
        }
        else
        {
            // a true leads to b: on to b's target only when both hold.
            if (b.FallthroughVa == a.FallthroughVa)
            {
                return new IrBranch(new JLogical(true, a.Condition, b.Condition), b.TargetVa, a.FallthroughVa);
            }

            if (b.TargetVa == a.FallthroughVa)
            {
                return new IrBranch(new JLogical(true, a.Condition, JLogical.Not(b.Condition)), b.FallthroughVa, a.FallthroughVa);
            }
        }

        return null;
    }

    private static int Height(IrExpr expression) => expression switch
    {
        JExpr j => j.Depth,
        IrCondition c => 1 + Math.Max(Height(c.Left), Height(c.Right)),
        IrUnary u => 1 + Height(u.Operand),
        _ => 1,
    };
}
