using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>
/// Local algebraic clean-ups: folds constant chains such as <c>(x - 40) + 40</c> → <c>x</c>,
/// <c>(x + 8) + 8</c> → <c>x + 16</c>, removes <c>x + 0</c>, folds constant-only arithmetic,
/// and simplifies casts of constants.
/// </summary>
public sealed class AlgebraicSimplificationPass : IIrPass
{
    public string Name => "algebraic-simplification";

    public void Run(IrFunction function)
    {
        foreach (var block in function.Blocks)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                block.Statements[i] = IrRewriter.RewriteStmt(block.Statements[i], Simplify);
            }

            // "mov edi, edi" (hot-patch padding) and similar self-assignments carry no meaning.
            block.Statements.RemoveAll(s => s is IrAssign { Dst: IrReg d, Src: IrReg v } && d.Name == v.Name && d.Bits == v.Bits);
        }
    }

    public static IrExpr Simplify(IrExpr e)
    {
        switch (e)
        {
            case IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub } b:
                return SimplifyAddSub(b);
            case IrBinary { Left: IrConst l, Right: IrConst r } b when Fold(b.Op, l, r, b.Bits) is { } folded:
                return folded;
            case IrCast { Operand: IrConst c } cast:
                return Truncate(c.Value, cast.Bits, cast.Signed);
            case IrCast { Operand: var inner } cast when inner.Bits == cast.Bits && !cast.Signed:
                return inner; // zero-extension to the same width is a no-op
            case IrBinary { Op: IrBinaryOp.Mul, Right: IrConst { Value: 1 } } m:
                return m.Left;
            case IrBinary { Op: IrBinaryOp.And, Right: IrConst rc } a when rc.Bits > 0 && rc.Value == -1:
                return a.Left;
            default:
                return e;
        }
    }

    private static IrExpr SimplifyAddSub(IrBinary b)
    {
        // Normalise "x + c" / "x - c" into (base, offset).
        var (baseExpr, offset) = Decompose(b);
        if (baseExpr is null)
        {
            // Both sides constant.
            if (b.Left is IrConst lc && b.Right is IrConst rc)
            {
                long v = b.Op == IrBinaryOp.Add ? lc.Value + rc.Value : lc.Value - rc.Value;
                return Truncate(v, b.Bits, true);
            }

            return b;
        }

        if (offset == 0)
        {
            return baseExpr;
        }

        int bits = b.Bits > 0 ? b.Bits : baseExpr.Bits;
        return offset < 0
            ? new IrBinary(IrBinaryOp.Sub, baseExpr, new IrConst(-offset, bits))
            : new IrBinary(IrBinaryOp.Add, baseExpr, new IrConst(offset, bits));
    }

    /// <summary>Peels constant add/sub layers: ((x - 40) + 40) → (x, 0). Returns (null, 0) when fully constant.</summary>
    private static (IrExpr? Base, long Offset) Decompose(IrExpr e)
    {
        long offset = 0;
        IrExpr cur = e;
        while (true)
        {
            if (cur is IrBinary { Op: IrBinaryOp.Add } a)
            {
                if (a.Right is IrConst rc)
                {
                    offset += rc.Value;
                    cur = a.Left;
                    continue;
                }

                if (a.Left is IrConst lc)
                {
                    offset += lc.Value;
                    cur = a.Right;
                    continue;
                }
            }
            else if (cur is IrBinary { Op: IrBinaryOp.Sub, Right: IrConst sc } s)
            {
                offset -= sc.Value;
                cur = s.Left;
                continue;
            }

            break;
        }

        if (cur is IrConst)
        {
            return (null, 0);
        }

        // Only report a change when at least one constant layer was peeled off.
        return ReferenceEquals(cur, e) ? (cur, 0) : (cur, offset);
    }

    private static IrExpr? Fold(IrBinaryOp op, IrConst l, IrConst r, int bits)
    {
        long a = l.Value, b = r.Value;
        long? v = op switch
        {
            IrBinaryOp.And => a & b,
            IrBinaryOp.Or => a | b,
            IrBinaryOp.Xor => a ^ b,
            IrBinaryOp.Mul or IrBinaryOp.SMul => a * b,
            IrBinaryOp.Shl when b is >= 0 and < 64 => a << (int)b,
            IrBinaryOp.Shr when b is >= 0 and < 64 => (long)((ulong)a >> (int)b),
            IrBinaryOp.Sar when b is >= 0 and < 64 => a >> (int)b,
            _ => null,
        };
        return v is null ? null : Truncate(v.Value, bits, true);
    }

    private static IrConst Truncate(long value, int bits, bool signed)
    {
        if (bits <= 0 || bits >= 64)
        {
            return new IrConst(value, bits <= 0 ? 64 : bits);
        }

        ulong mask = (1UL << bits) - 1;
        ulong u = (ulong)value & mask;
        long v = signed && (u & (1UL << (bits - 1))) != 0 ? (long)(u | ~mask) : (long)u;
        return new IrConst(v, bits);
    }
}
