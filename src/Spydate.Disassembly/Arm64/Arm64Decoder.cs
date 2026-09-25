using System.Globalization;

namespace Spydate.Disassembly.Arm64;

/// <summary>
/// Decodes one A64 instruction word. Every instruction is 32 bits, so there is no length to find and nothing to
/// resynchronise: a word decodes, or it is not an instruction (null). The groups follow the architecture's own
/// encoding index — data processing on immediates, branches and system, loads and stores, data processing on
/// registers, and SIMD and floating point — and each instruction comes out in its preferred disassembly: the
/// alias the architecture names for it where one applies (<c>mov</c>, <c>cmp</c>, <c>lsl</c>, <c>cset</c>).
/// </summary>
public static partial class Arm64Decoder
{
    /// <summary>
    /// The instruction <paramref name="word"/> encodes, at <paramref name="pc"/> (pc-relative operands are made
    /// absolute); null for an encoding that is unallocated or that this decoder does not know.
    /// </summary>
    public static Arm64Instruction? Decode(uint word, ulong pc)
    {
        return ((word >> 25) & 0xF) switch
        {
            0b0000 => word >> 16 == 0 ? new Arm64Instruction(word, "udf", [Imm(word & 0xFFFF)]) { Flow = InstructionFlow.Interrupt } : null,
            0b1000 or 0b1001 => DataImmediate(word, pc),
            0b1010 or 0b1011 => BranchSystem(word, pc),
            0b0100 or 0b0110 or 0b1100 or 0b1110 => LoadStore(word, pc),
            0b0101 or 0b1101 => DataRegister(word),
            0b0111 or 0b1111 => SimdFp(word),
            _ => null,
        };
    }

    // ---- fields and operands --------------------------------------------------------------------------------

    /// <summary>Bits <paramref name="hi"/>..<paramref name="lo"/> of the word.</summary>
    private static uint F(uint w, int hi, int lo) => (w >> lo) & (uint)((1UL << (hi - lo + 1)) - 1);

    private static bool Bit(uint w, int n) => ((w >> n) & 1) != 0;

    private static long SignExtend(ulong value, int bits) => (long)(value << (64 - bits)) >> (64 - bits);

    private static Arm64Instruction I(uint w, string mnemonic, params Arm64Operand[] operands) => new(w, mnemonic, operands);

    private static Arm64RegisterOperand X(uint n) => new(new Arm64Register(Arm64RegisterKind.X, (int)n));

    private static Arm64RegisterOperand W(uint n) => new(new Arm64Register(Arm64RegisterKind.W, (int)n));

    private static Arm64RegisterOperand XSp(uint n) => new(new Arm64Register(Arm64RegisterKind.XSp, (int)n));

    /// <summary>A general register, 64 or 32 bits by <paramref name="sf"/>, number 31 the zero register.</summary>
    private static Arm64RegisterOperand R(bool sf, uint n) => new(Reg(sf, n));

    /// <summary>A general register, number 31 the stack pointer.</summary>
    private static Arm64RegisterOperand RSp(bool sf, uint n) => new(RegSp(sf, n));

    private static Arm64Register Reg(bool sf, uint n) => new(sf ? Arm64RegisterKind.X : Arm64RegisterKind.W, (int)n);

    private static Arm64Register RegSp(bool sf, uint n) => new(sf ? Arm64RegisterKind.XSp : Arm64RegisterKind.WSp, (int)n);

    private static Arm64RegisterOperand V(Arm64RegisterKind kind, uint n) => new(new Arm64Register(kind, (int)n));

    private static Arm64Immediate Imm(long value) => new(value);

    private static Arm64Immediate Dec(long value) => new(value, Decimal: true);

    private static Arm64Text T(string text) => new(text);

    private static readonly string[] Conditions = ["eq", "ne", "cs", "cc", "mi", "pl", "vs", "vc", "hi", "ls", "ge", "lt", "gt", "le", "al", "nv"];

    private static Arm64Text Cond(uint c) => new(Conditions[c & 0xF]);

    private static string Hex(ulong v) => "0x" + v.ToString("x", CultureInfo.InvariantCulture);

    // ---- data processing: immediate ---------------------------------------------------------------------

