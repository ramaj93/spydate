using System.Windows;
using System.Windows.Controls;

namespace Spydate.App.Views.Documents;

public partial class GraphView : UserControl
{
    public GraphView()
    {
        InitializeComponent();
        // The canvas carries the open file in its Tag, because a popup has no window in its visual
        // tree to walk up to and the naming commands are not on the graph's own view model. That is
        // a binding in the XAML rather than an assignment here: it used to be set once on Loaded,
        // which was the same thing while there was one file for the life of a window — and stopped
        // being the same thing the moment the file became something the window could swap.
        Loaded += (_, _) => UpdateViewport();
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
