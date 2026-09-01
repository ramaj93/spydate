using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
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
public sealed partial class CodeDocumentViewModel : DocumentViewModel, ICaretContext
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

    /// <summary>Address of the line the caret is on, published by the editor.</summary>
    [ObservableProperty]
    private ulong? _caretAddress;

    /// <summary>Identifier under the caret, so commands can act on the name being read.</summary>
    [ObservableProperty]
    private string? _caretWord;

    /// <summary>A single-pane document does not move, so what it is about is what it opened on.</summary>
    public ulong? OwningFunctionVa => Address;

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

    public static CodeDocumentViewModel ForFunctionDisassembly(
        BinaryAnalysis analysis,
        Function function,
        Action<Function>? openPseudoC,
        Action<Function>? openSplit = null,
        Action<Function>? openGraph = null)
    {
        var actions = new List<CodeAction>();
        if (openPseudoC is not null)
        {
            actions.Add(new CodeAction("Decompile", SymbolRegular.Braces24, () => openPseudoC(function)));
        }

        if (openSplit is not null)
        {
            actions.Add(new CodeAction("Side by side", SymbolRegular.SplitHorizontal24, () => openSplit(function)));
        }

        if (openGraph is not null)
        {
            actions.Add(new CodeAction("Graph", SymbolRegular.Flowchart24, () => openGraph(function)));
        }

        return new CodeDocumentViewModel(
            $"disasm:{function.EntryVa:X}",
            function.Name,
            SymbolRegular.Code24,
            HighlightingService.Asm,
            _ =>
            {
                var current = analysis.TryGetFunction(function.EntryVa, out var latest) ? latest : function;
                return new CodeContent(AsmListing.ForFunction(analysis, current), current.Notes);
            },
            actions.ToArray())
        {
            Address = function.EntryVa,
        };
    }

    /// <summary>A read-only text document with no analysis behind it (decoded resources, notes).</summary>
    public static CodeDocumentViewModel ForText(string key, string title, SymbolRegular icon, string highlighting, string text)
        => new(key, title, icon, highlighting, _ => new CodeContent(text, Array.Empty<string>()));

    public static CodeDocumentViewModel ForRangeDisassembly(BinaryAnalysis analysis, ulong va, int byteCount, string title)
    {
        return new CodeDocumentViewModel(
            $"disasm-range:{va:X}",
            title,
            SymbolRegular.Code24,
            HighlightingService.Asm,
            _ => new CodeContent(AsmListing.ForRange(analysis, va, byteCount), Array.Empty<string>()))
        {
            Address = va,
        };
    }

    public static CodeDocumentViewModel ForPseudoC(NativeDecompiler decompiler, Function function, Action<Function>? openDisassembly, Func<Function>? current = null, Action<Function>? openSplit = null)
    {
        current ??= () => function;
        var actions = new List<CodeAction>();
        if (openDisassembly is not null)
        {
            actions.Add(new CodeAction("Disassembly", SymbolRegular.Code24, () => openDisassembly(function)));
        }

        if (openSplit is not null)
        {
            actions.Add(new CodeAction("Side by side", SymbolRegular.SplitHorizontal24, () => openSplit(function)));
        }

        return new CodeDocumentViewModel(
            $"pseudoc:{function.EntryVa:X}",
            $"{function.Name} (C)",
            SymbolRegular.Braces24,
            HighlightingService.PseudoC,
            _ =>
            {
                var result = decompiler.Decompile(current());
                return new CodeContent(result.Text, result.Warnings);
            },
            actions.ToArray())
        {
            Address = function.EntryVa,
        };
    }
}
