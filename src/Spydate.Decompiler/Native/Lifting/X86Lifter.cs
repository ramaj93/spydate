using Iced.Intel;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native.IR;
using Spydate.Disassembly;

namespace Spydate.Decompiler.Native.Lifting;

/// <summary>
/// Lifts x86 / x64 <see cref="DecodedInstruction"/>s to Spydate IR. Every instruction produces zero or more
/// statements; unsupported instructions are preserved verbatim as <see cref="IrAsm"/> and a warning is recorded.
/// The lifter never throws for unexpected input.
/// </summary>
public sealed class X86Lifter
{
    private readonly int _bitness;
    private readonly SymbolTable? _symbols;
    private IReadOnlyDictionary<ulong, JumpTable> _jumpTables = new Dictionary<ulong, JumpTable>();

    public X86Lifter(int bitness, SymbolTable? symbols = null)
    {
        _bitness = bitness;
        _symbols = symbols;
    }

    private string Sp => _bitness == 64 ? "rsp" : "esp";
    private string Bp => _bitness == 64 ? "rbp" : "ebp";
    private string Ax => _bitness == 64 ? "rax" : "eax";
    private string Dx => _bitness == 64 ? "rdx" : "edx";
    private string Cx => _bitness == 64 ? "rcx" : "ecx";
    private int PtrBits => _bitness;

