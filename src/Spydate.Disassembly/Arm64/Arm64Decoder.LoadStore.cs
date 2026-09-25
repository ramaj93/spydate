namespace Spydate.Disassembly.Arm64;

public static partial class Arm64Decoder
{
    private static Arm64Instruction? LoadStore(uint w, ulong pc)
    {
        // SIMD structure loads and stores: ld1-ld4, st1-st4, ld1r-ld4r.
        if (!Bit(w, 31) && F(w, 29, 25) == 0b00110)
        {
            return SimdStructure(w);
        }

        if (F(w, 29, 24) == 0b001000)
        {
            return Exclusive(w);
        }

        if (F(w, 29, 24) == 0b011001 && !Bit(w, 21) && F(w, 11, 10) == 0)
        {
            return OrderedUnscaled(w);
        }

        if (F(w, 29, 27) == 0b011 && F(w, 25, 24) == 0)
        {
            return Literal(w, pc);
        }

        if (F(w, 29, 27) == 0b101)
        {
            return Pair(w);
        }

        if (F(w, 29, 27) == 0b111)
        {
            if (Bit(w, 24))
            {
                return RegisterAccess(w, F(w, 21, 10), unsignedOffset: true);
            }

            if (!Bit(w, 21))
            {
                return RegisterAccess(w, F(w, 20, 10), unsignedOffset: false);
            }

            return F(w, 11, 10) switch
            {
                0b00 => Atomic(w),
                0b10 => RegisterOffset(w),
                _ => PointerAuthLoad(w),
            };
        }

        return null;
    }

    private static string SizeSuffix(uint size) => size switch { 0 => "b", 1 => "h", _ => string.Empty };

    private static Arm64Memory Base(uint rn, long offset = 0, Arm64Indexing indexing = Arm64Indexing.Offset)
        => new(new Arm64Register(Arm64RegisterKind.XSp, (int)rn), offset, indexing);

    private static Arm64Instruction? Exclusive(uint w)
    {
        uint size = F(w, 31, 30);
        bool o2 = Bit(w, 23);
        bool load = Bit(w, 22);
        bool o1 = Bit(w, 21);
        bool o0 = Bit(w, 15);
        uint rs = F(w, 20, 16);
        uint rt2 = F(w, 14, 10);
        uint rn = F(w, 9, 5);
        uint rt = F(w, 4, 0);
        bool x = size == 3;
        var memory = Base(rn);

        if (!o2 && o1)
        {
            if (size >= 2)
            {
                // Exclusive pairs.
                bool pairX = size == 3;
                return load
                    ? I(w, o0 ? "ldaxp" : "ldxp", R(pairX, rt), R(pairX, rt2), memory)
                    : I(w, o0 ? "stlxp" : "stxp", W(rs), R(pairX, rt), R(pairX, rt2), memory);
            }

            // casp: compare and swap a pair of consecutive registers.
            if (rt2 != 31 || rs % 2 != 0 || rt % 2 != 0)
            {
                return null;
            }

            bool caspX = size == 1;
            string casp = "casp" + (load ? "a" : string.Empty) + (o0 ? "l" : string.Empty);
            return I(w, casp, R(caspX, rs), R(caspX, rs + 1), R(caspX, rt), R(caspX, rt + 1), memory);
        }

        if (o2 && o1)
        {
            if (rt2 != 31)
            {
                return null;
            }

            string cas = "cas" + (load ? "a" : string.Empty) + (o0 ? "l" : string.Empty) + SizeSuffix(size);
            return I(w, cas, R(x, rs), R(x, rt), memory);
        }

        string suffix = SizeSuffix(size);
        return (o2, load, o0) switch
        {
            (false, false, false) => I(w, "stxr" + suffix, W(rs), R(x, rt), memory),
            (false, false, true) => I(w, "stlxr" + suffix, W(rs), R(x, rt), memory),
            (false, true, false) => I(w, "ldxr" + suffix, R(x, rt), memory),
            (false, true, true) => I(w, "ldaxr" + suffix, R(x, rt), memory),
            (true, false, false) => I(w, "stllr" + suffix, R(x, rt), memory),
            (true, false, true) => I(w, "stlr" + suffix, R(x, rt), memory),
            (true, true, false) => I(w, "ldlar" + suffix, R(x, rt), memory),
            (true, true, true) => I(w, "ldar" + suffix, R(x, rt), memory),
        };
    }

