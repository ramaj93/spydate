using System.Windows;
using System.Windows.Controls;

namespace Spydate.App.Views.Documents;

public partial class GraphView : UserControl
{
    public GraphView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // The naming commands live on the window; the canvas carries a reference so its context
            // menu can reach them, because a popup has no window in its visual tree to walk up to.
            Canvas.Tag = Window.GetWindow(this)?.DataContext;
            UpdateViewport();
        };
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateViewport();

    /// <summary>
    /// Tells the canvas which part of the drawing is on screen, in the drawing's own coordinates. A
    /// function of a few hundred blocks is tens of thousands of runs of text, and only the ones in view
    /// are worth building.
    /// </summary>
    private void UpdateViewport()
    {
        double zoom = Math.Max(Canvas.Zoom, 0.05);
        const double overscan = 200;   // a margin either side, so scrolling does not reveal blank space

        Canvas.Viewport = new Rect(
            (Scroller.HorizontalOffset / zoom) - overscan,
            (Scroller.VerticalOffset / zoom) - overscan,
            (Scroller.ViewportWidth / zoom) + (overscan * 2),
            (Scroller.ViewportHeight / zoom) + (overscan * 2));
    }
}
