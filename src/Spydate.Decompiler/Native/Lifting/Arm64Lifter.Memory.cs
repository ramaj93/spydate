using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Passes;
using Spydate.Disassembly;
using Spydate.Disassembly.Arm64;

namespace Spydate.Decompiler.Native.Lifting;

public sealed partial class Arm64Lifter
{
    // ------------------------------------------------------------------
    // Loads and stores
    // ------------------------------------------------------------------

    private bool LoadStore(string m, IReadOnlyList<Arm64Operand> ops, DecodedInstruction ins, Context ctx, IrFunction fn, Action<IrStmt> emit)
    {
        // Literal loads: the address is in the instruction.
        if (m is "ldr" or "ldrsw" && ops is [Arm64RegisterOperand literalTarget, Arm64Address literal])
        {
            int bits = m == "ldrsw" ? 32 : Bits(literalTarget);
            IrExpr loaded = new IrMem(SymbolOrConst(literal.Address, 64), bits);
            Set(literalTarget, m == "ldrsw" ? new IrCast(loaded, 64, true) : loaded, emit);
            return true;
        }

        if (ops.Count == 0 || ops[^1] is not Arm64Memory memory)
        {
            return false;
        }

        if (PlainAccess().Match(m) is { Success: true } plain && ops is [Arm64RegisterOperand register, Arm64Memory])
        {
            bool load = plain.Groups[1].Value == "ld";
            bool signed = plain.Groups[3].Value == "s";
            int width = plain.Groups[4].Value switch { "b" => 8, "h" => 16, "w" => 32, _ => Bits(register) };
            Access(memory, ins, emit, address =>
            {
                if (load)
                {
                    IrExpr value = new IrMem(address, width);
                    Set(register, width == Bits(register) ? value : new IrCast(value, Bits(register), signed), emit);
                }
                else
                {
                    var value = Read(register);
                    emit(new IrStore(address, width == value.Bits ? value : new IrCast(value, width, false), width));
                }
            });
            return true;
        }

        if (m is "ldp" or "stp" or "ldnp" or "stnp" or "ldpsw" && ops is [Arm64RegisterOperand first, Arm64RegisterOperand second, Arm64Memory])
        {
            bool load = m.StartsWith("ld", StringComparison.Ordinal);
            int width = m == "ldpsw" ? 32 : Bits(first);
            Access(memory, ins, emit, address =>
            {
                var high = Plus(address, width / 8);
                if (load)
                {
                    IrExpr a = new IrMem(address, width);
                    IrExpr b = new IrMem(high, width);

                    // ldp x0, x1, [x0] reads the base after writing x0: only then through a temporary.
                    if (!first.Register.IsZero && IrRewriter.Descendants(high).Any(e => e is IrReg r && RegisterAliases.Overlap(r.Name, Name(first.Register))))
                    {
                        var saved = ctx.NewTemp(width);
                        emit(new IrAssign(saved, b));
                        b = saved;
                    }

                    Set(first, m == "ldpsw" ? new IrCast(a, 64, true) : a, emit);
                    Set(second, m == "ldpsw" ? new IrCast(b, 64, true) : b, emit);
                }
                else
                {
                    emit(new IrStore(address, Read(first), width));
                    emit(new IrStore(high, Read(second), width));
                }
            });
            return true;
        }

        // Exclusive and ordered accesses are loads and stores, and an exclusive store reports whether it held.
        if (m.StartsWith("ldxr", StringComparison.Ordinal) || m.StartsWith("ldaxr", StringComparison.Ordinal))
        {
            if (ops is [Arm64RegisterOperand target, Arm64Memory])
            {
                int width = SizeOf(m, Bits(target));
                Set(target, Widen(new IrCall(new IrSymbol("__ldxr", 0, 0), [Address(memory, ins)], width), Bits(target)), emit);
                return true;
            }

            return false;
        }

        if (m.StartsWith("stxr", StringComparison.Ordinal) || m.StartsWith("stlxr", StringComparison.Ordinal))
        {
            if (ops is [Arm64RegisterOperand status, Arm64RegisterOperand source, Arm64Memory])
            {
                int width = SizeOf(m, Bits(source));
                var call = new IrCall(new IrSymbol("__stxr", 0, 0), [Address(memory, ins), Narrow(Read(source), width)], 32) { ConventionKnown = true };
                emit(new IrCallStmt(call, status.Register.IsZero ? null : new IrReg(Name(status.Register), 32)));
                return true;
            }

            return false;
        }

        if (AtomicAccess().Match(m) is { Success: true } atomic)
        {
            string operation = atomic.Groups[2].Value;
            int width = atomic.Groups[5].Value switch { "b" => 8, "h" => 16, _ => 0 };
            bool store = atomic.Groups[1].Value == "st";
            if (ops is [Arm64RegisterOperand operand, .., Arm64Memory])
            {
                width = width == 0 ? Bits(operand) : width;
                var call = new IrCall(new IrSymbol($"__atomic_fetch_{operation}", 0, 0), [Address(memory, ins), Narrow(Read(operand), width)], width) { ConventionKnown = true };
                IrExpr? result = !store && ops is [_, Arm64RegisterOperand old, _] && !old.Register.IsZero ? new IrReg(Name(old.Register), Bits(old)) : null;
                emit(new IrCallStmt(call, result));
                return true;
            }

            return false;
        }

        if (m.StartsWith("swp", StringComparison.Ordinal) && ops is [Arm64RegisterOperand swapIn, Arm64RegisterOperand swapOut, Arm64Memory])
        {
            int width = SizeOf(m.TrimEnd('a', 'l'), Bits(swapIn));
            var call = new IrCall(new IrSymbol("__atomic_exchange", 0, 0), [Address(memory, ins), Narrow(Read(swapIn), width)], width) { ConventionKnown = true };
            emit(new IrCallStmt(call, swapOut.Register.IsZero ? null : new IrReg(Name(swapOut.Register), Bits(swapOut))));
            return true;
        }

        if (m.StartsWith("cas", StringComparison.Ordinal) && !m.StartsWith("casp", StringComparison.Ordinal)
            && ops is [Arm64RegisterOperand expected, Arm64RegisterOperand replacement, Arm64Memory])
        {
            int width = Bits(expected);
            var call = new IrCall(new IrSymbol("__atomic_compare_exchange", 0, 0), [Address(memory, ins), Read(expected), Read(replacement)], width) { ConventionKnown = true };
            emit(new IrCallStmt(call, expected.Register.IsZero ? null : new IrReg(Name(expected.Register), width)));
            return true;
        }

        return false;
    }

