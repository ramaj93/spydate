namespace Spydate.Decompiler.Native.IR;

public enum IrUnaryOp
{
    Neg,
    Not,
    LogicalNot,
}

public enum IrBinaryOp
{
    Add,
    Sub,
    Mul,
    SMul,
    UDiv,
    SDiv,
    URem,
    SRem,
    And,
    Or,
    Xor,
    Shl,
    Shr,   // logical
    Sar,   // arithmetic
    Rol,
    Ror,
    /// <summary>Scalar floating-point arithmetic (SSE): the operands are floats, not integers.</summary>
    FAdd,
    FSub,
    FMul,
    FDiv,
}

/// <summary>Condition codes as produced by folding a flag-setting instruction into a jcc/setcc/cmovcc.</summary>
public enum IrCondCode
{
    Equal,
    NotEqual,
    /// <summary>Unsigned &lt;</summary>
    Below,
    /// <summary>Unsigned &gt;=</summary>
    AboveOrEqual,
    /// <summary>Unsigned &lt;=</summary>
    BelowOrEqual,
    /// <summary>Unsigned &gt;</summary>
    Above,
    /// <summary>Signed &lt;</summary>
    Less,
    /// <summary>Signed &gt;=</summary>
    GreaterOrEqual,
    /// <summary>Signed &lt;=</summary>
    LessOrEqual,
    /// <summary>Signed &gt;</summary>
    Greater,
    Sign,
    NotSign,
    Overflow,
    NotOverflow,
    Parity,
    NotParity,
}

/// <summary>Base of all IR expressions. Immutable records so passes can rebuild trees safely.</summary>
public abstract record IrExpr
{
    /// <summary>Width in bits (0 when unknown / not applicable).</summary>
    public abstract int Bits { get; }
}

public sealed record IrConst(long Value, int Bits) : IrExpr
{
    public override int Bits { get; } = Bits;
    public ulong Unsigned => Bits >= 64 ? (ulong)Value : (ulong)Value & ((1UL << Bits) - 1);
    public override string ToString() => Value is >= 0 and < 10 ? Value.ToString() : $"0x{Unsigned:X}";
}

public sealed record IrReg(string Name, int Bits) : IrExpr
{
    public override int Bits { get; } = Bits;
    public override string ToString() => Name;
}

public sealed record IrTemp(int Id, int Bits) : IrExpr
{
    public override int Bits { get; } = Bits;
    public override string ToString() => $"t{Id}";
}

/// <summary>A named stack slot (introduced by <c>StackVarNamingPass</c>).</summary>
public sealed record IrLocal(string Name, int Bits, long FrameOffset) : IrExpr
{
    public override int Bits { get; } = Bits;
    public override string ToString() => Name;
}

/// <summary>Address of a named object: a stack slot from <c>lea</c>, or a global an immediate points at.</summary>
public sealed record IrAddressOf(IrExpr Target, int Bits) : IrExpr
{
    public override int Bits { get; } = Bits;
    public override string ToString() => $"&{Target}";
}

/// <summary>
/// A named object in the image - the thing at an address, not the address itself. Produced by
/// <c>GlobalNamingPass</c> for absolute memory operands: <c>*(uint32_t*)(0x14003A100)</c> reads better
/// as <c>data_14003A100</c>, and named when the symbol table knows what lives there.
/// </summary>
public sealed record IrGlobal(string Name, ulong Va, int Bits) : IrExpr
{
    public override int Bits { get; } = Bits;
    public override string ToString() => Name;
}

/// <summary>A pointer to a scanned string literal, printed as the text it points at.</summary>
public sealed record IrStringLiteral(string Text, ulong Va, bool Wide, int Bits) : IrExpr
{
    public override int Bits { get; } = Bits;
    public override string ToString() => $"{(Wide ? "L" : string.Empty)}\"{Text}\"";
}

public sealed record IrMem(IrExpr Address, int Bits) : IrExpr
{
    public override int Bits { get; } = Bits;
    public override string ToString() => $"*({IrTypes.NameFor(Bits)}*)({Address})";
}

public sealed record IrUnary(IrUnaryOp Op, IrExpr Operand) : IrExpr
{
    public override int Bits => Operand.Bits;
    public override string ToString() => Op switch
    {
        IrUnaryOp.Neg => $"-({Operand})",
        IrUnaryOp.Not => $"~({Operand})",
        _ => $"!({Operand})",
    };
}

public sealed record IrBinary(IrBinaryOp Op, IrExpr Left, IrExpr Right) : IrExpr
{
    public override int Bits => Math.Max(Left.Bits, Right.Bits);
    public override string ToString() => $"({Left} {IrTypes.OperatorText(Op)} {Right})";
}

public sealed record IrCast(IrExpr Operand, int Bits, bool Signed) : IrExpr
{
    /// <summary>True when the target type is <c>float</c> / <c>double</c> rather than an integer.</summary>
    public bool IsFloat { get; init; }

    public override int Bits { get; } = Bits;
    public override string ToString() => $"({(IsFloat ? IrTypes.FloatNameFor(Bits) : IrTypes.NameFor(Bits, Signed))})({Operand})";
}

public sealed record IrSymbol(string Name, ulong Va, int Bits) : IrExpr
{
    public override int Bits { get; } = Bits;
    public override string ToString() => Name;
}