    /// <summary>The RCpc loads and stores with an unscaled offset: <c>ldapur</c>, <c>stlur</c>.</summary>
    private static Arm64Instruction? OrderedUnscaled(uint w)
    {
        uint size = F(w, 31, 30);
        uint opc = F(w, 23, 22);
        long offset = SignExtend(F(w, 20, 12), 9);
        var memory = Base(F(w, 9, 5), offset);
        uint rt = F(w, 4, 0);
        string suffix = SizeSuffix(size);
        return (opc, size) switch
        {
            (0b00, _) => I(w, "stlur" + suffix, R(size == 3, rt), memory),
            (0b01, _) => I(w, "ldapur" + suffix, R(size == 3, rt), memory),
            (0b10, 0 or 1) => I(w, "ldapurs" + suffix, X(rt), memory),
            (0b10, 2) => I(w, "ldapursw", X(rt), memory),
            (0b11, 0 or 1) => I(w, "ldapurs" + suffix, W(rt), memory),
            _ => null,
        };
    }

    private static Arm64Instruction? Literal(uint w, ulong pc)
    {
        uint opc = F(w, 31, 30);
        uint rt = F(w, 4, 0);
        var address = new Arm64Address((ulong)((long)pc + (SignExtend(F(w, 23, 5), 19) << 2)));
        if (Bit(w, 26))
        {
            return opc switch
            {
                0b00 => I(w, "ldr", V(Arm64RegisterKind.S, rt), address),
                0b01 => I(w, "ldr", V(Arm64RegisterKind.D, rt), address),
                0b10 => I(w, "ldr", V(Arm64RegisterKind.Q, rt), address),
                _ => null,
            };
        }

        return opc switch
        {
            0b00 => I(w, "ldr", W(rt), address),
            0b01 => I(w, "ldr", X(rt), address),
            0b10 => I(w, "ldrsw", X(rt), address),
            _ => I(w, "prfm", Prefetch(rt), address),
        };
    }

    private static Arm64Instruction? Pair(uint w)
    {
        uint opc = F(w, 31, 30);
        bool v = Bit(w, 26);
        uint mode = F(w, 24, 23);
        bool load = Bit(w, 22);
        long imm7 = SignExtend(F(w, 21, 15), 7);
        uint rt2 = F(w, 14, 10);
        uint rn = F(w, 9, 5);
        uint rt = F(w, 4, 0);

        Arm64RegisterKind kind;
        int scale;
        string name = mode == 0 ? (load ? "ldnp" : "stnp") : (load ? "ldp" : "stp");
        if (v)
        {
            (kind, scale) = opc switch
            {
                0b00 => (Arm64RegisterKind.S, 2),
                0b01 => (Arm64RegisterKind.D, 3),
                0b10 => (Arm64RegisterKind.Q, 4),
                _ => (Arm64RegisterKind.X, -1),
            };
        }
        else
        {
            (kind, scale) = opc switch
            {
                0b00 => (Arm64RegisterKind.W, 2),
                0b01 when load && mode != 0 => (Arm64RegisterKind.X, 2),
                0b10 => (Arm64RegisterKind.X, 3),
                _ => (Arm64RegisterKind.X, -1),
            };

            if (opc == 0b01)
            {
                name = "ldpsw";
            }
        }

        if (scale < 0)
        {
            return null;
        }

        var indexing = mode switch
        {
            0b01 => Arm64Indexing.PostIndex,
            0b11 => Arm64Indexing.PreIndex,
            _ => Arm64Indexing.Offset,
        };

        return I(w, name, V(kind, rt), V(kind, rt2), Base(rn, imm7 << scale, indexing));
    }

