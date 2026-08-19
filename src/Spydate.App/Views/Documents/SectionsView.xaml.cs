using System.Windows.Controls;
using System.Windows.Input;
using Spydate.App.ViewModels.Documents;

namespace Spydate.App.Views.Documents;

public partial class SectionsView : UserControl
{
    public SectionsView()
    {
        InitializeComponent();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SectionsDocumentViewModel vm && Grid.SelectedItem is SectionRow row)
        {
            vm.OpenInHexCommand.Execute(row);
        }
    }
}
