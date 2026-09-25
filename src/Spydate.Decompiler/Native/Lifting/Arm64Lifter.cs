using System.Text.RegularExpressions;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Passes;
using Spydate.Disassembly;
using Spydate.Disassembly.Arm64;

namespace Spydate.Decompiler.Native.Lifting;

/// <summary>
/// Lifts ARM64 <see cref="DecodedInstruction"/>s to the same IR the x86 lifter produces, so the passes, the
/// structurer and the emitter are shared. Registers keep their own names (<c>x0</c>, <c>w8</c>, <c>sp</c>, <c>d0</c>),
/// the zero register reads as 0 and swallows writes, and flags are what the last flag-setting instruction
/// compared, folded into the condition that reads them — as on x86, with <c>ccmp</c> chains becoming
/// <c>&amp;</c>/<c>|</c> of their comparisons.
///
/// An address ARM64 builds in two instructions is taken from the decoder, which already worked it out
/// (<see cref="DecodedInstruction.DataVa"/>): <c>adrp x8, page; ldr x9, [x8, #off]</c> reads the global at once, so
/// naming it does not wait on copy propagation. Vector instructions and system operations are kept verbatim.
/// The lifter never throws for unexpected input.
/// </summary>
public sealed partial class Arm64Lifter : INativeLifter
{
    private readonly SymbolTable? _symbols;
    private IReadOnlyDictionary<ulong, JumpTable> _jumpTables = new Dictionary<ulong, JumpTable>();
    private IReadOnlyDictionary<ulong, BasicBlock> _blocks = new Dictionary<ulong, BasicBlock>();

    public Arm64Lifter(SymbolTable? symbols = null) => _symbols = symbols;

