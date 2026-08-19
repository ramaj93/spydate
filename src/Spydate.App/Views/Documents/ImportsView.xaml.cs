using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using Spydate.App.ViewModels.Documents;

namespace Spydate.App.Views.Documents;

public partial class ImportsView : UserControl
{
    public ImportsView()
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
            : o => o is ImportRow r && (r.Module.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Function.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }
}
