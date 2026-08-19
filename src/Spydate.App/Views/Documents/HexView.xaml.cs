using System.Windows;
using System.Windows.Controls;
using Spydate.App.ViewModels.Documents;

namespace Spydate.App.Views.Documents;

public partial class HexView : UserControl
{
    private HexDocumentViewModel? _vm;

    public HexView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ScrollRequested -= OnScrollRequested;
        }

        _vm = e.NewValue as HexDocumentViewModel;
        if (_vm is not null)
        {
            _vm.ScrollRequested += OnScrollRequested;
            if (_vm.SelectedRow is { } row)
            {
                Dispatcher.BeginInvoke(() => ScrollTo(row));
            }
        }
    }

    private void OnScrollRequested(object? sender, HexRow row) => Dispatcher.BeginInvoke(() => ScrollTo(row));

    private void ScrollTo(HexRow row)
    {
        try
        {
            Grid.UpdateLayout();
            Grid.ScrollIntoView(row);
            Grid.SelectedItem = row;
        }
        catch (InvalidOperationException)
        {
            // Grid not yet realised; the selection binding still applies.
        }
    }
}
