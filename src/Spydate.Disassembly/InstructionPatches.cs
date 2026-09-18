using Iced.Intel;
using Spydate.Core.PE;
using Spydate.Core.Project;

namespace Spydate.Disassembly;

/// <summary>A patch that could be built, or the reason it could not.</summary>
public sealed record PatchProposal(Patch? Patch, string? Problem)
{
    public bool Ok => Patch is not null;

    public static PatchProposal Failed(string problem) => new(null, problem);
}

/// <summary>
/// How a replacement of a given size lands at an address: the whole instructions it covers, and the
/// bytes of nop that leaves over.
/// </summary>
public sealed record PatchFit(int Covered, int Instructions, int Padding, string? Problem)
{
    public bool Ok => Problem is null;

    /// <summary>
    /// What a dialog says under the box — the analyst is about to overwrite more than they typed,
    /// and how much more is the one thing they cannot work out by looking.
    /// </summary>
    public string Describe(int byteCount)
    {
        if (Problem is { } problem)
        {
            return problem;
        }

        string typed = byteCount == 1 ? "1 byte" : $"{byteCount} bytes";
        string over = Instructions == 1 ? "1 instruction" : $"{Instructions} instructions";

        return Padding == 0
            ? $"{typed} over {over} — an exact fit"
            : $"{typed} over {over} ({Covered} bytes), {Padding} padded with nop";
    }
}

/// <summary>
/// The patches worth having a button for: take an instruction out, or make a branch go the other
/// way. Both are answers to "stop this check from failing", which is most of what patching is for.
///
/// Every one of them replaces exactly the bytes it covers. Nothing moves, so no address the analyst
/// has written down changes meaning and no re-analysis is needed — see <see cref="PatchStore"/> for
/// why that rule is load-bearing rather than a convenience.
/// </summary>
public static class InstructionPatches
{
    /// <summary>
    /// Replaces the instructions covering <paramref name="va"/> with NOPs, one byte each.
    ///
    /// Whole instructions, never a prefix of one: NOPping the first three bytes of a five-byte call
    /// leaves the last two to be decoded as whatever they happen to spell, which is how a patch turns
    /// into a crash somewhere unrelated.
    /// </summary>
    public static PatchProposal NopOut(BinaryAnalysis analysis, ulong va, int count = 1)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (count < 1)
        {
            return PatchProposal.Failed("nothing to do");
        }

        var decoded = analysis.DisassembleRange(va, 16 * count, count);
        if (decoded.Count == 0)
        {
            return PatchProposal.Failed($"nothing decodes at 0x{va:X}");
        }

        int length = decoded.Sum(d => d.Length);
        if (Original(analysis.Image, va, length) is not { } original)
        {
            return PatchProposal.Failed($"0x{va:X} is not in any section of the file");
        }

