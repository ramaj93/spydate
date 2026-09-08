using System.Windows;

namespace Spydate.App.Views;

/// <summary>
/// What to run and how: the host, its arguments and where to start it.
///
/// A dialog because it is set once for a binary and then left alone, while the panel it came out of
/// is about what changes every time execution stops. It edits the debugger's own properties, which
/// write themselves through to where they are remembered — so there is nothing here to accept, and
/// no way to leave it half-applied.
/// </summary>
public partial class RunConfigWindow : Window
{
    public RunConfigWindow(object debugger)
    {
        InitializeComponent();
        DataContext = debugger;
    }
}
