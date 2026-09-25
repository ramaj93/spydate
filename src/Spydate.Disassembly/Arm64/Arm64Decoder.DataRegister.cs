namespace Spydate.Disassembly.Arm64;

public static partial class Arm64Decoder
{
    private static Arm64Instruction? DataRegister(uint w)
    {
        bool sf = Bit(w, 31);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        uint rm = F(w, 20, 16);

        if (!Bit(w, 28))
        {
            if (!Bit(w, 24))
            {
                return LogicalShifted(w, sf, rd, rn, rm);
            }

            return Bit(w, 21) ? AddSubExtended(w, sf, rd, rn, rm) : AddSubShifted(w, sf, rd, rn, rm);
        }

        if (Bit(w, 24))
        {
            return ThreeSource(w, sf, rd, rn, rm);
        }

        switch (F(w, 23, 21))
        {
            case 0b000 when F(w, 15, 10) == 0:
            {
                // adc / sbc, and ngc for sbc from the zero register.
                bool sub = Bit(w, 30);
                bool flags = Bit(w, 29);
                string suffix = flags ? "s" : string.Empty;
                return sub && rn == 31
                    ? I(w, "ngc" + suffix, R(sf, rd), R(sf, rm))
                    : I(w, (sub ? "sbc" : "adc") + suffix, R(sf, rd), R(sf, rn), R(sf, rm));
            }

            case 0b010:
                return ConditionalCompare(w, sf, rn, rm);

            case 0b100:
                return ConditionalSelect(w, sf, rd, rn, rm);

            case 0b110:
                return Bit(w, 30) ? OneSource(w, sf, rd, rn) : TwoSource(w, sf, rd, rn, rm);
        }

        return null;
    }

    private static Arm64Shift ShiftOf(uint field) => field switch
    {
        0 => Arm64Shift.Lsl,
        1 => Arm64Shift.Lsr,
        2 => Arm64Shift.Asr,
        _ => Arm64Shift.Ror,
    };

    private static Arm64Instruction? LogicalShifted(uint w, bool sf, uint rd, uint rn, uint rm)
    {
        uint opc = F(w, 30, 29);
        bool invert = Bit(w, 21);
        uint amount = F(w, 15, 10);
        if (!sf && amount >= 32)
        {
            return null;
        }

        var shift = ShiftOf(F(w, 23, 22));
        var operand = new Arm64ShiftedRegister(Reg(sf, rm), shift, (int)amount);
        switch (opc, invert)
        {
            case (0b01, false) when rn == 31 && amount == 0 && shift == Arm64Shift.Lsl:
                return I(w, "mov", R(sf, rd), R(sf, rm));
            case (0b01, true) when rn == 31:
                return I(w, "mvn", R(sf, rd), operand);
            case (0b11, false) when rd == 31:
                return I(w, "tst", R(sf, rn), operand);
        }

        string name = (opc, invert) switch
        {
            (0b00, false) => "and",
            (0b00, true) => "bic",
            (0b01, false) => "orr",
            (0b01, true) => "orn",
            (0b10, false) => "eor",
            (0b10, true) => "eon",
            (0b11, false) => "ands",
            _ => "bics",
        };

        return I(w, name, R(sf, rd), R(sf, rn), operand);
    }

    private static Arm64Instruction? AddSubShifted(uint w, bool sf, uint rd, uint rn, uint rm)
    {
        bool sub = Bit(w, 30);
        bool flags = Bit(w, 29);
        uint shift = F(w, 23, 22);
        uint amount = F(w, 15, 10);
        if (shift == 3 || (!sf && amount >= 32))
        {
            return null;
        }

        var operand = new Arm64ShiftedRegister(Reg(sf, rm), ShiftOf(shift), (int)amount);
        if (flags && rd == 31)
        {
            return I(w, sub ? "cmp" : "cmn", R(sf, rn), operand);
        }

        if (sub && rn == 31)
        {
            return I(w, flags ? "negs" : "neg", R(sf, rd), operand);
        }

        return I(w, (sub ? "sub" : "add") + (flags ? "s" : string.Empty), R(sf, rd), R(sf, rn), operand);
    }