    public IrFunction Lift(Function function)
    {
        ArgumentNullException.ThrowIfNull(function);
        _jumpTables = function.JumpTables.GroupBy(t => t.JumpVa).ToDictionary(g => g.Key, g => g.First());
        _blocks = function.BlockByVa;

        // Flags reach a block down every edge into it, not only a fall-through: b.ne to a cset eq is the same
        // compare. A first lift learns what each block leaves in the flags; the second starts each block with
        // what its predecessors agree on.
        var (_, exits) = LiftBlocks(function, null);
        var entries = new Dictionary<ulong, Flags?>();
        foreach (var block in function.Blocks)
        {
            var incoming = block.Predecessors.Select(p => exits.GetValueOrDefault(p)).Distinct().ToList();
            if (block.Predecessors.Count > 0 && incoming is [{ } agreed])
            {
                entries[block.StartVa] = agreed;
            }
        }

        var (ir, _) = LiftBlocks(function, entries);
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

    private (IrFunction Ir, Dictionary<ulong, Flags?> Exits) LiftBlocks(Function function, Dictionary<ulong, Flags?>? entries)
    {
        var ir = new IrFunction(function.EntryVa, function.Name, 64) { Convention = CallingConvention.Aapcs64 };
        var ctx = new Context();
        var exits = new Dictionary<ulong, Flags?>();

        Flags? carried = null;
        BasicBlock? previous = null;
        foreach (var block in function.Blocks)
        {
            var irBlock = new IrBlock(block.StartVa);
            irBlock.Successors.AddRange(block.Successors);
            irBlock.Predecessors.AddRange(block.Predecessors);

            // A compare at the end of one block and the branch at the start of the next, with nothing between;
            // or, on the second lift, whatever every edge into the block brings.
            ctx.Flags = entries is not null && entries.TryGetValue(block.StartVa, out var agreed) ? agreed
                : previous is not null && previous.EndVa == block.StartVa && !previous.Last.EndsBlock ? carried : null;

            foreach (var ins in block.Instructions)
            {
                try
                {
                    LiftInstruction(ins, ctx, irBlock, ir);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
                {
                    irBlock.Statements.Add(new IrAsm(ins.Text) { Va = ins.Va });
                    ir.Warnings.Add($"0x{ins.Va:X}: '{ins.Text}' kept as inline asm: {ex.Message}");
                    ctx.Flags = null;
                }
            }

            carried = ctx.Flags;
            exits[block.StartVa] = ctx.Flags;
            previous = block;
            ir.Blocks.Add(irBlock);
        }

        return (ir, exits);
    }

    // ------------------------------------------------------------------
    // Per-instruction lifting
    // ------------------------------------------------------------------

    private void LiftInstruction(DecodedInstruction ins, Context ctx, IrBlock block, IrFunction fn)
    {
        ulong va = ins.Va;
        void Emit(IrStmt s) => block.Statements.Add(s with { Va = va });

        if (ins.Arm64 is not { } a)
        {
            Emit(new IrAsm(ins.Text));
            fn.Warnings.Add($"0x{va:X}: invalid instruction bytes.");
            ctx.Flags = null;
            return;
        }

        var ops = a.Operands;
        string m = a.Mnemonic;

        // Conditional branches first: they read the flags.
        if (m.StartsWith("b.", StringComparison.Ordinal) || m.StartsWith("bc.", StringComparison.Ordinal))
        {
            var cond = ctx.Condition(m[(m.IndexOf('.', StringComparison.Ordinal) + 1)..]);
            Branch(ins, cond, Emit);
            return;
        }

        switch (m)
        {
            // --- nothing to say ---------------------------------------------------------------------
            case "nop" or "yield" or "bti" or "bti c" or "bti j" or "bti jc" or "csdb" or "esb" or "sevl" or "sev" or "wfe" or "wfi"
                or "paciasp" or "pacibsp" or "autiasp" or "autibsp" or "paciaz" or "pacibz" or "autiaz" or "autibz"
                or "xpaclri" or "pacia1716" or "pacib1716" or "autia1716" or "autib1716" or "prfm" or "prfum" or "hint"
                or "ssbb" or "pssbb" or "sb" or "clrex" or "dgh" or "psb csync" or "tsb csync" or "clrbhb":
                return;

            case "dmb" or "dsb" or "isb":
                Emit(new IrCallStmt(new IrCall(new IrSymbol("__" + m, 0, 0), Array.Empty<IrExpr>(), 0), null));
                return;

            // --- moves -------------------------------------------------------------------------------
            case "mov" when ops is [Arm64RegisterOperand d, var src]:
                Set(d, Value(src, Bits(d)), Emit);
                return;

            case "movz" or "movn" when ops is [Arm64RegisterOperand d, var src]:
            {
                long value = src switch
                {
                    Arm64ShiftedImmediate s => s.Value << s.Shift,
                    Arm64Immediate i => i.Value,
                    _ => throw new NotSupportedException("move-wide operand"),
                };
                Set(d, new IrConst(m == "movn" ? ~value : value, Bits(d)), Emit);
                return;
            }

            case "movk" when ops is [Arm64RegisterOperand d, var src]:
            {
                (long value, int shift) = src switch
                {
                    Arm64ShiftedImmediate s => (s.Value, s.Shift),
                    Arm64Immediate i => (i.Value, 0),
                    _ => throw new NotSupportedException("movk operand"),
                };
                int bits = Bits(d);
                var kept = new IrBinary(IrBinaryOp.And, Read(d), new IrConst(~(0xFFFFL << shift), bits));
                Set(d, new IrBinary(IrBinaryOp.Or, kept, new IrConst(value << shift, bits)), Emit);
                return;
            }

            // movi of zero is how compilers clear a vector register before storing it: that much is a value.
            case "movi" when ops is [Arm64Text vector, Arm64Immediate { Value: 0 }] && VectorNumber(vector.Text) is { } zeroed:
                Emit(new IrAssign(new IrReg("q" + zeroed, 128), new IrConst(0, 128)));
                return;
            case "movi" when ops is [Arm64RegisterOperand scalar, Arm64Immediate bytes]:
                Set(scalar, new IrConst(bytes.Value, Bits(scalar)), Emit);
                return;

            case "adr" or "adrp" when ops is [Arm64RegisterOperand d, Arm64Address address]:
                Set(d, m == "adrp" ? new IrConst((long)address.Address, 64) : SymbolOrConst(address.Address, 64), Emit);
                return;

            // --- arithmetic and logic ------------------------------------------------------------------
            case "add" or "sub" or "and" or "orr" or "eor" or "bic" or "orn" or "eon" or "mul" or "sdiv" or "udiv"
                or "lsl" or "lsr" or "asr" or "ror" or "smax" or "smin" or "umax" or "umin"
                when ops is [Arm64RegisterOperand d, var left, var right]:
                Arithmetic(m, d, left, right, ins, Emit);
                return;

            case "adds" or "subs" or "ands" or "bics" when ops is [Arm64RegisterOperand d, var left, var right]:
                FlagSetting(m, d, left, right, ctx, Emit);
                return;

            case "cmp" when ops is [var left, var right]:
            {
                int bits = OperandBits(left);
                ctx.Flags = Flags.Compare(Value(left, bits), Value(right, bits));
                return;
            }

            case "cmn" when ops is [var left, var right]:
            {
                int bits = OperandBits(left);
                ctx.Flags = Flags.Compare(Value(left, bits), new IrUnary(IrUnaryOp.Neg, Value(right, bits)));
                return;
            }

            case "tst" when ops is [var left, var right]:
            {
                int bits = OperandBits(left);
                ctx.Flags = Flags.Test(Value(left, bits), Value(right, bits));
                return;
            }

            case "neg" or "negs" when ops is [Arm64RegisterOperand d, var src]:
            {
                int bits = Bits(d);
                var value = Value(src, bits);
                if (m == "negs")
                {
                    ctx.Flags = Flags.Compare(new IrConst(0, bits), Snapshot(value, d, ctx, Emit));
                }

                Set(d, new IrUnary(IrUnaryOp.Neg, value), Emit);
                return;
            }

            case "mvn" when ops is [Arm64RegisterOperand d, var src]:
                Set(d, new IrUnary(IrUnaryOp.Not, Value(src, Bits(d))), Emit);
                return;

            case "mneg" when ops is [Arm64RegisterOperand d, var l, var r]:
                Set(d, new IrUnary(IrUnaryOp.Neg, new IrBinary(IrBinaryOp.Mul, Value(l, Bits(d)), Value(r, Bits(d)))), Emit);
                return;

            case "madd" or "msub" when ops is [Arm64RegisterOperand d, var l, var r, var acc]:
            {
                int bits = Bits(d);
                var product = new IrBinary(IrBinaryOp.Mul, Value(l, bits), Value(r, bits));
                Set(d, new IrBinary(m == "madd" ? IrBinaryOp.Add : IrBinaryOp.Sub, Value(acc, bits), product), Emit);
                return;
            }

            case "smull" or "umull" or "smnegl" or "umnegl" when ops is [Arm64RegisterOperand d, var l, var r]:
            {
                bool signed = m[0] == 's';
                IrExpr product = new IrBinary(signed ? IrBinaryOp.SMul : IrBinaryOp.Mul, Widen(l, signed), Widen(r, signed));
                Set(d, m.Contains("neg", StringComparison.Ordinal) ? new IrUnary(IrUnaryOp.Neg, product) : product, Emit);
                return;
            }

            case "smaddl" or "umaddl" or "smsubl" or "umsubl" when ops is [Arm64RegisterOperand d, var l, var r, var acc]:
            {
                bool signed = m[0] == 's';
                var product = new IrBinary(signed ? IrBinaryOp.SMul : IrBinaryOp.Mul, Widen(l, signed), Widen(r, signed));
                Set(d, new IrBinary(m.Contains("add", StringComparison.Ordinal) ? IrBinaryOp.Add : IrBinaryOp.Sub, Value(acc, 64), product), Emit);
                return;
            }

            case "smulh" or "umulh" when ops is [Arm64RegisterOperand d, var l, var r]:
                Set(d, new IrCall(new IrSymbol(m == "smulh" ? "__mulh" : "__umulh", 0, 0), [Value(l, 64), Value(r, 64)], 64), Emit);
                return;

            case "adc" or "adcs" or "sbc" or "sbcs" when ops is [Arm64RegisterOperand d, var l, var r]:
            {
                int bits = Bits(d);
                var carry = new IrCast(new IrReg("C", 1), bits, false);
                IrExpr result = m.StartsWith("adc", StringComparison.Ordinal)
                    ? new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Add, Value(l, bits), Value(r, bits)), carry)
                    : new IrBinary(IrBinaryOp.Sub, new IrBinary(IrBinaryOp.Sub, Value(l, bits), Value(r, bits)), new IrBinary(IrBinaryOp.Xor, carry, new IrConst(1, bits)));
                Set(d, result, Emit);
                ctx.Flags = m.EndsWith('s') ? Flags.Compare(Read(d), new IrConst(0, bits)) : ctx.Flags;
                return;
            }

            case "ngc" or "ngcs" when ops is [Arm64RegisterOperand d, var r]:
            {
                int bits = Bits(d);
                var borrow = new IrBinary(IrBinaryOp.Xor, new IrCast(new IrReg("C", 1), bits, false), new IrConst(1, bits));
                Set(d, new IrBinary(IrBinaryOp.Sub, new IrUnary(IrUnaryOp.Neg, Value(r, bits)), borrow), Emit);
                return;
            }

            case "ubfx" or "sbfx" or "ubfiz" or "sbfiz" or "bfi" or "bfxil" when ops is [Arm64RegisterOperand d, var src, Arm64Immediate lsb, Arm64Immediate width]:
                Bitfield(m, d, src, (int)lsb.Value, (int)width.Value, Emit);
                return;

            case "bfc" when ops is [Arm64RegisterOperand d, Arm64Immediate lsb, Arm64Immediate width]:
            {
                int bits = Bits(d);
                Set(d, new IrBinary(IrBinaryOp.And, Read(d), new IrConst(~(Mask((int)width.Value) << (int)lsb.Value), bits)), Emit);
                return;
            }

            case "sxtb" or "sxth" or "sxtw" or "uxtb" or "uxth" when ops is [Arm64RegisterOperand d, var src]:
            {
                int from = m[3] switch { 'b' => 8, 'h' => 16, _ => 32 };
                Set(d, new IrCast(new IrCast(Value(src, 32), from, m[0] == 's'), Bits(d), m[0] == 's'), Emit);
                return;
            }

            case "extr" when ops is [Arm64RegisterOperand d, var hi, var lo, Arm64Immediate lsb]:
            {
                int bits = Bits(d);
                int shift = (int)lsb.Value;
                Set(d, new IrBinary(IrBinaryOp.Or,
                    new IrBinary(IrBinaryOp.Shl, Value(hi, bits), new IrConst(bits - shift, 8)),
                    new IrBinary(IrBinaryOp.Shr, Value(lo, bits), new IrConst(shift, 8))), Emit);
                return;
            }

            case "clz" or "cls" or "rbit" or "rev" or "rev16" or "rev32" or "cnt" or "ctz" or "abs" when ops is [Arm64RegisterOperand d, var src]:
            {
                int bits = Bits(d);
                string name = (m, bits) switch
                {
                    ("clz", 64) => "__builtin_clzll",
                    ("clz", _) => "__builtin_clz",
                    ("rev", 64) => "__builtin_bswap64",
                    ("rev", _) => "__builtin_bswap32",
                    ("cnt", 64) => "__builtin_popcountll",
                    ("cnt", _) => "__builtin_popcount",
                    ("ctz", 64) => "__builtin_ctzll",
                    ("ctz", _) => "__builtin_ctz",
                    ("abs", 64) => "llabs",
                    ("abs", _) => "abs",
                    _ => "__" + m,
                };
                Set(d, new IrCall(new IrSymbol(name, 0, 0), [Value(src, bits)], bits), Emit);
                return;
            }

            case "csel" or "csinc" or "csinv" or "csneg" when ops is [Arm64RegisterOperand d, var t, var f, Arm64Text cc]:
            {
                int bits = Bits(d);
                IrExpr otherwise = Value(f, bits);
                otherwise = m switch
                {
                    "csinc" => new IrBinary(IrBinaryOp.Add, otherwise, new IrConst(1, bits)),
                    "csinv" => new IrUnary(IrUnaryOp.Not, otherwise),
                    "csneg" => new IrUnary(IrUnaryOp.Neg, otherwise),
                    _ => otherwise,
                };
                Set(d, new IrTernary(ctx.Condition(cc.Text), Value(t, bits), otherwise), Emit);
                return;
            }

            case "cset" or "csetm" when ops is [Arm64RegisterOperand d, Arm64Text cc]:
            {
                int bits = Bits(d);
                IrExpr flag = new IrCast(ctx.Condition(cc.Text), bits, false);
                Set(d, m == "csetm" ? new IrUnary(IrUnaryOp.Neg, flag) : flag, Emit);
                return;
            }

            case "cinc" or "cinv" or "cneg" when ops is [Arm64RegisterOperand d, var src, Arm64Text cc]:
            {
                int bits = Bits(d);
                var value = Value(src, bits);
                IrExpr changed = m switch
                {
                    "cinc" => new IrBinary(IrBinaryOp.Add, value, new IrConst(1, bits)),
                    "cinv" => new IrUnary(IrUnaryOp.Not, value),
                    _ => new IrUnary(IrUnaryOp.Neg, value),
                };
                Set(d, new IrTernary(ctx.Condition(cc.Text), changed, value), Emit);
                return;
            }

            case "ccmp" or "ccmn" when ops is [var left, var right, Arm64Immediate nzcv, Arm64Text cc]:
            {
                int bits = OperandBits(left);
                var r = Value(right, bits);
                var compare = Flags.Compare(Value(left, bits), m == "ccmn" ? new IrUnary(IrUnaryOp.Neg, r) : r);
                ctx.Flags = Flags.Chain(ctx.Flags, cc.Text, compare, (int)nzcv.Value);
                return;
            }

            case "crc32b" or "crc32h" or "crc32w" or "crc32x" or "crc32cb" or "crc32ch" or "crc32cw" or "crc32cx"
                when ops is [Arm64RegisterOperand d, var l, var r]:
                Set(d, new IrCall(new IrSymbol("__" + m, 0, 0), [Value(l, 32), Value(r, OperandBits(r))], 32), Emit);
                return;

            // --- branches and calls ------------------------------------------------------------------
            case "cbz" or "cbnz" when ops is [var reg, Arm64Address]:
            {
                int bits = OperandBits(reg);
                var cond = new IrCondition(m == "cbz" ? IrCondCode.Equal : IrCondCode.NotEqual, Value(reg, bits), new IrConst(0, bits));
                Branch(ins, cond, Emit);
                return;
            }

            case "tbz" or "tbnz" when ops is [var reg, Arm64Immediate bit, Arm64Address]:
            {
                int bits = OperandBits(reg);
                var tested = new IrBinary(IrBinaryOp.And, Value(reg, bits), new IrConst(1L << (int)bit.Value, bits));
                Branch(ins, new IrCondition(m == "tbz" ? IrCondCode.Equal : IrCondCode.NotEqual, tested, new IrConst(0, bits)), Emit);
                return;
            }

            case "b":
                if (ins.BranchTargetVa is { } target)
                {
                    // A branch that leaves the function is a tail call.
                    Emit(_blocks.ContainsKey(target) ? new IrGoto(target) : new IrReturn(new IrCall(CallTarget(ins), Array.Empty<IrExpr>(), 64)));
                    return;
                }

                break;

            case "bl" or "blr" or "blraa" or "blrab" or "blraaz" or "blrabz":
                Emit(new IrCallStmt(new IrCall(CallTarget(ins), Array.Empty<IrExpr>(), 64), new IrReg("x0", 64)));
                ctx.Flags = null;
                return;

            case "br" or "braa" or "brab" or "braaz" or "brabz":
                IndirectBranch(ins, fn, Emit);
                return;

            case "ret" or "retaa" or "retab":
                Emit(new IrReturn(new IrReg("x0", 64)));
                return;

            case "brk":
                Emit(new IrCallStmt(new IrCall(new IrSymbol("__debugbreak", 0, 0), Array.Empty<IrExpr>(), 0), null));
                return;
            case "udf":
                Emit(new IrCallStmt(new IrCall(new IrSymbol("__ud", 0, 0), Array.Empty<IrExpr>(), 0), null));
                return;
            case "hlt":
                Emit(new IrCallStmt(new IrCall(new IrSymbol("__halt", 0, 0), Array.Empty<IrExpr>(), 0), null));
                return;

            case "svc" when ops is [Arm64Immediate number]:
            {
                // Linux takes the call number in x8 and arguments in x0-x5; Windows takes the number in the
                // instruction. Both are passed, and the result comes back in x0.
                IrExpr[] args = [new IrConst(number.Value, 16), new IrReg("x8", 64), new IrReg("x0", 64), new IrReg("x1", 64), new IrReg("x2", 64), new IrReg("x3", 64), new IrReg("x4", 64), new IrReg("x5", 64)];
                Emit(new IrCallStmt(new IrCall(new IrSymbol("__svc", 0, 0), args, 64) { ConventionKnown = true }, new IrReg("x0", 64)));
                ctx.Flags = null;
                return;
            }

            // --- system registers --------------------------------------------------------------------
            case "mrs" when ops is [Arm64RegisterOperand d, Arm64Text register]:
                Set(d, new IrReg(register.Text, 64), Emit);
                return;
            case "msr" when ops is [Arm64Text register, Arm64RegisterOperand s]:
                Emit(new IrAssign(new IrReg(register.Text, 64), Read(s)));
                return;
        }

        if (FloatingPoint(m, ops, ins, ctx, Emit) || LoadStore(m, ops, ins, ctx, fn, Emit))
        {
            return;
        }

        // Kept verbatim: vector code, system operations, anything not modelled.
        Emit(new IrAsm(ins.Text));
        fn.Warnings.Add($"0x{va:X}: '{m}' kept as inline asm.");
        if (WritesFlags(m))
        {
            ctx.Flags = null;
        }
    }

