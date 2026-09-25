using System.Globalization;

namespace Spydate.Disassembly.Arm64;

public static partial class Arm64Decoder
{
    // ---- vector operands ----------------------------------------------------------------------------------

    /// <summary>A vector arrangement from an element size and Q: <c>8b</c>/<c>16b</c> … <c>1d</c>/<c>2d</c>.</summary>
    private static string Arr(uint size, bool q) => (size & 3) switch
    {
        0 => q ? "16b" : "8b",
        1 => q ? "8h" : "4h",
        2 => q ? "4s" : "2s",
        _ => q ? "2d" : "1d",
    };

    private static char ElementChar(uint size) => "bhsd"[(int)(size & 3)];

    private static Arm64RegisterKind ScalarKind(uint size) => (size & 3) switch
    {
        0 => Arm64RegisterKind.B,
        1 => Arm64RegisterKind.H,
        2 => Arm64RegisterKind.S,
        _ => Arm64RegisterKind.D,
    };

    private static Arm64Text Vec(uint n, string arrangement) => T(string.Create(CultureInfo.InvariantCulture, $"v{n}.{arrangement}"));

    private static Arm64Text Element(uint n, char type, uint index) => T(string.Create(CultureInfo.InvariantCulture, $"v{n}.{type}[{index}]"));

    /// <summary>A register list: <c>{v0.16b, v1.16b}</c>, or a range from three registers on, <c>{v0.16b-v3.16b}</c>.</summary>
    private static string ListText(uint first, int count, string suffix)
    {
        if (count >= 3 && first + count - 1 <= 31)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{{v{first}{suffix}-v{first + count - 1}{suffix}}}");
        }

