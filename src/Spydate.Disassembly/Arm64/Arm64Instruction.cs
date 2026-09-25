using System.Globalization;
using System.Text;

namespace Spydate.Disassembly.Arm64;

/// <summary>The register files an ARM64 operand can name, each at the width the instruction reads it.</summary>
public enum Arm64RegisterKind
{
    /// <summary>64-bit general register; number 31 is the zero register <c>xzr</c>.</summary>
    X,

    /// <summary>32-bit general register; number 31 is <c>wzr</c>.</summary>
    W,

    /// <summary>64-bit general register where number 31 is the stack pointer <c>sp</c>.</summary>
    XSp,

    /// <summary>32-bit general register where number 31 is <c>wsp</c>.</summary>
    WSp,

    /// <summary>SIMD and floating-point register, 8 bits.</summary>
    B,

    /// <summary>16 bits.</summary>
    H,

    /// <summary>32 bits.</summary>
    S,

    /// <summary>64 bits.</summary>
    D,

    /// <summary>128 bits.</summary>
    Q,
}

/// <summary>One register as an operand names it: its file and width, and its number.</summary>
public readonly record struct Arm64Register(Arm64RegisterKind Kind, int Number)
{
    public bool IsGeneral => Kind is Arm64RegisterKind.X or Arm64RegisterKind.W or Arm64RegisterKind.XSp or Arm64RegisterKind.WSp;

    /// <summary>The zero register: reads as 0, writes are discarded.</summary>
    public bool IsZero => Number == 31 && Kind is Arm64RegisterKind.X or Arm64RegisterKind.W;

    public bool IsStackPointer => Number == 31 && Kind is Arm64RegisterKind.XSp or Arm64RegisterKind.WSp;

    public bool Is64Bit => Kind is Arm64RegisterKind.X or Arm64RegisterKind.XSp;

    public override string ToString() => (Kind, Number) switch
    {
        (Arm64RegisterKind.X, 31) => "xzr",
        (Arm64RegisterKind.W, 31) => "wzr",
        (Arm64RegisterKind.XSp, 31) => "sp",
        (Arm64RegisterKind.WSp, 31) => "wsp",
        (Arm64RegisterKind.X or Arm64RegisterKind.XSp, _) => "x" + Number.ToString(CultureInfo.InvariantCulture),
        (Arm64RegisterKind.W or Arm64RegisterKind.WSp, _) => "w" + Number.ToString(CultureInfo.InvariantCulture),
        _ => Kind.ToString().ToLowerInvariant() + Number.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>A shift applied to a register operand.</summary>
public enum Arm64Shift
{
    Lsl,
    Lsr,
    Asr,
    Ror,
    Msl,
}

/// <summary>An extension applied to a register operand before it is used.</summary>
public enum Arm64Extend
{
    Uxtb,
    Uxth,
    Uxtw,
    Uxtx,
    Sxtb,
    Sxth,
    Sxtw,
    Sxtx,

    /// <summary><c>lsl</c> in place of <c>uxtx</c>/<c>uxtw</c> where the register is the full width.</summary>
    Lsl,
}

/// <summary>How a memory operand's address is formed and written back.</summary>
public enum Arm64Indexing
{
    /// <summary><c>[base, offset]</c>: the base is not changed.</summary>
    Offset,

    /// <summary><c>[base, offset]!</c>: the address is base + offset, and the base becomes it.</summary>
    PreIndex,

    /// <summary><c>[base], offset</c>: the address is the base, which then moves by the offset.</summary>
    PostIndex,
}

/// <summary>An operand of an ARM64 instruction.</summary>
public abstract record Arm64Operand
{
    /// <summary>The operand as text; <paramref name="name"/> names an address when a symbol is there.</summary>
    public abstract string Format(Func<ulong, string?>? name);

    public override string ToString() => Format(null);

    internal static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
}

/// <summary>A register.</summary>
public sealed record Arm64RegisterOperand(Arm64Register Register) : Arm64Operand
{
    public override string Format(Func<ulong, string?>? name) => Register.ToString();
}

/// <summary>An immediate. Written in hex, or in decimal where the number is a count — a shift, a bit position.</summary>
public sealed record Arm64Immediate(long Value, bool Decimal = false) : Arm64Operand
{
    public override string Format(Func<ulong, string?>? name) => Decimal
        ? "#" + Value.ToString(CultureInfo.InvariantCulture)
        : "#" + Hex((ulong)Value);
}

/// <summary>A floating-point immediate: <c>fmov d0, #1.0</c>.</summary>
public sealed record Arm64FloatImmediate(double Value) : Arm64Operand
{
    public override string Format(Func<ulong, string?>? name)
    {
        string text = Value.ToString("R", CultureInfo.InvariantCulture);
        return "#" + (text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0");
    }
}

/// <summary>A register shifted before use: <c>x2, lsl #3</c>.</summary>
public sealed record Arm64ShiftedRegister(Arm64Register Register, Arm64Shift Shift, int Amount) : Arm64Operand
{
    public override string Format(Func<ulong, string?>? name)
        => Amount == 0 && Shift == Arm64Shift.Lsl ? Register.ToString() : $"{Register}, {Shift.ToString().ToLowerInvariant()} #{Amount}";
}

/// <summary>A register extended before use: <c>w2, sxtw #2</c>.</summary>
public sealed record Arm64ExtendedRegister(Arm64Register Register, Arm64Extend Extend, int Amount) : Arm64Operand
{
    public override string Format(Func<ulong, string?>? name)
    {
        if (Extend == Arm64Extend.Lsl)
        {
            return Amount == 0 ? Register.ToString() : $"{Register}, lsl #{Amount}";
        }

        return Amount == 0 ? $"{Register}, {Extend.ToString().ToLowerInvariant()}" : $"{Register}, {Extend.ToString().ToLowerInvariant()} #{Amount}";
    }
}

/// <summary>
/// A memory operand: a base register and either an immediate offset or an index register, extended and scaled.
/// </summary>
public sealed record Arm64Memory(Arm64Register Base, long Offset, Arm64Indexing Indexing = Arm64Indexing.Offset) : Arm64Operand
{
    /// <summary>The index register, when the offset is one.</summary>
    public Arm64Register? Index { get; init; }

    public Arm64Extend Extend { get; init; } = Arm64Extend.Lsl;

    /// <summary>What the index is shifted left by.</summary>
    public int Amount { get; init; }

    /// <summary>Whether a shift of zero is written: a byte access that encodes one says <c>lsl #0</c>.</summary>
    public bool ShowZeroAmount { get; init; }

    /// <summary>A post-index by a register, as the structure loads have: <c>[x0], x2</c>.</summary>
    public Arm64Register? PostRegister { get; init; }

    public override string Format(Func<ulong, string?>? name)
    {
        var sb = new StringBuilder("[").Append(Base);
        if (Index is { } index)
        {
            sb.Append(", ").Append(index);
            string extend = Extend.ToString().ToLowerInvariant();
            if (Extend != Arm64Extend.Lsl || Amount != 0 || ShowZeroAmount)
            {
                sb.Append(", ").Append(extend);
                if (Amount != 0 || ShowZeroAmount)
                {
                    sb.Append(" #").Append(Amount.ToString(CultureInfo.InvariantCulture));
                }
            }

            return sb.Append(']').ToString();
        }

        string offset = "#" + Offset.ToString(CultureInfo.InvariantCulture);
        return Indexing switch
        {
            Arm64Indexing.PreIndex => sb.Append(", ").Append(offset).Append("]!").ToString(),
            Arm64Indexing.PostIndex when PostRegister is { } post => sb.Append("], ").Append(post).ToString(),
            Arm64Indexing.PostIndex => sb.Append("], ").Append(offset).ToString(),
            _ when Offset == 0 => sb.Append(']').ToString(),
            _ => sb.Append(", ").Append(offset).Append(']').ToString(),
        };
    }
}

/// <summary>An address the instruction computes from its own: a branch target, <c>adr</c>, a literal load.</summary>
/// <remarks>
/// A page (<see cref="IsPage"/>, from <c>adrp</c>) is written as the number: what sits at the start of the page is
/// rarely what the code wants, and the <c>add</c> or load that finishes the address says that.
/// </remarks>
public sealed record Arm64Address(ulong Address, bool IsPage = false) : Arm64Operand
{
    public override string Format(Func<ulong, string?>? name) => (IsPage ? null : name?.Invoke(Address)) ?? Hex(Address);
}

/// <summary>Anything written as it is: a condition, a system register, a barrier option, a vector register.</summary>
public sealed record Arm64Text(string Text) : Arm64Operand
{
    public override string Format(Func<ulong, string?>? name) => Text;
}

/// <summary>
/// One decoded ARM64 instruction: its encoding, the mnemonic in its preferred form (<c>mov</c> for <c>orr</c> from
/// the zero register, <c>cmp</c> for <c>subs</c> to it), the operands of that form, and how control leaves it.
/// </summary>
public sealed record Arm64Instruction(uint Word, string Mnemonic, IReadOnlyList<Arm64Operand> Operands)
{
    public InstructionFlow Flow { get; init; } = InstructionFlow.Next;

    /// <summary>A direct branch or call's target.</summary>
    public ulong? Target { get; init; }

    public string FormatOperands(Func<ulong, string?>? name = null)
        => string.Join(", ", Operands.Select(o => o.Format(name)));

    public override string ToString() => Operands.Count == 0 ? Mnemonic : $"{Mnemonic} {FormatOperands()}";
}
