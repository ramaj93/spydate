using System.Globalization;
using System.Text;
using Spydate.Core.Strings;

namespace Spydate.Disassembly;

/// <summary>
/// The disassembly listing: a function, or a run of bytes, as the text a person reads.
///
/// It lives here rather than in the window because it is text about a binary, not about a view —
/// the split view and the graph both want it, and so does anything driving the engine from outside
/// the app. It was in a WPF ViewModel until it needed a second kind of caller, which also meant it
/// had never been tested: the test project cannot reference the app.
/// </summary>
public static class AsmListing
{
    /// <summary>How many reference sites the header names before it gives up and counts them.</summary>
    private const int MaxCallersListed = 8;

    /// <summary>Column the operands start at, measured from the end of the mnemonic.</summary>
    private const int OperandColumn = 8;

    /// <summary>Width reserved for the raw bytes, so mnemonics line up down the page.</summary>
    private const int BytesColumn = 30;

    /// <summary>
    /// A function's listing: a header saying what analysis knows about it, then its blocks, with a
    /// label on every block something branches to.
    /// </summary>
    public static string ForFunction(BinaryAnalysis analysis, Function function)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(function);

        var sb = new StringBuilder();
        AppendHeader(sb, analysis, function);
        sb.Append(function.Name).Append(" proc").AppendLine();

        var labelTargets = new HashSet<ulong>();
        foreach (var ins in function.Instructions)
        {
            if (ins.BranchTargetVa is { } t && ins.Flow is InstructionFlow.ConditionalBranch or InstructionFlow.UnconditionalBranch)
            {
                labelTargets.Add(t);
            }
        }

        foreach (var block in function.Blocks)
        {
            if (labelTargets.Contains(block.StartVa) || block.Predecessors.Count > 1)
            {
                sb.AppendLine();
                sb.Append(analysis.NameFor(block.StartVa)).Append(':').AppendLine();
            }

            foreach (var ins in block.Instructions)
            {
                AppendInstruction(sb, ins, analysis);
            }
        }

        sb.Append(function.Name).Append(" endp").AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Linear disassembly of a byte range, for bytes no function claims. No blocks and no labels:
    /// nothing here knows where control enters.
    /// </summary>
    public static string ForRange(BinaryAnalysis analysis, ulong va, int byteCount)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var sb = new StringBuilder();
        sb.Append("; linear disassembly from 0x").Append(va.ToString("X", CultureInfo.InvariantCulture))
          .Append(", ").Append(byteCount).Append(" bytes").AppendLine();

        foreach (var ins in analysis.DisassembleRange(va, byteCount))
        {
            AppendInstruction(sb, ins, analysis);
        }

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, BinaryAnalysis analysis, Function function)
    {
        sb.Append("; ").Append(function.Name)
          .Append(" @ 0x").Append(function.EntryVa.ToString("X", CultureInfo.InvariantCulture))
          .Append("  (").Append(function.Blocks.Count).Append(" blocks, ")
          .Append(function.InstructionCount).Append(" instructions, 0x")
          .Append(function.CodeSize.ToString("X", CultureInfo.InvariantCulture)).Append(" bytes)").AppendLine();

        if (analysis.Image.SectionFromVa(function.EntryVa) is { } section)
        {
            sb.Append("; section ").Append(section.Name).AppendLine();
        }

        if (function.BoundsEnd is { } bounds)
        {
            sb.Append("; unwind table declares 0x").Append(function.EntryVa.ToString("X", CultureInfo.InvariantCulture))
              .Append("-0x").Append(bounds.ToString("X", CultureInfo.InvariantCulture))
              .Append(" (0x").Append(function.DeclaredSize.ToString("X", CultureInfo.InvariantCulture)).Append(" bytes)");
            if (function.ExtendsBeyondBounds)
            {
                sb.Append(" - decoding ran past it");
            }

            sb.AppendLine();
        }

        var callers = analysis.Xrefs.To(function.EntryVa);
        if (callers.Count > 0)
        {
            sb.Append("; referenced by ").Append(callers.Count).Append(callers.Count == 1 ? " site: " : " sites: ")
              .Append(string.Join(", ", callers.Take(MaxCallersListed).Select(x => $"0x{x.FromVa:X} ({x.Kind})")))
              .Append(callers.Count > MaxCallersListed ? ", …" : string.Empty).AppendLine();
        }
    }

    private static void AppendInstruction(StringBuilder sb, DecodedInstruction ins, BinaryAnalysis analysis)
    {
        int addrWidth = analysis.Image.Bitness == 64 ? 16 : 8;
        sb.Append(ins.Va.ToString($"X{addrWidth}", CultureInfo.InvariantCulture)).Append("  ");
        string bytes = ins.BytesText;
        sb.Append(bytes);
        sb.Append(' ', Math.Max(1, BytesColumn - bytes.Length));
        sb.Append(ins.Mnemonic);

        // Re-format operands now so symbols discovered after decoding (sub_XXXX) are shown.
        string operands = ins.Flow == InstructionFlow.Invalid ? ins.Operands : analysis.Disassembler.FormatOperands(ins.Native);
        if (operands.Length > 0)
        {
            sb.Append(' ', Math.Max(1, OperandColumn - ins.Mnemonic.Length)).Append(operands);
        }

        // Annotate direct branch/call targets and IAT slots that have names, and data references
        // that land in a string literal — the single most useful comment in a disassembly listing.
        string? comment = null;
        if (ins.BranchTargetVa is { } target && analysis.Symbols.TryGet(target, out var sym) && !operands.Contains(sym.Name, StringComparison.Ordinal))
        {
            comment = sym.Name;
        }
        else if (ins.IndirectSlotVa is { } slot && analysis.Symbols.TryGet(slot, out var slotSym) && !operands.Contains(slotSym.Name, StringComparison.Ordinal))
        {
            comment = slotSym.Name;
        }
        else if (StringComment(ins, analysis) is { } literal)
        {
            comment = literal;
        }

        if (comment is not null)
        {
            sb.Append("    ; ").Append(comment);
        }

        if (analysis.CommentFor(ins.Va) is { } note)
        {
            sb.Append(comment is null ? "    ; " : "   ; ").Append(note);
        }

        sb.AppendLine();
    }

    /// <summary>
    /// <c>"text"</c> when the instruction's data reference points into a string literal.
    /// The reference may land inside the string, so the offset is shown when it is not the start.
    /// </summary>
    private static string? StringComment(DecodedInstruction ins, BinaryAnalysis analysis)
    {
        foreach (var xref in analysis.Xrefs.From(ins.Va))
        {
            if (xref.IsCode || analysis.StringAt(xref.ToVa) is not { } literal || literal.Va is not { } start)
            {
                continue;
            }

            string text = StringLiterals.Escape(literal.Text);
            ulong offset = xref.ToVa - start;
            string prefix = literal.Encoding == StringEncodingKind.Utf16 ? "L" : string.Empty;
            return offset == 0 ? $"{prefix}\"{text}\"" : $"{prefix}\"{text}\"+{offset}";
        }

        return null;
    }
}