    private static int SizeOf(string mnemonic, int registerBits) => mnemonic[^1] switch
    {
        'b' => 8,
        'h' => 16,
        _ => registerBits,
    };

    private static IrExpr Narrow(IrExpr value, int bits) => value.Bits == bits ? value : new IrCast(value, bits, false);

    private static IrExpr Widen(IrExpr value, int bits) => value.Bits == bits ? value : new IrCast(value, bits, false);

    /// <summary>
    /// A memory access with its write-back: pre-indexed moves the base first and uses it, post-indexed uses the
    /// base and moves it after.
    /// </summary>
    private void Access(Arm64Memory memory, DecodedInstruction ins, Action<IrStmt> emit, Action<IrExpr> access)
    {
        var baseRegister = new IrReg(Name(memory.Base), 64);
        switch (memory.Indexing)
        {
            case Arm64Indexing.PreIndex:
                emit(new IrAssign(baseRegister, Offset(baseRegister, memory.Offset)));
                access(baseRegister);
                return;
            case Arm64Indexing.PostIndex:
                access(baseRegister);
                emit(new IrAssign(baseRegister, memory.PostRegister is { } by ? new IrBinary(IrBinaryOp.Add, baseRegister, Read(by)) : Offset(baseRegister, memory.Offset)));
                return;
            default:
                access(Address(memory, ins));
                return;
        }
    }

    /// <summary>The address an offset memory operand names: the decoder's when it traced it, else base + offset or index.</summary>
    private IrExpr Address(Arm64Memory memory, DecodedInstruction ins)
    {
        if (ins.DataVa is { } known && !memory.Base.IsStackPointer && memory.Index is null && memory.Indexing == Arm64Indexing.Offset)
        {
            return SymbolOrConst(known, 64);
        }

        IrExpr baseValue = new IrReg(Name(memory.Base), 64);
        if (memory.Index is { } index)
        {
            return new IrBinary(IrBinaryOp.Add, baseValue, Extend(Read(index), memory.Extend, memory.Amount, 64));
        }

        return memory.Indexing == Arm64Indexing.PostIndex ? baseValue : Offset(baseValue, memory.Offset);
    }

