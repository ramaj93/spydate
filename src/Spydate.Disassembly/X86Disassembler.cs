using Iced.Intel;
using Spydate.Core.Symbols;

namespace Spydate.Disassembly;

/// <summary>Assembly syntax flavour used for formatting.</summary>
public enum AsmSyntax
{
    Intel,
    Masm,
    Nasm,
    Gas,
}

/// <summary>
/// x86 / x64 disassembler built on Iced. Thread-safe for concurrent <see cref="Decode"/> calls
/// (each call creates its own decoder and formatter).
/// </summary>
public sealed class X86Disassembler
{
    private readonly SymbolTable? _symbols;

    public X86Disassembler(int bitness, SymbolTable? symbols = null, AsmSyntax syntax = AsmSyntax.Intel)
    {
        if (bitness is not (16 or 32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(bitness), bitness, "Bitness must be 16, 32 or 64.");
        }

        Bitness = bitness;
        Syntax = syntax;
        _symbols = symbols;
    }

    public int Bitness { get; }

    public AsmSyntax Syntax { get; }

    /// <summary>
    /// Decodes instructions from <paramref name="code"/> which is located at virtual address <paramref name="va"/>.
    /// Stops after <paramref name="maxInstructions"/> instructions or when the buffer is exhausted.
    /// Invalid bytes produce a one-byte <c>db</c> pseudo-instruction with <see cref="InstructionFlow.Invalid"/>.
    /// </summary>
    public IReadOnlyList<DecodedInstruction> Decode(ReadOnlyMemory<byte> code, ulong va, ulong imageBase, int maxInstructions = int.MaxValue)
    {
        var result = new List<DecodedInstruction>();
        foreach (var ins in DecodeLazy(code, va, imageBase))
        {
            result.Add(ins);
            if (result.Count >= maxInstructions)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Re-formats an instruction's operands with the current symbol table (symbols discovered after the
    /// original decode — e.g. <c>sub_XXXX</c> names — are picked up).
    /// </summary>
    public string FormatOperands(in Instruction instruction)
    {
        var formatter = CreateFormatter();
        var output = new StringOutput();
        formatter.FormatAllOperands(instruction, output);
        return output.ToStringAndReset();
    }

    /// <summary>Streaming variant of <see cref="Decode"/>.</summary>
    public IEnumerable<DecodedInstruction> DecodeLazy(ReadOnlyMemory<byte> code, ulong va, ulong imageBase)
    {
        if (code.IsEmpty)
        {
            yield break;
        }

        var reader = new MemoryCodeReader(code);
        var decoder = Decoder.Create(Bitness, reader, va, DecoderOptions.None);
        var formatter = CreateFormatter();
        var output = new StringOutput();

        while (reader.Position < code.Length)
        {
            decoder.Decode(out var instr);
            int length = instr.Length;
            if (instr.IsInvalid || length == 0)
            {
                // Consume one byte and emit a data pseudo-op so the caller can keep going.
                int badPos = (int)(instr.IP - va);
                if (badPos >= code.Length)
                {
                    yield break;
                }

                byte b = code.Span[badPos];
                yield return new DecodedInstruction
                {
                    Va = instr.IP,
                    Rva = (uint)(instr.IP - imageBase),
                    Length = 1,
                    Bytes = code.Slice(badPos, 1),
                    Mnemonic = "db",
                    Operands = $"0x{b:X2}",
                    Flow = InstructionFlow.Invalid,
                    Native = instr,
                };
                reader.Position = badPos + 1;
                decoder.IP = va + (ulong)reader.Position;
                continue;
            }

            int pos = (int)(instr.IP - va);
            output.Reset();
            formatter.FormatMnemonic(instr, output);
            string mnemonic = output.ToStringAndReset();
            formatter.FormatAllOperands(instr, output);
            string operands = output.ToStringAndReset();

            var flow = Classify(instr, out ulong? target, out ulong? slot);
            yield return new DecodedInstruction
            {
                Va = instr.IP,
                Rva = (uint)(instr.IP - imageBase),
                Length = length,
                Bytes = code.Slice(pos, length),
                Mnemonic = mnemonic,
                Operands = operands,
                Flow = flow,
                BranchTargetVa = target,
                IndirectSlotVa = slot,
                Native = instr,
            };
        }
    }

    private static InstructionFlow Classify(in Instruction instr, out ulong? target, out ulong? slot)
    {
        target = null;
        slot = null;
        switch (instr.FlowControl)
        {
            case FlowControl.Next:
                return InstructionFlow.Next;
            case FlowControl.UnconditionalBranch:
                target = NearTarget(instr);
                return InstructionFlow.UnconditionalBranch;
            case FlowControl.ConditionalBranch:
                target = NearTarget(instr);
                return InstructionFlow.ConditionalBranch;
            case FlowControl.IndirectBranch:
                slot = MemorySlot(instr);
                return InstructionFlow.IndirectBranch;
            case FlowControl.Call:
                target = NearTarget(instr);
                return InstructionFlow.Call;
            case FlowControl.IndirectCall:
                slot = MemorySlot(instr);
                return InstructionFlow.IndirectCall;
            case FlowControl.Return:
                return InstructionFlow.Return;
            case FlowControl.Interrupt:
            case FlowControl.Exception:
            case FlowControl.XbeginXabortXend:
                return InstructionFlow.Interrupt;
            default:
                return InstructionFlow.Next;
        }
    }

    private static ulong? NearTarget(in Instruction instr)
    {
        if (instr.OpCount == 0)
        {
            return null;
        }

        return instr.Op0Kind switch
        {
            OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64 => instr.NearBranchTarget,
            _ => null,
        };
    }

    /// <summary>For <c>call/jmp [mem]</c>: the absolute address of the memory slot when it is statically known.</summary>
    private static ulong? MemorySlot(in Instruction instr)
    {
        if (instr.OpCount == 0 || instr.Op0Kind != OpKind.Memory)
        {
            return null;
        }

        if (instr.IsIPRelativeMemoryOperand)
        {
            return instr.IPRelativeMemoryAddress;
        }

        if (instr.MemoryBase == Register.None && instr.MemoryIndex == Register.None)
        {
            return instr.MemoryDisplacement64;
        }

        return null;
    }

    private Formatter CreateFormatter()
    {
        var resolver = _symbols is null ? null : new SymbolResolver(_symbols);
        Formatter f = Syntax switch
        {
            AsmSyntax.Masm => new MasmFormatter(resolver),
            AsmSyntax.Nasm => new NasmFormatter(resolver),
            AsmSyntax.Gas => new GasFormatter(resolver),
            _ => new IntelFormatter(resolver),
        };

        var o = f.Options;
        o.HexPrefix = "0x";
        o.HexSuffix = null;
        o.UppercaseHex = false;
        o.UppercaseMnemonics = false;
        o.UppercaseRegisters = false;
        o.SpaceAfterOperandSeparator = true;
        o.RipRelativeAddresses = false;
        o.ShowSymbolAddress = false;
        o.BranchLeadingZeros = false;
        o.SmallHexNumbersInDecimal = true;
        o.SignedImmediateOperands = false;
        o.LeadingZeros = false;
        o.ShowBranchSize = false;
        o.MemorySizeOptions = MemorySizeOptions.Minimal;
        return f;
    }

    /// <summary>Resolves symbols for branch targets and absolute/RIP-relative memory operands.</summary>
    private sealed class SymbolResolver : ISymbolResolver
    {
        private readonly SymbolTable _symbols;

        public SymbolResolver(SymbolTable symbols) => _symbols = symbols;

        public bool TryGetSymbol(in Instruction instruction, int operand, int instructionOperand, ulong address, int addressSize, out SymbolResult symbol)
        {
            if (_symbols.TryGet(address, out var s))
            {
                symbol = new SymbolResult(address, s.Name);
                return true;
            }

            symbol = default;
            return false;
        }
    }

    /// <summary>Iced <see cref="CodeReader"/> over a <see cref="ReadOnlyMemory{T}"/>.</summary>
    private sealed class MemoryCodeReader : CodeReader
    {
        private readonly ReadOnlyMemory<byte> _data;

        public MemoryCodeReader(ReadOnlyMemory<byte> data) => _data = data;

        public int Position { get; set; }

        public override int ReadByte()
        {
            if (Position >= _data.Length)
            {
                return -1;
            }

            return _data.Span[Position++];
        }
    }
}