        string what = decoded.Count == 1 ? decoded[0].Text : $"{decoded.Count} instructions";
        return Build(analysis.Image, va, Enumerable.Repeat((byte)0x90, length).ToArray(), original, $"nop out {what}");
    }

    /// <summary>
    /// Makes a conditional branch take the other way — <c>jz</c> becomes <c>jnz</c> and so on.
    ///
    /// Preferred over NOPping the branch, which is the other reflex: this keeps the instruction and
    /// its length, so the code still reads as a decision that was made rather than one that silently
    /// stopped being asked, and it survives being read back six months later.
    /// </summary>
    public static PatchProposal InvertBranch(BinaryAnalysis analysis, ulong va)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var bytes = analysis.Image.ReadAtVa(va, 16);
        if (bytes.Length == 0)
        {
            return PatchProposal.Failed($"0x{va:X} is not in any section of the file");
        }

        var reader = new ByteArrayCodeReader(bytes.ToArray());
        var decoder = Decoder.Create(analysis.Image.Is64Bit ? 64 : 32, reader, va);
        var instruction = decoder.Decode();

        if (instruction.ConditionCode == ConditionCode.None)
        {
            return PatchProposal.Failed($"0x{va:X} is not a conditional branch");
        }

        // Iced knows the opposite of every condition, which beats a table of opcode bit flips that
        // has to be right for all of them.
        instruction.Code = instruction.Code.NegateConditionCode();

        var writer = new Collector();
        var encoder = Encoder.Create(analysis.Image.Is64Bit ? 64 : 32, writer);
        try
        {
            encoder.Encode(instruction, va);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return PatchProposal.Failed($"could not encode the inverted branch: {ex.Message}");
        }

        if (writer.Bytes.Count != instruction.Length)
        {
            return PatchProposal.Failed(
                $"the inverted branch is {writer.Bytes.Count} bytes where the original is {instruction.Length}");
        }

        if (Original(analysis.Image, va, instruction.Length) is not { } original)
        {
            return PatchProposal.Failed($"0x{va:X} is not in any section of the file");
        }

        return Build(analysis.Image, va, writer.Bytes.ToArray(), original, $"invert {instruction.Code.ToString().Split('_')[0].ToLowerInvariant()}");
    }

    /// <summary>
    /// Assembles what was typed and fits it over the instructions at <paramref name="va"/>.
    ///
    /// The replacement rarely lands on exactly the length it replaces, so the region grows to whole
    /// instructions until the new bytes fit and the remainder is padded with NOPs. Growing is not a
    /// courtesy: an instruction left half-overwritten decodes as whatever its tail happens to spell,
    /// and the analyst is told how many instructions went so it is a decision rather than a surprise.
    /// </summary>
    public static PatchProposal Assemble(BinaryAnalysis analysis, ulong va, string? text)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var encoded = X86Assembler.Encode(text, analysis.Image.Is64Bit, va);
        if (!encoded.Ok)
        {
            return PatchProposal.Failed(encoded.Problem!);
        }

        var fit = Fit(analysis, va, encoded.Bytes.Length);
        if (!fit.Ok)
        {
            return PatchProposal.Failed(fit.Problem!);
        }

        if (Original(analysis.Image, va, fit.Covered) is not { } original)
        {
            return PatchProposal.Failed($"0x{va:X} is not in any section of the file");
        }

        byte[] bytes = new byte[fit.Covered];
        Array.Fill(bytes, (byte)0x90);
        encoded.Bytes.CopyTo(bytes, 0);

        string what = fit.Instructions == 1 ? "1 instruction" : $"{fit.Instructions} instructions";
        string note = fit.Padding == 0 ? what : $"{what}, {fit.Padding} byte(s) padded";

        return Build(analysis.Image, va, bytes, original, $"{text?.Trim()} ({note})");
    }

    /// <summary>
    /// Works out how many whole instructions <paramref name="byteCount"/> bytes would take over at
    /// <paramref name="va"/>. Separate from <see cref="Assemble"/> because the patch dialog has to
    /// say it before anything is committed — an analyst typing two bytes over a five-byte call
    /// should see that the call is going, not find out afterwards.
    /// </summary>
    public static PatchFit Fit(BinaryAnalysis analysis, ulong va, int byteCount)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (byteCount < 1)
        {
            return new PatchFit(0, 0, 0, "nothing to write");
        }

        var decoded = analysis.DisassembleRange(va, Math.Max(64, byteCount + 32));
        int covered = 0;
        int taken = 0;
        foreach (var instruction in decoded)
        {
            covered += instruction.Length;
            taken++;
            if (covered >= byteCount)
            {
                break;
            }
        }

        return covered < byteCount
            ? new PatchFit(covered, taken, 0,
                $"{byteCount} bytes will not fit: only {covered} bytes of whole instructions could be read at 0x{va:X}")
            : new PatchFit(covered, taken, covered - byteCount, null);
    }

    /// <summary>
    /// Reads bytes back as the line they spell, for the other half of the patch dialog.
    ///
    /// Deliberately without the symbol table the listing uses. A call shown as
    /// <c>call ResolveSearchRoot</c> reads better but cannot be typed back in, and a box whose
    /// contents this cannot re-assemble is worse than one that says 0x140001408 out loud.
    /// </summary>
    public static string ReadBack(BinaryAnalysis analysis, ulong va, ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var plain = new X86Disassembler(analysis.Image.Is64Bit ? 64 : 32, symbols: null, analysis.Disassembler.Syntax);
        return string.Join("; ", plain.Decode(bytes, va, analysis.Image.ImageBase).Select(d => d.Text));
    }

    /// <summary>The bytes as they are now, or null when the address is not in the file.</summary>
    private static byte[]? Original(PeImage image, ulong va, int length)
    {
        var present = image.ReadAtVa(va, length);
        return present.Length == length ? present.ToArray() : null;
    }

    private static PatchProposal Build(PeImage image, ulong va, byte[] bytes, byte[] original, string comment)
    {
        if (image.VaToRva(va) is not { } rva)
        {
            return PatchProposal.Failed($"0x{va:X} is outside the image");
        }

        if (bytes.SequenceEqual(original))
        {
            return PatchProposal.Failed("that would change nothing");
        }

        return new PatchProposal(
            new Patch { Rva = rva, Bytes = bytes, Original = original, Comment = comment },
            null);
    }

    private sealed class Collector : CodeWriter
    {
        public List<byte> Bytes { get; } = new();

        public override void WriteByte(byte value) => Bytes.Add(value);
    }
}
