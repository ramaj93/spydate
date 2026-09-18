using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Spydate.App.ViewModels.Documents;

namespace Spydate.App.Views.Documents;

public partial class AnnotationsView : UserControl
{
    public AnnotationsView()
    {
        InitializeComponent();
    }

    private void OnFilterChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(Grid.ItemsSource);
        if (view is null)
        {
            return;
        }

        string filter = FilterBox.Text.Trim();
        view.Filter = filter.Length == 0
            ? null
            : o => o is AnnotationRow r
                   && (r.Content.Contains(filter, StringComparison.OrdinalIgnoreCase)
                       || r.Address.Contains(filter, StringComparison.OrdinalIgnoreCase)
                       || r.Where.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is AnnotationsDocumentViewModel vm && Grid.SelectedItem is AnnotationRow row)
        {
            vm.OpenCommand.Execute(row);
        }
    }
}
