using System.IO;
using System.Windows;
using Spydate.Core.Project;

namespace Spydate.App.Views;

/// <summary>
/// Where a newly opened file should go when one is already open.
///
/// It asks after the file has been picked rather than before, so the question is about a file the
/// reader has already chosen rather than a mode they have to decide on first. A new tab is the
/// default because it is the answer that throws nothing away.
/// </summary>
public partial class OpenDestinationWindow : Window
{
    public OpenDestinationWindow(string incoming, string current, bool currentIsDebugging)
    {
        InitializeComponent();
        DarkChrome.Apply(this);
        Title = "Open file";
        QuestionText.Text = $"Where should {Path.GetFileName(incoming)} go?";
        CurrentText.Text = $"{current} is open in the current tab.";

        // Only a running process is worth warning about. Names and comments are written out as the
        // tab closes, so replacing cannot lose analysis work — saying it could would be teaching
        // the reader to fear a safe button.
        if (currentIsDebugging)
        {
            WarningText.Text = $"Replacing the current tab stops the process being debugged in {current}.";
            WarningText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => NewTabButton.Focus();
    }

    /// <summary>What was chosen, once the dialog has been accepted.</summary>
    public OpenDestination Choice { get; private set; } = OpenDestination.NewTab;

    /// <summary>Whether that answer should become the setting, so this stops being asked.</summary>
    public bool Remember => RememberBox.IsChecked == true;

    private void OnNewTab(object sender, RoutedEventArgs e)
    {
        Choice = OpenDestination.NewTab;
        DialogResult = true;
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        Choice = OpenDestination.ReplaceCurrent;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