    /// <summary>The register number of a vector operand written as text: 3 for <c>v3.2d</c>.</summary>
    private static int? VectorNumber(string text)
        => text.Length > 1 && text[0] == 'v' && text.IndexOf('.', StringComparison.Ordinal) is > 1 and var dot
           && int.TryParse(text.AsSpan(1, dot - 1), System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : null;

    private static bool WritesFlags(string mnemonic)
        => mnemonic.EndsWith('s') && mnemonic is not ("ins" or "bics" or "abs") || mnemonic.StartsWith("fcmp", StringComparison.Ordinal) || mnemonic.StartsWith("msr", StringComparison.Ordinal);

    private void Branch(DecodedInstruction ins, IrExpr condition, Action<IrStmt> emit)
    {
        if (ins.BranchTargetVa is { } target)
        {
            emit(new IrBranch(condition, target, ins.NextVa));
        }
    }

    private void IndirectBranch(DecodedInstruction ins, IrFunction fn, Action<IrStmt> emit)
    {
        if (_jumpTables.TryGetValue(ins.Va, out var table) && table.Targets.Count > 0)
        {
            IrExpr index = table.IndexRegister is { } register ? new IrReg(register, 64) : new IrUnknown("switch index", 64);
            emit(new IrSwitch(index, table.Targets));
            foreach (ulong caseTarget in table.Targets)
            {
                fn.LabelTargets.Add(caseTarget);
            }

            return;
        }

        // Through a slot the decoder traced (an import), or through a register: either way the function ends
        // in someone else's code, which is a tail call.
        emit(new IrReturn(new IrCall(CallTarget(ins), Array.Empty<IrExpr>(), 64)));
    }

    private IrExpr CallTarget(DecodedInstruction ins)
    {
        if (ins.BranchTargetVa is { } direct)
        {
            return _symbols is not null && _symbols.TryGet(direct, out var s) && s.Kind != SymbolKind.Section
                ? new IrSymbol(s.Name, direct, 64)
                : new IrSymbol($"sub_{direct:X}", direct, 64);
        }

        if (ins.IndirectSlotVa is { } slot && _symbols is not null && _symbols.TryGet(slot, out var imported) && imported.Kind == SymbolKind.Import)
        {
            return new IrSymbol(imported.Name, slot, 64);
        }

        return ins.Arm64 is { Operands: [Arm64RegisterOperand target, ..] } ? Read(target) : new IrUnknown("call target", 64);
    }

    // ------------------------------------------------------------------
    // Arithmetic helpers
    // ------------------------------------------------------------------

    private void Arithmetic(string m, Arm64RegisterOperand d, Arm64Operand left, Arm64Operand right, DecodedInstruction ins, Action<IrStmt> emit)
    {
        int bits = Bits(d);

        // An add the decoder resolved to an address (adrp x8, page; add x8, x8, #off) is that address.
        if (m == "add" && ins.DataVa is { } address && left is Arm64RegisterOperand { Register.IsStackPointer: false })
        {
            Set(d, SymbolOrConst(address, 64), emit);
            return;
        }

        var l = Value(left, bits);
        var r = Value(right, bits);
        IrExpr result = m switch
        {
            "add" => new IrBinary(IrBinaryOp.Add, l, r),
            "sub" => new IrBinary(IrBinaryOp.Sub, l, r),
            "and" => new IrBinary(IrBinaryOp.And, l, r),
            "orr" => new IrBinary(IrBinaryOp.Or, l, r),
            "eor" => new IrBinary(IrBinaryOp.Xor, l, r),
            "bic" => new IrBinary(IrBinaryOp.And, l, new IrUnary(IrUnaryOp.Not, r)),
            "orn" => new IrBinary(IrBinaryOp.Or, l, new IrUnary(IrUnaryOp.Not, r)),
            "eon" => new IrBinary(IrBinaryOp.Xor, l, new IrUnary(IrUnaryOp.Not, r)),
            "mul" => new IrBinary(IrBinaryOp.Mul, l, r),
            "sdiv" => new IrBinary(IrBinaryOp.SDiv, l, r),
            "udiv" => new IrBinary(IrBinaryOp.UDiv, l, r),
            "lsl" => new IrBinary(IrBinaryOp.Shl, l, r),
            "lsr" => new IrBinary(IrBinaryOp.Shr, l, r),
            "asr" => new IrBinary(IrBinaryOp.Sar, new IrCast(l, bits, true), r),
            "ror" => new IrBinary(IrBinaryOp.Ror, l, r),
            _ => new IrCall(new IrSymbol(m, 0, 0), [l, r], bits),   // smax, smin, umax, umin
        };
        Set(d, result, emit);
    }

    private void FlagSetting(string m, Arm64RegisterOperand d, Arm64Operand left, Arm64Operand right, Context ctx, Action<IrStmt> emit)
    {
        int bits = Bits(d);
        var l = Snapshot(Value(left, bits), d, ctx, emit);
        var r = Snapshot(Value(right, bits), d, ctx, emit);
        IrExpr result = m switch
        {
            "adds" => new IrBinary(IrBinaryOp.Add, l, r),
            "subs" => new IrBinary(IrBinaryOp.Sub, l, r),
            "ands" => new IrBinary(IrBinaryOp.And, l, r),
            _ => new IrBinary(IrBinaryOp.And, l, new IrUnary(IrUnaryOp.Not, r)),
        };
        Set(d, result, emit);
        ctx.Flags = m switch
        {
            "subs" => Flags.Compare(l, r),
            "adds" => Flags.Compare(l, new IrUnary(IrUnaryOp.Neg, r)),
            "ands" => Flags.Test(l, r),
            _ => Flags.Test(l, new IrUnary(IrUnaryOp.Not, r)),
        };
    }

    /// <summary>
    /// A value the flags will be read from later: when the instruction overwrites a register the value reads
    /// (<c>subs x0, x0, #1</c>), it is first copied to a temporary so the condition sees the value before.
    /// </summary>
    private static IrExpr Snapshot(IrExpr value, Arm64RegisterOperand destination, Context ctx, Action<IrStmt> emit)
    {
        if (destination.Register.IsZero || value is IrConst
            || !IrRewriter.Descendants(value).Any(e => e is IrReg r && RegisterAliases.Overlap(r.Name, Name(destination.Register))))
        {
            return value;
        }

        var temp = ctx.NewTemp(value.Bits);
        emit(new IrAssign(temp, value));
        return temp;
    }

    private void Bitfield(string m, Arm64RegisterOperand d, Arm64Operand src, int lsb, int width, Action<IrStmt> emit)
    {
        int bits = Bits(d);
        var value = Value(src, bits);
        var mask = new IrConst(Mask(width), bits);
        IrExpr result = m switch
        {
            "ubfx" => new IrBinary(IrBinaryOp.And, lsb == 0 ? value : new IrBinary(IrBinaryOp.Shr, value, new IrConst(lsb, 8)), mask),
            "sbfx" => new IrBinary(IrBinaryOp.Sar,
                new IrCast(new IrBinary(IrBinaryOp.Shl, value, new IrConst(bits - lsb - width, 8)), bits, true),
                new IrConst(bits - width, 8)),
            "ubfiz" => new IrBinary(IrBinaryOp.Shl, new IrBinary(IrBinaryOp.And, value, mask), new IrConst(lsb, 8)),
            "sbfiz" => new IrBinary(IrBinaryOp.Shl,
                new IrBinary(IrBinaryOp.Sar, new IrCast(new IrBinary(IrBinaryOp.Shl, value, new IrConst(bits - width, 8)), bits, true), new IrConst(bits - width, 8)),
                new IrConst(lsb, 8)),
            "bfi" => new IrBinary(IrBinaryOp.Or,
                new IrBinary(IrBinaryOp.And, Read(d), new IrConst(~(Mask(width) << lsb), bits)),
                new IrBinary(IrBinaryOp.Shl, new IrBinary(IrBinaryOp.And, value, mask), new IrConst(lsb, 8))),
            _ => new IrBinary(IrBinaryOp.Or,   // bfxil
                new IrBinary(IrBinaryOp.And, Read(d), new IrConst(~Mask(width), bits)),
                new IrBinary(IrBinaryOp.And, new IrBinary(IrBinaryOp.Shr, value, new IrConst(lsb, 8)), mask)),
        };
        Set(d, result, emit);
    }

    private static long Mask(int width) => width >= 64 ? -1L : (1L << width) - 1;

    // ------------------------------------------------------------------
    // Operands
    // ------------------------------------------------------------------

    /// <summary>The IR name of a register: <c>x0</c>, <c>w8</c>, <c>sp</c>, <c>d0</c>.</summary>
    private static string Name(Arm64Register register) => register.Kind switch
    {
        Arm64RegisterKind.XSp when register.Number == 31 => "sp",
        Arm64RegisterKind.WSp when register.Number == 31 => "wsp",
        Arm64RegisterKind.X or Arm64RegisterKind.XSp => "x" + register.Number,
        Arm64RegisterKind.W or Arm64RegisterKind.WSp => "w" + register.Number,
        _ => register.ToString(),
    };

    private static int Bits(Arm64Register register) => register.Kind switch
    {
        Arm64RegisterKind.X or Arm64RegisterKind.XSp or Arm64RegisterKind.D => 64,
        Arm64RegisterKind.W or Arm64RegisterKind.WSp or Arm64RegisterKind.S => 32,
        Arm64RegisterKind.H => 16,
        Arm64RegisterKind.B => 8,
        _ => 128,
    };

    private static int Bits(Arm64RegisterOperand operand) => Bits(operand.Register);

    private static int OperandBits(Arm64Operand operand) => operand switch
    {
        Arm64RegisterOperand r => Bits(r.Register),
        Arm64ShiftedRegister s => Bits(s.Register),
        Arm64ExtendedRegister e => Bits(e.Register),
        _ => 64,
    };

    /// <summary>A register as a value: the zero register reads as 0.</summary>
    private static IrExpr Read(Arm64RegisterOperand operand) => Read(operand.Register);

    private static IrExpr Read(Arm64Register register)
        => register.IsZero ? new IrConst(0, Bits(register)) : new IrReg(Name(register), Bits(register));

    /// <summary>Writes a register; a write to the zero register is discarded.</summary>
    private static void Set(Arm64RegisterOperand destination, IrExpr value, Action<IrStmt> emit)
    {
        if (!destination.Register.IsZero)
        {
            emit(new IrAssign(new IrReg(Name(destination.Register), Bits(destination)), value));
        }
    }

    private IrExpr Value(Arm64Operand operand, int bits) => operand switch
    {
        Arm64RegisterOperand r => Read(r),
        Arm64Immediate i => new IrConst(i.Value, bits),
        Arm64ShiftedImmediate s when s.Kind == Arm64Shift.Lsl => new IrConst(s.Value << s.Shift, bits),
        Arm64ShiftedRegister s => Shifted(s, bits),
        Arm64ExtendedRegister e => Extended(e, bits),
        Arm64Address a => SymbolOrConst(a.Address, 64),
        _ => throw new NotSupportedException($"operand {operand}"),
    };

    private static IrExpr Shifted(Arm64ShiftedRegister s, int bits)
    {
        var value = Read(s.Register);
        if (s.Amount == 0)
        {
            return value;
        }

        var amount = new IrConst(s.Amount, 8);
        return s.Shift switch
        {
            Arm64Shift.Lsl => new IrBinary(IrBinaryOp.Shl, value, amount),
            Arm64Shift.Lsr => new IrBinary(IrBinaryOp.Shr, value, amount),
            Arm64Shift.Asr => new IrBinary(IrBinaryOp.Sar, new IrCast(value, Bits(s.Register), true), amount),
            _ => new IrBinary(IrBinaryOp.Ror, value, amount),
        };
    }

    /// <summary>An extended register: the low byte, half or word, zero- or sign-extended, then shifted.</summary>
    private static IrExpr Extended(Arm64ExtendedRegister e, int bits) => Extend(Read(e.Register), e.Extend, e.Amount, bits);

    private static IrExpr Extend(IrExpr value, Arm64Extend extend, int amount, int bits)
    {
        IrExpr extended = extend switch
        {
            Arm64Extend.Uxtb => new IrCast(new IrCast(value, 8, false), bits, false),
            Arm64Extend.Uxth => new IrCast(new IrCast(value, 16, false), bits, false),
            Arm64Extend.Uxtw => value.Bits == bits ? new IrCast(new IrCast(value, 32, false), bits, false) : new IrCast(value, bits, false),
            Arm64Extend.Sxtb => new IrCast(new IrCast(value, 8, true), bits, true),
            Arm64Extend.Sxth => new IrCast(new IrCast(value, 16, true), bits, true),
            Arm64Extend.Sxtw => value.Bits == bits ? new IrCast(new IrCast(value, 32, true), bits, true) : new IrCast(value, bits, true),
            _ => value.Bits < bits ? new IrCast(value, bits, false) : value,
        };

        return amount == 0 ? extended : new IrBinary(IrBinaryOp.Shl, extended, new IrConst(amount, 8));
    }

    /// <summary>A 32-bit multiply operand made 64 bits wide: <c>smull x0, w1, w2</c> multiplies the sign-extended words.</summary>
    private IrExpr Widen(Arm64Operand operand, bool signed) => new IrCast(Value(operand, 32), 64, signed);

    private IrExpr SymbolOrConst(ulong va, int bits)
        => _symbols is not null && _symbols.TryGet(va, out var symbol) && symbol.Kind != SymbolKind.Section
            ? new IrSymbol(symbol.Name, va, bits)
            : new IrConst((long)va, bits);

    [GeneratedRegex("^(ld|st)(r|ur|tr|ar|apr|apur|lar|lr|llr)(s?)([bhw]?)$")]
    private static partial Regex PlainAccess();

    [GeneratedRegex("^(ld|st)(add|clr|eor|set|smax|smin|umax|umin)(a?)(l?)([bh]?)$")]
    private static partial Regex AtomicAccess();
}