    private static Arm64Instruction? DataImmediate(uint w, ulong pc)
    {
        bool sf = Bit(w, 31);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        switch (F(w, 25, 23))
        {
            case 0b000:
            case 0b001:
            {
                // adr / adrp: a 21-bit offset from this instruction, or from its 4 KB page in pages.
                long imm = SignExtend((F(w, 23, 5) << 2) | F(w, 30, 29), 21);
                return Bit(w, 31)
                    ? I(w, "adrp", X(rd), new Arm64Address((ulong)((long)(pc & ~0xFFFUL) + (imm << 12)), IsPage: true))
                    : I(w, "adr", X(rd), new Arm64Address((ulong)((long)pc + imm)));
            }

            case 0b010:
            {
                bool sub = Bit(w, 30);
                bool setFlags = Bit(w, 29);
                long imm = F(w, 21, 10);
                bool shifted = Bit(w, 22);
                var operand = shifted ? new Arm64ShiftedImmediate(imm, 12) : (Arm64Operand)Imm(imm);
                if (setFlags && rd == 31)
                {
                    return I(w, sub ? "cmp" : "cmn", RSp(sf, rn), operand);
                }

                if (!sub && !setFlags && imm == 0 && !shifted && (rd == 31 || rn == 31))
                {
                    return I(w, "mov", RSp(sf, rd), RSp(sf, rn));
                }

                string mnemonic = (sub ? "sub" : "add") + (setFlags ? "s" : string.Empty);
                return I(w, mnemonic, setFlags ? R(sf, rd) : RSp(sf, rd), RSp(sf, rn), operand);
            }

            case 0b100:
            {
                uint opc = F(w, 30, 29);
                uint n = F(w, 22, 22);
                if (!sf && n == 1 || DecodeBitMask(n, F(w, 15, 10), F(w, 21, 16), sf ? 64 : 32) is not { } mask)
                {
                    return null;
                }

                var imm = Imm((long)mask);
                return opc switch
                {
                    0b00 => I(w, "and", RSp(sf, rd), R(sf, rn), imm),
                    0b01 when rn == 31 && !MoveWidePreferred(sf, n, F(w, 15, 10), F(w, 21, 16)) => I(w, "mov", RSp(sf, rd), imm),
                    0b01 => I(w, "orr", RSp(sf, rd), R(sf, rn), imm),
                    0b10 => I(w, "eor", RSp(sf, rd), R(sf, rn), imm),
                    _ when rd == 31 => I(w, "tst", R(sf, rn), imm),
                    _ => I(w, "ands", R(sf, rd), R(sf, rn), imm),
                };
            }

            case 0b101:
            {
                uint opc = F(w, 30, 29);
                uint hw = F(w, 22, 21);
                ulong imm16 = F(w, 20, 5);
                if (opc == 0b01 || (!sf && hw >= 2))
                {
                    return null;
                }

                int shift = (int)hw * 16;
                ulong width = sf ? ulong.MaxValue : 0xFFFF_FFFF;
                switch (opc)
                {
                    case 0b00 when !(imm16 == 0 && hw != 0) && (sf || imm16 != 0xFFFF):
                        return I(w, "mov", R(sf, rd), Imm(sf ? (long)~(imm16 << shift) : (long)(~(imm16 << shift) & width)));
                    case 0b10 when !(imm16 == 0 && hw != 0):
                        return I(w, "mov", R(sf, rd), Imm((long)(imm16 << shift)));
                }

                string mnemonic = opc switch { 0b00 => "movn", 0b10 => "movz", _ => "movk" };
                return shift == 0
                    ? I(w, mnemonic, R(sf, rd), Imm((long)imm16))
                    : I(w, mnemonic, R(sf, rd), new Arm64ShiftedImmediate((long)imm16, shift));
            }

            case 0b110:
                return Bitfield(w, sf, rd, rn);

            case 0b111:
            {
                // extr; ror when both sources are one register.
                if (F(w, 30, 29) != 0 || Bit(w, 22) != sf || Bit(w, 21) || (!sf && Bit(w, 15)))
                {
                    return null;
                }

                uint rm = F(w, 20, 16);
                long lsb = F(w, 15, 10);
                return rn == rm
                    ? I(w, "ror", R(sf, rd), R(sf, rn), Dec(lsb))
                    : I(w, "extr", R(sf, rd), R(sf, rn), R(sf, rm), Dec(lsb));
            }
        }

        return null;
    }

