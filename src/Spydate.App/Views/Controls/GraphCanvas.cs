using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Spydate.Core.Graph;
using Spydate.Disassembly;

namespace Spydate.App.Views.Controls;

/// <summary>
/// Draws a laid-out control-flow graph. Everything about <em>where</em> things go was decided by
/// <see cref="LayeredLayout"/>; this only puts ink on them, which is why the geometry could be tested
/// without a window.
///
/// Drawn directly rather than as a visual per block. A function of a few hundred blocks holding thirty
/// instructions each is ten thousand runs of text, and only the handful on screen are worth building —
/// so the render is culled to the viewport, and the text is dropped entirely once zoomed out far enough
/// that it could not be read anyway.
/// </summary>
public sealed class GraphCanvas : FrameworkElement
{
    /// <summary>Below this scale the text is a grey smear, so only the shape of the graph is drawn.</summary>
    private const double TextLegibleBelow = 0.45;

    private readonly Dictionary<int, FormattedText[]> _text = new();
    private Typeface _typeface = new("Consolas");
    private double _pixelsPerDip = 1;

    public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(
        nameof(Graph), typeof(FunctionGraph), typeof(GraphCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnGraphChanged));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(GraphCanvas),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The part of the drawing that is on screen, in layout coordinates. Empty means all of it.</summary>
    public static readonly DependencyProperty ViewportProperty = DependencyProperty.Register(
        nameof(Viewport), typeof(Rect), typeof(GraphCanvas),
        new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedVaProperty = DependencyProperty.Register(
        nameof(SelectedVa), typeof(ulong?), typeof(GraphCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Invoked with the block's VA when one is double-clicked.</summary>
    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.Register(
        nameof(ActivateCommand), typeof(ICommand), typeof(GraphCanvas), new PropertyMetadata(null));

    public static readonly DependencyProperty NodeFillProperty = DependencyProperty.Register(
        nameof(NodeFill), typeof(Brush), typeof(GraphCanvas), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NodeBorderProperty = DependencyProperty.Register(
        nameof(NodeBorder), typeof(Brush), typeof(GraphCanvas), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EntryBorderProperty = DependencyProperty.Register(
        nameof(EntryBorder), typeof(Brush), typeof(GraphCanvas), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedBorderProperty = DependencyProperty.Register(
        nameof(SelectedBorder), typeof(Brush), typeof(GraphCanvas), new FrameworkPropertyMetadata(Brushes.Khaki, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(GraphCanvas), new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender, OnTextChanged));

    public static readonly DependencyProperty HeaderBrushProperty = DependencyProperty.Register(
        nameof(HeaderBrush), typeof(Brush), typeof(GraphCanvas), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, OnTextChanged));

    public static readonly DependencyProperty EdgeBrushesProperty = DependencyProperty.Register(
        nameof(EdgeBrushes), typeof(BrushCollection), typeof(GraphCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(GraphCanvas),
        new FrameworkPropertyMetadata(new FontFamily("Consolas"), FrameworkPropertyMetadataOptions.AffectsRender, OnTextChanged));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(GraphCanvas),
        new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsRender, OnTextChanged));

    public FunctionGraph? Graph
    {
        get => (FunctionGraph?)GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public Rect Viewport
    {
        get => (Rect)GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    public ulong? SelectedVa
    {
        get => (ulong?)GetValue(SelectedVaProperty);
        set => SetValue(SelectedVaProperty, value);
    }

    public ICommand? ActivateCommand
    {
        get => (ICommand?)GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    public Brush NodeFill { get => (Brush)GetValue(NodeFillProperty); set => SetValue(NodeFillProperty, value); }

    public Brush NodeBorder { get => (Brush)GetValue(NodeBorderProperty); set => SetValue(NodeBorderProperty, value); }

    public Brush EntryBorder { get => (Brush)GetValue(EntryBorderProperty); set => SetValue(EntryBorderProperty, value); }

    public Brush SelectedBorder { get => (Brush)GetValue(SelectedBorderProperty); set => SetValue(SelectedBorderProperty, value); }

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public Brush HeaderBrush { get => (Brush)GetValue(HeaderBrushProperty); set => SetValue(HeaderBrushProperty, value); }

    /// <summary>Line colours, indexed by <see cref="GraphEdgeKind"/>.</summary>
    public BrushCollection? EdgeBrushes { get => (BrushCollection?)GetValue(EdgeBrushesProperty); set => SetValue(EdgeBrushesProperty, value); }

    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    private static void OnGraphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (GraphCanvas)d;
        canvas._text.Clear();
        canvas.InvalidateVisual();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((GraphCanvas)d)._text.Clear();

    protected override Size MeasureOverride(Size availableSize)
    {
        _ = availableSize;
        return Graph is { } g ? new Size(g.Layout.Width * Zoom, g.Layout.Height * Zoom) : new Size(0, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        if (Graph is not { } graph)
        {
            return;
        }

        _typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // A filled rectangle, even a transparent one, is what makes the whole surface answer a click.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        double zoom = Math.Max(Zoom, 0.05);
        dc.PushTransform(new ScaleTransform(zoom, zoom));
        try
        {
            // Until the scroller has said what is on screen, assume the top-left corner rather than the
            // whole drawing: the first paint of a large graph would otherwise build every run of text in
            // it before anything appeared, which is the one place this could visibly stall.
            var visible = Viewport.IsEmpty ? new Rect(0, 0, 2400, 2400) : Viewport;
            DrawEdges(dc, graph, visible);
            DrawNodes(dc, graph, visible, zoom);
        }
        finally
        {
            dc.Pop();
        }
    }

    private void DrawEdges(DrawingContext dc, FunctionGraph graph, Rect visible)
    {
        foreach (var edge in graph.Layout.Edges)
        {
            if (edge.Points.Count < 2 || !Bounds(edge).IntersectsWith(visible))
            {
                continue;
            }

            var pen = new Pen(BrushFor(edge.Kind), 1.4) { LineJoin = PenLineJoin.Round };
            pen.Freeze();

            var figure = new PathFigure { StartPoint = P(edge.Points[0]), IsClosed = false };
            for (int i = 1; i < edge.Points.Count; i++)
            {
                figure.Segments.Add(new LineSegment(P(edge.Points[i]), true));
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);

            DrawArrowhead(dc, pen.Brush, edge.Points[^2], edge.Points[^1]);
        }
    }

    /// <summary>A filled triangle at the target end, so the direction of control is not left to inference.</summary>
    private static void DrawArrowhead(DrawingContext dc, Brush brush, GraphPoint from, GraphPoint to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 0.01)
        {
            return;
        }

        dx /= length;
        dy /= length;
        const double size = 6;
        var tip = new Point(to.X, to.Y);
        var left = new Point(to.X - (dx * size) - (dy * size * 0.45), to.Y - (dy * size) + (dx * size * 0.45));
        var right = new Point(to.X - (dx * size) + (dy * size * 0.45), to.Y - (dy * size) - (dx * size * 0.45));

        var figure = new PathFigure { StartPoint = tip, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(left, false));
        figure.Segments.Add(new LineSegment(right, false));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    private void DrawNodes(DrawingContext dc, FunctionGraph graph, Rect visible, double zoom)
    {
        var blocks = graph.Blocks.ToDictionary(b => b.Id);
        bool withText = zoom >= TextLegibleBelow;
        double lineHeight = graph.Metrics.LineHeight;

        foreach (var node in graph.Layout.Nodes)
        {
            var box = new Rect(node.X, node.Y, node.Width, node.Height);
            if (!box.IntersectsWith(visible) || !blocks.TryGetValue(node.Id, out var block))
            {
                continue;
            }

            bool selected = SelectedVa == block.StartVa;
            var border = selected ? SelectedBorder : block.IsEntry ? EntryBorder : NodeBorder;
            var pen = new Pen(border, selected || block.IsEntry ? 1.8 : 1);
            pen.Freeze();
            dc.DrawRoundedRectangle(NodeFill, pen, box, 3, 3);

            if (!withText)
            {
                continue;
            }

            var lines = LinesFor(block);
            double y = node.Y + 4;
            for (int i = 0; i < lines.Length; i++)
            {
                dc.DrawText(lines[i], new Point(node.X + 8, y));
                y += lineHeight;
            }
        }
    }

    private FormattedText[] LinesFor(GraphBlock block)
    {
        if (_text.TryGetValue(block.Id, out var cached))
        {
            return cached;
        }

        var built = new FormattedText[block.Lines.Count + 1];
        built[0] = Make(block.Header, HeaderBrush);
        for (int i = 0; i < block.Lines.Count; i++)
        {
            built[i + 1] = Make(block.Lines[i], TextBrush);
        }

        _text[block.Id] = built;
        return built;

        FormattedText Make(string s, Brush brush) => new(
            s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, brush, _pixelsPerDip);
    }

    private Brush BrushFor(GraphEdgeKind kind)
    {
        var brushes = EdgeBrushes;
        int index = (int)kind;
        return brushes is not null && index < brushes.Count ? brushes[index] : NodeBorder;
    }

    private static Rect Bounds(EdgeRoute edge)
    {
        double minX = edge.Points.Min(p => p.X);
        double maxX = edge.Points.Max(p => p.X);
        double minY = edge.Points.Min(p => p.Y);
        double maxY = edge.Points.Max(p => p.Y);
        return new Rect(minX - 4, minY - 4, maxX - minX + 8, maxY - minY + 8);
    }

    private static Point P(GraphPoint p) => new(p.X, p.Y);

    // ------------------------------------------------------------------
    // Interaction
    // ------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (Graph is not { } graph)
        {
            return;
        }

        var position = e.GetPosition(this);
        double zoom = Math.Max(Zoom, 0.05);
        var block = graph.At(new GraphPoint(position.X / zoom, position.Y / zoom));
        if (block is null)
        {
            return;
        }

        SelectedVa = block.StartVa;
        if (e.ClickCount >= 2 && ActivateCommand is { } command && command.CanExecute(block.StartVa))
        {
            command.Execute(block.StartVa);
        }

        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;   // an unmodified wheel scrolls, which is what the ScrollViewer is for
        }

        Zoom = Math.Clamp(Zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.1, 3.0);
        e.Handled = true;
    }
}

/// <summary>Line colours by edge kind, so the palette lives in XAML with every other colour.</summary>
public sealed class BrushCollection : List<Brush>
{
}
