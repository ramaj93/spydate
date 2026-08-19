using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Spydate.App.ViewModels.Documents;

namespace Spydate.App.Views.Documents;

public partial class ExportsView : UserControl
{
    public ExportsView()
    {
        InitializeComponent();
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
            : o => o is ExportRow r && (r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Forwarder.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ExportsDocumentViewModel vm && Grid.SelectedItem is ExportRow row)
        {
            vm.OpenDisassemblyCommand.Execute(row);
        }
    }
}