    private static Arm64Instruction? AddSubExtended(uint w, bool sf, uint rd, uint rn, uint rm)
    {
        bool sub = Bit(w, 30);
        bool flags = Bit(w, 29);
        uint option = F(w, 15, 13);
        uint amount = F(w, 12, 10);
        if (F(w, 23, 22) != 0 || amount > 4)
        {
            return null;
        }

        // The index is a W register unless the extension is from 64 bits.
        bool wide = sf && (option & 3) == 3;
        var extend = (Arm64Extend)option;

        // Where the stack pointer is involved, the extension that changes nothing is written lsl.
        if ((rd == 31 || rn == 31) && option == (sf ? 0b011u : 0b010u))
        {
            extend = Arm64Extend.Lsl;
        }

        var operand = new Arm64ExtendedRegister(Reg(wide, rm), extend, (int)amount);
        if (flags && rd == 31)
        {
            return I(w, sub ? "cmp" : "cmn", RSp(sf, rn), operand);
        }

        return I(w, (sub ? "sub" : "add") + (flags ? "s" : string.Empty), flags ? R(sf, rd) : RSp(sf, rd), RSp(sf, rn), operand);
    }

    private static Arm64Instruction? ConditionalCompare(uint w, bool sf, uint rn, uint rm)
    {
        if (!Bit(w, 29) || Bit(w, 10) || Bit(w, 4))
        {
            return null;
        }

        string name = Bit(w, 30) ? "ccmp" : "ccmn";
        Arm64Operand second = Bit(w, 11) ? Imm(rm) : R(sf, rm);
        return I(w, name, R(sf, rn), second, Imm(F(w, 3, 0)), Cond(F(w, 15, 12)));
    }

    private static Arm64Instruction? ConditionalSelect(uint w, bool sf, uint rd, uint rn, uint rm)
    {
        if (Bit(w, 29) || Bit(w, 11))
        {
            return null;
        }

        uint cond = F(w, 15, 12);
        bool op = Bit(w, 30);
        bool o2 = Bit(w, 10);
        bool invertible = cond < 14;
        var inverse = Cond(cond ^ 1);

        switch (op, o2)
        {
            case (false, false):
                return I(w, "csel", R(sf, rd), R(sf, rn), R(sf, rm), Cond(cond));
            case (false, true):
                if (invertible && rn == rm)
                {
                    return rn == 31 ? I(w, "cset", R(sf, rd), inverse) : I(w, "cinc", R(sf, rd), R(sf, rn), inverse);
                }

                return I(w, "csinc", R(sf, rd), R(sf, rn), R(sf, rm), Cond(cond));
            case (true, false):
                if (invertible && rn == rm)
                {
                    return rn == 31 ? I(w, "csetm", R(sf, rd), inverse) : I(w, "cinv", R(sf, rd), R(sf, rn), inverse);
                }

                return I(w, "csinv", R(sf, rd), R(sf, rn), R(sf, rm), Cond(cond));
            default:
                return invertible && rn == rm
                    ? I(w, "cneg", R(sf, rd), R(sf, rn), inverse)
                    : I(w, "csneg", R(sf, rd), R(sf, rn), R(sf, rm), Cond(cond));
        }
    }

    private static Arm64Instruction? TwoSource(uint w, bool sf, uint rd, uint rn, uint rm)
    {
        uint opcode = F(w, 15, 10);
        bool flags = Bit(w, 29);
        if (flags)
        {
            return opcode == 0 && sf ? (rd == 31 ? I(w, "cmpp", XSp(rn), XSp(rm)) : I(w, "subps", X(rd), XSp(rn), XSp(rm))) : null;
        }

        switch (opcode)
        {
            case 0b000010: return I(w, "udiv", R(sf, rd), R(sf, rn), R(sf, rm));
            case 0b000011: return I(w, "sdiv", R(sf, rd), R(sf, rn), R(sf, rm));
            case 0b001000: return I(w, "lsl", R(sf, rd), R(sf, rn), R(sf, rm));
            case 0b001001: return I(w, "lsr", R(sf, rd), R(sf, rn), R(sf, rm));
            case 0b001010: return I(w, "asr", R(sf, rd), R(sf, rn), R(sf, rm));
            case 0b001011: return I(w, "ror", R(sf, rd), R(sf, rn), R(sf, rm));
            case 0b000000 when sf: return I(w, "subp", X(rd), XSp(rn), XSp(rm));
            case 0b000100 when sf: return I(w, "irg", XSp(rd), XSp(rn), X(rm));
            case 0b000101 when sf: return I(w, "gmi", X(rd), XSp(rn), X(rm));
            case 0b001100 when sf: return I(w, "pacga", X(rd), X(rn), XSp(rm));
            case 0b011000: return I(w, "smax", R(sf, rd), R(sf, rn), R(sf, rm));
            case 0b011001: return I(w, "umax", R(sf, rd), R(sf, rn), R(sf, rm));
            case 0b011010: return I(w, "smin", R(sf, rd), R(sf, rn), R(sf, rm));
            case 0b011011: return I(w, "umin", R(sf, rd), R(sf, rn), R(sf, rm));
        }

        // crc32b/h/w/x and crc32cb/ch/cw/cx: the data register is X only for the x forms.
        if (opcode >> 3 == 0b010)
        {
            uint size = opcode & 3;
            if ((size == 3) != sf)
            {
                return null;
            }

            string name = "crc32" + ((opcode & 4) != 0 ? "c" : string.Empty) + (size switch { 0 => "b", 1 => "h", 2 => "w", _ => "x" });
            return I(w, name, W(rd), W(rn), R(size == 3, rm));
        }

        return null;
    }

