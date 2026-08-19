using Iced.Intel;

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
/// One decoded machine instruction with formatted text. Immutable. The raw Iced
/// <see cref="Instruction"/> is exposed for the lifter; UI code should use the text properties.
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
    /// <summary>Raw Iced instruction (used by the native lifter).</summary>
    public required Instruction Native { get; init; }

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
