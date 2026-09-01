using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    /// Enter asks; Shift+Enter starts a new line, for a question worth more than one. IsDefault on
    /// the Ask button would fire for any Enter anywhere in the window, which is not what a text box
    /// wants.
    /// </summary>
    private void OnAssistantKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        if (DataContext is ViewModels.MainViewModel { Assistant: { } assistant } && assistant.AskCommand.CanExecute(null))
        {
            assistant.AskCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void OnXrefDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (XrefGrid.SelectedItem is XrefRow row)
        {
            _viewModel.GoToXrefCommand.Execute(row);
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
    // Menu / window plumbing
    // ------------------------------------------------------------------

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Annotations are the user's own work, so they are written out rather than dropped on exit.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.SaveAnnotationsIfDirty();
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
        _viewModel.SelectedNode = e.NewValue as ExplorerNodeViewModel;
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
