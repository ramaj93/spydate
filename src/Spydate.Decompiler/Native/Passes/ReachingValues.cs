using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>
/// The values that hold on entry to each block, so copy propagation is not confined to one block.
///
/// This is the sound half of SSA: instead of naming every definition and joining them with phi nodes, a
/// value survives into a block only when every predecessor agrees on it, and only when the value is one
/// that cannot change behind our back — a constant, a register, a frame address, a literal. Anything that
/// reads memory or calls is dropped at the block boundary. Predecessors that have not been processed yet
/// (the back edge of a loop) count as agreeing on nothing, which costs some propagation at loop headers
/// and keeps the result honest without a fixpoint.
/// </summary>
internal static class ReachingValues
{
    /// <summary>Entry values per block, keyed by the block's start address.</summary>
    public static Dictionary<ulong, List<(IrExpr Var, IrExpr Value)>> Compute(IrFunction function)
    {
        var result = new Dictionary<ulong, List<(IrExpr, IrExpr)>>();
        if (function.Blocks.Count <= 1)
        {
            return result;
        }

        var cfg = Cfg.Build(function);
        var outputs = new List<(IrExpr Var, IrExpr Value)>?[cfg.Count];

        foreach (int node in cfg.ReversePostOrder)
        {
            List<(IrExpr Var, IrExpr Value)>? merged = null;
            bool unknownPredecessor = false;
            foreach (int predecessor in cfg.Predecessors[node])
            {
                if (outputs[predecessor] is not { } exit)
                {
                    unknownPredecessor = true;
                    break;
                }

                merged = merged is null ? new List<(IrExpr, IrExpr)>(exit) : Intersect(merged, exit);
            }

            var entry = unknownPredecessor || merged is null ? new List<(IrExpr, IrExpr)>() : merged;
            if (entry.Count > 0)
            {
                result[cfg.Va(node)] = entry;
            }

            outputs[node] = Transfer(cfg.Blocks[node], entry, function.Bitness);
        }

        return result;
    }

    /// <summary>Only what both sides say, and only where they say the same thing.</summary>
    private static List<(IrExpr Var, IrExpr Value)> Intersect(List<(IrExpr Var, IrExpr Value)> a, List<(IrExpr Var, IrExpr Value)> b)
    {
        var kept = new List<(IrExpr, IrExpr)>(Math.Min(a.Count, b.Count));
        foreach (var (variable, value) in a)
        {
            foreach (var (otherVariable, otherValue) in b)
            {
                if (variable == otherVariable && value == otherValue)
                {
                    kept.Add((variable, value));
                    break;
                }
            }
        }

        return kept;
    }

    private static List<(IrExpr Var, IrExpr Value)> Transfer(IrBlock block, List<(IrExpr Var, IrExpr Value)> entry, int bitness)
    {
        var live = new List<(IrExpr Var, IrExpr Value)>(entry);

        foreach (var statement in block.Statements)
        {
            bool hasCall = statement is IrCallStmt;
            var written = IrRewriter.Destination(statement);

            live.RemoveAll(def =>
                (written is not null && (RegisterAliases.MayAlias(written, def.Var) || Reads(def.Value, written)))
                || (hasCall && Clobbers(def.Var, bitness)));

            if (written is not null && statement is IrAssign assign && IsStable(assign.Src) && !Reads(assign.Src, written))
            {
                live.Add((written, assign.Src));
            }
        }

        return live;
    }

    /// <summary>A call clobbers the volatile registers, and may write the caller's frame through a pointer.</summary>
    private static bool Clobbers(IrExpr variable, int bitness) => variable switch
    {
        IrLocal => true,
        IrReg r => IsCallerSaved(r.Name, bitness),
        _ => true,
    };

    private static bool IsCallerSaved(string register, int bitness)
    {
        string canonical = RegisterAliases.CanonicalOf(register);
        return bitness == 64
            ? canonical is "rax" or "rcx" or "rdx" or "r8" or "r9" or "r10" or "r11" || canonical.StartsWith("zmm", StringComparison.Ordinal)
            : canonical is "rax";
    }

    /// <summary>
    /// Values that mean the same thing at the start of the next block as they did at the end of this one:
    /// no memory read, no call, nothing whose width games would need re-checking.
    /// </summary>
    private static bool IsStable(IrExpr value) => IrRewriter.Descendants(value).All(e => e is IrConst or IrReg or IrLocal or IrSymbol or IrStringLiteral or IrAddressOf);

    private static bool Reads(IrExpr value, IrExpr variable)
        => IrRewriter.Descendants(value).Any(e => e is IrReg or IrTemp or IrLocal && RegisterAliases.MayAlias(e, variable));
}