    /// <summary>
    /// What a load or store of one register is, from its size, V and opc fields: the name in its scaled form, the
    /// register it moves, and the scale of its offset. Null for the unallocated combinations.
    /// </summary>
    private static (string Name, Arm64RegisterKind Kind, int Scale)? Access(uint size, bool v, uint opc)
    {
        if (v)
        {
            bool load = (opc & 1) != 0;
            string name = load ? "ldr" : "str";
            if ((opc & 2) != 0)
            {
                return size == 0 ? (name, Arm64RegisterKind.Q, 4) : null;
            }

            var kind = size switch
            {
                0 => Arm64RegisterKind.B,
                1 => Arm64RegisterKind.H,
                2 => Arm64RegisterKind.S,
                _ => Arm64RegisterKind.D,
            };
            return (name, kind, (int)size);
        }

        return (size, opc) switch
        {
            (0, 0) => ("strb", Arm64RegisterKind.W, 0),
            (0, 1) => ("ldrb", Arm64RegisterKind.W, 0),
            (0, 2) => ("ldrsb", Arm64RegisterKind.X, 0),
            (0, 3) => ("ldrsb", Arm64RegisterKind.W, 0),
            (1, 0) => ("strh", Arm64RegisterKind.W, 1),
            (1, 1) => ("ldrh", Arm64RegisterKind.W, 1),
            (1, 2) => ("ldrsh", Arm64RegisterKind.X, 1),
            (1, 3) => ("ldrsh", Arm64RegisterKind.W, 1),
            (2, 0) => ("str", Arm64RegisterKind.W, 2),
            (2, 1) => ("ldr", Arm64RegisterKind.W, 2),
            (2, 2) => ("ldrsw", Arm64RegisterKind.X, 2),
            (3, 0) => ("str", Arm64RegisterKind.X, 3),
            (3, 1) => ("ldr", Arm64RegisterKind.X, 3),
            (3, 2) => ("prfm", Arm64RegisterKind.X, 3),
            _ => null,
        };
    }

    /// <summary>A load or store with an immediate offset: unsigned and scaled, or signed and unscaled, pre- or post-indexed, or unprivileged.</summary>
    private static Arm64Instruction? RegisterAccess(uint w, uint field, bool unsignedOffset)
    {
        uint size = F(w, 31, 30);
        bool v = Bit(w, 26);
        uint opc = F(w, 23, 22);
        uint rn = F(w, 9, 5);
        uint rt = F(w, 4, 0);
        if (Access(size, v, opc) is not var (name, kind, scale))
        {
            return null;
        }

        bool prefetch = name == "prfm";
        Arm64Operand target = prefetch ? Prefetch(rt) : V(kind, rt);
        if (unsignedOffset)
        {
            return I(w, name, target, Base(rn, (long)field << scale));
        }

        long offset = SignExtend(field >> 2, 9);
        switch (field & 3)
        {
            case 0b00:
                return I(w, prefetch ? "prfum" : name[..2] + "u" + name[2..], target, Base(rn, offset));
            case 0b01 when !prefetch:
                return I(w, name, target, Base(rn, offset, Arm64Indexing.PostIndex));
            case 0b11 when !prefetch:
                return I(w, name, target, Base(rn, offset, Arm64Indexing.PreIndex));
            case 0b10 when !prefetch && !v:
                return I(w, name[..2] + "t" + name[2..], target, Base(rn, offset));
        }

        return null;
    }