    /// <summary>An address a few bytes on, folded into its constant so the frame pass sees <c>sp + 0x58</c>, not <c>(sp + 0x50) + 8</c>.</summary>
    private static IrExpr Plus(IrExpr address, long bytes) => address switch
    {
        IrBinary { Op: IrBinaryOp.Add, Right: IrConst c } add => Offset(add.Left, c.Value + bytes),
        IrBinary { Op: IrBinaryOp.Sub, Right: IrConst c } sub => Offset(sub.Left, bytes - c.Value),
        IrConst c => new IrConst(c.Value + bytes, c.Bits),
        _ => Offset(address, bytes),
    };

    private static IrExpr Offset(IrExpr value, long offset) => offset switch
    {
        0 => value,
        < 0 => new IrBinary(IrBinaryOp.Sub, value, new IrConst(-offset, 64)),
        _ => new IrBinary(IrBinaryOp.Add, value, new IrConst(offset, 64)),
    };

    // ------------------------------------------------------------------
    // Scalar floating point
    // ------------------------------------------------------------------

    private bool FloatingPoint(string m, IReadOnlyList<Arm64Operand> ops, DecodedInstruction ins, Context ctx, Action<IrStmt> emit)
    {
        if (m.Length < 2 || m[0] != 'f' && m is not ("scvtf" or "ucvtf"))
        {
            return false;
        }

        // Only scalar forms: a vector operand is written as text (v0.4s, v1.s[2]), and stays verbatim. A condition is text too.
        if (ops.Any(o => o is Arm64Text t && t.Text.IndexOfAny(['.', '[', '{']) >= 0))
        {
            return false;
        }

        switch (m, ops)
        {
            case ("fmov", [Arm64RegisterOperand d, Arm64FloatImmediate value]):
                Set(d, FloatConstant(value.Value, Bits(d)), emit);
                return true;
            case ("fmov", [Arm64RegisterOperand d, Arm64RegisterOperand s]):
                Set(d, Read(s), emit);
                return true;
            case ("fadd" or "fsub" or "fmul" or "fdiv" or "fnmul", [Arm64RegisterOperand d, Arm64RegisterOperand l, Arm64RegisterOperand r]):
            {
                var op = m switch { "fadd" => IrBinaryOp.FAdd, "fsub" => IrBinaryOp.FSub, "fdiv" => IrBinaryOp.FDiv, _ => IrBinaryOp.FMul };
                IrExpr result = new IrBinary(op, Read(l), Read(r));
                Set(d, m == "fnmul" ? new IrUnary(IrUnaryOp.Neg, result) : result, emit);
                return true;
            }

            case ("fmadd" or "fmsub" or "fnmadd" or "fnmsub", [Arm64RegisterOperand d, Arm64RegisterOperand l, Arm64RegisterOperand r, Arm64RegisterOperand a]):
            {
                IrExpr product = new IrBinary(IrBinaryOp.FMul, Read(l), Read(r));
                IrExpr result = m switch
                {
                    "fmadd" => new IrBinary(IrBinaryOp.FAdd, Read(a), product),
                    "fmsub" => new IrBinary(IrBinaryOp.FSub, Read(a), product),
                    "fnmadd" => new IrUnary(IrUnaryOp.Neg, new IrBinary(IrBinaryOp.FAdd, Read(a), product)),
                    _ => new IrBinary(IrBinaryOp.FSub, product, Read(a)),
                };
                Set(d, result, emit);
                return true;
            }

            case ("fneg", [Arm64RegisterOperand d, Arm64RegisterOperand s]):
                Set(d, new IrUnary(IrUnaryOp.Neg, Read(s)), emit);
                return true;

            case ("fabs" or "fsqrt" or "frintm" or "frintp" or "frintz" or "frinta" or "frintn" or "frintx" or "frinti", [Arm64RegisterOperand d, Arm64RegisterOperand s]):
            {
                string name = m switch
                {
                    "fabs" => "fabs",
                    "fsqrt" => "sqrt",
                    "frintm" => "floor",
                    "frintp" => "ceil",
                    "frintz" => "trunc",
                    "frinta" => "round",
                    "frintn" => "roundeven",
                    _ => "rint",
                };
                Set(d, new IrCall(new IrSymbol(Bits(d) == 32 ? name + "f" : name, 0, 0), [Read(s)], Bits(d)), emit);
                return true;
            }

            case ("fmax" or "fmin" or "fmaxnm" or "fminnm", [Arm64RegisterOperand d, Arm64RegisterOperand l, Arm64RegisterOperand r]):
            {
                string name = m.StartsWith("fmax", StringComparison.Ordinal) ? "fmax" : "fmin";
                Set(d, new IrCall(new IrSymbol(Bits(d) == 32 ? name + "f" : name, 0, 0), [Read(l), Read(r)], Bits(d)), emit);
                return true;
            }

            case ("fcvt", [Arm64RegisterOperand d, Arm64RegisterOperand s]):
                Set(d, new IrCast(Read(s), Bits(d), true) { IsFloat = true }, emit);
                return true;

            case ("scvtf" or "ucvtf", [Arm64RegisterOperand d, Arm64RegisterOperand s]):
                Set(d, new IrCast(Read(s), Bits(d), m == "scvtf") { IsFloat = true }, emit);
                return true;

            case ("fcvtzs" or "fcvtzu" or "fcvtms" or "fcvtmu" or "fcvtps" or "fcvtpu" or "fcvtns" or "fcvtnu" or "fcvtas" or "fcvtau", [Arm64RegisterOperand d, Arm64RegisterOperand s]):
            {
                // Toward zero is a C cast; the other roundings round first.
                IrExpr value = Read(s);
                string? round = m[4] switch { 'm' => "floor", 'p' => "ceil", 'n' => "roundeven", 'a' => "round", _ => null };
                if (round is not null)
                {
                    value = new IrCall(new IrSymbol(Bits(s) == 32 ? round + "f" : round, 0, 0), [value], Bits(s));
                }

                Set(d, new IrCast(value, Bits(d), m.EndsWith('s')), emit);
                return true;
            }

            case ("fcmp" or "fcmpe", [Arm64RegisterOperand l, var r]):
                ctx.Flags = Flags.FloatCompare(Read(l), r is Arm64FloatImmediate z ? FloatConstant(z.Value, Bits(l)) : Value(r, Bits(l)));
                return true;

            case ("fccmp" or "fccmpe", [Arm64RegisterOperand l, Arm64RegisterOperand r, Arm64Immediate nzcv, Arm64Text cc]):
                ctx.Flags = Flags.Chain(ctx.Flags, cc.Text, Flags.FloatCompare(Read(l), Read(r)), (int)nzcv.Value);
                return true;

            case ("fcsel", [Arm64RegisterOperand d, Arm64RegisterOperand t, Arm64RegisterOperand f, Arm64Text cc]):
                Set(d, new IrTernary(ctx.Condition(cc.Text), Read(t), Read(f)), emit);
                return true;
        }

        return false;
    }

