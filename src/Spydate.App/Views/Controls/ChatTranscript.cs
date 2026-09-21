using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Spydate.App.ViewModels;
using Spydate.App.Views.Chat;

namespace Spydate.App.Views.Controls;

/// <summary>
/// The assistant transcript: a virtualized list of <see cref="ChatMessage"/>s, one per line, with a
/// selection that lives in the lines' flat text rather than in any text control.
///
/// A <see cref="System.Windows.Controls.RichTextBox"/> held the whole conversation as one document,
/// which cost its full length in memory and layout, rebuilt itself on every tab switch, and was
/// walked end to end on every search keystroke. A list realizes only what is on screen; a tab switch
/// is an <c>ItemsSource</c> change; and because the selection is a pair of flat-text offsets, an
/// answer that is not realized is still part of a select-all, copying reads the line's text and never
/// a visual, and the find highlight is drawn by the same adorner — so a question bubble is searchable
/// too. The one thing the document gave for free, a selection swept across messages, this draws
/// itself from the <see cref="ChatMessage.Slices"/> of whatever is realized.
/// </summary>
public sealed class ChatTranscript : ListBox
{
    private ScrollViewer? _scroll;
    private Panel? _panel;
    private SelectionAdorner? _adorner;

    private INotifyCollectionChanged? _watching;
    private bool _follow = true;

    private TextPlace? _anchor;
    private TextPlace? _caret;
    private (int Line, int Start, int End)? _match;

    private bool _selecting;
    private DispatcherTimer? _edgeScroll;
    private Point _edgePoint;

    public ChatTranscript()
    {
        // An implicit style is keyed on the exact type, so a subclass of ListBox does not receive the
        // theme's ListBox style on its own: without this the transcript came up as WPF's stock white
        // box with a border, and near-white text drawn onto it. Ask for that style by its key, then
        // pin the few things this control wants differently from a plain list.
        SetResourceReference(StyleProperty, typeof(ListBox));
        SetResourceReference(FontFamilyProperty, "App.FontFamily");   // the theme's list font is mono
        SetResourceReference(FontSizeProperty, "App.FontSize");
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);

