using System.Windows.Controls;
using System.Windows.Input;
using Spydate.App.ViewModels.Documents;

namespace Spydate.App.Views.Documents;

public partial class ResourcesView : UserControl
{
    public ResourcesView()
    {
        InitializeComponent();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ResourcesDocumentViewModel vm && Grid.SelectedItem is ResourceRow row)
        {
            vm.OpenHexCommand.Execute(row);
        }
    }
}
