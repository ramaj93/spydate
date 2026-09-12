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
        _viewModel.Assistant.Discarding += (_, _) => DiscardStreamingLine();
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

        // What the reader does with the wheel, the keys and the scrollbar is the only evidence of
        // whether they still want the end followed. Preview, because the scrollbar lives inside this
        // control's own template and a tunnelling event reaches here before it reaches the bar.
        AssistantTranscript.PreviewMouseWheel += (_, _) => Gesture();
        AssistantTranscript.PreviewKeyDown += (_, e) =>
        {
            if (Scrolls(e.Key))
            {
                Gesture();
            }
        };

        AssistantTranscript.PreviewMouseLeftButtonDown += (_, _) => _dragging = true;
        AssistantTranscript.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _dragging = false;
            Gesture();
        };

        // A drag that ends off the control never sees its button-up here, and a _dragging left true
        // would treat every later move as the reader's doing.
        AssistantTranscript.LostMouseCapture += (_, _) => _dragging = false;

        // The window coming forward restores focus inside itself, which brings a caret into view and
        // moves the transcript for reasons that have nothing to do with the reader. Alt-tabbing away
        // and back while an answer was arriving was the commonest way to see it jump.
        Activated += (_, _) => HoldTranscriptStill();
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

    /// <summary>The paragraph an answer is streaming into, or null when nothing is arriving.</summary>
    private System.Windows.Documents.Paragraph? _streamingParagraph;

    /// <summary>How much of that line is already drawn, so that only the rest of it is added.</summary>
    private int _drawn;

    /// <summary>Whether a scroll-to-end is already queued, so a turn queues one pass and not one per token.</summary>
    private bool _scrollPending;

    /// <summary>
    /// How many scrolls this class has asked for and not yet seen land.
    ///
    /// ScrollChanged does not say who caused it, and the only thing that should stop the view
    /// following an answer is the reader scrolling away from the end themselves. A move this code
    /// made — following the answer down, or putting the view back after focus moved the caret — is
    /// not a vote about where to look, and counting it as one turned every focus change during a
    /// turn into a silent decision to stop following, which is the jump.
    ///
    /// A count rather than a flag because these overlap, and it is cleared at Input priority rather
    /// than straight after the call: the layout pass that raises ScrollChanged runs at Render, which
    /// is higher, so a flag reset inline would already be down by the time the event arrived.
    /// </summary>
    private int _ourScrolls;

    private void StopStreaming()
    {
        _streamingParagraph = null;
        _drawn = 0;
    }

    /// <summary>Moves the view, and has the move ignored as evidence of what the reader wants.</summary>
    private void Scrolling(Action move)
    {
        _ourScrolls++;
        move();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _ourScrolls--));
    }

    /// <summary>
    /// Whether new lines pull the view down with them. True until somebody scrolls up, and true
    /// again the moment they ask something — reading back through a long conversation while an
    /// answer arrives should not keep throwing them to the bottom of it.
    /// </summary>
    private bool _followTranscript = true;

    /// <summary>How many scroll gestures the reader has made that have not yet been accounted for.</summary>
    private int _gesture;

    /// <summary>Whether the mouse is down on the transcript, which covers a drag of the scrollbar.</summary>
    private bool _dragging;

    /// <summary>The keys that move the view. Typing does not, since this is read-only.</summary>
    private static bool Scrolls(Key key) =>
        key is Key.PageUp or Key.PageDown or Key.Up or Key.Down or Key.Home or Key.End;

    /// <summary>
    /// Notes that the reader just did something that moves the view.
    ///
    /// Cleared at Background priority, which is below the Render pass that raises ScrollChanged, so
    /// the gesture is still counted when the scroll it caused comes back.
    /// </summary>
    private void Gesture()
    {
        _gesture++;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _gesture--));
    }

    private bool AtBottom() =>
        AssistantTranscript.VerticalOffset
            >= AssistantTranscript.ExtentHeight - AssistantTranscript.ViewportHeight - 4;

    /// <summary>
    /// Decides whether the reader still wants the end followed.
    ///
    /// Only a gesture they made counts. This used to infer it from the event instead — if the extent
    /// and viewport had not changed, a person must have scrolled — and that reasoning is wrong for a
    /// FlowDocument, which measures lazily: scrolling through text that has not been laid out yet
    /// changes the extent as you go. So a genuine scroll upwards was read as layout noise and
    /// discarded, following stayed on, and the next thing to call ScrollAssistantToEnd — a window
    /// being activated was enough — threw the reader back to the bottom of a conversation they were
    /// in the middle of reading. That needed no streaming at all, which is why it happened when
    /// nothing was arriving.
    /// </summary>
    private void OnTranscriptScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_ourScrolls > 0)
        {
            return;
        }

        if (_gesture > 0 || _dragging)
        {
            _followTranscript = AtBottom();
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
        bool follow = _followTranscript;

        // Counted from here rather than around the correction below, because the scroll being
        // corrected is the caret being brought into view, and that happens in between the two.
        _ourScrolls++;

        // Later than Render and Loaded, which is when the caret is brought into view, and earlier
        // than the Background pass that follows a growing answer down.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // Where to look is already decided when the end is being followed, and this offset was
            // measured before the last tokens arrived. Putting it back would haul the view up and
            // let the next pass drop it to the bottom again - the flicker this is meant to prevent.
            if (follow)
            {
                ScrollAssistantToEnd();
            }
            else if (Math.Abs(AssistantTranscript.VerticalOffset - offset) > 0.5)
            {
                AssistantTranscript.ScrollToVerticalOffset(offset);
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _ourScrolls--));
        }));
    }

    private void OnTranscriptChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            AssistantTranscript.Document.Blocks.Clear();
            StopStreaming();
            _followTranscript = true;
            return;
        }

        foreach (ViewModels.AssistantLine line in e.NewItems?.OfType<ViewModels.AssistantLine>() ?? [])
        {
            // A new line means the previous one is finished, whatever it was.
            StopStreaming();

            // Asking is a request to be shown the answer, wherever the reader had scrolled to.
            _followTranscript |= line.IsYou;
            Append(line);
        }

        ScrollAssistantToEnd();
    }

    /// <summary>
    /// The last line grew. Add what is new to it, as plain text while it is still arriving.
    ///
    /// Only the new characters. This used to redraw the whole line on every token — throw away its
    /// blocks, re-split the entire answer so far, and rebuild every Run and LineBreak in it — which
    /// is work proportional to the answer's length, done once per token, so the cost of a turn grew
    /// as the square of its length. A long code listing is exactly the case where that gets large,
    /// and with a full document re-layout behind each pass the window stopped responding.
    /// </summary>
    private void RedrawStreamingLine()
    {
        if (_streamingParagraph is not { } paragraph || _viewModel.Assistant.Transcript.Count == 0)
        {
            return;
        }

        string text = _viewModel.Assistant.Transcript[^1].Text;

        // Normally the line has only grown. If it has somehow shrunk, the paragraph is built again
        // rather than having the difference appended to text that is no longer underneath it. The
        // check is on length alone: comparing the whole prefix would cost per token exactly what
        // this method exists to stop paying.
        if (text.Length < _drawn)
        {
            paragraph.Inlines.Clear();
            _drawn = 0;
        }

        if (text.Length == _drawn)
        {
            return;
        }

        MarkdownFlow.AppendPlain(paragraph.Inlines, text[_drawn..]);
        _drawn = text.Length;
        ScrollAssistantToEnd();
    }

    /// <summary>
    /// The line being written turned out to be a tool call the model wrote instead of made, so it
    /// comes off the screen before the retry is drawn underneath.
    /// </summary>
    private void DiscardStreamingLine()
    {
        // The whole document, because scrubbing can change or remove a line anywhere in it, not
        // only the one at the end. A transcript is a few hundred blocks and this happens once a
        // turn at most.
        AssistantTranscript.Document.Blocks.Clear();
        StopStreaming();

        foreach (var line in _viewModel.Assistant.Transcript)
        {
            Add(Blocks(line), line);
        }

        ScrollAssistantToEnd();
    }

    /// <summary>The turn ended: draw the answer properly, now that all of it is known.</summary>
    private void FinishStreamingLine()
    {
        if (_streamingParagraph is not { } paragraph || _viewModel.Assistant.Transcript.Count == 0)
        {
            return;
        }

        var line = _viewModel.Assistant.Transcript[^1];
        AssistantTranscript.Document.Blocks.Remove(paragraph);
        StopStreaming();
        Append(line);
        ScrollAssistantToEnd();
    }

    private void Append(ViewModels.AssistantLine line)
    {
        // An answer still arriving is one paragraph of plain text, kept so the tokens that follow
        // can be added to it rather than replacing it.
        if (line.Kind == "assistant" && _viewModel.Assistant.IsBusy)
        {
            var paragraph = MarkdownFlow.PlainParagraph(line.Text);
            AssistantTranscript.Document.Blocks.Add(paragraph);
            _streamingParagraph = paragraph;
            _drawn = line.Text.Length;
            return;
        }

        Add(Blocks(line), line);
    }

    private static IEnumerable<Block> Blocks(ViewModels.AssistantLine line) => line.Kind switch
    {
        "you" => MarkdownFlow.Bubble(line.Text),
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
            // A question draws itself: it is a bubble, and colouring it as well would be saying the
            // same thing twice in a panel that already has enough going on.
            if (line.IsProblem)
            {
                block.Foreground = (Brush)FindResource("Semantic.Error");
            }

            AssistantTranscript.Document.Blocks.Add(block);
            count++;
        }

        return count;
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
        // One pass in flight at a time. Every token asks to be followed, and each ask used to queue
        // a chain of up to twelve, each of which measures the whole document: a streamed answer was
        // scheduling thousands of full re-measurements it did not need. The pass already queued
        // re-reads the extent when it runs, so it covers whatever arrived while it was waiting.
        if (!_followTranscript || attempts <= 0 || _scrollPending)
        {
            return;
        }

        _scrollPending = true;
        double was = AssistantTranscript.ExtentHeight;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollPending = false;

            if (!_followTranscript)
            {
                return;
            }

            Scrolling(() => AssistantTranscript.ScrollToEnd());

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