    /// <summary>Lifts a discovered function into an <see cref="IrFunction"/>.</summary>
    public IrFunction Lift(Function function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var ir = new IrFunction(function.EntryVa, function.Name, _bitness);
        var ctx = new LiftContext();
        _jumpTables = function.JumpTables.GroupBy(t => t.JumpVa).ToDictionary(g => g.Key, g => g.First());

        FlagState? carried = null;
        BasicBlock? previous = null;
        foreach (var block in function.Blocks)
        {
            var irBlock = new IrBlock(block.StartVa);
            irBlock.Successors.AddRange(block.Successors);
            irBlock.Predecessors.AddRange(block.Predecessors);

            // Carry the flag producer across a pure fallthrough boundary (cmp at end of block A, jcc at start of block B).
            ctx.Flags = previous is not null && previous.EndVa == block.StartVa && !previous.Last.EndsBlock ? carried : null;

            foreach (var ins in block.Instructions)
            {
                try
                {
                    LiftInstruction(ins, ctx, irBlock, ir);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
                {
                    irBlock.Statements.Add(new IrAsm(ins.Text) { Va = ins.Va });
                    ir.Warnings.Add($"0x{ins.Va:X}: lifter error for '{ins.Text}': {ex.Message}");
                }
            }

            carried = ctx.Flags;
            previous = block;
            ir.Blocks.Add(irBlock);
        }

        foreach (var stmt in ir.AllStatements)
        {
            switch (stmt)
            {
                case IrGoto g:
                    ir.LabelTargets.Add(g.TargetVa);
                    break;
                case IrBranch b:
                    ir.LabelTargets.Add(b.TargetVa);
                    break;
            }
        }

        return ir;
    }

    // ------------------------------------------------------------------
    // Per-instruction lifting
    // ------------------------------------------------------------------

    private void LiftInstruction(DecodedInstruction ins, LiftContext ctx, IrBlock block, IrFunction fn)
    {
        var instr = ins.Native;
        ulong va = ins.Va;
        var stmts = block.Statements;

        void Emit(IrStmt s) => stmts.Add(s with { Va = va });

        if (ins.Flow == InstructionFlow.Invalid)
        {
            Emit(new IrAsm(ins.Text));
            fn.Warnings.Add($"0x{va:X}: invalid instruction bytes.");
            return;
        }

        // Conditional families first (jcc / setcc / cmovcc share ConditionCode).
        if (instr.ConditionCode != ConditionCode.None)
        {
            var cc = MapCc(instr.ConditionCode);
            var cond = ctx.BuildCondition(cc);
            if (ins.Flow == InstructionFlow.ConditionalBranch && ins.BranchTargetVa is { } target)
            {
                Emit(new IrBranch(cond, target, ins.NextVa));
                return;
            }

            string mn = instr.Mnemonic.ToString();
            if (mn.StartsWith("Set", StringComparison.Ordinal) && instr.OpCount == 1)
            {
                Emit(Assign(instr, 0, new IrCast(cond, 8, false)));
                return;
            }

            if (mn.StartsWith("Cmov", StringComparison.Ordinal) && instr.OpCount == 2)
            {
                Emit(Assign(instr, 0, new IrTernary(cond, Operand(instr, 1), Operand(instr, 0))));
                return;
            }
        }

        switch (instr.Mnemonic)
        {
            case Mnemonic.Nop:
            case Mnemonic.Endbr32:
            case Mnemonic.Endbr64:
            case Mnemonic.Pause:
                return;

            case Mnemonic.Mov:
            case Mnemonic.Movaps:
            case Mnemonic.Movups:
            case Mnemonic.Movdqa:
            case Mnemonic.Movdqu:
            case Mnemonic.Movapd:
            case Mnemonic.Movupd:
            case Mnemonic.Movd:
            case Mnemonic.Movq:
            case Mnemonic.Movss:
            case Mnemonic.Movsd when instr.OpCount == 2:
            case Mnemonic.Vmovaps:
            case Mnemonic.Vmovups:
            case Mnemonic.Vmovdqa:
            case Mnemonic.Vmovdqu:
                Emit(Assign(instr, 0, Operand(instr, 1)));
                return;

            case Mnemonic.Movzx:
                Emit(Assign(instr, 0, new IrCast(Operand(instr, 1), OperandBits(instr, 0), false)));
                return;

            case Mnemonic.Movsx:
            case Mnemonic.Movsxd:
                Emit(Assign(instr, 0, new IrCast(Operand(instr, 1), OperandBits(instr, 0), true)));
                return;

            case Mnemonic.Lea:
                Emit(Assign(instr, 0, MemoryAddress(instr)));
                return;

            case Mnemonic.Add:
                LiftBinary(instr, IrBinaryOp.Add, ctx, Emit);
                return;
            case Mnemonic.Sub:
                LiftBinary(instr, IrBinaryOp.Sub, ctx, Emit);
                return;
            case Mnemonic.And:
                LiftBinary(instr, IrBinaryOp.And, ctx, Emit);
                return;
            case Mnemonic.Or:
                LiftBinary(instr, IrBinaryOp.Or, ctx, Emit);
                return;
            case Mnemonic.Xor:
            case Mnemonic.Pxor:
            case Mnemonic.Xorps:
            case Mnemonic.Xorpd:
            case Mnemonic.Vpxor:
            case Mnemonic.Vxorps:
                if (SameRegister(instr))
                {
                    var zero = new IrConst(0, OperandBits(instr, 0));
                    Emit(Assign(instr, 0, zero));
                    ctx.SetFlagsFromResult(Operand(instr, 0));
                    return;
                }

                LiftBinary(instr, IrBinaryOp.Xor, ctx, Emit);
                return;

            case Mnemonic.Adc:
                Emit(Assign(instr, 0, new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Add, Operand(instr, 0), Operand(instr, 1)), new IrReg("CF", 1))));
                ctx.SetFlagsFromResult(Operand(instr, 0));
                return;
            case Mnemonic.Sbb:
                if (SameRegister(instr))
                {
                    Emit(Assign(instr, 0, new IrUnary(IrUnaryOp.Neg, new IrCast(new IrReg("CF", 1), OperandBits(instr, 0), false))));
                }
                else
                {
                    Emit(Assign(instr, 0, new IrBinary(IrBinaryOp.Sub, new IrBinary(IrBinaryOp.Sub, Operand(instr, 0), Operand(instr, 1)), new IrReg("CF", 1))));
                }

                ctx.SetFlagsFromResult(Operand(instr, 0));
                return;

            case Mnemonic.Inc:
                Emit(Assign(instr, 0, new IrBinary(IrBinaryOp.Add, Operand(instr, 0), new IrConst(1, OperandBits(instr, 0)))));
                ctx.SetFlagsFromResult(Operand(instr, 0));
                return;
            case Mnemonic.Dec:
                Emit(Assign(instr, 0, new IrBinary(IrBinaryOp.Sub, Operand(instr, 0), new IrConst(1, OperandBits(instr, 0)))));
                ctx.SetFlagsFromResult(Operand(instr, 0));
                return;
            case Mnemonic.Neg:
                Emit(Assign(instr, 0, new IrUnary(IrUnaryOp.Neg, Operand(instr, 0))));
                ctx.SetFlagsFromResult(Operand(instr, 0));
                return;
            case Mnemonic.Not:
                Emit(Assign(instr, 0, new IrUnary(IrUnaryOp.Not, Operand(instr, 0))));
                return;

            case Mnemonic.Shl:
            case Mnemonic.Sal:
                LiftShift(instr, IrBinaryOp.Shl, ctx, Emit);
                return;
            case Mnemonic.Shr:
                LiftShift(instr, IrBinaryOp.Shr, ctx, Emit);
                return;
            case Mnemonic.Sar:
                LiftShift(instr, IrBinaryOp.Sar, ctx, Emit);
                return;
            case Mnemonic.Rol:
                LiftShift(instr, IrBinaryOp.Rol, ctx, Emit);
                return;
            case Mnemonic.Ror:
                LiftShift(instr, IrBinaryOp.Ror, ctx, Emit);
                return;

            case Mnemonic.Imul:
                LiftImul(instr, ctx, Emit);
                return;
            case Mnemonic.Mul:
                {
                    int bits = OperandBits(instr, 0);
                    var (lo, hi) = AccumulatorPair(bits);
                    var product = new IrBinary(IrBinaryOp.Mul, lo, Operand(instr, 0));
                    Emit(new IrAssign(hi, new IrUnknown($"high {bits} bits of {product}", bits)));
                    Emit(new IrAssign(lo, product));
                    ctx.Flags = null;
                    return;
                }
            case Mnemonic.Div:
            case Mnemonic.Idiv:
                {
                    int bits = OperandBits(instr, 0);
                    bool signed = instr.Mnemonic == Mnemonic.Idiv;
                    var (lo, hi) = AccumulatorPair(bits);
                    var dividend = ctx.NewTemp(bits);
                    var divisor = Operand(instr, 0);
                    Emit(new IrAssign(dividend, lo));
                    Emit(new IrAssign(lo, new IrBinary(signed ? IrBinaryOp.SDiv : IrBinaryOp.UDiv, dividend, divisor)));
                    Emit(new IrAssign(hi, new IrBinary(signed ? IrBinaryOp.SRem : IrBinaryOp.URem, dividend, divisor)));
                    ctx.Flags = null;
                    return;
                }

            case Mnemonic.Cmp:
                ctx.Flags = FlagState.Compare(Operand(instr, 0), Operand(instr, 1));
                return;
            case Mnemonic.Test:
                ctx.Flags = FlagState.Test(Operand(instr, 0), Operand(instr, 1));
                return;

            case Mnemonic.Push:
                {
                    var sp = new IrReg(Sp, PtrBits);
                    var value = Operand(instr, 0);
                    Emit(new IrAssign(sp, new IrBinary(IrBinaryOp.Sub, sp, new IrConst(PtrBits / 8, PtrBits))));
                    Emit(new IrStore(sp, value, PtrBits));
                    return;
                }
            case Mnemonic.Pop:
                {
                    var sp = new IrReg(Sp, PtrBits);
                    Emit(Assign(instr, 0, new IrMem(sp, PtrBits)));
                    Emit(new IrAssign(sp, new IrBinary(IrBinaryOp.Add, sp, new IrConst(PtrBits / 8, PtrBits))));
                    return;
                }
            case Mnemonic.Leave:
                {
                    var sp = new IrReg(Sp, PtrBits);
                    var bp = new IrReg(Bp, PtrBits);
                    Emit(new IrAssign(sp, bp));
                    Emit(new IrAssign(bp, new IrMem(sp, PtrBits)));
                    Emit(new IrAssign(sp, new IrBinary(IrBinaryOp.Add, sp, new IrConst(PtrBits / 8, PtrBits))));
                    return;
                }

            case Mnemonic.Call:
                {
                    var target = CallTarget(ins);
                    var call = new IrCall(target, Array.Empty<IrExpr>(), PtrBits);
                    Emit(new IrCallStmt(call, new IrReg(Ax, PtrBits)));
                    ctx.Flags = null;
                    return;
                }
            case Mnemonic.Ret:
            case Mnemonic.Retf:
                Emit(new IrReturn(new IrReg(Ax, PtrBits)));
                return;

            case Mnemonic.Jmp:
                if (ins.BranchTargetVa is { } jt)
                {
                    Emit(new IrGoto(jt));
                    return;
                }

                if (_jumpTables.TryGetValue(va, out var table) && table.Targets.Count > 0)
                {
                    // A recovered switch: the index selects the target, so the dispatch becomes the
                    // statement rather than the arithmetic that computed the address.
                    IrExpr index = table.IndexRegister is { } register
                        ? new IrReg(register, table.IndexBits == 0 ? PtrBits : table.IndexBits)
                        : new IrUnknown("switch index", PtrBits);
                    Emit(new IrSwitch(index, table.Targets));
                    foreach (ulong caseTarget in table.Targets)
                    {
                        fn.LabelTargets.Add(caseTarget);
                    }

                    return;
                }

                {
                    var target = CallTarget(ins);
                    if (target is IrSymbol)
                    {
                        // jmp [iat] — tail call through the import table.
                        Emit(new IrReturn(new IrCall(target, Array.Empty<IrExpr>(), PtrBits)));
                        return;
                    }

                    Emit(new IrComment("indirect jump (switch table?)"));
                    Emit(new IrAsm(ins.Text));
                    fn.Warnings.Add($"0x{va:X}: indirect jump not resolved.");
                    return;
                }

            case Mnemonic.Jecxz:
            case Mnemonic.Jrcxz:
            case Mnemonic.Jcxz:
                if (ins.BranchTargetVa is { } zt)
                {
                    int bits = instr.Mnemonic == Mnemonic.Jrcxz ? 64 : instr.Mnemonic == Mnemonic.Jecxz ? 32 : 16;
                    string reg = bits == 64 ? "rcx" : bits == 32 ? "ecx" : "cx";
                    Emit(new IrBranch(new IrCondition(IrCondCode.Equal, new IrReg(reg, bits), new IrConst(0, bits)), zt, ins.NextVa));
                    return;
                }

                break;

            case Mnemonic.Xchg:
                {
                    var t = ctx.NewTemp(OperandBits(instr, 0));
                    Emit(new IrAssign(t, Operand(instr, 0)));
                    Emit(Assign(instr, 0, Operand(instr, 1)));
                    Emit(Assign(instr, 1, t));
                    return;
                }

            case Mnemonic.Cdq:
                Emit(new IrAssign(new IrReg("edx", 32), new IrBinary(IrBinaryOp.Sar, new IrCast(new IrReg("eax", 32), 32, true), new IrConst(31, 32))));
                return;
            case Mnemonic.Cqo:
                Emit(new IrAssign(new IrReg("rdx", 64), new IrBinary(IrBinaryOp.Sar, new IrCast(new IrReg("rax", 64), 64, true), new IrConst(63, 64))));
                return;
            case Mnemonic.Cwd:
                Emit(new IrAssign(new IrReg("dx", 16), new IrBinary(IrBinaryOp.Sar, new IrCast(new IrReg("ax", 16), 16, true), new IrConst(15, 16))));
                return;
            case Mnemonic.Cdqe:
                Emit(new IrAssign(new IrReg("rax", 64), new IrCast(new IrReg("eax", 32), 64, true)));
                return;
            case Mnemonic.Cwde:
                Emit(new IrAssign(new IrReg("eax", 32), new IrCast(new IrReg("ax", 16), 32, true)));
                return;
            case Mnemonic.Cbw:
                Emit(new IrAssign(new IrReg("ax", 16), new IrCast(new IrReg("al", 8), 16, true)));
                return;

            case Mnemonic.Int3:
                Emit(new IrCallStmt(new IrCall(new IrSymbol("__debugbreak", 0, 0), Array.Empty<IrExpr>(), 0), null));
                return;
            case Mnemonic.Hlt:
                Emit(new IrCallStmt(new IrCall(new IrSymbol("__halt", 0, 0), Array.Empty<IrExpr>(), 0), null));
                return;
            case Mnemonic.Ud2:
                Emit(new IrCallStmt(new IrCall(new IrSymbol("__ud2", 0, 0), Array.Empty<IrExpr>(), 0), null));
                return;
        }

