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

        _viewModel.Assistant.Transcript.CollectionChanged += (_, e) => OnTranscriptChanged(e);

        // Not CollectionChanged: an answer streams into a line that is already in the list, so the
        // transcript grows without the collection changing at all.
        _viewModel.Assistant.Advancing += (_, _) => RedrawStreamingLine();
        _viewModel.Assistant.TurnFinished += (_, _) => FinishStreamingLine();

        // A restored conversation is loaded while the Output tab is the one showing, so the
        // transcript has never been laid out and there is nothing to scroll. Without this it opens
        // at the top of a conversation whose newest line is the point of opening it.
        AssistantTranscript.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                ScrollAssistantToEnd();
            }
        };

        AssistantTranscript.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnTranscriptScrolled));
        AssistantTranscript.GotKeyboardFocus += (_, _) => HoldTranscriptStill();
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
    // Assistant transcript
    //
    // The document is built here rather than bound, because a transcript is one flowing document —
    // that is what makes it selectable across messages and scrollable by content rather than by
    // item. The view model still owns the lines; this only draws them.
    // ------------------------------------------------------------------

    /// <summary>How many blocks the line being streamed into currently occupies, so it can be redrawn.</summary>
    private int _streamingBlocks;

    /// <summary>
    /// Whether new lines pull the view down with them. True until somebody scrolls up, and true
    /// again the moment they ask something — reading back through a long conversation while an
    /// answer arrives should not keep throwing them to the bottom of it.
    /// </summary>
    private bool _followTranscript = true;

    private void OnTranscriptScrolled(object sender, ScrollChangedEventArgs e)
    {
        // Only a scroll somebody made says anything about whether they want to follow along. The
        // view moving because the document grew underneath it says nothing, and reading it as an
        // instruction would turn every streamed token into a vote about where to look.
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            _followTranscript = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1;
        }
    }

    /// <summary>
    /// Keeps focus from moving the transcript.
    ///
    /// A read-only RichTextBox still has a caret; it sits at the start of the document, and taking
    /// keyboard focus brings it into view. So clicking the panel to select a line, or tabbing into
    /// it, threw the reader back to the first message of the conversation — the jump. The position
    /// is read before the caret is honoured and put back afterwards.
    /// </summary>
    private void HoldTranscriptStill()
    {
        double offset = AssistantTranscript.VerticalOffset;

        // Later than Render and Loaded, which is when the caret is brought into view, and earlier
        // than the Background pass that follows a growing answer down.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (Math.Abs(AssistantTranscript.VerticalOffset - offset) > 0.5)
            {
                AssistantTranscript.ScrollToVerticalOffset(offset);
            }
        }));
    }

    private void OnTranscriptChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            AssistantTranscript.Document.Blocks.Clear();
            _streamingBlocks = 0;
            _followTranscript = true;
            return;
        }

        foreach (ViewModels.AssistantLine line in e.NewItems?.OfType<ViewModels.AssistantLine>() ?? [])
        {
            // A new line means the previous one is finished, whatever it was.
            _streamingBlocks = 0;

            // Asking is a request to be shown the answer, wherever the reader had scrolled to.
            _followTranscript |= line.IsYou;
            Append(line);
        }

        ScrollAssistantToEnd();
    }

    /// <summary>The last line grew. Redraw just that line, as plain text while it is still arriving.</summary>
    private void RedrawStreamingLine()
    {
        if (_streamingBlocks == 0 || _viewModel.Assistant.Transcript.Count == 0)
        {
            return;
        }

        var line = _viewModel.Assistant.Transcript[^1];
        RemoveLastBlocks(_streamingBlocks);
        _streamingBlocks = Add(MarkdownFlow.Plain(line.Text), line);
        ScrollAssistantToEnd();
    }

    /// <summary>The turn ended: draw the answer properly, now that all of it is known.</summary>
    private void FinishStreamingLine()
    {
        if (_streamingBlocks == 0 || _viewModel.Assistant.Transcript.Count == 0)
        {
            return;
        }

        var line = _viewModel.Assistant.Transcript[^1];
        RemoveLastBlocks(_streamingBlocks);
        _streamingBlocks = 0;
        Append(line);
        ScrollAssistantToEnd();
    }

    private void Append(ViewModels.AssistantLine line)
    {
        int count = Add(Blocks(line), line);
        if (line.Kind == "assistant" && _viewModel.Assistant.IsBusy)
        {
            _streamingBlocks = count;
        }
    }

    private static IEnumerable<Block> Blocks(ViewModels.AssistantLine line) => line.Kind switch
    {
        "tool" => MarkdownFlow.Tool(line.Text),
        "note" => MarkdownFlow.Note(line.Text),
        "assistant" => MarkdownFlow.Render(line.Text),
        _ => MarkdownFlow.Plain(line.Text),
    };

    private int Add(IEnumerable<Block> blocks, ViewModels.AssistantLine line)
    {
        int count = 0;
        foreach (var block in blocks)
        {
            if (line.IsYou)
            {
                block.Foreground = (Brush)FindResource("Accent.Hover");
                block.FontWeight = FontWeights.SemiBold;
            }
            else if (line.IsProblem)
            {
                block.Foreground = (Brush)FindResource("Semantic.Error");
            }

            AssistantTranscript.Document.Blocks.Add(block);
            count++;
        }

        return count;
    }

    private void RemoveLastBlocks(int count)
    {
        for (int i = 0; i < count && AssistantTranscript.Document.Blocks.LastBlock is { } last; i++)
        {
            AssistantTranscript.Document.Blocks.Remove(last);
        }
    }

    /// <summary>
    /// After layout, not during it. Blocks have just been added and the document has not been
    /// measured yet, so scrolling now scrolls to the end of what was there a moment ago — which for
    /// a restored conversation means sitting at the top of it, and for a streaming answer means
    /// falling steadily further behind.
    ///
    /// Only while the end is what is being watched. Following an answer down is right; hauling
    /// somebody back down from the middle of a conversation they are reading is not.
    /// </summary>
    private void ScrollAssistantToEnd(int attempts = 12)
    {
        if (!_followTranscript || attempts <= 0)
        {
            return;
        }

        double was = AssistantTranscript.ExtentHeight;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_followTranscript)
            {
                return;
            }

            AssistantTranscript.ScrollToEnd();

            // Again if the document grew under that scroll, or if it landed short of the bottom:
            // a FlowDocument of several hundred messages is not measured in one pass, so the first
            // scroll reaches the end of what had been laid out rather than the end of the text.
            // Each retry is queued behind the layout it is waiting for.
            bool grew = AssistantTranscript.ExtentHeight > was;
            bool shortOfIt = AssistantTranscript.VerticalOffset
                             < AssistantTranscript.ExtentHeight - AssistantTranscript.ViewportHeight - 1;
            if (grew || shortOfIt)
            {
                ScrollAssistantToEnd(attempts - 1);
            }
        }));
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
