using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
using Spydate.Core.Graph;
using Spydate.Disassembly;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>
/// One function's control flow as a picture: a box per basic block, laid out with the entry at the top
/// and control running downwards. Reading a listing tells you what the instructions are; this tells you
/// the shape — where the loops are, which branch rejoins where, which block nothing reaches.
/// </summary>
public sealed partial class GraphDocumentViewModel : DocumentViewModel, ICaretContext
{
    /// <summary>
    /// Past this many blocks the drawing is longer than any screen and slower to read than the listing.
    /// The document says so rather than spending a second producing something unusable.
    /// </summary>
    public const int TooManyBlocks = 600;

    private readonly ulong _entryVa;
    private readonly Func<CancellationToken, Function> _current;
    private readonly IFileDialogService? _dialogs;

    public GraphDocumentViewModel(ulong entryVa, string title, Func<CancellationToken, Function> current, IFileDialogService? dialogs = null)
        : base($"graph:{entryVa:X}", title, SymbolRegular.Flowchart24)
    {
        _entryVa = entryVa;
        _current = current;
        _dialogs = dialogs;
        Address = entryVa;
    }

    public static GraphDocumentViewModel For(BinaryAnalysis analysis, Function function, Func<Function> current, IFileDialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(current);

        return new GraphDocumentViewModel(function.EntryVa, $"{function.Name} (graph)", _ => current(), dialogs);
    }

    [ObservableProperty]
    private FunctionGraph? _graph;

    /// <summary>Why there is no picture, when there is none.</summary>
    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private double _zoom = 1.0;

    /// <summary>Start VA of the block the user picked, which is what the rest of the app follows.</summary>
    [ObservableProperty]
    private ulong? _selectedVa;

    /// <summary>The caret is the selected block; with no word under it, naming acts on the address.</summary>
    public ulong? CaretAddress => SelectedVa;

    public string? CaretWord => null;

    public ulong? OwningFunctionVa => _entryVa;

    public bool HasGraph => Graph is not null;

    partial void OnGraphChanged(FunctionGraph? value) => OnPropertyChanged(nameof(HasGraph));

    partial void OnSelectedVaChanged(ulong? value)
    {
        // The Xrefs panel follows the block being looked at, the same way it follows a line of listing.
        Address = value ?? _entryVa;
        OnPropertyChanged(nameof(CaretAddress));
    }

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var function = await Task.Run(() => _current(cancellationToken), cancellationToken).ConfigureAwait(true);

        if (function.Blocks.Count == 0)
        {
            Graph = null;
            Problem = "This function has no blocks to draw.";
            return;
        }

        if (function.Blocks.Count > TooManyBlocks)
        {
            Graph = null;
            Problem = $"{function.Blocks.Count} blocks is more than a picture can usefully show. The listing is the better way to read this one.";
            Summary = $"{function.Blocks.Count} blocks";
            return;
        }

        var graph = await Task.Run(() => FunctionGraphs.Build(function), cancellationToken).ConfigureAwait(true);

        Problem = null;
        Graph = graph;
        SelectedVa = function.EntryVa;
        Summary = $"{graph.Blocks.Count} blocks · {graph.Layout.Edges.Count} edges · "
                  + $"{graph.Layout.Edges.Count(e => e.Kind == GraphEdgeKind.Back)} loop";
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(Zoom * 1.2, 3.0);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(Zoom / 1.2, 0.1);

    [RelayCommand]
    private void ZoomReset() => Zoom = 1.0;

    /// <summary>
    /// Writes the drawing to SVG. Worth having beyond export: it is the same geometry the window draws,
    /// in a form that can be looked at anywhere, which is how the layout was checked in the first place.
    /// </summary>
    [RelayCommand]
    private void ExportSvg()
    {
        if (Graph is not { } graph || _dialogs is null)
        {
            return;
        }

        if (_dialogs.SaveFile("Export control-flow graph", "SVG image (*.svg)|*.svg", $"{SafeName(Title)}.svg") is not { } path)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, graph.ToSvg());
            StatusMessage = $"Written to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not write it: {ex.Message}";
        }
    }

    private static string SafeName(string title)
    {
        var cleaned = title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray();
        string name = new string(cleaned).Trim().Replace(' ', '_');
        return name.Length == 0 ? "graph" : name;
    }
}
