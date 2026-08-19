using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
using Spydate.Core.Strings;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>Text produced by a code document loader plus optional analysis notes.</summary>
public sealed record CodeContent(string Text, IReadOnlyList<string> Notes);

/// <summary>A toolbar action shown above a code document (e.g. "Decompile", "Disassembly").</summary>
public sealed class CodeAction
{
    public CodeAction(string label, SymbolRegular icon, Action execute)
    {
        Label = label;
        Icon = icon;
        Command = new RelayCommand(execute);
    }

    public string Label { get; }
    public SymbolRegular Icon { get; }
    public IRelayCommand Command { get; }
}

/// <summary>Read-only syntax-highlighted text document (disassembly, pseudo-C, …) loaded lazily off-thread.</summary>
public sealed partial class CodeDocumentViewModel : DocumentViewModel
{
    private readonly Func<CancellationToken, CodeContent> _loader;

    public CodeDocumentViewModel(string key, string title, SymbolRegular icon, string highlighting, Func<CancellationToken, CodeContent> loader, params CodeAction[] actions)
        : base(key, title, icon)
    {
        Highlighting = highlighting;
        _loader = loader;
        Actions = actions;
    }

    public string Highlighting { get; }

    public IReadOnlyList<CodeAction> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    [ObservableProperty]
    private string _text = string.Empty;

    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty]
    private bool _hasNotes;

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var content = await Task.Run(() => _loader(cancellationToken), cancellationToken).ConfigureAwait(true);
        Text = content.Text;
        Notes.Clear();
        foreach (var n in content.Notes)
        {
            Notes.Add(n);
        }

        HasNotes = Notes.Count > 0;
    }

    // ------------------------------------------------------------------
    // Factories
    // ------------------------------------------------------------------

    public static CodeDocumentViewModel ForFunctionDisassembly(BinaryAnalysis analysis, Function function, Action<Function>? openPseudoC)
    {
        var actions = new List<CodeAction>();
        if (openPseudoC is not null)
        {
            actions.Add(new CodeAction("Decompile", SymbolRegular.Braces24, () => openPseudoC(function)));
        }

        return new CodeDocumentViewModel(
            $"disasm:{function.EntryVa:X}",
            function.Name,
            SymbolRegular.Code24,
            HighlightingService.Asm,
            _ => new CodeContent(FormatFunction(analysis, function), function.Notes),
            actions.ToArray())
        {
            Address = function.EntryVa,
        };
    }

    public static CodeDocumentViewModel ForRangeDisassembly(BinaryAnalysis analysis, ulong va, int byteCount, string title)
    {
        return new CodeDocumentViewModel(
            $"disasm-range:{va:X}",
            title,
            SymbolRegular.Code24,
            HighlightingService.Asm,
            // ReSharper disable once ConvertClosureToMethodGroup
            _ =>
            {
                var insns = analysis.DisassembleRange(va, byteCount);
                var sb = new StringBuilder();
                sb.Append("; linear disassembly from 0x").Append(va.ToString("X", CultureInfo.InvariantCulture))
                  .Append(", ").Append(byteCount).Append(" bytes").AppendLine();
                foreach (var ins in insns)
                {
                    AppendInstruction(sb, ins, analysis);
                }

                return new CodeContent(sb.ToString(), Array.Empty<string>());
            })
        {
            Address = va,
        };
    }

    public static CodeDocumentViewModel ForPseudoC(NativeDecompiler decompiler, Function function, Action<Function>? openDisassembly)
    {
        var actions = new List<CodeAction>();
        if (openDisassembly is not null)
        {
            actions.Add(new CodeAction("Disassembly", SymbolRegular.Code24, () => openDisassembly(function)));
        }

        return new CodeDocumentViewModel(
            $"pseudoc:{function.EntryVa:X}",
            $"{function.Name} (C)",
            SymbolRegular.Braces24,
            HighlightingService.PseudoC,
            _ =>
            {
                var result = decompiler.Decompile(function);
                return new CodeContent(result.Text, result.Warnings);
            },
            actions.ToArray())
        {
            Address = function.EntryVa,
        };
    }

    // ------------------------------------------------------------------
    // Formatting
    // ------------------------------------------------------------------

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

            string text = Escape(literal.Text);
            ulong offset = xref.ToVa - start;
            string prefix = literal.Encoding == StringEncodingKind.Utf16 ? "L" : string.Empty;
            return offset == 0 ? $"{prefix}\"{text}\"" : $"{prefix}\"{text}\"+{offset}";
        }

        return null;
    }

    /// <summary>
    /// Trims literal text and escapes what would break the line. Backslashes are left alone:
    /// doubling them turns every Windows path in the listing into noise.
    /// </summary>
    private static string Escape(string text)
    {
        const int max = 60;
        string trimmed = text.Length <= max ? text : text[..max] + "…";
        var sb = new StringBuilder(trimmed.Length + 2);
        foreach (char c in trimmed)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(char.IsControl(c) ? '.' : c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string FormatFunction(BinaryAnalysis analysis, Function function)
    {
        var sb = new StringBuilder();
        sb.Append("; ").Append(function.Name)
          .Append(" @ 0x").Append(function.EntryVa.ToString("X", CultureInfo.InvariantCulture))
          .Append("  (").Append(function.Blocks.Count).Append(" blocks, ")
          .Append(function.InstructionCount).Append(" instructions, 0x")
          .Append(function.CodeSize.ToString("X", CultureInfo.InvariantCulture)).Append(" bytes)").AppendLine();

        var section = analysis.Image.SectionFromVa(function.EntryVa);
        if (section is not null)
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
              .Append(string.Join(", ", callers.Take(8).Select(x => $"0x{x.FromVa:X} ({x.Kind})")))
              .Append(callers.Count > 8 ? ", …" : string.Empty).AppendLine();
        }

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
                sb.Append("loc_").Append(block.StartVa.ToString("X", CultureInfo.InvariantCulture)).Append(':').AppendLine();
            }

            foreach (var ins in block.Instructions)
            {
                AppendInstruction(sb, ins, analysis);
            }
        }

        sb.Append(function.Name).Append(" endp").AppendLine();
        return sb.ToString();
    }

    private static void AppendInstruction(StringBuilder sb, DecodedInstruction ins, BinaryAnalysis analysis)
    {
        int addrWidth = analysis.Image.Bitness == 64 ? 16 : 8;
        sb.Append(ins.Va.ToString($"X{addrWidth}", CultureInfo.InvariantCulture)).Append("  ");
        string bytes = ins.BytesText;
        sb.Append(bytes);
        int pad = 30 - bytes.Length;
        sb.Append(' ', Math.Max(1, pad));
        sb.Append(ins.Mnemonic);
        // Re-format operands now so symbols discovered after decoding (sub_XXXX) are shown.
        string operands = ins.Flow == InstructionFlow.Invalid ? ins.Operands : analysis.Disassembler.FormatOperands(ins.Native);
        if (operands.Length > 0)
        {
            sb.Append(' ', Math.Max(1, 8 - ins.Mnemonic.Length)).Append(operands);
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

        sb.AppendLine();
    }
}
