using System.Globalization;
using System.Text;

namespace Spydate.Core.Graph;

/// <summary>Colours for an exported drawing, so it can match either theme.</summary>
public sealed record GraphSvgTheme(
    string Background,
    string NodeFill,
    string NodeStroke,
    string EntryStroke,
    string Text,
    string Header,
    string Fallthrough,
    string Taken,
    string Jump,
    string Back,
    string Switch)
{
    public static GraphSvgTheme Dark { get; } = new(
        Background: "#1b1b1f",
        NodeFill: "#242429",
        NodeStroke: "#3a3a42",
        EntryStroke: "#6aa9ff",
        Text: "#d6d6dd",
        Header: "#8a8a96",
        Fallthrough: "#8a8a96",
        Taken: "#5fbf72",
        Jump: "#6aa9ff",
        Back: "#d08a4a",
        Switch: "#b07ad0");

    public static GraphSvgTheme Light { get; } = new(
        Background: "#ffffff",
        NodeFill: "#f7f7f9",
        NodeStroke: "#c9c9d1",
        EntryStroke: "#1f6feb",
        Text: "#1f1f24",
        Header: "#63636e",
        Fallthrough: "#63636e",
        Taken: "#1a7f37",
        Jump: "#1f6feb",
        Back: "#a35200",
        Switch: "#7a3fa3");

    public string For(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.Taken => Taken,
        GraphEdgeKind.Jump => Jump,
        GraphEdgeKind.Back => Back,
        GraphEdgeKind.Switch => Switch,
        _ => Fallthrough,
    };
}

/// <summary>What to draw in one box: a heading line and the lines under it.</summary>
public sealed record GraphBoxText(int Id, string Header, IReadOnlyList<string> Lines, bool IsEntry);

/// <summary>
/// Draws a laid-out graph as SVG. Two things want this: exporting a function's control flow to
/// something that can be put in a document, and being able to look at a layout at all — the numbers
/// <see cref="LayeredLayout"/> produces are testable, but only a picture shows whether they read well.
/// </summary>
public static class GraphSvg
{
    public static string Render(
        GraphLayoutResult layout,
        IReadOnlyList<GraphBoxText> boxes,
        double charWidth,
        double lineHeight,
        GraphSvgTheme? theme = null,
        string? title = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(boxes);
        theme ??= GraphSvgTheme.Dark;

        var text = boxes.ToDictionary(b => b.Id);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(layout.Width)}\" height=\"{N(layout.Height)}\" viewBox=\"0 0 {N(layout.Width)} {N(layout.Height)}\">");
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"100%\" height=\"100%\" fill=\"{theme.Background}\"/>");
        if (title is not null)
        {
            sb.Append("<title>").Append(Escape(title)).Append("</title>");
        }

        // One marker per edge colour: SVG markers do not inherit the line's stroke in every renderer.
        sb.Append("<defs>");
        foreach (var kind in Enum.GetValues<GraphEdgeKind>())
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<marker id=\"a{(int)kind}\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"3\" orient=\"auto\"><path d=\"M0,0 L7,3 L0,6 z\" fill=\"{theme.For(kind)}\"/></marker>");
        }

        sb.Append("</defs>");

        // Edges first, so a line never covers the text of the box it arrives at.
        foreach (var edge in layout.Edges)
        {
            if (edge.Points.Count < 2)
            {
                continue;
            }

            string points = string.Join(" ", edge.Points.Select(p => $"{N(p.X)},{N(p.Y)}"));
            sb.Append(CultureInfo.InvariantCulture,
                $"<polyline points=\"{points}\" fill=\"none\" stroke=\"{theme.For(edge.Kind)}\" stroke-width=\"1.4\" marker-end=\"url(#a{(int)edge.Kind})\"/>");
        }

        foreach (var node in layout.Nodes)
        {
            text.TryGetValue(node.Id, out var box);
            bool entry = box?.IsEntry ?? false;
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{N(node.X)}\" y=\"{N(node.Y)}\" width=\"{N(node.Width)}\" height=\"{N(node.Height)}\" rx=\"3\" fill=\"{theme.NodeFill}\" stroke=\"{(entry ? theme.EntryStroke : theme.NodeStroke)}\" stroke-width=\"{(entry ? "1.6" : "1")}\"/>");

            if (box is null)
            {
                continue;
            }

            double x = node.X + 8;
            double y = node.Y + lineHeight;
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{N(x)}\" y=\"{N(y)}\" font-family=\"Consolas, monospace\" font-size=\"{N(lineHeight * 0.78)}\" fill=\"{theme.Header}\">{Escape(box.Header)}</text>");

            foreach (string line in box.Lines)
            {
                y += lineHeight;
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text x=\"{N(x)}\" y=\"{N(y)}\" font-family=\"Consolas, monospace\" font-size=\"{N(lineHeight * 0.78)}\" fill=\"{theme.Text}\" xml:space=\"preserve\">{Escape(line)}</text>");
            }
        }

        _ = charWidth;
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string N(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

    private static string Escape(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
