using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.Core.Project;
using Spydate.Disassembly;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>
/// One annotation as the list shows it: where it is, a line summarising it for the table, and the
/// parts spelled out for the pane beside it. Flattened here rather than read from the store by the
/// view, so the list is a snapshot that cannot change underneath a selection.
/// </summary>
public sealed record AnnotationRow(
    ulong Va,
    string Address,
    string Content,
    string? Name,
    string? Comment,
    IReadOnlyList<PropertyRow> Locals,
    string Source,
    string Modified,
    string Where)
{
    public bool HasName => !string.IsNullOrEmpty(Name);

    public bool HasComment => !string.IsNullOrEmpty(Comment);

    public bool HasLocals => Locals.Count > 0;
}

/// <summary>
/// Everything the reader has said about this binary, in one place: the renames, the comments and the
/// named stack slots, listed by address with the one under the cursor spelled out beside them.
///
/// It exists because annotations are the reader's own work and were, until now, only visible where
/// they were made — scattered through whichever listings happened to be open. A session's worth of
/// naming is a thing to review, and reviewing it means seeing it as a list.
/// </summary>
public sealed partial class AnnotationsDocumentViewModel : DocumentViewModel
{
    private readonly BinaryAnalysis _analysis;
    private readonly Action<ulong> _open;

    public AnnotationsDocumentViewModel(BinaryAnalysis analysis, Action<ulong> open)
        : base("annotations", "Annotations", SymbolRegular.Comment24)
    {
        _analysis = analysis;
        _open = open;
    }

    public ObservableCollection<AnnotationRow> Rows { get; } = new();

    /// <summary>
    /// The row being read. Setting it points the Xrefs panel at the address, the way selecting a
    /// string does — what refers to the thing you just named is usually the next question.
    /// </summary>
    [ObservableProperty]
    private AnnotationRow? _selectedRow;

    partial void OnSelectedRowChanged(AnnotationRow? value) => Address = value?.Va;

    [ObservableProperty]
    private string _summary = string.Empty;

    public override Task LoadAsync(CancellationToken cancellationToken)
    {
        Refresh();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rebuilds the list from the store, keeping the reader on the row they were on. Kept by address
    /// rather than by row, since the row is a snapshot and a re-read makes a new one — editing the
    /// comment you are looking at should not throw you back to the top of the list.
    /// </summary>
    public void Refresh()
    {
        ulong? was = SelectedRow?.Va;

        Rows.Clear();
        foreach (var (va, annotation) in _analysis.Annotations.Snapshot())
        {
            Rows.Add(Build(va, annotation));
        }

        SelectedRow = was is { } keep
            ? Rows.FirstOrDefault(r => r.Va == keep) ?? Rows.FirstOrDefault()
            : Rows.FirstOrDefault();

        Summary = Rows.Count switch
        {
            0 => "no names or comments yet",
            1 => "1 annotation",
            _ => $"{Rows.Count} annotations",
        };
    }

    private AnnotationRow Build(ulong va, Annotation annotation)
    {
        var locals = annotation.Locals is { Count: > 0 } named
            ? named.OrderBy(l => l.Key, StringComparer.Ordinal)
                   .Select(l => new PropertyRow(l.Key, l.Value))
                   .ToList()
            : (IReadOnlyList<PropertyRow>)Array.Empty<PropertyRow>();

        // The function it sits in, which is the context a bare address does not give. An address
        // outside any discovered function simply has none to name.
        string where = _analysis.FunctionContaining(va)?.Name ?? string.Empty;

        return new AnnotationRow(
            va,
            $"0x{va:X}",
            Describe(annotation, locals.Count),
            annotation.Name,
            annotation.Comment,
            locals,
            annotation.Source == AnnotationSource.Agent ? "agent" : "you",
            annotation.Modified is { } when ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : string.Empty,
            where);
    }

    /// <summary>
    /// One line for the table. A name and a comment are different kinds of thing, so they are joined
    /// rather than one winning; slot names have no line of their own and are counted at the end.
    /// </summary>
    private static string Describe(Annotation annotation, int locals)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(annotation.Name))
        {
            parts.Add(annotation.Name);
        }

        if (!string.IsNullOrEmpty(annotation.Comment))
        {
            parts.Add(annotation.Comment);
        }

        if (locals > 0)
        {
            parts.Add(locals == 1 ? "1 local name" : $"{locals} local names");
        }

        return string.Join("  —  ", parts);
    }

    /// <summary>Opens the code the annotation is on — the list's double-click.</summary>
    [RelayCommand]
    private void Open(AnnotationRow? row)
    {
        if (row is not null)
        {
            _open(row.Va);
        }
    }
}
