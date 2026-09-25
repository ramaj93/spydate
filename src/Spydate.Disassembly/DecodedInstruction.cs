using Iced.Intel;
using Spydate.Disassembly.Arm64;

namespace Spydate.Disassembly;

/// <summary>Architecture-neutral control-flow classification of an instruction.</summary>
public enum InstructionFlow
{
    /// <summary>Falls through to the next instruction.</summary>
    Next,
    UnconditionalBranch,
    ConditionalBranch,
    IndirectBranch,
    Call,
    IndirectCall,
    Return,
    Interrupt,
    /// <summary>Could not be decoded; treated as data.</summary>
    Invalid,
}

/// <summary>
/// One decoded machine instruction with formatted text. Immutable. What the decoder knows beyond the text is kept
/// for the analyses that read it: the raw Iced <see cref="Instruction"/> for x86, <see cref="Arm64Instruction"/> for
/// ARM64. UI code should use the text properties.
/// </summary>
public sealed record DecodedInstruction
{
    public required ulong Va { get; init; }
    public required uint Rva { get; init; }
    public required int Length { get; init; }
    public required ReadOnlyMemory<byte> Bytes { get; init; }
    /// <summary>Lower-case mnemonic, e.g. <c>mov</c>, <c>jne</c>.</summary>
    public required string Mnemonic { get; init; }
    /// <summary>Formatted operand text (may be empty).</summary>
    public required string Operands { get; init; }
    public required InstructionFlow Flow { get; init; }
    /// <summary>Direct branch/call target VA if <see cref="Flow"/> is a direct branch or call.</summary>
    public ulong? BranchTargetVa { get; init; }
    /// <summary>For <c>call [mem]</c> / <c>jmp [mem]</c> with an absolute or RIP-relative address: the memory slot read.</summary>
    public ulong? IndirectSlotVa { get; init; }
    /// <summary>
    /// An address the instruction reads, writes or takes that the decoder worked out across instructions — on ARM64,
    /// the <c>adrp</c> that set the page and the <c>add</c> or load that finishes it. x86 carries its addresses in the
    /// instruction itself and leaves this null.
    /// </summary>
    public ulong? DataVa { get; init; }
    /// <summary>How <see cref="DataVa"/> is used: read, written, or only taken.</summary>
    public XrefKind DataKind { get; init; } = XrefKind.Offset;
    /// <summary>The raw Iced instruction, for x86 (used by the native lifter). Default for any other architecture.</summary>
    public Instruction Native { get; init; }
    /// <summary>The decoded ARM64 instruction; null for any other architecture.</summary>
    public Arm64Instruction? Arm64 { get; init; }

    public ulong NextVa => Va + (ulong)Length;

    /// <summary>Full text: mnemonic + operands.</summary>
    public string Text => Operands.Length == 0 ? Mnemonic : $"{Mnemonic} {Operands}";

    /// <summary>Hex bytes separated by spaces, e.g. <c>48 89 5C 24 08</c>.</summary>
    public string BytesText => Convert.ToHexString(Bytes.Span).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b);

    public bool IsBranch => Flow is InstructionFlow.UnconditionalBranch or InstructionFlow.ConditionalBranch or InstructionFlow.IndirectBranch;
    public bool IsCall => Flow is InstructionFlow.Call or InstructionFlow.IndirectCall;
    public bool EndsBlock => Flow is not (InstructionFlow.Next or InstructionFlow.Call or InstructionFlow.IndirectCall);

    public override string ToString() => $"{Va:X8}  {Text}";
}
