using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Spydate.App.ViewModels.Documents;

namespace Spydate.App.Views.Documents;

/// <summary>Draws any <see cref="RecordsDocumentViewModel"/>: the columns come from the document, not from this file.</summary>
public partial class RecordsView : UserControl
{
    private PropertyInfo[] _filtered = [];

    public RecordsView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Grid.Columns.Clear();
        FilterBox.Text = string.Empty;
        if (DataContext is not RecordsDocumentViewModel vm)
        {
            return;
        }

        foreach (var column in vm.Columns)
        {
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Header,
                Binding = new Binding(column.Path),
                Width = column.Width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(column.Width),
            });
        }

        // The filter matches the text of every shown column, read once per row type.
        var type = vm.Rows.Count > 0 ? vm.Rows[0]!.GetType() : null;
        _filtered = type is null
            ? []
            : vm.Columns.Select(c => type.GetProperty(c.Path)).OfType<PropertyInfo>().ToArray();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        string filter = FilterBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(Grid.ItemsSource);
        if (view is null)
        {
            return;
        }

        view.Filter = filter.Length == 0
            ? null
            : o => _filtered.Any(p => p.GetValue(o)?.ToString()?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is RecordsDocumentViewModel vm && Grid.SelectedItem is { } row)
        {
            vm.OpenCommand.Execute(row);
        }
    }
}