        return "{" + string.Join(", ", Enumerable.Range(0, count).Select(i => string.Create(CultureInfo.InvariantCulture, $"v{(first + i) % 32}{suffix}"))) + "}";
    }

    private static Arm64Instruction? AdvancedSimd(uint w)
    {
        uint op0 = F(w, 31, 28);

        // Cryptographic extensions with their own fixed prefixes.
        if (F(w, 31, 24) == 0b01001110 && F(w, 21, 17) == 0b10100 && F(w, 11, 10) == 0b10)
        {
            return Aes(w);
        }

        if (F(w, 31, 24) == 0b01011110)
        {
            if (!Bit(w, 21) && !Bit(w, 15) && F(w, 11, 10) == 0)
            {
                return ShaThree(w);
            }

            if (F(w, 21, 17) == 0b10100 && F(w, 11, 10) == 0b10)
            {
                return ShaTwo(w);
            }
        }

        if (F(w, 31, 24) == 0b11001110)
        {
            return Sha512(w);
        }

        bool scalar = (op0 & 0b1101) == 0b0101;
        bool vector = (op0 & 0b1001) == 0b0000;
        if (!scalar && !vector)
        {
            return null;
        }

        uint op1 = F(w, 24, 23);
        if (scalar)
        {
            if (op1 == 0b00 && F(w, 22, 21) == 0b00 && !Bit(w, 15) && Bit(w, 10) && F(w, 28, 21) == 0b11110000)
            {
                return ScalarCopy(w);
            }

            if (F(w, 28, 24) == 0b11110)
            {
                if (Bit(w, 21) && Bit(w, 10))
                {
                    return ScalarThreeSame(w);
                }

                if (Bit(w, 21) && F(w, 11, 10) == 0)
                {
                    return ScalarThreeDifferent(w);
                }

                if (F(w, 21, 17) == 0b10000 && F(w, 11, 10) == 0b10)
                {
                    return ScalarTwoMisc(w);
                }

                if (F(w, 21, 17) == 0b11000 && F(w, 11, 10) == 0b10)
                {
                    return ScalarPairwise(w);
                }

                return null;
            }

            if (F(w, 28, 23) == 0b111110 && Bit(w, 10))
            {
                return ScalarShift(w);
            }

            if (F(w, 28, 24) == 0b11111 && !Bit(w, 10))
            {
                return IndexedElement(w, scalar: true);
            }

            return null;
        }

        if (F(w, 29, 24) == 0b001110 && !Bit(w, 21) && !Bit(w, 15) && !Bit(w, 10))
        {
            return F(w, 11, 10) switch
            {
                0b00 => TableLookup(w),
                0b10 => Permute(w),
                _ => null,
            };
        }

        if (F(w, 29, 24) == 0b101110 && F(w, 23, 21) == 0 && !Bit(w, 15) && !Bit(w, 10))
        {
            bool q = Bit(w, 30);
            uint index = F(w, 14, 11);
            if (!q && index >= 8)
            {
                return null;
            }

            string arrangement = q ? "16b" : "8b";
            return I(w, "ext", Vec(F(w, 4, 0), arrangement), Vec(F(w, 9, 5), arrangement), Vec(F(w, 20, 16), arrangement), Imm(index));
        }

        if (F(w, 28, 21) == 0b01110000 && !Bit(w, 15) && Bit(w, 10))
        {
            return Copy(w);
        }

        if (F(w, 28, 24) == 0b01110)
        {
            if (!Bit(w, 21) && Bit(w, 15) && Bit(w, 10))
            {
                return ThreeRegisterExtension(w);
            }

            if (Bit(w, 21) && Bit(w, 10))
            {
                return ThreeSame(w);
            }

            if (Bit(w, 21) && F(w, 11, 10) == 0)
            {
                return ThreeDifferent(w);
            }

            if (F(w, 21, 17) == 0b10000 && F(w, 11, 10) == 0b10)
            {
                return TwoMisc(w);
            }

            if (F(w, 21, 17) == 0b11000 && F(w, 11, 10) == 0b10)
            {
                return AcrossLanes(w);
            }

            return null;
        }

        if (F(w, 28, 23) == 0b011110 && Bit(w, 10))
        {
            return F(w, 22, 19) == 0 ? ModifiedImmediate(w) : ShiftByImmediate(w);
        }

        if (F(w, 28, 24) == 0b01111 && !Bit(w, 10))
        {
            return IndexedElement(w, scalar: false);
        }

        return null;
    }

    // ---- cryptography -------------------------------------------------------------------------------------

    private static Arm64Instruction? Aes(uint w)
    {
        if (F(w, 23, 22) != 0)
        {
            return null;
        }

        string? name = F(w, 16, 12) switch
        {
            0b00100 => "aese",
            0b00101 => "aesd",
            0b00110 => "aesmc",
            0b00111 => "aesimc",
            _ => null,
        };
        return name is null ? null : I(w, name, Vec(F(w, 4, 0), "16b"), Vec(F(w, 9, 5), "16b"));
    }

    private static Arm64Instruction? ShaThree(uint w)
    {
        if (F(w, 23, 22) != 0)
        {
            return null;
        }

        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        uint rm = F(w, 20, 16);
        return F(w, 14, 12) switch
        {
            0b000 => I(w, "sha1c", V(Arm64RegisterKind.Q, rd), V(Arm64RegisterKind.S, rn), Vec(rm, "4s")),
            0b001 => I(w, "sha1p", V(Arm64RegisterKind.Q, rd), V(Arm64RegisterKind.S, rn), Vec(rm, "4s")),
            0b010 => I(w, "sha1m", V(Arm64RegisterKind.Q, rd), V(Arm64RegisterKind.S, rn), Vec(rm, "4s")),
            0b011 => I(w, "sha1su0", Vec(rd, "4s"), Vec(rn, "4s"), Vec(rm, "4s")),
            0b100 => I(w, "sha256h", V(Arm64RegisterKind.Q, rd), V(Arm64RegisterKind.Q, rn), Vec(rm, "4s")),
            0b101 => I(w, "sha256h2", V(Arm64RegisterKind.Q, rd), V(Arm64RegisterKind.Q, rn), Vec(rm, "4s")),
            0b110 => I(w, "sha256su1", Vec(rd, "4s"), Vec(rn, "4s"), Vec(rm, "4s")),
            _ => null,
        };
    }

    private static Arm64Instruction? ShaTwo(uint w)
    {
        if (F(w, 23, 22) != 0)
        {
            return null;
        }

        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        return F(w, 16, 12) switch
        {
            0b00000 => I(w, "sha1h", V(Arm64RegisterKind.S, rd), V(Arm64RegisterKind.S, rn)),
            0b00001 => I(w, "sha1su1", Vec(rd, "4s"), Vec(rn, "4s")),
            0b00010 => I(w, "sha256su0", Vec(rd, "4s"), Vec(rn, "4s")),
            _ => null,
        };
    }

    /// <summary>SHA-3 and SHA-512: <c>eor3</c>, <c>bcax</c>, <c>rax1</c>, <c>xar</c>, <c>sha512h</c> and the rest.</summary>
    private static Arm64Instruction? Sha512(uint w)
    {
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        uint rm = F(w, 20, 16);
        uint ra = F(w, 14, 10);

        if (F(w, 23, 23) == 0 && !Bit(w, 15))
        {
            return F(w, 22, 21) switch
            {
                0b00 => I(w, "eor3", Vec(rd, "16b"), Vec(rn, "16b"), Vec(rm, "16b"), Vec(ra, "16b")),
                0b01 => I(w, "bcax", Vec(rd, "16b"), Vec(rn, "16b"), Vec(rm, "16b"), Vec(ra, "16b")),
                0b10 => I(w, "sm3ss1", Vec(rd, "4s"), Vec(rn, "4s"), Vec(rm, "4s"), Vec(ra, "4s")),
                _ => null,
            };
        }

        if (F(w, 23, 21) == 0b100)
        {
            return I(w, "xar", Vec(rd, "2d"), Vec(rn, "2d"), Vec(rm, "2d"), Imm(F(w, 15, 10)));
        }

        if (F(w, 23, 21) == 0b011 && F(w, 15, 14) == 0b10)
        {
            return (Bit(w, 11), F(w, 10, 10), F(w, 13, 12)) switch
            {
                (false, 0, 0b00) => I(w, "sha512h", V(Arm64RegisterKind.Q, rd), V(Arm64RegisterKind.Q, rn), Vec(rm, "2d")),
                (false, 1, 0b00) => I(w, "sha512h2", V(Arm64RegisterKind.Q, rd), V(Arm64RegisterKind.Q, rn), Vec(rm, "2d")),
                (true, 0, 0b00) => I(w, "sha512su1", Vec(rd, "2d"), Vec(rn, "2d"), Vec(rm, "2d")),
                (true, 1, 0b00) => I(w, "rax1", Vec(rd, "2d"), Vec(rn, "2d"), Vec(rm, "2d")),
                _ => null,
            };
        }

        if (F(w, 23, 10) == 0b11000000100000)
        {
            return I(w, "sha512su0", Vec(rd, "2d"), Vec(rn, "2d"));
        }

        if (F(w, 23, 10) == 0b11000000100001)
        {
            return I(w, "sm4e", Vec(rd, "4s"), Vec(rn, "4s"));
        }

        return null;
    }

    // ---- copies, permutes, tables ------------------------------------------------------------------------

    /// <summary>The element size and index an <c>imm5</c> field encodes: the lowest set bit is the size.</summary>
    private static (uint Size, uint Index)? ElementOf(uint imm5)
    {
        for (uint size = 0; size < 4; size++)
        {
            if ((imm5 & (1u << (int)size)) != 0)
            {
                return (size, imm5 >> (int)(size + 1));
            }
        }

        return null;
    }

    private static Arm64Instruction? ScalarCopy(uint w)
    {
        if (Bit(w, 29) || F(w, 14, 11) != 0 || ElementOf(F(w, 20, 16)) is not var (size, index))
        {
            return null;
        }

        return I(w, "mov", V(ScalarKind(size), F(w, 4, 0)), Element(F(w, 9, 5), ElementChar(size), index));
    }

    private static Arm64Instruction? Copy(uint w)
    {
        bool q = Bit(w, 30);
        bool op = Bit(w, 29);
        uint imm4 = F(w, 14, 11);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        if (ElementOf(F(w, 20, 16)) is not var (size, index))
        {
            return null;
        }

        char type = ElementChar(size);
        if (op)
        {
            // ins (element): the source index is in imm4, at the element's size.
            return q ? I(w, "mov", Element(rd, type, index), Element(rn, type, imm4 >> (int)size)) : null;
        }

        switch (imm4)
        {
            case 0b0000 when size != 3 || q:
                return I(w, "dup", Vec(rd, Arr(size, q)), Element(rn, type, index));
            case 0b0001 when size != 3 || q:
                return I(w, "dup", Vec(rd, Arr(size, q)), R(size == 3, rn));
            case 0b0011 when q:
                return I(w, "mov", Element(rd, type, index), R(size == 3, rn));
            case 0b0101 when size < 2 || (size == 2 && q):
                return I(w, "smov", R(q, rd), Element(rn, type, index));
            case 0b0111 when (size < 3 && !q) || (size == 3 && q):
                return I(w, size >= 2 ? "mov" : "umov", R(q, rd), Element(rn, type, index));
        }

        return null;
    }

    private static Arm64Instruction? TableLookup(uint w)
    {
        if (F(w, 23, 22) != 0)
        {
            return null;
        }

        string arrangement = Bit(w, 30) ? "16b" : "8b";
        int count = (int)F(w, 14, 13) + 1;
        string name = Bit(w, 12) ? "tbx" : "tbl";
        return I(w, name, Vec(F(w, 4, 0), arrangement), T(ListText(F(w, 9, 5), count, ".16b")), Vec(F(w, 20, 16), arrangement));
    }

    private static Arm64Instruction? Permute(uint w)
    {
        uint size = F(w, 23, 22);
        bool q = Bit(w, 30);
        if (size == 3 && !q)
        {
            return null;
        }

        string? name = F(w, 14, 12) switch
        {
            0b001 => "uzp1",
            0b010 => "trn1",
            0b011 => "zip1",
            0b101 => "uzp2",
            0b110 => "trn2",
            0b111 => "zip2",
            _ => null,
        };
        string arrangement = Arr(size, q);
        return name is null ? null : I(w, name, Vec(F(w, 4, 0), arrangement), Vec(F(w, 9, 5), arrangement), Vec(F(w, 20, 16), arrangement));
    }

    // ---- three registers ----------------------------------------------------------------------------------

    private static Arm64Instruction? ThreeSame(uint w)
    {
        bool q = Bit(w, 30);
        bool u = Bit(w, 29);
        uint size = F(w, 23, 22);
        uint opcode = F(w, 15, 11);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        uint rm = F(w, 20, 16);

        if (opcode == 0b00011)
        {
            string arrangement = q ? "16b" : "8b";
            if (!u && size == 0b10 && rn == rm)
            {
                return I(w, "mov", Vec(rd, arrangement), Vec(rn, arrangement));
            }

            string logical = (u, size) switch
            {
                (false, 0) => "and",
                (false, 1) => "bic",
                (false, 2) => "orr",
                (false, _) => "orn",
                (true, 0) => "eor",
                (true, 1) => "bsl",
                (true, 2) => "bit",
                _ => "bif",
            };
            return I(w, logical, Vec(rd, arrangement), Vec(rn, arrangement), Vec(rm, arrangement));
        }

        if (opcode >= 0b11000)
        {
            // Floating point: bit 23 picks between a pair of operations, bit 22 the precision.
            bool high = Bit(w, 23);
            bool sz = Bit(w, 22);
            if (sz && !q)
            {
                return null;
            }

            string? fp = (u, opcode, high) switch
            {
                (false, 0b11000, false) => "fmaxnm",
                (false, 0b11000, true) => "fminnm",
                (false, 0b11001, false) => "fmla",
                (false, 0b11001, true) => "fmls",
                (false, 0b11010, false) => "fadd",
                (false, 0b11010, true) => "fsub",
                (false, 0b11011, false) => "fmulx",
                (false, 0b11100, false) => "fcmeq",
                (false, 0b11110, false) => "fmax",
                (false, 0b11110, true) => "fmin",
                (false, 0b11111, false) => "frecps",
                (false, 0b11111, true) => "frsqrts",
                (true, 0b11000, false) => "fmaxnmp",
                (true, 0b11000, true) => "fminnmp",
                (true, 0b11010, false) => "faddp",
                (true, 0b11010, true) => "fabd",
                (true, 0b11011, false) => "fmul",
                (true, 0b11100, false) => "fcmge",
                (true, 0b11100, true) => "fcmgt",
                (true, 0b11101, false) => "facge",
                (true, 0b11101, true) => "facgt",
                (true, 0b11110, false) => "fmaxp",
                (true, 0b11110, true) => "fminp",
                (true, 0b11111, false) => "fdiv",
                _ => null,
            };

            string arrangement = sz ? "2d" : (q ? "4s" : "2s");
            return fp is null ? null : I(w, fp, Vec(rd, arrangement), Vec(rn, arrangement), Vec(rm, arrangement));
        }

        string? name = (u, opcode) switch
        {
            (false, 0b00000) => "shadd",
            (false, 0b00001) => "sqadd",
            (false, 0b00010) => "srhadd",
            (false, 0b00100) => "shsub",
            (false, 0b00101) => "sqsub",
            (false, 0b00110) => "cmgt",
            (false, 0b00111) => "cmge",
            (false, 0b01000) => "sshl",
            (false, 0b01001) => "sqshl",
            (false, 0b01010) => "srshl",
            (false, 0b01011) => "sqrshl",
            (false, 0b01100) => "smax",
            (false, 0b01101) => "smin",
            (false, 0b01110) => "sabd",
            (false, 0b01111) => "saba",
            (false, 0b10000) => "add",
            (false, 0b10001) => "cmtst",
            (false, 0b10010) => "mla",
            (false, 0b10011) => "mul",
            (false, 0b10100) => "smaxp",
            (false, 0b10101) => "sminp",
            (false, 0b10110) => "sqdmulh",
            (false, 0b10111) => "addp",
            (true, 0b00000) => "uhadd",
            (true, 0b00001) => "uqadd",
            (true, 0b00010) => "urhadd",
            (true, 0b00100) => "uhsub",
            (true, 0b00101) => "uqsub",
            (true, 0b00110) => "cmhi",
            (true, 0b00111) => "cmhs",
            (true, 0b01000) => "ushl",
            (true, 0b01001) => "uqshl",
            (true, 0b01010) => "urshl",
            (true, 0b01011) => "uqrshl",
            (true, 0b01100) => "umax",
            (true, 0b01101) => "umin",
            (true, 0b01110) => "uabd",
            (true, 0b01111) => "uaba",
            (true, 0b10000) => "sub",
            (true, 0b10001) => "cmeq",
            (true, 0b10010) => "mls",
            (true, 0b10011) => "pmul",
            (true, 0b10100) => "umaxp",
            (true, 0b10101) => "uminp",
            (true, 0b10110) => "sqrdmulh",
            _ => null,
        };

        if (name is null || (size == 3 && !q))
        {
            return null;
        }

        // Only some operations exist on 64-bit elements; multiplies are narrower still.
        bool doubles = opcode is 0b00001 or 0b00101 or 0b00110 or 0b00111 or 0b01000 or 0b01001 or 0b01010 or 0b01011
            or 0b10000 or 0b10001 or 0b10111;
        if (size == 3 && !doubles)
        {
            return null;
        }

        if (opcode == 0b10011 && u && size != 0)
        {
            return null;
        }

        if (opcode == 0b10110 && size is 0 or 3)
        {
            return null;
        }

        string arr = Arr(size, q);
        return I(w, name, Vec(rd, arr), Vec(rn, arr), Vec(rm, arr));
    }

    private static Arm64Instruction? ScalarThreeSame(uint w)
    {
        bool u = Bit(w, 29);
        uint size = F(w, 23, 22);
        uint opcode = F(w, 15, 11);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        uint rm = F(w, 20, 16);

        if (opcode >= 0b11000)
        {
            bool high = Bit(w, 23);
            var k = Bit(w, 22) ? Arm64RegisterKind.D : Arm64RegisterKind.S;
            string? fp = (u, opcode, high) switch
            {
                (false, 0b11011, false) => "fmulx",
                (false, 0b11100, false) => "fcmeq",
                (false, 0b11111, false) => "frecps",
                (false, 0b11111, true) => "frsqrts",
                (true, 0b11010, true) => "fabd",
                (true, 0b11100, false) => "fcmge",
                (true, 0b11100, true) => "fcmgt",
                (true, 0b11101, false) => "facge",
                (true, 0b11101, true) => "facgt",
                _ => null,
            };
            return fp is null ? null : I(w, fp, V(k, rd), V(k, rn), V(k, rm));
        }

        string? name = (u, opcode) switch
        {
            (false, 0b00001) => "sqadd",
            (false, 0b00101) => "sqsub",
            (false, 0b00110) => "cmgt",
            (false, 0b00111) => "cmge",
            (false, 0b01000) => "sshl",
            (false, 0b01001) => "sqshl",
            (false, 0b01010) => "srshl",
            (false, 0b01011) => "sqrshl",
            (false, 0b10000) => "add",
            (false, 0b10001) => "cmtst",
            (false, 0b10110) => "sqdmulh",
            (true, 0b00001) => "uqadd",
            (true, 0b00101) => "uqsub",
            (true, 0b00110) => "cmhi",
            (true, 0b00111) => "cmhs",
            (true, 0b01000) => "ushl",
            (true, 0b01001) => "uqshl",
            (true, 0b01010) => "urshl",
            (true, 0b01011) => "uqrshl",
            (true, 0b10000) => "sub",
            (true, 0b10001) => "cmeq",
            (true, 0b10110) => "sqrdmulh",
            _ => null,
        };

        if (name is null)
        {
            return null;
        }

        // Comparisons, plain shifts and add/sub exist only on 64-bit scalars; the doubling multiplies only on h and s.
        bool onlyDoubles = opcode is 0b00110 or 0b00111 or 0b01000 or 0b01010 or 0b10000 or 0b10001;
        if ((onlyDoubles && size != 3) || (opcode == 0b10110 && size is 0 or 3))
        {
            return null;
        }

        var kind = ScalarKind(size);
        return I(w, name, V(kind, rd), V(kind, rn), V(kind, rm));
    }

    private static Arm64Instruction? ThreeDifferent(uint w)
    {
        bool q = Bit(w, 30);
        bool u = Bit(w, 29);
        uint size = F(w, 23, 22);
        uint opcode = F(w, 15, 12);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        uint rm = F(w, 20, 16);

        string? name = (u, opcode) switch
        {
            (false, 0b0000) => "saddl",
            (false, 0b0001) => "saddw",
            (false, 0b0010) => "ssubl",
            (false, 0b0011) => "ssubw",
            (false, 0b0100) => "addhn",
            (false, 0b0101) => "sabal",
            (false, 0b0110) => "subhn",
            (false, 0b0111) => "sabdl",
            (false, 0b1000) => "smlal",
            (false, 0b1001) => "sqdmlal",
            (false, 0b1010) => "smlsl",
            (false, 0b1011) => "sqdmlsl",
            (false, 0b1100) => "smull",
            (false, 0b1101) => "sqdmull",
            (false, 0b1110) => "pmull",
            (true, 0b0000) => "uaddl",
            (true, 0b0001) => "uaddw",
            (true, 0b0010) => "usubl",
            (true, 0b0011) => "usubw",
            (true, 0b0100) => "raddhn",
            (true, 0b0101) => "uabal",
            (true, 0b0110) => "rsubhn",
            (true, 0b0111) => "uabdl",
            (true, 0b1000) => "umlal",
            (true, 0b1010) => "umlsl",
            (true, 0b1100) => "umull",
            _ => null,
        };

        if (name is null)
        {
            return null;
        }

        if (opcode == 0b1110)
        {
            // pmull: bytes to halfwords, or 64-bit to 128.
            if (size is 1 or 2)
            {
                return null;
            }

            string wide = size == 0 ? "8h" : "1q";
            return I(w, q ? "pmull2" : "pmull", Vec(rd, wide), Vec(rn, Arr(size, q)), Vec(rm, Arr(size, q)));
        }

        if (size == 3 || (opcode is 0b1001 or 0b1011 or 0b1101 && size == 0))
        {
            return null;
        }

        string full = Arr(size + 1, true);
        string half = Arr(size, q);
        name += q ? "2" : string.Empty;
        return opcode switch
        {
            0b0001 or 0b0011 => I(w, name, Vec(rd, full), Vec(rn, full), Vec(rm, half)),
            0b0100 or 0b0110 => I(w, name, Vec(rd, half), Vec(rn, full), Vec(rm, full)),
            _ => I(w, name, Vec(rd, full), Vec(rn, half), Vec(rm, half)),
        };
    }

    private static Arm64Instruction? ScalarThreeDifferent(uint w)
    {
        uint size = F(w, 23, 22);
        if (Bit(w, 29) || size is 0 or 3)
        {
            return null;
        }

        string? name = F(w, 15, 12) switch
        {
            0b1001 => "sqdmlal",
            0b1011 => "sqdmlsl",
            0b1101 => "sqdmull",
            _ => null,
        };
        return name is null ? null : I(w, name, V(ScalarKind(size + 1), F(w, 4, 0)), V(ScalarKind(size), F(w, 9, 5)), V(ScalarKind(size), F(w, 20, 16)));
    }

    /// <summary>The dot products and rounding multiply-accumulates added after ARMv8.0.</summary>
    private static Arm64Instruction? ThreeRegisterExtension(uint w)
    {
        bool q = Bit(w, 30);
        bool u = Bit(w, 29);
        uint size = F(w, 23, 22);
        uint opcode = F(w, 14, 11);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        uint rm = F(w, 20, 16);

        switch (opcode, u)
        {
            case (0b0000, true) when size is 1 or 2:
                return I(w, "sqrdmlah", Vec(rd, Arr(size, q)), Vec(rn, Arr(size, q)), Vec(rm, Arr(size, q)));
            case (0b0001, true) when size is 1 or 2:
                return I(w, "sqrdmlsh", Vec(rd, Arr(size, q)), Vec(rn, Arr(size, q)), Vec(rm, Arr(size, q)));
            case (0b0010, _) when size == 2:
                return I(w, u ? "udot" : "sdot", Vec(rd, q ? "4s" : "2s"), Vec(rn, q ? "16b" : "8b"), Vec(rm, q ? "16b" : "8b"));
            case (0b0011, false) when size == 2:
                return I(w, "usdot", Vec(rd, q ? "4s" : "2s"), Vec(rn, q ? "16b" : "8b"), Vec(rm, q ? "16b" : "8b"));
            case (0b0100, _) when size == 2 && q:
                return I(w, u ? "ummla" : "smmla", Vec(rd, "4s"), Vec(rn, "16b"), Vec(rm, "16b"));
            case (0b0101, false) when size == 2 && q:
                return I(w, "usmmla", Vec(rd, "4s"), Vec(rn, "16b"), Vec(rm, "16b"));
        }

        return null;
    }

    // ---- two registers --------------------------------------------------------------------------------

    private static Arm64Instruction? TwoMisc(uint w)
    {
        bool q = Bit(w, 30);
        bool u = Bit(w, 29);
        uint size = F(w, 23, 22);
        uint opcode = F(w, 16, 12);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        string arr = Arr(size, q);

        // Floating point: opcodes 01100-01111 on the high size bit, and 10110 onwards.
        bool fp = opcode >= 0b11000 || (opcode is >= 0b01100 and <= 0b01111 && Bit(w, 23)) || opcode is 0b10110 or 0b10111;
        if (fp)
        {
            return TwoMiscFloat(w, q, u, opcode, rd, rn);
        }

        switch (u, opcode)
        {
            case (false, 0b00000) when size < 3:
                return I(w, "rev64", Vec(rd, arr), Vec(rn, arr));
            case (false, 0b00001) when size == 0:
                return I(w, "rev16", Vec(rd, arr), Vec(rn, arr));
            case (true, 0b00000) when size < 2:
                return I(w, "rev32", Vec(rd, arr), Vec(rn, arr));
            case (_, 0b00010) when size < 3:
                return I(w, u ? "uaddlp" : "saddlp", Vec(rd, Arr(size + 1, q)), Vec(rn, arr));
            case (_, 0b00110) when size < 3:
                return I(w, u ? "uadalp" : "sadalp", Vec(rd, Arr(size + 1, q)), Vec(rn, arr));
            case (_, 0b00011) when size < 3 || q:
                return I(w, u ? "usqadd" : "suqadd", Vec(rd, arr), Vec(rn, arr));
            case (_, 0b00111) when size < 3 || q:
                return I(w, u ? "sqneg" : "sqabs", Vec(rd, arr), Vec(rn, arr));
            case (_, 0b00100) when size < 3:
                return I(w, u ? "clz" : "cls", Vec(rd, arr), Vec(rn, arr));
            case (false, 0b00101) when size == 0:
                return I(w, "cnt", Vec(rd, arr), Vec(rn, arr));
            case (true, 0b00101) when size == 0:
                return I(w, "mvn", Vec(rd, arr), Vec(rn, arr));
            case (true, 0b00101) when size == 1:
                return I(w, "rbit", Vec(rd, q ? "16b" : "8b"), Vec(rn, q ? "16b" : "8b"));
            case (_, 0b01000) when size < 3 || q:
                return I(w, u ? "cmge" : "cmgt", Vec(rd, arr), Vec(rn, arr), Dec(0));
            case (_, 0b01001) when size < 3 || q:
                return I(w, u ? "cmle" : "cmeq", Vec(rd, arr), Vec(rn, arr), Dec(0));
            case (false, 0b01010) when size < 3 || q:
                return I(w, "cmlt", Vec(rd, arr), Vec(rn, arr), Dec(0));
            case (_, 0b01011) when size < 3 || q:
                return I(w, u ? "neg" : "abs", Vec(rd, arr), Vec(rn, arr));
            case (_, 0b10010) or (_, 0b10100) when size < 3:
            {
                string name = (u, opcode) switch
                {
                    (false, 0b10010) => "xtn",
                    (true, 0b10010) => "sqxtun",
                    (false, _) => "sqxtn",
                    _ => "uqxtn",
                };
                return I(w, name + (q ? "2" : string.Empty), Vec(rd, arr), Vec(rn, Arr(size + 1, true)));
            }

            case (true, 0b10011) when size < 3:
                return I(w, q ? "shll2" : "shll", Vec(rd, Arr(size + 1, true)), Vec(rn, arr), Dec(8 << (int)size));
        }

        return null;
    }

    private static Arm64Instruction? TwoMiscFloat(uint w, bool q, bool u, uint opcode, uint rd, uint rn)
    {
        bool sz = Bit(w, 22);
        bool high = Bit(w, 23);
        string arr = sz ? "2d" : (q ? "4s" : "2s");

        switch (u, opcode)
        {
            // Precision changes: fcvtn narrows, fcvtl widens, fcvtxn narrows rounding to odd.
            case (false, 0b10110) when !high:
                return I(w, q ? "fcvtn2" : "fcvtn", Vec(rd, sz ? (q ? "4s" : "2s") : (q ? "8h" : "4h")), Vec(rn, sz ? "2d" : "4s"));
            case (false, 0b10111) when !high:
                return I(w, q ? "fcvtl2" : "fcvtl", Vec(rd, sz ? "2d" : "4s"), Vec(rn, sz ? (q ? "4s" : "2s") : (q ? "8h" : "4h")));
            case (true, 0b10110) when !high && sz:
                return I(w, q ? "fcvtxn2" : "fcvtxn", Vec(rd, q ? "4s" : "2s"), Vec(rn, "2d"));
        }

        if (sz && !q)
        {
            return null;
        }

        if (opcode is >= 0b01100 and <= 0b01111)
        {
            string? compare = (u, opcode) switch
            {
                (false, 0b01100) => "fcmgt",
                (false, 0b01101) => "fcmeq",
                (false, 0b01110) => "fcmlt",
                (true, 0b01100) => "fcmge",
                (true, 0b01101) => "fcmle",
                _ => null,
            };

            if (compare is not null)
            {
                return I(w, compare, Vec(rd, arr), Vec(rn, arr), new Arm64FloatImmediate(0));
            }

            return opcode == 0b01111 ? I(w, u ? "fneg" : "fabs", Vec(rd, arr), Vec(rn, arr)) : null;
        }

        string? name = (u, opcode, high) switch
        {
            (false, 0b11000, false) => "frintn",
            (false, 0b11000, true) => "frintp",
            (false, 0b11001, false) => "frintm",
            (false, 0b11001, true) => "frintz",
            (false, 0b11010, false) => "fcvtns",
            (false, 0b11010, true) => "fcvtps",
            (false, 0b11011, false) => "fcvtms",
            (false, 0b11011, true) => "fcvtzs",
            (false, 0b11100, false) => "fcvtas",
            (false, 0b11100, true) when !sz => "urecpe",
            (false, 0b11101, false) => "scvtf",
            (false, 0b11101, true) => "frecpe",
            (false, 0b11110, false) => "frint32z",
            (false, 0b11111, false) => "frint64z",
            (true, 0b11000, false) => "frinta",
            (true, 0b11001, false) => "frintx",
            (true, 0b11001, true) => "frinti",
            (true, 0b11010, false) => "fcvtnu",
            (true, 0b11010, true) => "fcvtpu",
            (true, 0b11011, false) => "fcvtmu",
            (true, 0b11011, true) => "fcvtzu",
            (true, 0b11100, false) => "fcvtau",
            (true, 0b11100, true) when !sz => "ursqrte",
            (true, 0b11101, false) => "ucvtf",
            (true, 0b11101, true) => "frsqrte",
            (true, 0b11110, false) => "frint32x",
            (true, 0b11111, false) => "frint64x",
            (true, 0b11111, true) => "fsqrt",
            _ => null,
        };

        return name is null ? null : I(w, name, Vec(rd, arr), Vec(rn, arr));
    }

    private static Arm64Instruction? ScalarTwoMisc(uint w)
    {
        bool u = Bit(w, 29);
        uint size = F(w, 23, 22);
        uint opcode = F(w, 16, 12);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        var kind = ScalarKind(size);

        if (opcode >= 0b11010 || (opcode is >= 0b01100 and <= 0b01111 && Bit(w, 23)) || opcode == 0b10110)
        {
            var fk = Bit(w, 22) ? Arm64RegisterKind.D : Arm64RegisterKind.S;
            bool high = Bit(w, 23);
            if (opcode == 0b10110)
            {
                return u && !high && Bit(w, 22) ? I(w, "fcvtxn", V(Arm64RegisterKind.S, rd), V(Arm64RegisterKind.D, rn)) : null;
            }

            string? compare = (u, opcode) switch
            {
                (false, 0b01100) => "fcmgt",
                (false, 0b01101) => "fcmeq",
                (false, 0b01110) => "fcmlt",
                (true, 0b01100) => "fcmge",
                (true, 0b01101) => "fcmle",
                _ => null,
            };
            if (compare is not null)
            {
                return I(w, compare, V(fk, rd), V(fk, rn), new Arm64FloatImmediate(0));
            }

            string? name = (u, opcode, high) switch
            {
                (false, 0b11010, false) => "fcvtns",
                (false, 0b11010, true) => "fcvtps",
                (false, 0b11011, false) => "fcvtms",
                (false, 0b11011, true) => "fcvtzs",
                (false, 0b11100, false) => "fcvtas",
                (false, 0b11101, false) => "scvtf",
                (false, 0b11101, true) => "frecpe",
                (false, 0b11111, true) => "frecpx",
                (true, 0b11010, false) => "fcvtnu",
                (true, 0b11010, true) => "fcvtpu",
                (true, 0b11011, false) => "fcvtmu",
                (true, 0b11011, true) => "fcvtzu",
                (true, 0b11100, false) => "fcvtau",
                (true, 0b11101, false) => "ucvtf",
                (true, 0b11101, true) => "frsqrte",
                _ => null,
            };
            return name is null ? null : I(w, name, V(fk, rd), V(fk, rn));
        }

        switch (u, opcode)
        {
            case (_, 0b00011):
                return I(w, u ? "usqadd" : "suqadd", V(kind, rd), V(kind, rn));
            case (_, 0b00111):
                return I(w, u ? "sqneg" : "sqabs", V(kind, rd), V(kind, rn));
            case (_, 0b01000) when size == 3:
                return I(w, u ? "cmge" : "cmgt", V(kind, rd), V(kind, rn), Dec(0));
            case (_, 0b01001) when size == 3:
                return I(w, u ? "cmle" : "cmeq", V(kind, rd), V(kind, rn), Dec(0));
            case (false, 0b01010) when size == 3:
                return I(w, "cmlt", V(kind, rd), V(kind, rn), Dec(0));
            case (_, 0b01011) when size == 3:
                return I(w, u ? "neg" : "abs", V(kind, rd), V(kind, rn));
            case (true, 0b10010) when size < 3:
                return I(w, "sqxtun", V(kind, rd), V(ScalarKind(size + 1), rn));
            case (_, 0b10100) when size < 3:
                return I(w, u ? "uqxtn" : "sqxtn", V(kind, rd), V(ScalarKind(size + 1), rn));
        }

        return null;
    }

    private static Arm64Instruction? ScalarPairwise(uint w)
    {
        bool u = Bit(w, 29);
        uint size = F(w, 23, 22);
        uint opcode = F(w, 16, 12);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);

        if (!u && opcode == 0b11011 && size == 3)
        {
            return I(w, "addp", V(Arm64RegisterKind.D, rd), Vec(rn, "2d"));
        }

        if (!u)
        {
            return null;
        }

        bool sz = Bit(w, 22);
        var k = sz ? Arm64RegisterKind.D : Arm64RegisterKind.S;
        string arr = sz ? "2d" : "2s";
        string? name = (opcode, Bit(w, 23)) switch
        {
            (0b01100, false) => "fmaxnmp",
            (0b01100, true) => "fminnmp",
            (0b01101, false) => "faddp",
            (0b01111, false) => "fmaxp",
            (0b01111, true) => "fminp",
            _ => null,
        };
        return name is null ? null : I(w, name, V(k, rd), Vec(rn, arr));
    }

    private static Arm64Instruction? AcrossLanes(uint w)
    {
        bool q = Bit(w, 30);
        bool u = Bit(w, 29);
        uint size = F(w, 23, 22);
        uint opcode = F(w, 16, 12);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);

        if (opcode is 0b01100 or 0b01111)
        {
            // fmaxnmv / fminnmv / fmaxv / fminv: single precision, four lanes.
            if (!u || !q || Bit(w, 22))
            {
                return null;
            }

            string fp = (opcode, Bit(w, 23)) switch
            {
                (0b01100, false) => "fmaxnmv",
                (0b01100, true) => "fminnmv",
                (_, false) => "fmaxv",
                _ => "fminv",
            };
            return I(w, fp, V(Arm64RegisterKind.S, rd), Vec(rn, "4s"));
        }

        if (size == 3 || (size == 2 && !q))
        {
            return null;
        }

        return (u, opcode) switch
        {
            (_, 0b00011) => I(w, u ? "uaddlv" : "saddlv", V(ScalarKind(size + 1), rd), Vec(rn, Arr(size, q))),
            (_, 0b01010) => I(w, u ? "umaxv" : "smaxv", V(ScalarKind(size), rd), Vec(rn, Arr(size, q))),
            (_, 0b11010) => I(w, u ? "uminv" : "sminv", V(ScalarKind(size), rd), Vec(rn, Arr(size, q))),
            (false, 0b11011) => I(w, "addv", V(ScalarKind(size), rd), Vec(rn, Arr(size, q))),
            _ => null,
        };
    }

    // ---- immediates and shifts ------------------------------------------------------------------------

    private static Arm64Instruction? ModifiedImmediate(uint w)
    {
        bool q = Bit(w, 30);
        bool op = Bit(w, 29);
        uint cmode = F(w, 15, 12);
        uint imm8 = (F(w, 18, 16) << 5) | F(w, 9, 5);
        uint rd = F(w, 4, 0);

        if (Bit(w, 11))
        {
            // o2: the half-precision fmov (FEAT_FP16).
            return cmode == 0b1111 && !op ? I(w, "fmov", Vec(rd, q ? "8h" : "4h"), new Arm64FloatImmediate(ExpandFloat(imm8))) : null;
        }

        if ((cmode & 0b1000) == 0)
        {
            // 32-bit elements, the byte shifted left by 0, 8, 16 or 24.
            int shift = (int)((cmode >> 1) & 3) * 8;
            string name = (cmode & 1) == 0 ? (op ? "mvni" : "movi") : (op ? "bic" : "orr");
            return Shifted(name, q ? "4s" : "2s", shift);
        }

        if ((cmode & 0b1100) == 0b1000)
        {
            int shift = (int)((cmode >> 1) & 1) * 8;
            string name = (cmode & 1) == 0 ? (op ? "mvni" : "movi") : (op ? "bic" : "orr");
            return Shifted(name, q ? "8h" : "4h", shift);
        }

        if ((cmode & 0b1110) == 0b1100)
        {
            // Shifting ones in: msl #8 / #16.
            int shift = (cmode & 1) == 0 ? 8 : 16;
            return I(w, op ? "mvni" : "movi", Vec(rd, q ? "4s" : "2s"), new Arm64ShiftedImmediate(imm8, shift, Arm64Shift.Msl));
        }

        if (cmode == 0b1110)
        {
            if (!op)
            {
                return I(w, "movi", Vec(rd, q ? "16b" : "8b"), Imm(imm8));
            }

            // Each bit of the immediate stands for a byte of ones or zeros.
            ulong bytes = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((imm8 & (1u << i)) != 0)
                {
                    bytes |= 0xFFUL << (i * 8);
                }
            }

            return q ? I(w, "movi", Vec(rd, "2d"), Imm((long)bytes)) : I(w, "movi", V(Arm64RegisterKind.D, rd), Imm((long)bytes));
        }

        // cmode 1111: fmov of a single- or, with op, double-precision immediate.
        if (op && !q)
        {
            return null;
        }

        return I(w, "fmov", Vec(rd, op ? "2d" : (q ? "4s" : "2s")), new Arm64FloatImmediate(ExpandFloat(imm8)));

        Arm64Instruction Shifted(string name, string arrangement, int shift)
            => shift == 0 ? I(w, name, Vec(rd, arrangement), Imm(imm8)) : I(w, name, Vec(rd, arrangement), new Arm64ShiftedImmediate(imm8, shift));
    }

    /// <summary>The element size an <c>immh</c> field encodes, as log2 of bytes; null when it is zero.</summary>
    private static uint? ShiftElement(uint immh) => immh switch
    {
        0 => null,
        1 => 0,
        <= 3 => 1,
        <= 7 => 2,
        _ => 3,
    };

    private static Arm64Instruction? ShiftByImmediate(uint w)
    {
        bool q = Bit(w, 30);
        bool u = Bit(w, 29);
        uint immh = F(w, 22, 19);
        uint immhb = F(w, 22, 16);
        uint opcode = F(w, 15, 11);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        if (ShiftElement(immh) is not { } size)
        {
            return null;
        }

        int esize = 8 << (int)size;
        long right = (2 * esize) - immhb;
        long left = immhb - esize;
        string arr = Arr(size, q);

        // Narrowing shifts: the result is half the width of the source, so a 64-bit source is as wide as they go.
        if (opcode is >= 0b10000 and <= 0b10011)
        {
            if (size == 3)
            {
                return null;
            }

            string? narrow = (u, opcode) switch
            {
                (false, 0b10000) => "shrn",
                (false, 0b10001) => "rshrn",
                (false, 0b10010) => "sqshrn",
                (false, 0b10011) => "sqrshrn",
                (true, 0b10000) => "sqshrun",
                (true, 0b10001) => "sqrshrun",
                (true, 0b10010) => "uqshrn",
                _ => "uqrshrn",
            };
            return I(w, narrow + (q ? "2" : string.Empty), Vec(rd, arr), Vec(rn, Arr(size + 1, true)), Dec(right));
        }

        if (opcode == 0b10100)
        {
            if (size == 3)
            {
                return null;
            }

            string name = (u ? "ushll" : "sshll") + (q ? "2" : string.Empty);
            if (left == 0)
            {
                return I(w, (u ? "uxtl" : "sxtl") + (q ? "2" : string.Empty), Vec(rd, Arr(size + 1, true)), Vec(rn, arr));
            }

            return I(w, name, Vec(rd, Arr(size + 1, true)), Vec(rn, arr), Dec(left));
        }

        if (size == 3 && !q)
        {
            return null;
        }

        if (opcode is 0b11100 or 0b11111)
        {
            // Fixed-point conversions, on single- and double-precision lanes.
            if (size < 2)
            {
                return null;
            }

            string conversion = opcode == 0b11100 ? (u ? "ucvtf" : "scvtf") : (u ? "fcvtzu" : "fcvtzs");
            return I(w, conversion, Vec(rd, arr), Vec(rn, arr), Dec(right));
        }

        (string? Name, bool Left) shift = (u, opcode) switch
        {
            (false, 0b00000) => ("sshr", false),
            (false, 0b00010) => ("ssra", false),
            (false, 0b00100) => ("srshr", false),
            (false, 0b00110) => ("srsra", false),
            (false, 0b01010) => ("shl", true),
            (false, 0b01110) => ("sqshl", true),
            (true, 0b00000) => ("ushr", false),
            (true, 0b00010) => ("usra", false),
            (true, 0b00100) => ("urshr", false),
            (true, 0b00110) => ("ursra", false),
            (true, 0b01000) => ("sri", false),
            (true, 0b01010) => ("sli", true),
            (true, 0b01100) => ("sqshlu", true),
            (true, 0b01110) => ("uqshl", true),
            _ => (null, false),
        };

        return shift.Name is null ? null : I(w, shift.Name, Vec(rd, arr), Vec(rn, arr), Dec(shift.Left ? left : right));
    }

    private static Arm64Instruction? ScalarShift(uint w)
    {
        bool u = Bit(w, 29);
        uint immh = F(w, 22, 19);
        uint immhb = F(w, 22, 16);
        uint opcode = F(w, 15, 11);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        if (ShiftElement(immh) is not { } size)
        {
            return null;
        }

        int esize = 8 << (int)size;
        long right = (2 * esize) - immhb;
        long left = immhb - esize;
        var kind = ScalarKind(size);

        if (opcode is >= 0b10000 and <= 0b10011)
        {
            string? narrow = (u, opcode) switch
            {
                (false, 0b10010) => "sqshrn",
                (false, 0b10011) => "sqrshrn",
                (true, 0b10000) => "sqshrun",
                (true, 0b10001) => "sqrshrun",
                (true, 0b10010) => "uqshrn",
                (true, 0b10011) => "uqrshrn",
                _ => null,
            };
            return narrow is null || size == 3 ? null : I(w, narrow, V(kind, rd), V(ScalarKind(size + 1), rn), Dec(right));
        }

        if (opcode is 0b11100 or 0b11111)
        {
            if (size < 2)
            {
                return null;
            }

            string conversion = opcode == 0b11100 ? (u ? "ucvtf" : "scvtf") : (u ? "fcvtzu" : "fcvtzs");
            return I(w, conversion, V(kind, rd), V(kind, rn), Dec(right));
        }

        (string? Name, bool Left, bool AnySize) shift = (u, opcode) switch
        {
            (false, 0b00000) => ("sshr", false, false),
            (false, 0b00010) => ("ssra", false, false),
            (false, 0b00100) => ("srshr", false, false),
            (false, 0b00110) => ("srsra", false, false),
            (false, 0b01010) => ("shl", true, false),
            (false, 0b01110) => ("sqshl", true, true),
            (true, 0b00000) => ("ushr", false, false),
            (true, 0b00010) => ("usra", false, false),
            (true, 0b00100) => ("urshr", false, false),
            (true, 0b00110) => ("ursra", false, false),
            (true, 0b01000) => ("sri", false, false),
            (true, 0b01010) => ("sli", true, false),
            (true, 0b01100) => ("sqshlu", true, true),
            (true, 0b01110) => ("uqshl", true, true),
            _ => (null, false, false),
        };

        if (shift.Name is null || (!shift.AnySize && size != 3))
        {
            return null;
        }

        return I(w, shift.Name, V(kind, rd), V(kind, rn), Dec(shift.Left ? left : right));
    }

    // ---- by element ---------------------------------------------------------------------------------------

    private static Arm64Instruction? IndexedElement(uint w, bool scalar)
    {
        bool q = Bit(w, 30);
        bool u = Bit(w, 29);
        uint size = F(w, 23, 22);
        uint opcode = F(w, 15, 12);
        uint rd = F(w, 4, 0);
        uint rn = F(w, 9, 5);
        uint h = F(w, 11, 11);
        uint l = F(w, 21, 21);
        uint m = F(w, 20, 20);
        uint rm = F(w, 19, 16);

        // Floating point: fmla, fmls, fmul, fmulx on s or d lanes (FP16's h lanes are left out).
        if (opcode is 0b0001 or 0b0101 or 0b1001 && size >= 2)
        {
            string? fp = (u, opcode) switch
            {
                (false, 0b0001) => "fmla",
                (false, 0b0101) => "fmls",
                (false, 0b1001) => "fmul",
                (true, 0b1001) => "fmulx",
                _ => null,
            };
            if (fp is null || (size == 3 && (l == 1 || (!q && !scalar))))
            {
                return null;
            }

            uint index = size == 2 ? (h << 1) | l : h;
            char type = size == 2 ? 's' : 'd';
            var element = Element((m << 4) | rm, type, index);
            if (scalar)
            {
                var k = size == 2 ? Arm64RegisterKind.S : Arm64RegisterKind.D;
                return I(w, fp, V(k, rd), V(k, rn), element);
            }

            string arr = size == 2 ? (q ? "4s" : "2s") : "2d";
            return I(w, fp, Vec(rd, arr), Vec(rn, arr), element);
        }

        // Integer lanes: h indexes by H:L:M with Rm in v0-v15, s by H:L with M:Rm.
        if (size is 0 or 3)
        {
            return null;
        }

        uint lane = size == 1 ? (h << 2) | (l << 1) | m : (h << 1) | l;
        uint reg = size == 1 ? rm : (m << 4) | rm;
        var elementOperand = Element(reg, ElementChar(size), lane);

        if (!scalar && opcode == 0b1110 && size == 2)
        {
            return I(w, u ? "udot" : "sdot", Vec(rd, q ? "4s" : "2s"), Vec(rn, q ? "16b" : "8b"), T($"v{reg}.4b[{lane}]"));
        }

        (string? Name, int Form) op = (u, opcode) switch
        {
            (false, 0b0010) => ("smlal", 1),
            (false, 0b0011) => ("sqdmlal", 1),
            (false, 0b0110) => ("smlsl", 1),
            (false, 0b0111) => ("sqdmlsl", 1),
            (false, 0b1000) => ("mul", 0),
            (false, 0b1010) => ("smull", 1),
            (false, 0b1011) => ("sqdmull", 1),
            (false, 0b1100) => ("sqdmulh", 0),
            (false, 0b1101) => ("sqrdmulh", 0),
            (true, 0b0000) => ("mla", 0),
            (true, 0b0010) => ("umlal", 1),
            (true, 0b0100) => ("mls", 0),
            (true, 0b0110) => ("umlsl", 1),
            (true, 0b1010) => ("umull", 1),
            (true, 0b1101) => ("sqrdmlah", 0),
            (true, 0b1111) => ("sqrdmlsh", 0),
            _ => (null, 0),
        };

        if (op.Name is null)
        {
            return null;
        }

        if (scalar)
        {
            // By element, a scalar has only the saturating doubling forms.
            bool saturating = op.Name.StartsWith("sq", StringComparison.Ordinal);
            if (!saturating)
            {
                return null;
            }

            var kd = op.Form == 1 ? ScalarKind(size + 1) : ScalarKind(size);
            return I(w, op.Name, V(kd, rd), V(ScalarKind(size), rn), elementOperand);
        }

        return op.Form == 1
            ? I(w, op.Name + (q ? "2" : string.Empty), Vec(rd, Arr(size + 1, true)), Vec(rn, Arr(size, q)), elementOperand)
            : I(w, op.Name, Vec(rd, Arr(size, q)), Vec(rn, Arr(size, q)), elementOperand);
    }

    // ---- structure loads and stores -----------------------------------------------------------------------

    private static Arm64Instruction? SimdStructure(uint w)
    {
        bool q = Bit(w, 30);
        bool load = Bit(w, 22);
        bool post = Bit(w, 23);
        uint rm = F(w, 20, 16);
        uint rn = F(w, 9, 5);
        uint rt = F(w, 4, 0);
        if (!post && rm != 0)
        {
            return null;
        }

        if (!Bit(w, 24))
        {
            // Multiple structures: whole registers, 1 to 4 of them, interleaved for ld2-ld4.
            if (Bit(w, 21))
            {
                return null;
            }

            uint size = F(w, 11, 10);
            (int Structure, int Registers) shape = F(w, 15, 12) switch
            {
                0b0000 => (4, 4),
                0b0010 => (1, 4),
                0b0100 => (3, 3),
                0b0110 => (1, 3),
                0b0111 => (1, 1),
                0b1000 => (2, 2),
                0b1010 => (1, 2),
                _ => (0, 0),
            };
            if (shape.Structure == 0 || (shape.Structure > 1 && size == 3 && !q))
            {
                return null;
            }

            string name = (load ? "ld" : "st") + shape.Structure.ToString(CultureInfo.InvariantCulture);
            var list = T(ListText(rt, shape.Registers, "." + Arr(size, q)));
            return I(w, name, list, StructureAddress(rn, post, rm, shape.Registers * (q ? 16 : 8)));
        }

        // Single structures: one lane of each register, or (ldNr) one element to every lane.
        uint opcode = F(w, 15, 13);
        bool s = Bit(w, 12);
        uint sizeField = F(w, 11, 10);
        int count = (int)(((opcode & 1) << 1) | F(w, 21, 21)) + 1;
        string op = (load ? "ld" : "st") + count.ToString(CultureInfo.InvariantCulture);
        uint qs = (q ? 1u : 0) << 1 | (s ? 1u : 0);

        switch (opcode >> 1)
        {
            case 0b00:
                return I(w, op, T(ListText(rt, count, ".b") + $"[{(qs << 2) | sizeField}]"), StructureAddress(rn, post, rm, count));
            case 0b01:
                if ((sizeField & 1) != 0)
                {
                    return null;
                }

                return I(w, op, T(ListText(rt, count, ".h") + $"[{(qs << 1) | (sizeField >> 1)}]"), StructureAddress(rn, post, rm, count * 2));
            case 0b10:
                if (sizeField == 0)
                {
                    return I(w, op, T(ListText(rt, count, ".s") + $"[{qs}]"), StructureAddress(rn, post, rm, count * 4));
                }

                if (sizeField == 1 && !s)
                {
                    return I(w, op, T(ListText(rt, count, ".d") + $"[{(q ? 1 : 0)}]"), StructureAddress(rn, post, rm, count * 8));
                }

                return null;
            default:
                if (!load || s)
                {
                    return null;
                }

                return I(w, op + "r", T(ListText(rt, count, "." + Arr(sizeField, q))), StructureAddress(rn, post, rm, count << (int)sizeField));
        }
    }

    /// <summary>A structure load's address: <c>[x0]</c>, post-indexed by what it moved <c>[x0], #32</c>, or by a register.</summary>
    private static Arm64Memory StructureAddress(uint rn, bool post, uint rm, int bytes)
    {
        if (!post)
        {
            return Base(rn);
        }

        return rm == 31
            ? Base(rn, bytes, Arm64Indexing.PostIndex)
            : Base(rn, 0, Arm64Indexing.PostIndex) with { PostRegister = new Arm64Register(Arm64RegisterKind.X, (int)rm) };
    }
}