    private static Arm64Instruction? Bitfield(uint w, bool sf, uint rd, uint rn)
    {
        uint opc = F(w, 30, 29);
        uint immr = F(w, 21, 16);
        uint imms = F(w, 15, 10);
        if (opc == 0b11 || Bit(w, 22) != sf || (!sf && (immr >= 32 || imms >= 32)))
        {
            return null;
        }

        int width = sf ? 64 : 32;
        uint top = (uint)width - 1;
        long lsbInsert = (width - immr) % width;
        long insertWidth = imms + 1;
        long lsbExtract = immr;
        long extractWidth = imms - immr + 1;

        switch (opc)
        {
            case 0b00:
                if (imms == top)
                {
                    return I(w, "asr", R(sf, rd), R(sf, rn), Dec(immr));
                }

                if (imms < immr)
                {
                    return I(w, "sbfiz", R(sf, rd), R(sf, rn), Dec(lsbInsert), Dec(insertWidth));
                }

                if (BfxPreferred(sf, false, imms, immr))
                {
                    return I(w, "sbfx", R(sf, rd), R(sf, rn), Dec(lsbExtract), Dec(extractWidth));
                }

                if (immr == 0)
                {
                    switch (imms)
                    {
                        case 7: return I(w, "sxtb", R(sf, rd), W(rn));
                        case 15: return I(w, "sxth", R(sf, rd), W(rn));
                        case 31: return I(w, "sxtw", R(sf, rd), W(rn));
                    }
                }

                return I(w, "sbfm", R(sf, rd), R(sf, rn), Dec(immr), Dec(imms));

            case 0b01:
                if (imms < immr)
                {
                    return rn == 31
                        ? I(w, "bfc", R(sf, rd), Dec(lsbInsert), Dec(insertWidth))
                        : I(w, "bfi", R(sf, rd), R(sf, rn), Dec(lsbInsert), Dec(insertWidth));
                }

                return I(w, "bfxil", R(sf, rd), R(sf, rn), Dec(lsbExtract), Dec(extractWidth));

            default:
                if (imms != top && imms + 1 == immr)
                {
                    return I(w, "lsl", R(sf, rd), R(sf, rn), Dec(top - imms));
                }

                if (imms == top)
                {
                    return I(w, "lsr", R(sf, rd), R(sf, rn), Dec(immr));
                }

                if (imms < immr)
                {
                    return I(w, "ubfiz", R(sf, rd), R(sf, rn), Dec(lsbInsert), Dec(insertWidth));
                }

                if (BfxPreferred(sf, true, imms, immr))
                {
                    return I(w, "ubfx", R(sf, rd), R(sf, rn), Dec(lsbExtract), Dec(extractWidth));
                }

                if (immr == 0 && !sf)
                {
                    switch (imms)
                    {
                        case 7: return I(w, "uxtb", W(rd), W(rn));
                        case 15: return I(w, "uxth", W(rd), W(rn));
                    }
                }

                return I(w, "ubfm", R(sf, rd), R(sf, rn), Dec(immr), Dec(imms));
        }
    }

