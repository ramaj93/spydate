using System.Collections;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>One column of a <see cref="RecordsDocumentViewModel"/>: its header, the row property it shows, and its width (0 = fill).</summary>
public sealed record RecordColumn(string Header, string Path, double Width = 100);

/// <summary>
/// A table of one format's own structures — an ELF's segments, section headers, dynamic entries, symbols. Each
/// format has tables no other has, and a hand-written view per table would be a dozen near-identical files; the
/// columns are declared with the rows instead, and one view draws any of them. Double-clicking a row runs
/// <see cref="Open"/>, which opens its bytes or its code.
/// </summary>
public sealed partial class RecordsDocumentViewModel : DocumentViewModel
{
    private readonly Action<object>? _open;

    public RecordsDocumentViewModel(
        string key,
        string title,
        SymbolRegular icon,
        string hint,
        IReadOnlyList<RecordColumn> columns,
        IList rows,
        Action<object>? open = null)
        : base(key, title, icon)
    {
        Hint = hint;
        Columns = columns;
        Rows = rows;
        _open = open;
    }

    /// <summary>A line above the table saying what a row is and what double-clicking one does.</summary>
    public string Hint { get; }

    public IReadOnlyList<RecordColumn> Columns { get; }

    public IList Rows { get; }

    public string Summary => $"{Rows.Count:N0} {(Rows.Count == 1 ? "row" : "rows")}";

    [RelayCommand]
    private void Open(object? row)
    {
        if (row is not null)
        {
            _open?.Invoke(row);
        }
    }
}
