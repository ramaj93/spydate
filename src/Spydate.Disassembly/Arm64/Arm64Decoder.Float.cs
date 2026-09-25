namespace Spydate.Disassembly.Arm64;

public static partial class Arm64Decoder
{
    private static Arm64Instruction? SimdFp(uint w)
    {
        // Scalar floating point: bit 30 clear, bits 28:24 11110 (or 11111 for the three-source forms).
        if (!Bit(w, 30) && !Bit(w, 29))
        {
            if (F(w, 28, 24) == 0b11111)
            {
                return Bit(w, 31) ? null : FloatThreeSource(w);
            }

            if (F(w, 28, 24) == 0b11110)
            {
                return ScalarFloat(w);
            }
        }

        return AdvancedSimd(w);
    }

    /// <summary>A scalar floating-point register of the width the type field says, or null for the reserved type.</summary>
    private static Arm64RegisterKind? FloatKind(uint type) => type switch
    {
        0b00 => Arm64RegisterKind.S,
        0b01 => Arm64RegisterKind.D,
        0b11 => Arm64RegisterKind.H,
        _ => null,
    };

    private static Arm64Instruction? FloatThreeSource(uint w)
    {
        if (FloatKind(F(w, 23, 22)) is not { } k)
        {
            return null;
        }

        string name = (Bit(w, 21), Bit(w, 15)) switch
        {
            (false, false) => "fmadd",
            (false, true) => "fmsub",
            (true, false) => "fnmadd",
            _ => "fnmsub",
        };
        return I(w, name, V(k, F(w, 4, 0)), V(k, F(w, 9, 5)), V(k, F(w, 20, 16)), V(k, F(w, 14, 10)));
    }

    private static Arm64Instruction? ScalarFloat(uint w)
    {
        uint type = F(w, 23, 22);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        uint rm = F(w, 20, 16);

        if (!Bit(w, 21))
        {
            return FixedPointConversion(w, type, rd, rn);
        }

        // Only the conversions to and from general registers use bit 31 (as sf); for everything else it is M, which is 0.
        if (Bit(w, 31) && !(F(w, 11, 10) == 0 && F(w, 15, 10) == 0))
        {
            return null;
        }

        switch (F(w, 11, 10))
        {
            case 0b01:
            {
                if (FloatKind(type) is not { } k)
                {
                    return null;
                }

                return I(w, Bit(w, 4) ? "fccmpe" : "fccmp", V(k, rn), V(k, rm), Imm(F(w, 3, 0)), Cond(F(w, 15, 12)));
            }

            case 0b10:
            {
                string? name = F(w, 15, 12) switch
                {
                    0b0000 => "fmul",
                    0b0001 => "fdiv",
                    0b0010 => "fadd",
                    0b0011 => "fsub",
                    0b0100 => "fmax",
                    0b0101 => "fmin",
                    0b0110 => "fmaxnm",
                    0b0111 => "fminnm",
                    0b1000 => "fnmul",
                    _ => null,
                };
                return name is null || FloatKind(type) is not { } k ? null : I(w, name, V(k, rd), V(k, rn), V(k, rm));
            }

            case 0b11:
                return FloatKind(type) is not { } kind ? null : I(w, "fcsel", V(kind, rd), V(kind, rn), V(kind, rm), Cond(F(w, 15, 12)));
        }

        if (F(w, 15, 10) == 0)
        {
            return IntegerConversion(w, type, rd, rn);
        }

        if (F(w, 14, 10) == 0b10000)
        {
            return FloatOneSource(w, type, rd, rn);
        }

        if (F(w, 13, 10) == 0b1000)
        {
            if (FloatKind(type) is not { } k || F(w, 15, 14) != 0 || (F(w, 2, 0) != 0))
            {
                return null;
            }

            bool zero = Bit(w, 3);
            string name = Bit(w, 4) ? "fcmpe" : "fcmp";
            return zero ? I(w, name, V(k, rn), new Arm64FloatImmediate(0)) : I(w, name, V(k, rn), V(k, rm));
        }

        if (F(w, 12, 10) == 0b100)
        {
            return F(w, 9, 5) != 0 || FloatKind(type) is not { } k ? null : I(w, "fmov", V(k, rd), new Arm64FloatImmediate(ExpandFloat(F(w, 20, 13))));
        }

        return null;
    }

    /// <summary>The value an 8-bit floating-point immediate stands for: ±(16 + fraction)/16 × 2^exponent.</summary>
    internal static double ExpandFloat(uint imm8)
    {
        bool negative = (imm8 & 0x80) != 0;
        uint b = (imm8 >> 6) & 1;
        int cd = (int)((imm8 >> 4) & 3);
        int exponent = b == 0 ? cd + 1 : cd - 3;
        double value = (16 + (imm8 & 0xF)) / 16.0 * Math.Pow(2, exponent);
        return negative ? -value : value;
    }