    /// <summary>The architecture's test for whether an extract reads better as <c>ubfx</c>/<c>sbfx</c> than as another alias.</summary>
    private static bool BfxPreferred(bool sf, bool unsigned, uint imms, uint immr)
    {
        if (imms < immr || imms == (sf ? 63u : 31u))
        {
            return false;
        }

        if (immr == 0)
        {
            if (!sf && imms is 7 or 15)
            {
                return false;
            }

            if (sf && !unsigned && imms is 7 or 15 or 31)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The value a logical immediate encodes: a run of ones, rotated within an element of 2 to 64 bits, repeated
    /// across the register. Null for the reserved encodings.
    /// </summary>
    internal static ulong? DecodeBitMask(uint n, uint imms, uint immr, int dataSize)
    {
        uint combined = (n << 6) | (~imms & 0x3F);
        if (combined == 0)
        {
            return null;
        }

        int len = 31 - System.Numerics.BitOperations.LeadingZeroCount(combined);
        if (len < 1)
        {
            return null;
        }

        int esize = 1 << len;
        if (esize > dataSize)
        {
            return null;
        }

        uint levels = (uint)esize - 1;
        uint s = imms & levels;
        uint r = immr & levels;
        if (s == levels)
        {
            return null;
        }

        ulong elementMask = esize == 64 ? ulong.MaxValue : (1UL << esize) - 1;
        ulong welem = s + 1 == 64 ? ulong.MaxValue : (1UL << (int)(s + 1)) - 1;
        ulong rotated = r == 0 ? welem : ((welem >> (int)r) | (welem << (esize - (int)r))) & elementMask;

        ulong result = 0;
        for (int i = 0; i < dataSize; i += esize)
        {
            result |= rotated << i;
        }

        return dataSize == 64 ? result : result & 0xFFFF_FFFF;
    }

    /// <summary>Whether a <c>movz</c> or <c>movn</c> could make the value a logical immediate encodes — then <c>orr</c> is not written as <c>mov</c>.</summary>
    private static bool MoveWidePreferred(bool sf, uint n, uint imms, uint immr)
    {
        int s = (int)imms;
        int r = (int)immr;
        int width = sf ? 64 : 32;
        if (sf && n != 1)
        {
            return false;
        }

        if (!sf && (n != 0 || (imms & 0x20) != 0))
        {
            return false;
        }

        if (s < 16)
        {
            return ((-r % 16) + 16) % 16 <= 15 - s;
        }

        if (s >= width - 15)
        {
            return r % 16 <= s - (width - 15);
        }

        return false;
    }

    // ---- branches, exceptions, system -------------------------------------------------------------------

    private static Arm64Instruction? BranchSystem(uint w, ulong pc)
    {
        // b / bl: 26-bit word offset.
        if (F(w, 30, 26) == 0b00101)
        {
            ulong target = (ulong)((long)pc + (SignExtend(F(w, 25, 0), 26) << 2));
            return Bit(w, 31)
                ? I(w, "bl", new Arm64Address(target)) with { Flow = InstructionFlow.Call, Target = target }
                : I(w, "b", new Arm64Address(target)) with { Flow = InstructionFlow.UnconditionalBranch, Target = target };
        }

        // cbz / cbnz
        if (F(w, 30, 25) == 0b011010)
        {
            bool sf = Bit(w, 31);
            ulong target = (ulong)((long)pc + (SignExtend(F(w, 23, 5), 19) << 2));
            return I(w, Bit(w, 24) ? "cbnz" : "cbz", R(sf, F(w, 4, 0)), new Arm64Address(target))
                with { Flow = InstructionFlow.ConditionalBranch, Target = target };
        }

        // tbz / tbnz: the bit number's top bit also picks the register width.
        if (F(w, 30, 25) == 0b011011)
        {
            uint bit = (F(w, 31, 31) << 5) | F(w, 23, 19);
            ulong target = (ulong)((long)pc + (SignExtend(F(w, 18, 5), 14) << 2));
            return I(w, Bit(w, 24) ? "tbnz" : "tbz", R(Bit(w, 31), F(w, 4, 0)), Dec(bit), new Arm64Address(target))
                with { Flow = InstructionFlow.ConditionalBranch, Target = target };
        }

        // b.cond, and bc.cond (FEAT_HBC)
        if (F(w, 31, 25) == 0b0101010 && !Bit(w, 24))
        {
            ulong target = (ulong)((long)pc + (SignExtend(F(w, 23, 5), 19) << 2));
            string mnemonic = (Bit(w, 4) ? "bc." : "b.") + Conditions[F(w, 3, 0)];
            bool always = F(w, 3, 0) >= 14;
            return I(w, mnemonic, new Arm64Address(target))
                with { Flow = always ? InstructionFlow.UnconditionalBranch : InstructionFlow.ConditionalBranch, Target = target };
        }

        if (F(w, 31, 24) == 0b11010100)
        {
            return Exception(w);
        }

        if (F(w, 31, 22) == 0b1101010100)
        {
            return SystemInstruction(w);
        }

        if (F(w, 31, 25) == 0b1101011)
        {
            return BranchRegister(w);
        }

        return null;
    }

    private static Arm64Instruction? Exception(uint w)
    {
        uint opc = F(w, 23, 21);
        uint ll = F(w, 1, 0);
        var imm = Imm(F(w, 20, 5));
        if (F(w, 4, 2) != 0)
        {
            return null;
        }

        return (opc, ll) switch
        {
            (0b000, 0b01) => I(w, "svc", imm),
            (0b000, 0b10) => I(w, "hvc", imm),
            (0b000, 0b11) => I(w, "smc", imm),
            (0b001, 0b00) => I(w, "brk", imm) with { Flow = InstructionFlow.Interrupt },
            (0b010, 0b00) => I(w, "hlt", imm) with { Flow = InstructionFlow.Interrupt },
            (0b011, 0b00) => I(w, "tcancel", imm),
            (0b101, 0b01) => F(w, 20, 5) == 0 ? I(w, "dcps1") : I(w, "dcps1", imm),
            (0b101, 0b10) => F(w, 20, 5) == 0 ? I(w, "dcps2") : I(w, "dcps2", imm),
            (0b101, 0b11) => F(w, 20, 5) == 0 ? I(w, "dcps3") : I(w, "dcps3", imm),
            _ => null,
        };
    }

    private static Arm64Instruction? BranchRegister(uint w)
    {
        uint opc = F(w, 24, 21);
        uint op2 = F(w, 20, 16);
        uint op3 = F(w, 15, 10);
        uint rn = F(w, 9, 5);
        uint op4 = F(w, 4, 0);
        if (op2 != 0b11111)
        {
            return null;
        }

        switch (opc, op3)
        {
            case (0b0000, 0) when op4 == 0:
                return I(w, "br", X(rn)) with { Flow = InstructionFlow.IndirectBranch };
            case (0b0001, 0) when op4 == 0:
                return I(w, "blr", X(rn)) with { Flow = InstructionFlow.IndirectCall };
            case (0b0010, 0) when op4 == 0:
                return (rn == 30 ? I(w, "ret") : I(w, "ret", X(rn))) with { Flow = InstructionFlow.Return };
            case (0b0100, 0) when rn == 31 && op4 == 0:
                return I(w, "eret") with { Flow = InstructionFlow.Return };
            case (0b0101, 0) when rn == 31 && op4 == 0:
                return I(w, "drps") with { Flow = InstructionFlow.Return };

            // Pointer authentication: the target is authenticated with a key before the branch.
            case (0b0000, 0b000010 or 0b000011) when op4 == 31:
                return I(w, op3 == 2 ? "braaz" : "brabz", X(rn)) with { Flow = InstructionFlow.IndirectBranch };
            case (0b0001, 0b000010 or 0b000011) when op4 == 31:
                return I(w, op3 == 2 ? "blraaz" : "blrabz", X(rn)) with { Flow = InstructionFlow.IndirectCall };
            case (0b0010, 0b000010 or 0b000011) when rn == 31 && op4 == 31:
                return I(w, op3 == 2 ? "retaa" : "retab") with { Flow = InstructionFlow.Return };
            case (0b0100, 0b000010 or 0b000011) when rn == 31 && op4 == 31:
                return I(w, op3 == 2 ? "eretaa" : "eretab") with { Flow = InstructionFlow.Return };
            case (0b1000, 0b000010 or 0b000011):
                return I(w, op3 == 2 ? "braa" : "brab", X(rn), XSp(op4)) with { Flow = InstructionFlow.IndirectBranch };
            case (0b1001, 0b000010 or 0b000011):
                return I(w, op3 == 2 ? "blraa" : "blrab", X(rn), XSp(op4)) with { Flow = InstructionFlow.IndirectCall };
        }

        return null;
    }
}

/// <summary>An immediate shifted left: <c>#0x1, lsl #12</c> in <c>add</c>, <c>#0xffff, lsl #16</c> in <c>movk</c>.</summary>
public sealed record Arm64ShiftedImmediate(long Value, int Shift, Arm64Shift Kind = Arm64Shift.Lsl) : Arm64Operand
{
    public override string Format(Func<ulong, string?>? name)
        => $"#{Hex((ulong)Value)}, {Kind.ToString().ToLowerInvariant()} #{Shift}";
}