    private static Arm64Instruction? OneSource(uint w, bool sf, uint rd, uint rn)
    {
        if (Bit(w, 29))
        {
            return null;
        }

        uint opcode2 = F(w, 20, 16);
        uint opcode = F(w, 15, 10);
        if (opcode2 == 0)
        {
            return opcode switch
            {
                0b000000 => I(w, "rbit", R(sf, rd), R(sf, rn)),
                0b000001 => I(w, "rev16", R(sf, rd), R(sf, rn)),
                0b000010 => I(w, sf ? "rev32" : "rev", R(sf, rd), R(sf, rn)),
                0b000011 when sf => I(w, "rev", R(sf, rd), R(sf, rn)),
                0b000100 => I(w, "clz", R(sf, rd), R(sf, rn)),
                0b000101 => I(w, "cls", R(sf, rd), R(sf, rn)),
                0b000110 => I(w, "ctz", R(sf, rd), R(sf, rn)),
                0b000111 => I(w, "cnt", R(sf, rd), R(sf, rn)),
                0b001000 => I(w, "abs", R(sf, rd), R(sf, rn)),
                _ => null,
            };
        }

        if (opcode2 != 1 || !sf)
        {
            return null;
        }

        // Pointer authentication on a general register.
        string[] names = ["pacia", "pacib", "pacda", "pacdb", "autia", "autib", "autda", "autdb"];
        if (opcode < 8)
        {
            return I(w, names[opcode], X(rd), XSp(rn));
        }

        if (opcode < 16 && rn == 31)
        {
            string name = names[opcode - 8];
            return I(w, name[..4] + "z" + name[4..], X(rd));
        }

        return (opcode, rn) switch
        {
            (0b010000, 31) => I(w, "xpaci", X(rd)),
            (0b010001, 31) => I(w, "xpacd", X(rd)),
            _ => null,
        };
    }

    private static Arm64Instruction? ThreeSource(uint w, bool sf, uint rd, uint rn, uint rm)
    {
        uint op31 = F(w, 23, 21);
        bool o0 = Bit(w, 15);
        uint ra = F(w, 14, 10);
        if (F(w, 30, 29) != 0)
        {
            return null;
        }

        if (op31 == 0)
        {
            if (ra == 31)
            {
                return I(w, o0 ? "mneg" : "mul", R(sf, rd), R(sf, rn), R(sf, rm));
            }

            return I(w, o0 ? "msub" : "madd", R(sf, rd), R(sf, rn), R(sf, rm), R(sf, ra));
        }

        if (!sf)
        {
            return null;
        }

        switch (op31, o0)
        {
            case (0b010, false):
                return I(w, "smulh", X(rd), X(rn), X(rm));
            case (0b110, false):
                return I(w, "umulh", X(rd), X(rn), X(rm));
            case (0b001, _) or (0b101, _):
            {
                bool unsigned = op31 == 0b101;
                string prefix = unsigned ? "u" : "s";
                if (ra == 31)
                {
                    return I(w, prefix + (o0 ? "mnegl" : "mull"), X(rd), W(rn), W(rm));
                }

                return I(w, prefix + (o0 ? "msubl" : "maddl"), X(rd), W(rn), W(rm), X(ra));
            }
        }

        return null;
    }
}
