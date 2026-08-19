using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Spydate.App.ViewModels.Documents;

namespace Spydate.App.Views.Documents;

public partial class StringsView : UserControl
{
    private INotifyPropertyChanged? _hooked;

    public StringsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        // The scan can finish before this view exists, so filter on load as well as on notification.
        Loaded += (_, _) => ScheduleFilter();
        HookViewModel();
    }

    /// <summary>Rows arrive after an off-thread scan, so the filter has to be re-applied then too.</summary>
    private void HookViewModel()
    {
        if (_hooked is not null)
        {
            _hooked.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _hooked = DataContext as INotifyPropertyChanged;
        if (_hooked is not null)
        {
            _hooked.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StringsDocumentViewModel.Rows))
        {
            ScheduleFilter();
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    /// <summary>
    /// Runs the filter after the pending binding update. This handler and the grid's ItemsSource
    /// binding both listen for Rows, and filtering the previous list has no effect.
    /// </summary>
    private void ScheduleFilter() => Dispatcher.BeginInvoke(new Action(ApplyFilter), DispatcherPriority.Background);

    private void ApplyFilter()
    {
        var view = CollectionViewSource.GetDefaultView(Grid.ItemsSource);
        if (view is null)
        {
            return;
        }

        string filter = FilterBox.Text.Trim();
        bool includeCode = IncludeCode.IsChecked == true;
        bool referencedOnly = ReferencedOnly.IsChecked == true;

        // Strings inside executable sections are mostly instruction bytes that happen to be
        // printable, so they stay hidden until asked for.
        view.Filter = o => o is StringRow r
                           && (includeCode || !r.InCodeSection)
                           && (!referencedOnly || r.Refs > 0)
                           && (filter.Length == 0
                               || r.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
                               || r.Section.Contains(filter, StringComparison.OrdinalIgnoreCase)
                               || r.Va.Contains(filter, StringComparison.OrdinalIgnoreCase)
                               || r.Offset.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is StringsDocumentViewModel vm && Grid.SelectedItem is StringRow row)
        {
            vm.OpenHexCommand.Execute(row);
        }
    }
}
