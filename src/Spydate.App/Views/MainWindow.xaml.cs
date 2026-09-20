using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.ViewModels;
using Wpf.Ui.Controls;

namespace Spydate.App.Views;

public partial class MainWindow : FluentWindow
{
    private const double DefaultExplorerWidth = 280;
    private const double DefaultOutputHeight = 170;

    private readonly MainViewModel _viewModel;
    private double _lastExplorerWidth = DefaultExplorerWidth;
    private double _lastOutputHeight = DefaultOutputHeight;
    private bool _wasDebugging;

    /// <summary>The active file's debugger, so it can be let go of when another tab comes forward.</summary>
    private ViewModels.DebuggerViewModel? _watched;

    private void WatchActiveDebugger()
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnDebuggerStateChanged;
        }

        _watched = _viewModel.Active?.Debugger;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnDebuggerStateChanged;
        }

        // Taken from the tab now in front rather than left as the last one's, so arriving on a tab
        // whose process is already up does not read as a run that has just begun.
        _wasDebugging = _watched?.IsDebugging ?? false;
    }

    /// <summary>
    /// Brings the Debug pane forward when a run begins — that is where the run reports itself, and
    /// where its registers, stack and threads are. Only on the start transition, so the reader who
    /// steps over to another tab mid-session is not yanked back at the next stop.
    /// </summary>
    private void OnDebuggerStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.DebuggerViewModel.State) || _watched is null)
        {
            return;
        }

        bool debugging = _watched.IsDebugging;
        if (debugging && !_wasDebugging)
        {
            DebugTab.IsSelected = true;
        }

        _wasDebugging = debugging;
    }

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        FocusGotoCommand = new RelayCommand(() =>
        {
            GotoBox.Focus();
            GotoBox.SelectAll();
        });
        InitializeComponent();
        _viewModel.Output.CollectionChanged += (_, _) => ScrollOutputToEnd();

        // Each file has its own debugger, so this follows whichever tab is in front rather than
        // being wired once to the window's. The assistant needs nothing here any more: its transcript
        // is a bound list, so a tab switch is the ItemsSource changing under it, and it follows its
        // own tail and keeps its own selection.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.Active))
            {
                WatchActiveDebugger();
            }
        };
        WatchActiveDebugger();
    }

    /// <summary>Ctrl+G: focus the go-to box.</summary>
    public ICommand FocusGotoCommand { get; }

    // ------------------------------------------------------------------
    // Panel visibility (View menu + the ✕ on each tool window)
    // ------------------------------------------------------------------

    public static readonly DependencyProperty IsExplorerVisibleProperty = DependencyProperty.Register(
        nameof(IsExplorerVisible), typeof(bool), typeof(MainWindow),
        new PropertyMetadata(true, OnIsExplorerVisibleChanged));

    public static readonly DependencyProperty IsOutputVisibleProperty = DependencyProperty.Register(
        nameof(IsOutputVisible), typeof(bool), typeof(MainWindow),
        new PropertyMetadata(true, OnIsOutputVisibleChanged));

    public bool IsExplorerVisible
    {
        get => (bool)GetValue(IsExplorerVisibleProperty);
        set => SetValue(IsExplorerVisibleProperty, value);
    }

    public bool IsOutputVisible
    {
        get => (bool)GetValue(IsOutputVisibleProperty);
        set => SetValue(IsOutputVisibleProperty, value);
    }

    private static void OnIsExplorerVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MainWindow)d).ApplyExplorerVisibility((bool)e.NewValue);

    private static void OnIsOutputVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MainWindow)d).ApplyOutputVisibility((bool)e.NewValue);

    private void ApplyExplorerVisibility(bool visible)
    {
        if (visible)
        {
            ExplorerColumn.Width = new GridLength(_lastExplorerWidth <= 0 ? DefaultExplorerWidth : _lastExplorerWidth);
            ExplorerPanel.Visibility = Visibility.Visible;
            ExplorerSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            _lastExplorerWidth = ExplorerColumn.ActualWidth > 0 ? ExplorerColumn.ActualWidth : _lastExplorerWidth;
            ExplorerPanel.Visibility = Visibility.Collapsed;
            ExplorerSplitter.Visibility = Visibility.Collapsed;
            ExplorerColumn.Width = new GridLength(0);
        }
    }

    private void ApplyOutputVisibility(bool visible)
    {
        if (visible)
        {
            OutputRow.Height = new GridLength(_lastOutputHeight <= 0 ? DefaultOutputHeight : _lastOutputHeight);
            OutputPanel.Visibility = Visibility.Visible;
            OutputSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            _lastOutputHeight = OutputRow.ActualHeight > 0 ? OutputRow.ActualHeight : _lastOutputHeight;
            OutputPanel.Visibility = Visibility.Collapsed;
            OutputSplitter.Visibility = Visibility.Collapsed;
            OutputRow.Height = new GridLength(0);
        }
    }

    /// <summary>
    /// Enter asks; Shift+Enter starts a new line, for a question worth more than one.
    ///
    /// Preview, not KeyDown. A TextBox handles Enter in a class handler, and class handlers run
    /// before instance ones - so handling it on the way back up meant the newline had already been
    /// inserted, and Enter both broke the line and sent the question. Tunnelling gets there first.
    ///
    /// IsDefault on the button would fire for any Enter anywhere in the window, which is not what a
    /// text box wants.
    /// </summary>
    private void OnAssistantKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        if (DataContext is ViewModels.MainViewModel { Active.Assistant: { } assistant } && assistant.AskCommand.CanExecute(null))
        {
            assistant.AskCommand.Execute(null);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Selects the row a right-click landed on, before its context menu opens.
    ///
    /// A DataGrid does not select on right-click, so "Set value…" — whose command parameter is the
    /// grid's SelectedItem — would write into whatever was left-clicked last, or do nothing when
    /// nothing was. Selecting the row under the pointer first makes the menu act on the row aimed at.
    /// </summary>
    private void SelectRowOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement((System.Windows.Controls.DataGrid)sender, source) is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }

    private void OnXrefDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (XrefGrid.SelectedItem is XrefRow row)
        {
            _viewModel.Active?.GoToXrefCommand.Execute(row);
        }
    }

    /// <summary>Double-clicking a breakpoint in the pane opens the code it is in.</summary>
    private void OpenBreakpointRow(object sender, MouseButtonEventArgs e)
    {
        if (BreakpointsGrid.SelectedItem is BreakpointRow row)
        {
            _viewModel.Active?.Debugger.GoToBreakpointCommand.Execute(row);
        }
    }

    /// <summary>
    /// Double-clicking the instruction pointer goes to the line it names. Only that register: the
    /// others hold whatever they hold, and sending the listing to wherever rsp happens to point
    /// would be a navigation that means nothing.
    /// </summary>
    private void OpenRegisterRow(object sender, MouseButtonEventArgs e)
    {
        if (RegisterGrid.SelectedItem is RegisterRow { Name: "rip" or "eip" })
        {
            _viewModel.Active?.Debugger.GoToExecutionCommand.Execute(null);
        }
    }

    private void OnHideExplorerClick(object sender, RoutedEventArgs e) => IsExplorerVisible = false;

    private void OnHideOutputClick(object sender, RoutedEventArgs e) => IsOutputVisible = false;

    private void ScrollOutputToEnd()
    {
        if (OutputList.Items.Count > 0)
        {
            OutputList.ScrollIntoView(OutputList.Items[^1]);
        }
    }

    // ------------------------------------------------------------------
    // Find in the conversation
    //
    // The find bar over the transcript, styled and driven like the editors' search panel. The search
    // itself belongs to the ChatTranscript — it looks through the lines' flat text and paints the
    // match through its own selection adorner — so this is only the bar: open it, feed it the query,
    // show "n of m". A question bubble is searched the same as an answer, because both are just lines.
    // ------------------------------------------------------------------

    /// <summary>
    /// Ctrl+F opens the find bar over the conversation, the way it opens the search panel over an
    /// editor, and F3 / Shift+F3 cycle once it is open. On the panel rather than the transcript alone,
    /// so it works whether focus is in the transcript, the question box or anywhere between.
    /// </summary>
    private void OnAssistantPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowAssistantFind();
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && AssistantFindBar.Visibility == Visibility.Visible)
        {
            FindStep(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0, fromMatchEnd: true);
            e.Handled = true;
        }
    }

    private void OnAssistantFindOpen(object sender, RoutedEventArgs e) => ShowAssistantFind();

    private void ShowAssistantFind()
    {
        AssistantFindBar.Visibility = Visibility.Visible;

        // Seed with the selected text, as an editor's search does, so "find this" is one gesture.
        if (AssistantTranscript.SelectedText is { Length: > 0 and < 80 } selected)
        {
            AssistantFindBox.Text = selected;
        }

        AssistantFindBox.Focus();
        AssistantFindBox.SelectAll();
    }

    private void HideAssistantFind()
    {
        AssistantTranscript.ClearMatch();
        AssistantFindBar.Visibility = Visibility.Collapsed;
        AssistantFindStatus.Text = string.Empty;
        AssistantTranscript.Focus();
    }

    private void OnAssistantFindClose(object sender, RoutedEventArgs e) => HideAssistantFind();

    private void OnAssistantFindNext(object sender, RoutedEventArgs e) => FindStep(forward: true, fromMatchEnd: true);

    private void OnAssistantFindPrevious(object sender, RoutedEventArgs e) => FindStep(forward: false, fromMatchEnd: false);

    private void OnAssistantFindOptionChanged(object sender, RoutedEventArgs e) => FindStep(forward: true, fromMatchEnd: false);

    private void OnAssistantFindTextChanged(object sender, TextChangedEventArgs e)
        // As the query grows, search from where the current match starts rather than after it, so the
        // selection settles on the first hit for what has been typed instead of skipping past it.
        => FindStep(forward: true, fromMatchEnd: false);

    private void OnAssistantFindKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                FindStep(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0, fromMatchEnd: true);
                e.Handled = true;
                break;
            case Key.Escape:
                HideAssistantFind();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Runs the current query against the transcript and writes "n of m" or "No matches".</summary>
    private void FindStep(bool forward, bool fromMatchEnd)
    {
        string query = AssistantFindBox.Text;
        if (query.Length == 0)
        {
            AssistantTranscript.ClearMatch();
            AssistantFindStatus.Text = string.Empty;
            return;
        }

        var (index, count) = AssistantTranscript.Find(query, AssistantFindCase.IsChecked == true, forward, fromMatchEnd);
        AssistantFindStatus.Text = count == 0 ? "No matches" : $"{index} of {count}";
    }

    // ------------------------------------------------------------------
    // Menu / window plumbing
    // ------------------------------------------------------------------

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Annotations are the user's own work, so they are written out rather than dropped on exit —
    /// every open file's, not only the tab in front.
    ///
    /// Then every file is closed, which is what stops the processes. Windows would terminate a
    /// debuggee when its debugger exits anyway, but leaving that to the OS means the moment an
    /// untrusted binary stops running depends on how the window went away, and closing them here
    /// makes it the same either way.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.SaveAnnotationsIfDirty();
        _viewModel.CloseAllFilesCommand.Execute(null);
        base.OnClosing(e);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        System.Windows.MessageBox.Show(
            this,
            $"""
            Spydate {version?.ToMajorMinorString() ?? "0.1"} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, .NET {Environment.Version})

            Windows PE disassembler and decompiler.

            Third-party components (all MIT):
              Iced — x86/x64 decoder
              ICSharpCode.Decompiler — C#/IL decompilation
              AvalonEdit — code editor
              WPF-UI — window chrome and icons
              CommunityToolkit.Mvvm
            """,
            "About Spydate",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private void OnExplorerSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_viewModel.Active is { } file)
        {
            file.SelectedNode = e.NewValue as ExplorerNodeViewModel;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            await _viewModel.OpenPathAsync(files[0]);
        }
    }
}

internal static class VersionExtensions
{
    public static string ToMajorMinorString(this Version version) => $"v{version.Major}.{version.Minor}";
}
