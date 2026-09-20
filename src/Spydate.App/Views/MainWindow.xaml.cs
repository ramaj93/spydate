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

    /// <summary>
    /// The active file's assistant, whose transcript this window is drawing. Held so its events can
    /// be let go of when another tab comes forward — each file has its own conversation now.
    /// </summary>
    private ViewModels.AssistantViewModel? _assistant;

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
    /// Draws the active tab's conversation, and moves the transcript events onto it.
    ///
    /// Each file has its own assistant now, so switching tabs is switching conversations. The window
    /// draws one transcript into one RichTextBox, so the events that grow and finish a line have to
    /// follow the tab in front — detached from the one leaving, attached to the one arriving — and
    /// the document is rebuilt from the arriving conversation, resuming a mid-answer stream if there
    /// is one. With no file open there is nothing to draw and nothing to listen to.
    /// </summary>
    private void WatchActiveAssistant()
    {
        if (_assistant is not null)
        {
            _assistant.Transcript.CollectionChanged -= OnAssistantTranscriptChanged;
            _assistant.Advancing -= OnAssistantAdvancing;
            _assistant.Discarding -= OnAssistantDiscarding;
            _assistant.TurnFinished -= OnAssistantTurnFinished;
        }

        _assistant = _viewModel.Active?.Assistant;

        if (_assistant is not null)
        {
            _assistant.Transcript.CollectionChanged += OnAssistantTranscriptChanged;

            // Not CollectionChanged: an answer streams into a line that is already in the list, so
            // the transcript grows without the collection changing at all.
            _assistant.Advancing += OnAssistantAdvancing;
            _assistant.Discarding += OnAssistantDiscarding;
            _assistant.TurnFinished += OnAssistantTurnFinished;
        }

        Redraw();
    }

    private void OnAssistantTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnTranscriptChanged(e);

    private void OnAssistantAdvancing(object? sender, EventArgs e) => RedrawStreamingLine();

    private void OnAssistantDiscarding(object? sender, EventArgs e) => DiscardStreamingLine();

    private void OnAssistantTurnFinished(object? sender, EventArgs e) => FinishStreamingLine();

    /// <summary>
    /// Rebuilds the whole transcript for the tab that just came forward.
    ///
    /// The last line goes through <see cref="Append"/>, which starts a streaming paragraph for it
    /// when a turn is still running — so returning to a tab whose answer is mid-flight picks the
    /// stream back up rather than freezing it half-drawn. The rest are laid out finished.
    /// </summary>
    private void Redraw()
    {
        AssistantTranscript.Document.Blocks.Clear();
        ClearMatchHighlight();
        StopStreaming();
        _anchor = null;
        _followTranscript = true;

        if (_assistant is not { } assistant)
        {
            return;
        }

        for (int i = 0; i < assistant.Transcript.Count; i++)
        {
            var line = assistant.Transcript[i];
            if (i == assistant.Transcript.Count - 1)
            {
                Append(line);
            }
            else
            {
                Add(Blocks(line), line);
            }
        }

        ScrollAssistantToEnd();
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
        // being wired once to the window's.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.Active))
            {
                WatchActiveDebugger();
                WatchActiveAssistant();
            }
        };
        WatchActiveDebugger();
        WatchActiveAssistant();

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
        AssistantTranscript.GotKeyboardFocus += (_, _) => RestoreAnchor();

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

        // A resize reflows the document and lets the ScrollViewer clamp the offset toward the top;
        // the anchor is what survives that, and puts the same line back once layout has settled.
        AssistantTranscript.SizeChanged += (_, _) => RestoreAnchor();

        // The window coming forward restores focus inside itself, which brings a caret into view and
        // moves the transcript for reasons that have nothing to do with the reader. Alt-tabbing away
        // and back while an answer was arriving was the commonest way to see it jump.
        Activated += (_, _) => RestoreAnchor();
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
            RememberAnchor();
        }
    }

    /// <summary>
    /// The reader's place in the conversation, as a position in the text and not a pixel offset.
    ///
    /// A pixel offset does not survive a reflow. Maximizing the window widens the panel, the
    /// document re-wraps and re-measures from the top, and the ScrollViewer clamps the offset toward
    /// zero before anyone is asked where to look; reading that clamped value back and restoring it
    /// is how the view jumped to the top on maximize. A TextPointer names a line of the answer
    /// instead and stays valid across the reflow, so the same line can be put back afterwards.
    /// </summary>
    private TextPointer? _anchor;

    /// <summary>Remembers the line at the top of the view, so a later reflow can bring it back.</summary>
    private void RememberAnchor()
    {
        // Following the end needs no anchor - the end is the position, and ScrollToEnd finds it.
        _anchor = _followTranscript
            ? null
            : AssistantTranscript.GetPositionFromPoint(new Point(4, 4), snapToText: true);
    }

    /// <summary>
    /// Puts the remembered line back at the top of the view, after whatever moved it.
    ///
    /// A reflow (the window resized) and focus landing in the panel (its caret is at the document
    /// start and gets brought into view) both move the transcript for reasons that are not the
    /// reader's. When the end is being followed that is simply the end again; otherwise the anchor
    /// is scrolled back up. Bounded retries at Background priority, because a long document
    /// re-measures over several passes and the anchor's position is not settled on the first - and
    /// Background, run after layout has settled, is what keeps this calm. Correcting sooner, mid
    /// reflow, reads a position that is not final yet and scrolls by the wrong amount, which is
    /// erratic. The cost is that on maximize the corrected position is applied a beat after the
    /// reflowed one is shown, so the reader briefly sees it scroll back into place.
    /// </summary>
    private void RestoreAnchor(int attempts = 8)
    {
        if (_followTranscript)
        {
            ScrollAssistantToEnd();
            return;
        }

        if (_anchor is null || attempts <= 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_followTranscript || _anchor is not { } current)
            {
                return;
            }

            // A pointer into a document that has since been rebuilt names nothing here.
            if (!current.IsInSameDocument(AssistantTranscript.Document.ContentStart))
            {
                _anchor = null;
                return;
            }

            Rect rect = current.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty)
            {
                RestoreAnchor(attempts - 1);   // not laid out yet; wait for the next pass
                return;
            }

            // rect.Top is where the line sits relative to the viewport; move it back to the top.
            double delta = rect.Top;
            if (Math.Abs(delta) > 0.5)
            {
                Scrolling(() => AssistantTranscript.ScrollToVerticalOffset(
                    Math.Max(0, AssistantTranscript.VerticalOffset + delta)));
                RestoreAnchor(attempts - 1);
            }
        }));
    }

    private void OnTranscriptChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            AssistantTranscript.Document.Blocks.Clear();
            ClearMatchHighlight();
            StopStreaming();
            _anchor = null;
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
        if (_streamingParagraph is not { } paragraph || _assistant is not { } assistant || assistant.Transcript.Count == 0)
        {
            return;
        }

        string text = assistant.Transcript[^1].Text;

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
        ClearMatchHighlight();
        StopStreaming();

        foreach (var line in _assistant?.Transcript ?? [])
        {
            Add(Blocks(line), line);
        }

        ScrollAssistantToEnd();
    }

    /// <summary>The turn ended: draw the answer properly, now that all of it is known.</summary>
    private void FinishStreamingLine()
    {
        if (_streamingParagraph is not { } paragraph || _assistant is not { } assistant || assistant.Transcript.Count == 0)
        {
            return;
        }

        var line = assistant.Transcript[^1];
        AssistantTranscript.Document.Blocks.Remove(paragraph);
        StopStreaming();
        Append(line);
        ScrollAssistantToEnd();
    }

    private void Append(ViewModels.AssistantLine line)
    {
        // An answer still arriving is one paragraph of plain text, kept so the tokens that follow
        // can be added to it rather than replacing it.
        if (line.Kind == "assistant" && _assistant?.IsBusy == true)
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

    // ------------------------------------------------------------------
    // Find in the conversation
    //
    // The transcript is a FlowDocument, not an AvalonEdit editor, so the editor's SearchPanel does
    // not reach it. This is the equivalent: a find bar over the RichTextBox that selects and scrolls
    // to each match. The document is walked into a flat string with a per-character pointer map on
    // every search rather than cached, because the transcript is rebuilt whenever an answer streams,
    // a tab switches or a template is scrubbed, and a cached pointer into an old document is invalid.
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
            FindInTranscript(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0, fromMatchEnd: true);
            e.Handled = true;
        }
    }

    private void OnAssistantFindOpen(object sender, RoutedEventArgs e) => ShowAssistantFind();

    private void ShowAssistantFind()
    {
        AssistantFindBar.Visibility = Visibility.Visible;

        // Seed with the selected word, as an editor's search does, so "find this" is one gesture.
        if (AssistantTranscript.Selection is { IsEmpty: false } sel && sel.Text.Length is > 0 and < 80)
        {
            AssistantFindBox.Text = sel.Text;
        }

        AssistantFindBox.Focus();
        AssistantFindBox.SelectAll();
    }

    private void HideAssistantFind()
    {
        ClearMatchHighlight();
        AssistantFindBar.Visibility = Visibility.Collapsed;
        AssistantFindStatus.Text = string.Empty;
        AssistantTranscript.Focus();
    }

    private void OnAssistantFindClose(object sender, RoutedEventArgs e) => HideAssistantFind();

    private void OnAssistantFindNext(object sender, RoutedEventArgs e) => FindInTranscript(forward: true, fromMatchEnd: true);

    private void OnAssistantFindPrevious(object sender, RoutedEventArgs e) => FindInTranscript(forward: false, fromMatchEnd: false);

    private void OnAssistantFindOptionChanged(object sender, RoutedEventArgs e) => FindInTranscript(forward: true, fromMatchEnd: false);

    private void OnAssistantFindTextChanged(object sender, TextChangedEventArgs e)
        // As the query grows, search from where the current match starts rather than after it, so the
        // selection settles on the first hit for what has been typed instead of skipping past it.
        => FindInTranscript(forward: true, fromMatchEnd: false);

    private void OnAssistantFindKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                FindInTranscript(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0, fromMatchEnd: true);
                e.Handled = true;
                break;
            case Key.Escape:
                HideAssistantFind();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Selects the next (or previous) match for what is in the find box, wrapping round the ends, and
    /// writes "n of m" or "No matches". <paramref name="fromMatchEnd"/> is false while typing and when
    /// options change (stay on the current hit) and true for an explicit next (move past it).
    /// </summary>
    private void FindInTranscript(bool forward, bool fromMatchEnd)
    {
        string query = AssistantFindBox.Text;
        if (query.Length == 0)
        {
            AssistantFindStatus.Text = string.Empty;
            return;
        }

        var (text, points) = IndexTranscript();
        if (text.Length == 0)
        {
            AssistantFindStatus.Text = "No matches";
            return;
        }

        var comparison = AssistantFindCase.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // Every match, for the "n of m" count and to pick the one after (or before) where we are.
        var matches = new List<int>();
        for (int i = text.IndexOf(query, comparison); i >= 0; i = text.IndexOf(query, i + 1, comparison))
        {
            matches.Add(i);
        }

        if (matches.Count == 0)
        {
            AssistantFindStatus.Text = "No matches";
            return;
        }

        // Where we are now, as offsets into the flat text.
        int selStart = OffsetOf(points, AssistantTranscript.Selection.Start);
        int selEnd = OffsetOf(points, AssistantTranscript.Selection.End);

        // Forward: the first match at or after here — past the current one on an explicit next, still
        // on it while typing. Backward: the last match strictly before the current one. Both wrap.
        int pick;
        if (forward)
        {
            int threshold = fromMatchEnd ? selEnd : selStart;
            pick = matches.FindIndex(m => m >= threshold);
            if (pick < 0) { pick = 0; }
        }
        else
        {
            pick = FindLastIndex(matches, m => m < selStart);
            if (pick < 0) { pick = matches.Count - 1; }
        }

        int start = matches[pick];
        var from = points[start];
        var to = points[Math.Min(start + query.Length, points.Length - 1)];

        // Paint the match's own background rather than lean on the selection: a read-only RichTextBox
        // whose focus is in the find box draws its selection with the system's inactive brush, which
        // is all but invisible on the dark panel. A background on the range shows whatever has focus.
        ClearMatchHighlight();
        var range = new System.Windows.Documents.TextRange(from, to);
        try { range.ApplyPropertyValue(TextElement.BackgroundProperty, MatchBrush); } catch { }
        _matchHighlight = range;

        AssistantTranscript.Selection.Select(from, to);   // still selected, so Ctrl+C copies the hit
        BringMatchIntoView(from);

        AssistantFindStatus.Text = $"{pick + 1} of {matches.Count}";
    }

    /// <summary>The amber wash behind the current match. Frozen so it can be shared and is cheap to apply.</summary>
    private static readonly Brush MatchBrush = FrozenBrush(Color.FromArgb(0x99, 0xF2, 0xC7, 0x44));

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The range painted for the current match, so it can be un-painted before the next one.</summary>
    private System.Windows.Documents.TextRange? _matchHighlight;

    private void ClearMatchHighlight()
    {
        if (_matchHighlight is { } range)
        {
            try { range.ApplyPropertyValue(TextElement.BackgroundProperty, null); } catch { }
            _matchHighlight = null;
        }
    }

    /// <summary>The transcript's text, and a pointer at the start of each character (plus an end one).</summary>
    private (string Text, System.Windows.Documents.TextPointer[] Points) IndexTranscript()
    {
        var builder = new System.Text.StringBuilder();
        var points = new List<System.Windows.Documents.TextPointer>();

        var pointer = AssistantTranscript.Document.ContentStart;
        while (pointer is not null)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                string run = pointer.GetTextInRun(LogicalDirection.Forward);
                for (int i = 0; i < run.Length; i++)
                {
                    builder.Append(run[i]);
                    points.Add(pointer.GetPositionAtOffset(i) ?? pointer);
                }
            }

            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }

        points.Add(AssistantTranscript.Document.ContentEnd);   // the end of the last character
        return (builder.ToString(), points.ToArray());
    }

    /// <summary>The index in the point map of the first character at or after <paramref name="edge"/>.</summary>
    private static int OffsetOf(System.Windows.Documents.TextPointer[] points, System.Windows.Documents.TextPointer edge)
    {
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i].CompareTo(edge) >= 0)
            {
                return i;
            }
        }

        return points.Length - 1;
    }

    private static int FindLastIndex(List<int> list, Predicate<int> match)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (match(list[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Scrolls a match into view without the follow logic reading it as the reader's own move.</summary>
    private void BringMatchIntoView(System.Windows.Documents.TextPointer at)
    {
        _followTranscript = false;   // they are searching, not watching the end

        Rect rect = at.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty)
        {
            return;
        }

        double top = AssistantTranscript.VerticalOffset + rect.Top;
        double viewport = AssistantTranscript.ViewportHeight;
        if (rect.Top < 0 || rect.Bottom > viewport)
        {
            Scrolling(() => AssistantTranscript.ScrollToVerticalOffset(Math.Max(0, top - viewport / 3)));
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