        Focusable = true;
        SelectionMode = SelectionMode.Single;
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);   // lines wrap
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection(), (_, e) => e.CanExecute = HasSelection));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, (_, _) => SelectEverything(), (_, e) => e.CanExecute = Count > 0));

        ContextMenu = BuildMenu();

        // The selection dims when the list loses focus, so it has to be repainted when that changes.
        IsKeyboardFocusWithinChanged += (_, _) => Redraw();
        LostMouseCapture += (_, _) => StopSelecting();
        Unloaded += (_, _) => DetachAdorner();

        // A line re-rendering (rebuilt, or grown by a streamed token) moves the blocks the highlight
        // sits on, and so does the list changing width; both drop the cached rectangles. Nothing else
        // repaints the highlight uninvited.
        AddHandler(ChatMessage.RenderedEvent, new RoutedEventHandler(OnMessageRendered));
        SizeChanged += (_, _) => { ForgetRects(); Redraw(); };

        // The scroll surface and its adorner layer are not there yet when the template is applied;
        // they are once the control is in a shown window, so wire them then too.
        Loaded += (_, _) =>
        {
            EnsureScroll();
            EnsureAdorner();
        };
    }

    /// <summary>The selected text, or the empty string. Used to seed the find box.</summary>
    public string SelectedText => HasSelection ? CopyText() : string.Empty;

    private bool HasSelection => _anchor is { } a && _caret is { } c && !a.Equals(c);

    /// <summary>Whether the adorner has anything to paint — its cue to follow the layout while streaming.</summary>
    internal bool Drawing => HasSelection || _match is not null;

    /// <summary>Whether the selection adorner was found and attached. For the panel probe to check.</summary>
    public bool HasAdorner => _adorner is not null;

    /// <summary>
    /// The boxes the selection would be painted as, in the surface's coordinates. For the panel
    /// probe: a selection over a fenced listing must cover every one of its lines, and whether it
    /// skips one is a question about numbers that a screenshot only invites guessing at.
    /// </summary>
    public IReadOnlyList<Rect> HighlightBoxes()
    {
        var boxes = new List<Rect>();
        if (!HasSelection)
        {
            return boxes;
        }

        var (start, end) = Ordered();
        foreach (var (message, index, _) in Realized())
        {
            if (index < start.Line || index > end.Line)
            {
                continue;
            }

            foreach (var slice in message.Slices)
            {
                boxes.AddRange(RectsFor(slice, index, start, end));
            }
        }

        return boxes;
    }

    private int Count => Items.Count;


    protected override void OnItemsSourceChanged(System.Collections.IEnumerable oldValue, System.Collections.IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);

        if (_watching is not null)
        {
            _watching.CollectionChanged -= OnItemsChanged;
        }

        _watching = newValue as INotifyCollectionChanged;
        if (_watching is not null)
        {
            _watching.CollectionChanged += OnItemsChanged;
        }

        // A different tab's conversation: nothing carries over, and it opens at its end.
        _anchor = _caret = null;
        _match = null;
        _follow = true;
        Redraw();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FollowToEnd);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        EnsureScroll();
        EnsureAdorner();
    }

    /// <summary>
    /// Finds the scroller, its content surface and the items panel, and wires the scroll handler.
    ///
    /// Each is re-found whenever what is held is no longer part of this control. Switching the
    /// bottom pane away from the Assistant and back rebuilds the tab's content, so the scroller,
    /// the surface and the panel are all new objects — and holding the old ones was the bug that
    /// made dragging a selection stop working "randomly": nothing realized was a descendant of the
    /// dead surface any more, so the list believed no line was on screen and a press selected
    /// nothing. The highlight and the follow-the-tail scrolling died with it, for the same reason.
    /// </summary>
    private void EnsureScroll()
    {
        if (_scroll is not null && !_scroll.IsDescendantOf(this))
        {
            _scroll.ScrollChanged -= OnScrolled;
            _scroll = null;
            ForgetRects();
        }

        if (_scroll is null && Descendant<ScrollViewer>(this) is { } scroll)
        {
            _scroll = scroll;
            _scroll.ScrollChanged += OnScrolled;
        }
    }

    /// <summary>The height a reader sees, for the edge of a drag and for bringing a match into view.</summary>
    private double Viewport => _scroll?.ViewportHeight ?? ActualHeight;

    /// <summary>
    /// A line redrew itself, so what was measured of it is stale. Only that line's blocks are
    /// dropped: forgetting every block whenever any of them changed meant that scrolling, which
    /// re-renders a recycled line each step, remeasured the whole screen each step too.
    /// </summary>
    private void OnMessageRendered(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ChatMessage message)
        {
            Forget(message);
        }

        Redraw();
    }

    private void DetachAdorner()
    {
        if (_adorner is { } adorner)
        {
            AdornerLayer.GetAdornerLayer(adorner.AdornedElement)?.Remove(adorner);
            _adorner = null;
        }
    }

    /// <summary>
    /// Attaches the selection adorner. It adorns the list itself rather than the scroller's content
    /// presenter, because the control is the one element here that cannot be replaced underneath it
    /// — so the coordinates the highlight is drawn in stay meaningful for as long as it exists. Not
    /// at apply-template time: the adorner layer is only reachable once the control is in a window.
    /// </summary>
    private void EnsureAdorner()
    {
        if (_adorner is not null || AdornerLayer.GetAdornerLayer(this) is not { } layer)
        {
            return;
        }

        _adorner = new SelectionAdorner(this, this);
        layer.Add(_adorner);
    }

    // ---- following the tail -------------------------------------------------

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _follow)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, FollowToEnd);
        }

        // A question re-arms following: asking means wanting to watch the answer arrive.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is [AssistantLine { IsYou: true }, ..])
        {
            _follow = true;
        }

        // Lines can be removed or scrubbed under a selection; drop it rather than point past the end.
        if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset)
        {
            _anchor = _caret = null;
            _match = null;
        }

        Redraw();
    }

    private void FollowToEnd()
    {
        if (_follow)
        {
            _scroll?.ScrollToEnd();
        }
    }

    private void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0)
        {
            // The content grew or shrank under the viewport — an answer streaming into its line, a
            // line settling from plain text into Markdown, a line realizing at its real height. That
            // is not the reader moving; if the tail was being followed, keep following it.
            if (_follow)
            {
                _scroll?.ScrollToEnd();
            }
        }
        else if (e.VerticalChange != 0)
        {
            // A pixel-scrolled list otherwise moves only when the reader moves it, so following holds
            // only while they are at the bottom.
            _follow = AtBottom();
        }

        Redraw();
    }

    private bool AtBottom() =>
        _scroll is null || _scroll.VerticalOffset >= _scroll.ScrollableHeight - 4;

    /// <summary>
    /// The keys a reader scrolls a transcript with. The items take no focus, so the list's own key
    /// handling — which moves a selection between items — has nothing to do; scroll the viewer
    /// instead, the way the document did.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_scroll is not null)
        {
            bool handled = true;
            switch (e.Key)
            {
                case Key.PageUp: _scroll.PageUp(); break;
                case Key.PageDown: _scroll.PageDown(); break;
                case Key.Up: _scroll.LineUp(); break;
                case Key.Down: _scroll.LineDown(); break;
                case Key.Home when Keyboard.Modifiers == ModifierKeys.Control: _scroll.ScrollToTop(); break;
                case Key.End when Keyboard.Modifiers == ModifierKeys.Control: _scroll.ScrollToEnd(); break;
                case Key.Escape: _anchor = _caret = null; Redraw(); break;
                default: handled = false; break;
            }

            if (handled)
            {
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    // ---- selection by mouse -------------------------------------------------

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Before anything is compared against the surface, not after: the test below is what decides
        // whether this press is ours, so a surface left over from a rebuilt tab would fail it and
        // return here — never reaching the code that notices it is stale and finds the new one.
        EnsureScroll();

        // Only a press on the scrolled content starts a selection. The scrollbar sits beside that
        // surface, not on it, and a press there must stay the scrollbar's — otherwise every attempt
        // to drag the thumb began a selection instead and the thumb never moved. A button or a link
        // inside the content owns its own click too.
        if (!OnSurface(e.OriginalSource)
            || Over<ButtonBase>(e.OriginalSource)
            || Over<Hyperlink>(e.OriginalSource)
            || PlaceAt(e.GetPosition(this)) is not { } place)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }

        Focus();

        if (e.ClickCount == 2)
        {
            SelectWord(place);
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _anchor is not null)
        {
            _caret = place;
        }
        else
        {
            _anchor = _caret = place;
        }

        _selecting = true;
        CaptureMouse();
        Redraw();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (PlaceAt(point) is { } place)
        {
            _caret = place;
            _follow = false;   // dragging over the text is reading it, not watching the end
            Redraw();
        }

        EdgeScroll(point);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_selecting)
        {
            ReleaseMouseCapture();
            StopSelecting();
        }
    }

    /// <summary>
    /// The drag is over, however it ended. Also reached through <see cref="UIElement.LostMouseCapture"/>,
    /// because a capture can be taken away — a menu opening, another window coming forward — without
    /// this control ever seeing the button come up, and a drag left running then kept the
    /// edge-scroll timer alive and moved the selection on every later mouse move.
    /// </summary>
    private void StopSelecting()
    {
        _selecting = false;
        StopEdgeScroll();
    }

    private void EdgeScroll(Point at)
    {
        if (_scroll is null)
        {
            return;
        }

        double margin = 24;
        bool near = at.Y < margin || at.Y > Viewport - margin;
        _edgePoint = at;
        if (!near)
        {
            StopEdgeScroll();
            return;
        }

        _edgeScroll ??= NewEdgeTimer();
        _edgeScroll.Start();
    }

    private DispatcherTimer NewEdgeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            if (!_selecting || _scroll is null)
            {
                StopEdgeScroll();
                return;
            }

            double step = _edgePoint.Y < 24 ? -18 : _edgePoint.Y > Viewport - 24 ? 18 : 0;
            if (step == 0)
            {
                return;
            }

            _follow = false;
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + step);
            if (PlaceAt(_edgePoint) is { } place)
            {
                _caret = place;
            }

            Redraw();
        };

        return timer;
    }

    private void StopEdgeScroll() => _edgeScroll?.Stop();

    // ---- point <-> place ----------------------------------------------------

    private TextPlace? PlaceAt(Point onSurface)
    {
        var messages = Realized();
        if (messages.Count == 0)
        {
            return null;
        }

        var target = messages[0];
        bool found = false;
        foreach (var candidate in messages)
        {
            if (onSurface.Y >= candidate.Bounds.Top && onSurface.Y <= candidate.Bounds.Bottom)
            {
                target = candidate;
                found = true;
                break;
            }
        }

        if (!found)
        {
            // Above the first or below the last realized line: clamp to the nearer one's edge.
            target = onSurface.Y < messages[0].Bounds.Top ? messages[0] : messages[^1];
        }

        return new TextPlace(target.Index, OffsetIn(target.Message, onSurface));
    }

    private int OffsetIn(ChatMessage message, Point onSurface)
    {
        var slices = message.Slices;
        if (slices.Count == 0)
        {
            return 0;
        }

        TextSlice best = slices[0];
        double bestGap = double.MaxValue;
        Point bestLocal = default;

        foreach (var slice in slices)
        {
            if (!TryLocal(slice.Block, onSurface, out var local, out var bounds))
            {
                continue;
            }

            if (onSurface.Y >= bounds.Top && onSurface.Y <= bounds.Bottom)
            {
                best = slice;
                bestLocal = local;
                break;
            }

            double gap = onSurface.Y < bounds.Top ? bounds.Top - onSurface.Y : onSurface.Y - bounds.Bottom;
            if (gap < bestGap)
            {
                bestGap = gap;
                best = slice;
                bestLocal = local;
            }
        }

        return best.Base + MapFor(best.Block).IndexAt(bestLocal);
    }

    private bool TryLocal(TextBlock block, Point at, out Point local, out Rect bounds)
    {
        local = default;
        bounds = default;
        if (!block.IsDescendantOf(this))
        {
            return false;
        }

        var toHere = block.TransformToAncestor(this);
        bounds = toHere.TransformBounds(new Rect(block.RenderSize));
        local = new Point(at.X - bounds.Left, at.Y - bounds.Top);
        return true;
    }

    /// <summary>
    /// The lines that are on screen, each with its index and where it sits in this control.
    ///
    /// Found by walking for the messages that exist — only realized ones do — and asking the
    /// generator which item each belongs to. It used to read a remembered items panel and measure
    /// against a remembered scroll surface, and when either of those was replaced (a rebuilt tab, a
    /// re-applied template) every line looked absent: a press then selected nothing at all, which
    /// is the "drag stopped working" that had no reliable way to be reproduced. Nothing is
    /// remembered here now, and ~20 realized containers cost nothing to walk.
    /// </summary>
    private List<(ChatMessage Message, int Index, Rect Bounds)> Realized()
    {
        var list = new List<(ChatMessage, int, Rect)>();
        if (ContainerHost() is not { } panel)
        {
            return list;
        }

        foreach (UIElement child in panel.Children)
        {
            if (child is not ListBoxItem container || Near<ChatMessage>(container) is not { Line: not null } message)
            {
                continue;
            }

            int index = ItemContainerGenerator.IndexFromContainer(container);
            if (index < 0)
            {
                continue;
            }

            var bounds = message.TransformToAncestor(this).TransformBounds(new Rect(message.RenderSize));
            list.Add((message, index, bounds));
        }

        list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return list;
    }

    /// <summary>
    /// Whether a press belongs to the text rather than to the scrollbars beside it.
    ///
    /// Anywhere over the conversation counts, including the gaps between messages, the margins
    /// around them and the strip above a code block — a press there starts a selection at the
    /// nearest line, the way it does in any document. Requiring the press to land on a descendant
    /// of the content presenter instead was wrong: those gaps hit the scroller's own panel, so a
    /// drag begun a few pixels off a line did nothing at all.
    /// </summary>
    private static bool OnSurface(object source) =>
        source is DependencyObject start && Ancestor<ScrollBar>(start) is null;

    private void SelectWord(TextPlace place)
    {
        string text = FlatOf(place.Line);
        int start = place.Offset;
        int end = place.Offset;
        while (start > 0 && IsWord(text[start - 1]))
        {
            start--;
        }

        while (end < text.Length && IsWord(text[end]))
        {
            end++;
        }

        _anchor = new TextPlace(place.Line, start);
        _caret = new TextPlace(place.Line, end);
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_';

    // ---- copy ---------------------------------------------------------------

    private void CopySelection()
    {
        if (!HasSelection)
        {
            return;
        }

        try
        {
            Clipboard.SetText(CopyText());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another program holds the clipboard; nothing to do but let it be tried again.
        }
    }

    private string CopyText()
    {
        var (start, end) = Ordered();
        if (start.Line == end.Line)
        {
            string only = FlatOf(start.Line);
            return only[Clamp(start.Offset, only)..Clamp(end.Offset, only)];
        }

        var text = new StringBuilder();
        for (int line = start.Line; line <= end.Line; line++)
        {
            string flat = FlatOf(line);
            int from = line == start.Line ? Clamp(start.Offset, flat) : 0;
            int to = line == end.Line ? Clamp(end.Offset, flat) : flat.Length;
            if (line > start.Line)
            {
                text.Append('\n');
            }

            text.Append(flat[from..to]);
        }

        return text.ToString();
    }

    private static int Clamp(int offset, string text) => Math.Clamp(offset, 0, text.Length);

    private void SelectEverything()
    {
        if (Count == 0)
        {
            return;
        }

        _anchor = new TextPlace(0, 0);
        _caret = new TextPlace(Count - 1, FlatOf(Count - 1).Length);
        Redraw();
    }

    // ---- find ---------------------------------------------------------------

    /// <summary>
    /// The next match of <paramref name="needle"/>, made the current selection and highlighted.
    /// Returns its position and the total, both zero when there is none — the caller shows "n of m".
    /// </summary>
    public (int Index, int Count) Find(string needle, bool matchCase, bool forward, bool fromMatchEnd)
    {
        if (needle.Length == 0)
        {
            _match = null;
            Redraw();
            return (0, 0);
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<(int Line, int Start)>();
        for (int line = 0; line < Count; line++)
        {
            string flat = FlatOf(line);
            for (int at = flat.IndexOf(needle, comparison); at >= 0; at = flat.IndexOf(needle, at + 1, comparison))
            {
                matches.Add((line, at));
            }
        }

        if (matches.Count == 0)
        {
            _match = null;
            Redraw();
            return (0, 0);
        }

        var from = _match is { } m ? new TextPlace(m.Line, fromMatchEnd ? m.End : m.Start)
            : _caret ?? new TextPlace(0, 0);

        int pick = forward ? Next(matches, from) : Previous(matches, from);
        var (hitLine, hitStart) = matches[pick];
        _match = (hitLine, hitStart, hitStart + needle.Length);
        _anchor = new TextPlace(hitLine, hitStart);
        _caret = new TextPlace(hitLine, hitStart + needle.Length);

        _follow = false;
        ScrollLineIntoView(hitLine);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RevealMatch);
        Redraw();
        return (pick + 1, matches.Count);
    }

    /// <summary>
    /// After the line is realized, scrolls so the match itself is in view. ScrollIntoView brings the
    /// line in, which is not the same thing for a long answer whose hit is far down inside it.
    /// </summary>
    private void RevealMatch()
    {
        if (_scroll is null || FirstMatchRect() is not { } rect)
        {
            return;
        }

        double viewport = Viewport;
        if (rect.Top < 0 || rect.Bottom > viewport)
        {
            _scroll.ScrollToVerticalOffset(Math.Max(0, _scroll.VerticalOffset + rect.Top - viewport / 3));
        }
    }

    public void ClearMatch()
    {
        _match = null;
        Redraw();
    }

    private static int Next(List<(int Line, int Start)> matches, TextPlace from)
    {
        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i].Line > from.Line || (matches[i].Line == from.Line && matches[i].Start >= from.Offset))
            {
                return i;
            }
        }

        return 0;
    }

    private static int Previous(List<(int Line, int Start)> matches, TextPlace from)
    {
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            if (matches[i].Line < from.Line || (matches[i].Line == from.Line && matches[i].Start < from.Offset))
            {
                return i;
            }
        }

        return matches.Count - 1;
    }

    private void ScrollLineIntoView(int line)
    {
        if (line >= 0 && line < Count)
        {
            ScrollIntoView(Items[line]);
        }
    }

    // ---- what the adorner draws ---------------------------------------------

    /// <summary>
    /// Paints the selection and the match over the realized lines. One pass: the realized set is
    /// read once, and a slice's rectangles come from <see cref="_rects"/>, keyed on the block and the
    /// range inside it, so a drag that moves one end of the selection recomputes the line that end
    /// is on and reuses everything else. Scrolling changes only the transform, never the cached
    /// rectangles; a line re-rendering or the list resizing empties the cache.
    /// </summary>
    internal void Paint(DrawingContext context, Brush selection, Brush match)
    {
        if (!Drawing)
        {
            return;
        }

        var realized = Realized();

        if (HasSelection)
        {
            var (start, end) = Ordered();
            foreach (var (message, index, _) in realized)
            {
                if (index < start.Line || index > end.Line)
                {
                    continue;
                }

                foreach (var slice in message.Slices)
                {
                    foreach (var rect in RectsFor(slice, index, start, end))
                    {
                        context.DrawRectangle(selection, null, rect);
                    }
                }
            }
        }

        foreach (var rect in MatchRects(realized))
        {
            context.DrawRectangle(match, null, rect);
        }
    }

    private IEnumerable<Rect> MatchRects(List<(ChatMessage Message, int Index, Rect Bounds)> realized)
    {
        if (_match is not { } match)
        {
            yield break;
        }

        var start = new TextPlace(match.Line, match.Start);
        var end = new TextPlace(match.Line, match.End);
        foreach (var (message, index, _) in realized)
        {
            if (index != match.Line)
            {
                continue;
            }

            foreach (var slice in message.Slices)
            {
                foreach (var rect in RectsFor(slice, index, start, end))
                {
                    yield return rect;
                }
            }
        }
    }

    private Rect? FirstMatchRect()
    {
        foreach (var rect in MatchRects(Realized()))
        {
            return rect;
        }

        return null;
    }

    /// <summary>
    /// Where each realized block draws its characters, so highlighting a range and hit-testing one
    /// are both arithmetic. Weak, so a block that has been drawn over falls out on its own; blocks
    /// that are redrawn in place — a streaming answer growing — are dropped by name when they say so.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, LineMap> _maps = new();

    private LineMap MapFor(TextBlock block)
    {
        if (!_maps.TryGetValue(block, out var map))
        {
            map = LineMap.Of(block);
            _maps.Add(block, map);
        }

        return map;
    }

    private void ForgetRects() => _maps.Clear();

    private void Forget(ChatMessage message)
    {
        foreach (var slice in message.Slices)
        {
            _maps.Remove(slice.Block);
        }
    }

    private IEnumerable<Rect> RectsFor(TextSlice slice, int lineIndex, TextPlace start, TextPlace end)
    {
        if (lineIndex < start.Line || lineIndex > end.Line)
        {
            yield break;
        }

        int from = lineIndex == start.Line ? start.Offset : slice.Base;
        int to = lineIndex == end.Line ? end.Offset : slice.End;
        from = Math.Max(from, slice.Base);
        to = Math.Min(to, slice.End);
        if (to <= from || !slice.Block.IsDescendantOf(this))
        {
            yield break;
        }

        var map = MapFor(slice.Block);

        // The adorner draws in its adorned element's coordinate space, which is this control's.
        var toSurface = slice.Block.TransformToAncestor(this);
        foreach (var rect in map.Rects(from - slice.Base, to - slice.Base))
        {
            yield return toSurface.TransformBounds(rect);
        }
    }

    private (TextPlace, TextPlace) Ordered()
    {
        var a = _anchor!.Value;
        var b = _caret!.Value;
        return Compare(a, b) <= 0 ? (a, b) : (b, a);
    }

    private static int Compare(TextPlace a, TextPlace b) =>
        a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Offset.CompareTo(b.Offset);

    private string FlatOf(int line) => line >= 0 && line < Count && Items[line] is AssistantLine row ? row.FlatText : string.Empty;

    private void Redraw()
    {
        EnsureScroll();
        EnsureAdorner();
        _adorner?.InvalidateVisual();
    }

    // ---- context menu -------------------------------------------------------

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Copy", ApplicationCommands.Copy));
        menu.Items.Add(Item("Select all", ApplicationCommands.SelectAll));
        return menu;

        MenuItem Item(string header, RoutedCommand command) => new()
        {
            Header = header,
            Command = command,
            CommandTarget = this,
        };
    }

    // ---- visual-tree helpers ------------------------------------------------

    private static bool Over<T>(object source) where T : DependencyObject =>
        source is DependencyObject start && Ancestor<T>(start) is not null;

    private static T? Ancestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var at = start; at is not null; at = ParentOf(at))
        {
            if (at is T hit)
            {
                return hit;
            }
        }

        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject at) =>
        at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at);

    /// <summary>
    /// The panel the containers live in. Held, but checked against this control before it is used —
    /// holding one without checking is what made a rebuilt tab look like an empty conversation, and
    /// finding it afresh every time meant walking the whole rendering on every mouse move.
    /// </summary>
    private Panel? ContainerHost()
    {
        if (_panel is { IsItemsHost: true } held && held.IsDescendantOf(this))
        {
            return _panel;
        }

        _panel = Descendants<Panel>(this).FirstOrDefault(p => p.IsItemsHost);
        return _panel;
    }

    /// <summary>The nearest descendant of a container's own template, without descending into its content.</summary>
    private static T? Near<T>(DependencyObject root, int depth = 4) where T : DependencyObject
    {
        if (root is T hit)
        {
            return hit;
        }

        if (depth <= 0)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (Near<T>(VisualTreeHelper.GetChild(root, i), depth - 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject =>
        Descendants<T>(root).FirstOrDefault();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
            {
                yield return hit;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
