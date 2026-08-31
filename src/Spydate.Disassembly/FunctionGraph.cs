using System.Globalization;
using Spydate.Core.Graph;

namespace Spydate.Disassembly;

/// <summary>How big a character is, so a box can be sized without anything here knowing about fonts.</summary>
public sealed record GraphMetrics(double CharWidth = 7.0, double LineHeight = 15.0, double PaddingX = 16, double PaddingY = 12)
{
    /// <summary>
    /// Longest a box gets before its middle is elided. A block of four hundred instructions makes a box
    /// no screen can show and no reader wants; the head and tail are what identify it.
    /// </summary>
    public int MaxLines { get; init; } = 32;

    public static GraphMetrics Default { get; } = new();
}

/// <summary>One basic block as something to draw: the lines of text and the size they need.</summary>
public sealed record GraphBlock(int Id, ulong StartVa, string Header, IReadOnlyList<string> Lines, bool IsEntry);

/// <summary>A function's control flow, laid out and ready to draw.</summary>
public sealed record FunctionGraph(
    Function Function,
    IReadOnlyList<GraphBlock> Blocks,
    GraphLayoutResult Layout,
    GraphMetrics Metrics)
{
    /// <summary>The block whose box contains a point, for hit-testing a click.</summary>
    public GraphBlock? At(GraphPoint point)
    {
        foreach (var placement in Layout.Nodes)
        {
            if (placement.Contains(point))
            {
                return Blocks.FirstOrDefault(b => b.Id == placement.Id);
            }
        }

        return null;
    }

    public string ToSvg(GraphSvgTheme? theme = null) => GraphSvg.Render(
        Layout,
        Blocks.Select(b => new GraphBoxText(b.Id, b.Header, b.Lines, b.IsEntry)).ToList(),
        Metrics.CharWidth,
        Metrics.LineHeight,
        theme,
        Function.Name);
}

/// <summary>
/// Turns a discovered <see cref="Function"/> into something a graph view can draw: one box per basic
/// block holding its instructions, and one edge per way control leaves a block, classified so the
/// picture can say which is which.
///
/// The classification comes from the block's last instruction rather than from the successor list
/// alone, because a list of two says nothing about which one the branch was taken to. Discovery records
/// the fall-through first, and that ordering is what makes the distinction recoverable.
/// </summary>
public static class FunctionGraphs
{
    public static FunctionGraph Build(Function function, GraphMetrics? metrics = null, GraphLayoutOptions? layout = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        metrics ??= GraphMetrics.Default;

        var blocks = new List<GraphBlock>(function.Blocks.Count);
        var nodes = new List<GraphNode>(function.Blocks.Count);
        var idOf = new Dictionary<ulong, int>(function.Blocks.Count);

        for (int i = 0; i < function.Blocks.Count; i++)
        {
            var block = function.Blocks[i];
            idOf[block.StartVa] = i;

            var lines = Lines(block, metrics.MaxLines);
            string header = $"{block.StartVa:X}  ({block.Instructions.Count} instr)";
            double widest = lines.Concat(new[] { header }).Max(l => l.Length);

            blocks.Add(new GraphBlock(i, block.StartVa, header, lines, block.StartVa == function.EntryVa));
            nodes.Add(new GraphNode(
                i,
                Math.Round((widest * metrics.CharWidth) + metrics.PaddingX),
                Math.Round(((lines.Count + 1) * metrics.LineHeight) + metrics.PaddingY)));
        }

        var edges = new List<GraphEdge>();
        foreach (var block in function.Blocks)
        {
            int from = idOf[block.StartVa];
            for (int i = 0; i < block.Successors.Count; i++)
            {
                if (idOf.TryGetValue(block.Successors[i], out int to))
                {
                    edges.Add(new GraphEdge(from, to, KindOf(block, i)));
                }
            }
        }

        int entry = idOf.TryGetValue(function.EntryVa, out int e) ? e : 0;
        return new FunctionGraph(function, blocks, LayeredLayout.Compute(nodes, edges, entry, layout), metrics);
    }

    /// <summary>True when the function is small enough to be worth drawing rather than reading.</summary>
    public static bool CanDraw(Function function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return function.Blocks.Count is > 0 and <= LayeredLayout.MaxNodes;
    }

    private static GraphEdgeKind KindOf(BasicBlock block, int successorIndex)
    {
        var flow = block.Last.Flow;
        if (flow == InstructionFlow.IndirectBranch)
        {
            return GraphEdgeKind.Switch;   // the only indirect jump with successors is a recovered table
        }

        if (block.Successors.Count == 1)
        {
            return flow == InstructionFlow.UnconditionalBranch ? GraphEdgeKind.Jump : GraphEdgeKind.Fallthrough;
        }

        // Discovery records the fall-through first and the branch target after it.
        return successorIndex == 0 ? GraphEdgeKind.Fallthrough : GraphEdgeKind.Taken;
    }

    /// <summary>
    /// The block's instructions, address and all. A block past the line budget keeps its head and tail
    /// and says how much was left out, which is more use than either truncating silently or drawing a
    /// box a metre tall.
    /// </summary>
    private static List<string> Lines(BasicBlock block, int maxLines)
    {
        var all = block.Instructions
            .Select(i => $"{i.Va.ToString("X", CultureInfo.InvariantCulture)}  {i.Text}")
            .ToList();

        if (all.Count <= maxLines || maxLines < 4)
        {
            return all;
        }

        int head = (maxLines - 1) / 2;
        int tail = maxLines - 1 - head;
        var trimmed = new List<string>(maxLines);
        trimmed.AddRange(all.Take(head));
        trimmed.Add($"… {all.Count - head - tail} more instructions");
        trimmed.AddRange(all.Skip(all.Count - tail));
        return trimmed;
    }
}
