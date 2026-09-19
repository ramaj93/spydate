using System.Windows;
using Spydate.Core.Project;

namespace Spydate.App.Views;

/// <summary>
/// The per-user settings, and the only place in the window that writes them.
///
/// Until this existed the store was readable and not writable, which is why the prompt it can
/// suppress shipped without its own "don't ask again": a checkbox whose window cannot untick it
/// leaves the reader hand-editing JSON.
/// </summary>
public partial class PreferencesWindow : Window
{
    public PreferencesWindow(Preferences current)
    {
        ArgumentNullException.ThrowIfNull(current);

        InitializeComponent();
        DarkChrome.Apply(this);

        AskEachTime.IsChecked = current.OpenDestination == OpenDestination.Ask;
        AlwaysNewTab.IsChecked = current.OpenDestination == OpenDestination.NewTab;
        AlwaysReplace.IsChecked = current.OpenDestination == OpenDestination.ReplaceCurrent;

        ModuleInCurrentTab.IsChecked = current.ForeignModule == ForeignModuleView.CurrentTab;
        ModuleInOwnTab.IsChecked = current.ForeignModule == ForeignModuleView.OwnTab;

        Result = current;
    }

    /// <summary>What was chosen, once the dialog has been accepted.</summary>
    public Preferences Result { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = new Preferences
        {
            OpenDestination = AlwaysNewTab.IsChecked == true ? OpenDestination.NewTab
                : AlwaysReplace.IsChecked == true ? OpenDestination.ReplaceCurrent
                : OpenDestination.Ask,
            ForeignModule = ModuleInOwnTab.IsChecked == true
                ? ForeignModuleView.OwnTab
                : ForeignModuleView.CurrentTab,
        };

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
