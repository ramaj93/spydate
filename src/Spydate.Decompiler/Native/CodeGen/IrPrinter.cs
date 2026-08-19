using System.Globalization;
using System.Text;
using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.CodeGen;

/// <summary>Precedence-aware C-style printer for IR expressions.</summary>
public static class IrPrinter
{
    // Higher binds tighter.
    private const int PrecTernary = 1;
    private const int PrecOr = 4;
    private const int PrecXor = 5;
    private const int PrecAnd = 6;
    private const int PrecEquality = 7;
    private const int PrecRelational = 8;
    private const int PrecShift = 9;
    private const int PrecAdditive = 10;
    private const int PrecMultiplicative = 11;
    private const int PrecUnary = 12;
    private const int PrecPrimary = 13;

    public static string Print(IrExpr expr)
    {
        var sb = new StringBuilder();
        Write(sb, expr, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, IrExpr expr, int parentPrec)
    {
        int prec = PrecedenceOf(expr);
        bool parens = prec < parentPrec;
        if (parens)
        {
            sb.Append('(');
        }

        switch (expr)
        {
            case IrConst c:
                sb.Append(FormatConst(c));
                break;
            case IrReg r:
                sb.Append(r.Name);
                break;
            case IrTemp t:
                sb.Append('t').Append(t.Id.ToString(CultureInfo.InvariantCulture));
                break;
            case IrLocal l:
                sb.Append(l.Name);
                break;
            case IrSymbol s:
                sb.Append(s.Name);
                break;
            case IrAddressOf a:
                sb.Append('&').Append(a.Local.Name);
                break;
            case IrUnknown u:
                sb.Append('<').Append(u.Description).Append('>');
                break;
            case IrMem m:
                sb.Append("*(").Append(IrTypes.NameFor(m.Bits)).Append("*)");
                Write(sb, m.Address, PrecUnary);
                break;
            case IrUnary u:
                sb.Append(u.Op switch { IrUnaryOp.Neg => "-", IrUnaryOp.Not => "~", _ => "!" });
                Write(sb, u.Operand, PrecUnary);
                break;
            case IrCast c:
                sb.Append('(').Append(IrTypes.NameFor(c.Bits, c.Signed)).Append(')');
                Write(sb, c.Operand, PrecUnary);
                break;
            case IrBinary b:
                WriteBinary(sb, b, prec);
                break;
            case IrCondition cond:
                WriteCondition(sb, cond, prec);
                break;
            case IrTernary t:
                Write(sb, t.Condition, PrecTernary + 1);
                sb.Append(" ? ");
                Write(sb, t.Then, PrecTernary + 1);
                sb.Append(" : ");
                Write(sb, t.Else, PrecTernary);
                break;
            case IrCall call:
                Write(sb, call.Target, PrecPrimary);
                sb.Append('(');
                for (int i = 0; i < call.Args.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(sb, call.Args[i], 0);
                }

                sb.Append(')');
                break;
            default:
                sb.Append(expr);
                break;
        }

        if (parens)
        {
            sb.Append(')');
        }
    }

    private static void WriteBinary(StringBuilder sb, IrBinary b, int prec)
    {
        // Rotates have no C operator; print as intrinsics.
        if (b.Op is IrBinaryOp.Rol or IrBinaryOp.Ror)
        {
            sb.Append(b.Op == IrBinaryOp.Rol ? "__rol" : "__ror").Append('(');
            Write(sb, b.Left, 0);
            sb.Append(", ");
            Write(sb, b.Right, 0);
            sb.Append(')');
            return;
        }

        bool signed = b.Op is IrBinaryOp.SMul or IrBinaryOp.SDiv or IrBinaryOp.SRem or IrBinaryOp.Sar;
        var left = b.Left;
        if (signed && left is not IrCast { Signed: true } && b.Op != IrBinaryOp.SMul)
        {
            left = new IrCast(left, left.Bits, true);
        }

        Write(sb, left, prec);
        sb.Append(' ').Append(IrTypes.OperatorText(b.Op)).Append(' ');
        // Left-associative: right operand needs a higher precedence to avoid re-association.
        Write(sb, b.Right, prec + 1);
    }

    private static void WriteCondition(StringBuilder sb, IrCondition c, int prec)
    {
        switch (c.Cc)
        {
            case IrCondCode.Sign:
            case IrCondCode.NotSign:
                Write(sb, new IrCast(c.Left, c.Left.Bits, true), prec + 1);
                sb.Append(c.Cc == IrCondCode.Sign ? " < 0" : " >= 0");
                return;
            case IrCondCode.Overflow:
            case IrCondCode.NotOverflow:
            case IrCondCode.Parity:
            case IrCondCode.NotParity:
                sb.Append(c.Cc == IrCondCode.Overflow ? "__overflow(" : c.Cc == IrCondCode.NotOverflow ? "!__overflow(" : c.Cc == IrCondCode.Parity ? "__parity(" : "!__parity(");
                Write(sb, c.Left, 0);
                sb.Append(", ");
                Write(sb, c.Right, 0);
                sb.Append(')');
                return;
        }

        bool signed = IrTypes.IsSignedCompare(c.Cc);
        var left = signed && c.Left is not IrCast { Signed: true } && c.Left is not IrConst ? new IrCast(c.Left, c.Left.Bits, true) : c.Left;
        Write(sb, left, prec + 1);
        sb.Append(' ').Append(IrTypes.ConditionText(c.Cc)).Append(' ');
        Write(sb, c.Right, prec + 1);
    }

    private static int PrecedenceOf(IrExpr e) => e switch
    {
        IrBinary b => b.Op switch
        {
            IrBinaryOp.Mul or IrBinaryOp.SMul or IrBinaryOp.UDiv or IrBinaryOp.SDiv or IrBinaryOp.URem or IrBinaryOp.SRem => PrecMultiplicative,
            IrBinaryOp.Add or IrBinaryOp.Sub => PrecAdditive,
            IrBinaryOp.Shl or IrBinaryOp.Shr or IrBinaryOp.Sar => PrecShift,
            IrBinaryOp.And => PrecAnd,
            IrBinaryOp.Xor => PrecXor,
            IrBinaryOp.Or => PrecOr,
            _ => PrecPrimary, // rol/ror printed as calls
        },
        IrCondition c => c.Cc is IrCondCode.Equal or IrCondCode.NotEqual ? PrecEquality
            : c.Cc is IrCondCode.Overflow or IrCondCode.NotOverflow or IrCondCode.Parity or IrCondCode.NotParity ? PrecPrimary
            : PrecRelational,
        IrTernary => PrecTernary,
        IrUnary or IrCast or IrMem => PrecUnary,
        _ => PrecPrimary,
    };

    public static string FormatConst(IrConst c)
    {
        long v = c.Value;
        // Small magnitudes read better in decimal; larger values are usually masks, offsets or addresses.
        if (v is > -0x10000 and < 256)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        return "0x" + c.Unsigned.ToString("X", CultureInfo.InvariantCulture);
    }
}