    private static Arm64Instruction? RegisterOffset(uint w)
    {
        uint size = F(w, 31, 30);
        bool v = Bit(w, 26);
        uint opc = F(w, 23, 22);
        uint rm = F(w, 20, 16);
        uint option = F(w, 15, 13);
        bool s = Bit(w, 12);
        uint rn = F(w, 9, 5);
        uint rt = F(w, 4, 0);
        if ((option & 2) == 0 || Access(size, v, opc) is not var (name, kind, scale))
        {
            return null;
        }

        var extend = option switch
        {
            0b010 => Arm64Extend.Uxtw,
            0b011 => Arm64Extend.Lsl,
            0b110 => Arm64Extend.Sxtw,
            _ => Arm64Extend.Sxtx,
        };

        var memory = Base(rn) with
        {
            Index = new Arm64Register((option & 1) != 0 ? Arm64RegisterKind.X : Arm64RegisterKind.W, (int)rm),
            Extend = extend,
            Amount = s ? scale : 0,
            ShowZeroAmount = s && scale == 0,
        };

        return I(w, name, name == "prfm" ? Prefetch(rt) : V(kind, rt), memory);
    }

    /// <summary>The single-copy atomic operations (LSE): <c>ldadd</c>, <c>swp</c> and the rest, and their <c>st</c> forms.</summary>
    private static Arm64Instruction? Atomic(uint w)
    {
        if (Bit(w, 26))
        {
            return null;
        }

        uint size = F(w, 31, 30);
        bool acquire = Bit(w, 23);
        bool release = Bit(w, 22);
        uint rs = F(w, 20, 16);
        bool o3 = Bit(w, 15);
        uint opc = F(w, 14, 12);
        uint rn = F(w, 9, 5);
        uint rt = F(w, 4, 0);
        bool x = size == 3;
        string order = (acquire ? "a" : string.Empty) + (release ? "l" : string.Empty);
        string suffix = SizeSuffix(size);

        if (o3)
        {
            return opc switch
            {
                0b000 => I(w, "swp" + order + suffix, R(x, rs), R(x, rt), Base(rn)),
                0b100 when acquire && !release && rs == 31 => I(w, "ldapr" + suffix, R(x, rt), Base(rn)),
                _ => null,
            };
        }

        string operation = opc switch
        {
            0b000 => "add",
            0b001 => "clr",
            0b010 => "eor",
            0b011 => "set",
            0b100 => "smax",
            0b101 => "smin",
            0b110 => "umax",
            _ => "umin",
        };

        // With the old value thrown away and no acquire, it is a store: stadd, stclrl, ...
        return rt == 31 && !acquire
            ? I(w, "st" + operation + order + suffix, R(x, rs), Base(rn))
            : I(w, "ld" + operation + order + suffix, R(x, rs), R(x, rt), Base(rn));
    }

    /// <summary><c>ldraa</c> / <c>ldrab</c>: a load through a pointer authenticated first.</summary>
    private static Arm64Instruction? PointerAuthLoad(uint w)
    {
        if (F(w, 31, 30) != 3 || Bit(w, 26))
        {
            return null;
        }

        long offset = SignExtend((F(w, 22, 22) << 9) | F(w, 20, 12), 10) << 3;
        string name = Bit(w, 23) ? "ldrab" : "ldraa";
        var indexing = Bit(w, 11) ? Arm64Indexing.PreIndex : Arm64Indexing.Offset;
        return I(w, name, X(F(w, 4, 0)), Base(F(w, 9, 5), offset, indexing));
    }

    /// <summary>A prefetch's operation: <c>pldl1keep</c>, or the number when it has no name.</summary>
    private static Arm64Operand Prefetch(uint rt)
    {
        uint type = rt >> 3;
        uint target = (rt >> 1) & 3;
        if (type == 3 || target == 3)
        {
            return Imm(rt);
        }

        string kind = type switch { 0 => "pld", 1 => "pli", _ => "pst" };
        return T($"{kind}l{target + 1}{((rt & 1) == 0 ? "keep" : "strm")}");
    }
}