    /// <summary>
    /// A floating-point constant, as the literal C would write: <c>1.0</c>, <c>0.5f</c>. The IR has no float
    /// constant, and a symbol is a leaf every pass leaves alone and the emitter prints by name.
    /// </summary>
    private static IrExpr FloatConstant(double value, int bits)
    {
        string text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (!text.Contains('.', StringComparison.Ordinal) && !text.Contains('E', StringComparison.Ordinal))
        {
            text += ".0";
        }

        return new IrSymbol(bits == 32 ? text + "f" : text, 0, bits);
    }

    // ------------------------------------------------------------------
    // Flags
    // ------------------------------------------------------------------

    /// <summary>
    /// What the last flag-setting instruction compared, and how a condition code reads it. A conditional compare
    /// (<c>ccmp</c>) is a chain: if the earlier condition held, the flags are this compare's; if not, they are the
    /// constant the instruction names.
    /// </summary>
    private sealed record Flags(IrExpr Left, IrExpr Right, bool IsTest, bool IsFloat)
    {
        public (Flags? Previous, string Condition, int Nzcv)? Chained { get; init; }

        public static Flags Compare(IrExpr left, IrExpr right) => new(left, right, false, false);

        public static Flags Test(IrExpr left, IrExpr right) => new(left, right, true, false);