    private static Arm64Instruction? FloatOneSource(uint w, uint type, uint rd, uint rn)
    {
        uint opcode = F(w, 20, 15);
        if (FloatKind(type) is not { } k)
        {
            return null;
        }

        if (opcode >> 2 == 0b0001)
        {
            // fcvt between precisions; the low bits are the destination's type.
            if (opcode == 0b000110)
            {
                return type == 0b01 ? I(w, "bfcvt", V(Arm64RegisterKind.H, rd), V(Arm64RegisterKind.S, rn)) : null;
            }

            uint to = opcode & 3;
            return to == type || FloatKind(to) is not { } target ? null : I(w, "fcvt", V(target, rd), V(k, rn));
        }

        string? name = opcode switch
        {
            0b000000 => "fmov",
            0b000001 => "fabs",
            0b000010 => "fneg",
            0b000011 => "fsqrt",
            0b001000 => "frintn",
            0b001001 => "frintp",
            0b001010 => "frintm",
            0b001011 => "frintz",
            0b001100 => "frinta",
            0b001110 => "frintx",
            0b001111 => "frinti",
            0b010000 when type != 0b11 => "frint32z",
            0b010001 when type != 0b11 => "frint32x",
            0b010010 when type != 0b11 => "frint64z",
            0b010011 when type != 0b11 => "frint64x",
            _ => null,
        };

        return name is null ? null : I(w, name, V(k, rd), V(k, rn));
    }

    private static Arm64Instruction? IntegerConversion(uint w, uint type, uint rd, uint rn)
    {
        bool sf = Bit(w, 31);
        uint rmode = F(w, 20, 19);
        uint opcode = F(w, 18, 16);

        // fmov between a general register and the top half of a vector register.
        if (type == 0b10)
        {
            return (sf, rmode, opcode) switch
            {
                (true, 0b01, 0b110) => I(w, "fmov", X(rd), T($"v{rn}.d[1]")),
                (true, 0b01, 0b111) => I(w, "fmov", T($"v{rd}.d[1]"), X(rn)),
                _ => null,
            };
        }

        if (FloatKind(type) is not { } k)
        {
            return null;
        }

        switch (rmode, opcode)
        {
            case (0b00, 0b110) when (!sf && type == 0b00) || (sf && type == 0b01) || type == 0b11:
                return I(w, "fmov", R(sf, rd), V(k, rn));
            case (0b00, 0b111) when (!sf && type == 0b00) || (sf && type == 0b01) || type == 0b11:
                return I(w, "fmov", V(k, rd), R(sf, rn));
            case (0b00, 0b010):
                return I(w, "scvtf", V(k, rd), R(sf, rn));
            case (0b00, 0b011):
                return I(w, "ucvtf", V(k, rd), R(sf, rn));
            case (0b11, 0b110) when !sf && type == 0b01:
                return I(w, "fjcvtzs", W(rd), V(k, rn));
        }

        string? name = (rmode, opcode) switch
        {
            (0b00, 0b000) => "fcvtns",
            (0b00, 0b001) => "fcvtnu",
            (0b00, 0b100) => "fcvtas",
            (0b00, 0b101) => "fcvtau",
            (0b01, 0b000) => "fcvtps",
            (0b01, 0b001) => "fcvtpu",
            (0b10, 0b000) => "fcvtms",
            (0b10, 0b001) => "fcvtmu",
            (0b11, 0b000) => "fcvtzs",
            (0b11, 0b001) => "fcvtzu",
            _ => null,
        };

        return name is null ? null : I(w, name, R(sf, rd), V(k, rn));
    }

    private static Arm64Instruction? FixedPointConversion(uint w, uint type, uint rd, uint rn)
    {
        bool sf = Bit(w, 31);
        uint scale = F(w, 15, 10);
        if (FloatKind(type) is not { } k || (!sf && scale < 32))
        {
            return null;
        }

        var fbits = Dec(64 - scale);
        return (F(w, 20, 19), F(w, 18, 16)) switch
        {
            (0b11, 0b000) => I(w, "fcvtzs", R(sf, rd), V(k, rn), fbits),
            (0b11, 0b001) => I(w, "fcvtzu", R(sf, rd), V(k, rn), fbits),
            (0b00, 0b010) => I(w, "scvtf", V(k, rd), R(sf, rn), fbits),
            (0b00, 0b011) => I(w, "ucvtf", V(k, rd), R(sf, rn), fbits),
            _ => null,
        };
    }
}
