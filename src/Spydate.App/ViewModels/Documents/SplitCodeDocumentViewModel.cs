using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Spydate.App.Services;
using Spydate.Core.Text;
using Spydate.Decompiler.Native;
using Spydate.Core.Project;
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

    public static SplitCodeDocumentViewModel For(BinaryAnalysis analysis, NativeDecompiler decompiler, Function function, Func<Function> current, PatchStore? patches = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(decompiler);
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(current);

        return new SplitCodeDocumentViewModel(function.EntryVa, $"{function.Name} (split)", _ =>
        {
            var latest = current();
            var decompiled = decompiler.Decompile(latest);
            return (AsmListing.ForFunction(analysis, latest, patches), decompiled.Text, decompiled.Warnings);
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

    private ulong? _caretAddress;

    /// <summary>
    /// Where the caret is, whichever pane it was last moved in.
    ///
    /// It raises a change like any other property: the commands that act on the caret ask whether
    /// they apply, and they can only be re-asked if this says when it moves.
    /// </summary>
    public ulong? CaretAddress
    {
        get => _caretAddress;
        private set
        {
            if (_caretAddress != value)
            {
                _caretAddress = value;
                OnPropertyChanged();
            }
        }
    }

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

        // A stop that opened this document asked for its line before there was text; now there is.
        ApplyPendingReveal();
    }

    /// <summary>
    /// Scrolls both panes to an address, if this function has a line for it.
    ///
    /// What a stop needs and what opening a function needs are not the same thing: a document opens
    /// on the function's entry, which is the top of the listing, and stopping two hundred
    /// instructions in would otherwise leave the reader looking at the header with the arrow
    /// somewhere off screen below.
    /// </summary>
    public void RevealAddress(ulong va)
    {
        _pendingReveal = va;
        ApplyPendingReveal();
    }

    /// <summary>
    /// An address to scroll to as soon as there is a listing to scroll. A document opened by a stop
    /// loads in the background, so the stop asks for its line before either pane has any text.
    /// </summary>
    private ulong? _pendingReveal;

    private void ApplyPendingReveal()
    {
        if (_pendingReveal is not { } va || _disassemblyMap.Count == 0)
        {
            return;
        }

        _pendingReveal = null;

        // Through the same guard the panes use on each other, so moving them here does not read as a
        // caret the reader put there and send them chasing.
        _syncing = true;
        try
        {
            // Zero first: a line only moves the pane when it changes, and stopping twice on one line
            // — a breakpoint hit after stepping away and back — would otherwise not scroll at all.
            if (_disassemblyMap.LineFor(va) is { } disassembly && _disassemblyMap.Covers(va))
            {
                DisassemblyLine = 0;
                DisassemblyLine = disassembly;
            }

            if (_pseudoCMap.LineFor(va) is { } pseudoC && _pseudoCMap.Covers(va))
            {
                PseudoCLine = 0;
                PseudoCLine = pseudoC;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnDisassemblyCaretAddressChanged(ulong? value) => Follow(value, toPseudoC: true);

    partial void OnPseudoCCaretAddressChanged(ulong? value) => Follow(value, toPseudoC: false);

    /// <summary>Moves the other pane to the address the caret is on.</summary>
    private void Follow(ulong? address, bool toPseudoC)
    {
        if (_syncing)
        {
            return;
        }

        // Where the caret is, first and unconditionally. Both of the early returns below are about
        // whether the *other* pane can follow, which is a different question — and answering it
        // first left the caret reading as its previous address on every line that has none, so
        // Rename and Patch stayed lit over blank lines and acted on whatever was last clicked.
        CaretAddress = address;

        if (address is not { } va)
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

            Address = va;   // the Xrefs panel follows the line the caret is on
        }
        finally
        {
            _syncing = false;
        }
    }
}
