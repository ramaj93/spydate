using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Spydate.App.Services;
using Spydate.Core.Text;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>
/// One function shown twice: its disassembly beside its pseudo-C, each following the other. Picking a
/// line in either pane moves the other to the address that line is about, which is what makes the
/// decompiled version checkable rather than merely readable.
/// </summary>
public sealed partial class SplitCodeDocumentViewModel : DocumentViewModel, ICaretContext
{
    private readonly ulong _entryVa;
    private readonly Func<CancellationToken, (string Disassembly, string PseudoC, IReadOnlyList<string> Notes)> _loader;
    private LineAddressMap _disassemblyMap = LineAddressMap.Empty;
    private LineAddressMap _pseudoCMap = LineAddressMap.Empty;

    /// <summary>Set while one pane is being moved to match the other, so they cannot chase each other.</summary>
    private bool _syncing;

    public SplitCodeDocumentViewModel(ulong entryVa, string title, Func<CancellationToken, (string, string, IReadOnlyList<string>)> loader)
        : base($"split:{entryVa:X}", title, SymbolRegular.SplitHorizontal24)
    {
        _loader = loader;
        _entryVa = entryVa;
        Address = entryVa;
    }

    public static SplitCodeDocumentViewModel For(BinaryAnalysis analysis, NativeDecompiler decompiler, Function function, Func<Function> current)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(decompiler);
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(current);

        return new SplitCodeDocumentViewModel(function.EntryVa, $"{function.Name} (split)", _ =>
        {
            var latest = current();
            var decompiled = decompiler.Decompile(latest);
            return (AsmListing.ForFunction(analysis, latest), decompiled.Text, decompiled.Warnings);
        });
    }

    public string DisassemblyHighlighting => HighlightingService.Asm;

    public string PseudoCHighlighting => HighlightingService.PseudoC;

    [ObservableProperty]
    private string _disassemblyText = string.Empty;

    [ObservableProperty]
    private string _pseudoCText = string.Empty;

    /// <summary>Line the disassembly pane should show, 1-based; 0 leaves it alone.</summary>
    [ObservableProperty]
    private int _disassemblyLine;

    [ObservableProperty]
    private int _pseudoCLine;

    /// <summary>Address of the line the caret is on in each pane, published by the editors.</summary>
    [ObservableProperty]
    private ulong? _disassemblyCaretAddress;

    [ObservableProperty]
    private ulong? _pseudoCCaretAddress;

    /// <summary>Identifier under the caret in whichever pane was used last, for renaming.</summary>
    [ObservableProperty]
    private string? _caretWord;

    [ObservableProperty]
    private string _syncStatus = string.Empty;

    /// <summary>The function both panes are showing; the caret moves inside it.</summary>
    public ulong? OwningFunctionVa => _entryVa;

    /// <summary>Where the caret is, whichever pane it was last moved in.</summary>
    public ulong? CaretAddress { get; private set; }

    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty]
    private bool _hasNotes;

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var (disassembly, pseudoC, notes) = await Task.Run(() => _loader(cancellationToken), cancellationToken).ConfigureAwait(true);

        _syncing = true;
        try
        {
            DisassemblyText = disassembly;
            PseudoCText = pseudoC;
            _disassemblyMap = LineAddressMap.Build(disassembly);
            _pseudoCMap = LineAddressMap.Build(pseudoC);
        }
        finally
        {
            _syncing = false;
        }

        Notes.Clear();
        foreach (string note in notes)
        {
            Notes.Add(note);
        }

        HasNotes = Notes.Count > 0;
        SyncStatus = $"{_disassemblyMap.Count} instructions · {_pseudoCMap.Count} statements";
    }

    partial void OnDisassemblyCaretAddressChanged(ulong? value) => Follow(value, toPseudoC: true);

    partial void OnPseudoCCaretAddressChanged(ulong? value) => Follow(value, toPseudoC: false);

    /// <summary>Moves the other pane to the address the caret is on.</summary>
    private void Follow(ulong? address, bool toPseudoC)
    {
        if (_syncing || address is not { } va)
        {
            return;
        }

        var map = toPseudoC ? _pseudoCMap : _disassemblyMap;
        if (map.LineFor(va) is not { } line)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (toPseudoC)
            {
                PseudoCLine = line;
            }
            else
            {
                DisassemblyLine = line;
            }

            CaretAddress = va;
            Address = va;   // the Xrefs panel follows the line the caret is on
        }
        finally
        {
            _syncing = false;
        }
    }
}