        // Fallback: keep the instruction verbatim.
        Emit(new IrAsm(ins.Text));
        fn.Warnings.Add($"0x{va:X}: unsupported instruction '{ins.Mnemonic}' kept as inline asm.");
        ctx.Flags = null;
    }

    private void LiftBinary(in Instruction instr, IrBinaryOp op, LiftContext ctx, Action<IrStmt> emit)
    {
        var dst = Operand(instr, 0);
        var src = Operand(instr, 1);
        emit(Assign(instr, 0, new IrBinary(op, dst, src)));
        // Flags reflect the result compared with zero (good enough for jz/jnz/js/jl after arithmetic).
        ctx.SetFlagsFromResult(Operand(instr, 0));
    }

    private void LiftShift(in Instruction instr, IrBinaryOp op, LiftContext ctx, Action<IrStmt> emit)
    {
        var dst = Operand(instr, 0);
        IrExpr count = instr.OpCount >= 2 ? Operand(instr, 1) : new IrConst(1, 8);
        emit(Assign(instr, 0, new IrBinary(op, op == IrBinaryOp.Sar ? new IrCast(dst, dst.Bits, true) : dst, count)));
        ctx.SetFlagsFromResult(Operand(instr, 0));
    }

    private void LiftImul(in Instruction instr, LiftContext ctx, Action<IrStmt> emit)
    {
        switch (instr.OpCount)
        {
            case 1:
                {
                    int bits = OperandBits(instr, 0);
                    var (lo, hi) = AccumulatorPair(bits);
                    var product = new IrBinary(IrBinaryOp.SMul, lo, Operand(instr, 0));
                    emit(new IrAssign(hi, new IrUnknown($"high {bits} bits of {product}", bits)));
                    emit(new IrAssign(lo, product));
                    break;
                }
            case 2:
                emit(Assign(instr, 0, new IrBinary(IrBinaryOp.SMul, Operand(instr, 0), Operand(instr, 1))));
                break;
            default:
                emit(Assign(instr, 0, new IrBinary(IrBinaryOp.SMul, Operand(instr, 1), Operand(instr, 2))));
                break;
        }

        ctx.Flags = null;
    }

    private (IrReg Lo, IrReg Hi) AccumulatorPair(int bits) => bits switch
    {
        64 => (new IrReg("rax", 64), new IrReg("rdx", 64)),
        32 => (new IrReg("eax", 32), new IrReg("edx", 32)),
        16 => (new IrReg("ax", 16), new IrReg("dx", 16)),
        _ => (new IrReg("al", 8), new IrReg("ah", 8)),
    };

    // ------------------------------------------------------------------
    // Operands
    // ------------------------------------------------------------------

    private static bool SameRegister(in Instruction instr)
        => instr.OpCount == 2 && instr.Op0Kind == OpKind.Register && instr.Op1Kind == OpKind.Register && instr.Op0Register == instr.Op1Register;

    private IrStmt Assign(in Instruction instr, int operand, IrExpr value)
    {
        var kind = instr.GetOpKind(operand);
        if (kind == OpKind.Memory)
        {
            return new IrStore(MemoryAddress(instr), value, OperandBits(instr, operand));
        }

        return new IrAssign(Operand(instr, operand), value);
    }

    private IrExpr Operand(in Instruction instr, int operand)
    {
        var kind = instr.GetOpKind(operand);
        switch (kind)
        {
            case OpKind.Register:
                {
                    var reg = instr.GetOpRegister(operand);
                    return new IrReg(RegName(reg), reg.GetSize() * 8);
                }
            case OpKind.Memory:
                return new IrMem(MemoryAddress(instr), OperandBits(instr, operand));
            case OpKind.Immediate8:
                return new IrConst((sbyte)instr.Immediate8, 8);
            case OpKind.Immediate8_2nd:
                return new IrConst((sbyte)instr.Immediate8_2nd, 8);
            case OpKind.Immediate16:
                return new IrConst((short)instr.Immediate16, 16);
            case OpKind.Immediate32:
                return new IrConst((int)instr.Immediate32, 32);
            case OpKind.Immediate64:
                return new IrConst((long)instr.Immediate64, 64);
            case OpKind.Immediate8to16:
                return new IrConst(instr.Immediate8to16, 16);
            case OpKind.Immediate8to32:
                return new IrConst(instr.Immediate8to32, 32);
            case OpKind.Immediate8to64:
                return new IrConst(instr.Immediate8to64, 64);
            case OpKind.Immediate32to64:
                return new IrConst(instr.Immediate32to64, 64);
            case OpKind.NearBranch16:
            case OpKind.NearBranch32:
            case OpKind.NearBranch64:
                return SymbolOrConst(instr.NearBranchTarget, PtrBits);
            case OpKind.MemorySegSI:
            case OpKind.MemorySegESI:
            case OpKind.MemorySegRSI:
                return new IrMem(new IrReg(kind == OpKind.MemorySegRSI ? "rsi" : kind == OpKind.MemorySegESI ? "esi" : "si", kind == OpKind.MemorySegRSI ? 64 : kind == OpKind.MemorySegESI ? 32 : 16), OperandBits(instr, operand));
            case OpKind.MemoryESDI:
            case OpKind.MemoryESEDI:
            case OpKind.MemoryESRDI:
                return new IrMem(new IrReg(kind == OpKind.MemoryESRDI ? "rdi" : kind == OpKind.MemoryESEDI ? "edi" : "di", kind == OpKind.MemoryESRDI ? 64 : kind == OpKind.MemoryESEDI ? 32 : 16), OperandBits(instr, operand));
            default:
                return new IrUnknown(kind.ToString(), 0);
        }
    }

    private int OperandBits(in Instruction instr, int operand)
    {
        var kind = instr.GetOpKind(operand);
        return kind switch
        {
            OpKind.Register => instr.GetOpRegister(operand).GetSize() * 8,
            OpKind.Memory => instr.MemorySize.GetSize() * 8 is var b and > 0 ? b : PtrBits,
            OpKind.Immediate8 or OpKind.Immediate8_2nd => 8,
            OpKind.Immediate16 or OpKind.Immediate8to16 => 16,
            OpKind.Immediate32 or OpKind.Immediate8to32 => 32,
            OpKind.Immediate64 or OpKind.Immediate8to64 or OpKind.Immediate32to64 => 64,
            _ => PtrBits,
        };
    }

    /// <summary>Effective address of the memory operand as an IR expression.</summary>
    private IrExpr MemoryAddress(in Instruction instr)
    {
        if (instr.IsIPRelativeMemoryOperand)
        {
            return SymbolOrConst(instr.IPRelativeMemoryAddress, PtrBits);
        }

        IrExpr? expr = null;
        if (instr.MemoryBase != Register.None)
        {
            expr = new IrReg(RegName(instr.MemoryBase), instr.MemoryBase.GetSize() * 8);
        }

        if (instr.MemoryIndex != Register.None)
        {
            IrExpr index = new IrReg(RegName(instr.MemoryIndex), instr.MemoryIndex.GetSize() * 8);
            if (instr.MemoryIndexScale > 1)
            {
                index = new IrBinary(IrBinaryOp.Mul, index, new IrConst(instr.MemoryIndexScale, PtrBits));
            }

            expr = expr is null ? index : new IrBinary(IrBinaryOp.Add, expr, index);
        }

        // Displacements are sign-extended from the address size: in 32-bit code (or with a 32-bit base register)
        // Iced reports e.g. -0x19 as 0xFFFFFFE7, which must not be treated as a large positive offset.
        bool addr64 = _bitness == 64 && (instr.MemoryBase == Register.None || instr.MemoryBase.GetSize() == 8)
                                      && (instr.MemoryIndex == Register.None || instr.MemoryIndex.GetSize() == 8);
        long disp = addr64 ? (long)instr.MemoryDisplacement64 : (int)instr.MemoryDisplacement32;

        bool segmented = instr.SegmentPrefix is Register.FS or Register.GS;
        if (expr is null && !segmented)
        {
            return SymbolOrConst((ulong)disp, PtrBits);
        }

        if (expr is null)
        {
            expr = new IrConst(disp, PtrBits);
        }
        else if (disp != 0)
        {
            expr = disp < 0
                ? new IrBinary(IrBinaryOp.Sub, expr, new IrConst(-disp, PtrBits))
                : new IrBinary(IrBinaryOp.Add, expr, new IrConst(disp, PtrBits));
        }

        // fs:/gs: segment-relative access (TEB / TLS / SEH chain on Windows).
        if (segmented)
        {
            expr = new IrBinary(IrBinaryOp.Add, new IrReg(instr.SegmentPrefix == Register.FS ? "fs_base" : "gs_base", PtrBits), expr);
        }

        return expr;
    }

    private IrExpr SymbolOrConst(ulong va, int bits)
    {
        if (_symbols is not null && _symbols.TryGet(va, out var sym) && sym.Kind != SymbolKind.Section)
        {
            return new IrSymbol(sym.Name, va, bits);
        }

        return new IrConst((long)va, bits);
    }

    private IrExpr CallTarget(DecodedInstruction ins)
    {
        var instr = ins.Native;
        if (ins.BranchTargetVa is { } direct)
        {
            return _symbols is not null && _symbols.TryGet(direct, out var s) && s.Kind != SymbolKind.Section
                ? new IrSymbol(s.Name, direct, PtrBits)
                : new IrSymbol($"sub_{direct:X}", direct, PtrBits);
        }

        if (ins.IndirectSlotVa is { } slot && _symbols is not null && _symbols.TryGet(slot, out var imp) && imp.Kind == SymbolKind.Import)
        {
            return new IrSymbol(imp.Name, slot, PtrBits);
        }

        return Operand(instr, 0);
    }

    private static string RegName(Register reg) => reg.ToString().ToLowerInvariant();

    private static IrCondCode MapCc(ConditionCode cc) => cc switch
    {
        ConditionCode.o => IrCondCode.Overflow,
        ConditionCode.no => IrCondCode.NotOverflow,
        ConditionCode.b => IrCondCode.Below,
        ConditionCode.ae => IrCondCode.AboveOrEqual,
        ConditionCode.e => IrCondCode.Equal,
        ConditionCode.ne => IrCondCode.NotEqual,
        ConditionCode.be => IrCondCode.BelowOrEqual,
        ConditionCode.a => IrCondCode.Above,
        ConditionCode.s => IrCondCode.Sign,
        ConditionCode.ns => IrCondCode.NotSign,
        ConditionCode.p => IrCondCode.Parity,
        ConditionCode.np => IrCondCode.NotParity,
        ConditionCode.l => IrCondCode.Less,
        ConditionCode.ge => IrCondCode.GreaterOrEqual,
        ConditionCode.le => IrCondCode.LessOrEqual,
        ConditionCode.g => IrCondCode.Greater,
        _ => IrCondCode.Equal,
    };

    // ------------------------------------------------------------------
    // Context
    // ------------------------------------------------------------------

    /// <summary>What the last flag-setting instruction compared.</summary>
    private sealed record FlagState(IrExpr Left, IrExpr Right, bool IsTest)
    {
        public static FlagState Compare(IrExpr left, IrExpr right) => new(left, right, false);

        public static FlagState Test(IrExpr left, IrExpr right) => new(left, right, true);

        public IrExpr ToCondition(IrCondCode cc)
        {
            if (!IsTest)
            {
                return new IrCondition(cc, Left, Right);
            }

            IrExpr subject = Left.Equals(Right) ? Left : new IrBinary(IrBinaryOp.And, Left, Right);
            return new IrCondition(cc, subject, new IrConst(0, subject.Bits));
        }
    }

    private sealed class LiftContext
    {
        private int _nextTemp;

        public FlagState? Flags { get; set; }

        public IrTemp NewTemp(int bits) => new(_nextTemp++, bits);

        public void SetFlagsFromResult(IrExpr result) => Flags = FlagState.Compare(result, new IrConst(0, result.Bits));

        public IrExpr BuildCondition(IrCondCode cc)
            => Flags?.ToCondition(cc) ?? new IrUnknown($"{cc} flags", 1);
    }
}