public sealed record IrCall(IrExpr Target, IReadOnlyList<IrExpr> Args, int Bits) : IrExpr
{
    /// <summary>
    /// True when the callee's calling convention was established rather than assumed, so passes need not
    /// keep argument registers alive on the chance that one of them carries an argument.
    /// </summary>
    public bool ConventionKnown { get; init; }

    public override int Bits { get; } = Bits;
    public override string ToString() => $"{Target}({string.Join(", ", Args)})";
}

public sealed record IrCondition(IrCondCode Cc, IrExpr Left, IrExpr Right) : IrExpr
{
    public override int Bits => 1;
    public override string ToString() => $"{Left} {IrTypes.ConditionText(Cc)} {Right}";
}

public sealed record IrTernary(IrExpr Condition, IrExpr Then, IrExpr Else) : IrExpr
{
    public override int Bits => Math.Max(Then.Bits, Else.Bits);
    public override string ToString() => $"({Condition}) ? ({Then}) : ({Else})";
}

/// <summary>Opaque expression whose value is unknown to the lifter (e.g. result of an unsupported instruction).</summary>
public sealed record IrUnknown(string Description, int Bits) : IrExpr
{
    public override int Bits { get; } = Bits;
    public override string ToString() => $"<{Description}>";
}

public static class IrTypes
{
    public static string NameFor(int bits, bool signed = false) => (bits, signed) switch
    {
        (8, false) => "uint8_t",
        (8, true) => "int8_t",
        (16, false) => "uint16_t",
        (16, true) => "int16_t",
        (32, false) => "uint32_t",
        (32, true) => "int32_t",
        (64, false) => "uint64_t",
        (64, true) => "int64_t",
        (128, _) => "__m128",
        (256, _) => "__m256",
        (512, _) => "__m512",
        (80, _) => "long double",
        _ => bits <= 0 ? "void" : $"uint{bits}_t",
    };

    /// <summary>Type name for a scalar floating-point value of the given width.</summary>
    public static string FloatNameFor(int bits) => bits switch
    {
        32 => "float",
        64 => "double",
        80 => "long double",
        _ => "double",
    };

    public static string OperatorText(IrBinaryOp op) => op switch
    {
        IrBinaryOp.Add or IrBinaryOp.FAdd => "+",
        IrBinaryOp.Sub or IrBinaryOp.FSub => "-",
        IrBinaryOp.Mul or IrBinaryOp.SMul or IrBinaryOp.FMul => "*",
        IrBinaryOp.UDiv or IrBinaryOp.SDiv or IrBinaryOp.FDiv => "/",
        IrBinaryOp.URem or IrBinaryOp.SRem => "%",
        IrBinaryOp.And => "&",
        IrBinaryOp.Or => "|",
        IrBinaryOp.Xor => "^",
        IrBinaryOp.Shl => "<<",
        IrBinaryOp.Shr or IrBinaryOp.Sar => ">>",
        IrBinaryOp.Rol => "<<<",
        IrBinaryOp.Ror => ">>>",
        _ => "?",
    };

    public static string ConditionText(IrCondCode cc) => cc switch
    {
        IrCondCode.Equal => "==",
        IrCondCode.NotEqual => "!=",
        IrCondCode.Below or IrCondCode.Less => "<",
        IrCondCode.AboveOrEqual or IrCondCode.GreaterOrEqual => ">=",
        IrCondCode.BelowOrEqual or IrCondCode.LessOrEqual => "<=",
        IrCondCode.Above or IrCondCode.Greater => ">",
        IrCondCode.Sign => "< 0 /*sign*/",
        IrCondCode.NotSign => ">= 0 /*!sign*/",
        IrCondCode.Overflow => "/*overflow*/",
        IrCondCode.NotOverflow => "/*!overflow*/",
        IrCondCode.Parity => "/*parity*/",
        IrCondCode.NotParity => "/*!parity*/",
        _ => "?",
    };

    public static bool IsSignedCompare(IrCondCode cc) => cc is IrCondCode.Less or IrCondCode.LessOrEqual or IrCondCode.Greater or IrCondCode.GreaterOrEqual;

    public static IrCondCode Invert(IrCondCode cc) => cc switch
    {
        IrCondCode.Equal => IrCondCode.NotEqual,
        IrCondCode.NotEqual => IrCondCode.Equal,
        IrCondCode.Below => IrCondCode.AboveOrEqual,
        IrCondCode.AboveOrEqual => IrCondCode.Below,
        IrCondCode.BelowOrEqual => IrCondCode.Above,
        IrCondCode.Above => IrCondCode.BelowOrEqual,
        IrCondCode.Less => IrCondCode.GreaterOrEqual,
        IrCondCode.GreaterOrEqual => IrCondCode.Less,
        IrCondCode.LessOrEqual => IrCondCode.Greater,
        IrCondCode.Greater => IrCondCode.LessOrEqual,
        IrCondCode.Sign => IrCondCode.NotSign,
        IrCondCode.NotSign => IrCondCode.Sign,
        IrCondCode.Overflow => IrCondCode.NotOverflow,
        IrCondCode.NotOverflow => IrCondCode.Overflow,
        IrCondCode.Parity => IrCondCode.NotParity,
        IrCondCode.NotParity => IrCondCode.Parity,
        _ => cc,
    };
}
