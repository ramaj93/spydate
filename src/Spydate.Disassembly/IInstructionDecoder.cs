using Spydate.Core.Binary;
using Spydate.Core.Symbols;

namespace Spydate.Disassembly;

/// <summary>
/// One instruction set's decoder, and what the analysis above it needs to know about that instruction set: how
/// long an instruction can be, where one may start, what the filler between functions and the first instruction
/// of a function look like, which instructions stop execution, and how a switch reads its table.
///
/// Everything above this — function discovery, blocks, cross-references, the listing — works on
/// <see cref="DecodedInstruction"/> and asks these questions here, so it is the same for every architecture.
/// </summary>
public interface IInstructionDecoder
{
    Architecture Architecture { get; }

    AsmSyntax Syntax { get; }

    /// <summary>The longest an instruction can be, in bytes: how far past a buffer's end decoding may need to see.</summary>
    int MaxInstructionLength { get; }

    /// <summary>Every instruction starts at a multiple of this: 1 on x86, 4 on ARM64.</summary>
    int InstructionAlignment { get; }

    /// <summary>
    /// Decodes instructions from <paramref name="code"/>, which is located at <paramref name="va"/>. Bytes that are
    /// not an instruction produce a pseudo-instruction with <see cref="InstructionFlow.Invalid"/>, one alignment
    /// unit long, so the caller can keep going.
    /// </summary>
    IReadOnlyList<DecodedInstruction> Decode(ReadOnlyMemory<byte> code, ulong va, ulong imageBase, int maxInstructions = int.MaxValue);

    /// <summary>Streaming variant of <see cref="Decode"/>.</summary>
    IEnumerable<DecodedInstruction> DecodeLazy(ReadOnlyMemory<byte> code, ulong va, ulong imageBase);

    /// <summary>An instruction's operands formatted again, with the names the symbol table has now.</summary>
    string FormatOperands(DecodedInstruction instruction);

    /// <summary>
    /// Whether the bytes at the start of <paramref name="code"/> are filler: between blocks, or with
    /// <paramref name="betweenFunctions"/> between whole functions, where a linker also pads with zeros.
    /// </summary>
    bool IsPadding(ReadOnlySpan<byte> code, bool betweenFunctions = false);

    /// <summary>
    /// Whether <paramref name="code"/> looks like the start of a function. Conservative: a false positive invents a
    /// function out of data, which is worse than missing one.
    /// </summary>
    bool LooksLikeFunctionStart(ReadOnlySpan<byte> code);

    /// <summary>An instruction execution never continues after: a breakpoint, a trap, a fail-fast.</summary>
    bool NeverContinues(DecodedInstruction instruction);

    /// <summary>
    /// The switch table an indirect jump reads, from the instructions physically before it, last one the jump.
    /// <paramref name="accept"/> limits the targets when there is no range check to count the entries by.
    /// </summary>
    JumpTable? RecoverJumpTable(IReadOnlyList<DecodedInstruction> trailing, ICodeSource source, Func<ulong, bool>? accept);
}

/// <summary>Picks the decoder for an image's instruction set.</summary>
public static class InstructionDecoders
{
    /// <summary>Whether there is a decoder for <paramref name="architecture"/>.</summary>
    public static bool Supports(Architecture architecture) => architecture is Architecture.X86 or Architecture.X64 or Architecture.Arm64;

    /// <summary>The decoder for <paramref name="image"/>, or null when its instruction set has none.</summary>
    public static IInstructionDecoder? For(IBinaryImage image, SymbolTable? symbols = null, AsmSyntax syntax = AsmSyntax.Intel)
        => image.Architecture switch
        {
            Architecture.X86 or Architecture.X64 => new X86Disassembler(image.Bitness, symbols, syntax),
            Architecture.Arm64 => new Arm64.Arm64Disassembler(symbols, syntax),
            _ => null,
        };
}