        public static Flags FloatCompare(IrExpr left, IrExpr right) => new(left, right, false, true);

        public static Flags Chain(Flags? previous, string condition, Flags compare, int nzcv) => compare with { Chained = (previous, condition, nzcv) };

        public IrExpr ToCondition(string cc)
        {
            var own = Own(cc);
            if (Chained is not var (previous, condition, nzcv))
            {
                return own;
            }

            IrExpr earlier = previous?.ToCondition(condition) ?? new IrUnknown($"{condition} flags", 1);

            // When the earlier condition failed, the flags are nzcv: the whole is "earlier and this" if nzcv
            // makes the condition false, "not earlier, or this" if it makes it true.
            return Holds(nzcv, cc)
                ? new IrBinary(IrBinaryOp.Or, new IrUnary(IrUnaryOp.LogicalNot, earlier), own)
                : new IrBinary(IrBinaryOp.And, earlier, own);
        }

        private IrExpr Own(string cc)
        {
            if (cc is "al" or "nv")
            {
                return new IrConst(1, 1);
            }

            if (IsTest)
            {
                IrExpr subject = Left.Equals(Right) ? Left : new IrBinary(IrBinaryOp.And, Left, Right);
                return new IrCondition(Code(cc), subject, new IrConst(0, subject.Bits));
            }

            return new IrCondition(IsFloat ? FloatCode(cc) : Code(cc), Left, Right);
        }

        private static IrCondCode Code(string cc) => cc switch
        {
            "eq" => IrCondCode.Equal,
            "ne" => IrCondCode.NotEqual,
            "cs" or "hs" => IrCondCode.AboveOrEqual,
            "cc" or "lo" => IrCondCode.Below,
            "mi" => IrCondCode.Sign,
            "pl" => IrCondCode.NotSign,
            "vs" => IrCondCode.Overflow,
            "vc" => IrCondCode.NotOverflow,
            "hi" => IrCondCode.Above,
            "ls" => IrCondCode.BelowOrEqual,
            "ge" => IrCondCode.GreaterOrEqual,
            "lt" => IrCondCode.Less,
            "gt" => IrCondCode.Greater,
            _ => IrCondCode.LessOrEqual,
        };

        /// <summary>After <c>fcmp</c>, the conditions compilers use read as ordered comparisons.</summary>
        private static IrCondCode FloatCode(string cc) => cc switch
        {
            "eq" => IrCondCode.Equal,
            "ne" => IrCondCode.NotEqual,
            "mi" or "lt" or "cc" or "lo" => IrCondCode.Less,
            "ls" or "le" => IrCondCode.LessOrEqual,
            "gt" or "hi" => IrCondCode.Greater,
            "ge" or "pl" or "cs" or "hs" => IrCondCode.GreaterOrEqual,
            "vs" => IrCondCode.Parity,
            _ => IrCondCode.NotParity,
        };

        /// <summary>Whether a condition holds for constant flags N:Z:C:V.</summary>
        private static bool Holds(int nzcv, string cc)
        {
            bool n = (nzcv & 8) != 0, z = (nzcv & 4) != 0, c = (nzcv & 2) != 0, v = (nzcv & 1) != 0;
            return cc switch
            {
                "eq" => z,
                "ne" => !z,
                "cs" or "hs" => c,
                "cc" or "lo" => !c,
                "mi" => n,
                "pl" => !n,
                "vs" => v,
                "vc" => !v,
                "hi" => c && !z,
                "ls" => !c || z,
                "ge" => n == v,
                "lt" => n != v,
                "gt" => !z && n == v,
                "le" => z || n != v,
                _ => true,
            };
        }
    }

    private sealed class Context
    {
        private int _nextTemp;

        public Flags? Flags { get; set; }

        public IrTemp NewTemp(int bits) => new(_nextTemp++, bits);

        public IrExpr Condition(string cc) => Flags?.ToCondition(cc) ?? (cc is "al" or "nv" ? new IrConst(1, 1) : new IrUnknown($"{cc} flags", 1));
    }
}
