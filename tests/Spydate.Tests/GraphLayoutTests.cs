using Spydate.Core.Graph;
using Spydate.Core.PE;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// The graph view is drawn in a document tab, which cannot be inspected from here, so everything that
/// decides whether the picture is right lives in the layout and is asserted on the numbers: boxes that
/// do not overlap, edges that go the right way between the right boxes, and lines that stay off the
/// boxes they pass. A drawing that satisfies all of these is a readable one.
/// </summary>
public class GraphLayoutTests
{
    private static GraphNode Node(int id, double w = 100, double h = 40) => new(id, w, h);

    /// <summary>if/else: 0 branches to 1 or 2, both joining at 3.</summary>
    private static (List<GraphNode> Nodes, List<GraphEdge> Edges) Diamond() => (
        new List<GraphNode> { Node(0), Node(1), Node(2), Node(3) },
        new List<GraphEdge>
        {
            new(0, 1, GraphEdgeKind.Fallthrough),
            new(0, 2, GraphEdgeKind.Taken),
            new(1, 3, GraphEdgeKind.Jump),
            new(2, 3, GraphEdgeKind.Fallthrough),
        });

    /// <summary>A loop: 0 → 1 → 2, with 2 going back to 1, and 1 leaving to 3.</summary>
    private static (List<GraphNode> Nodes, List<GraphEdge> Edges) Loop() => (
        new List<GraphNode> { Node(0), Node(1), Node(2), Node(3) },
        new List<GraphEdge>
        {
            new(0, 1, GraphEdgeKind.Fallthrough),
            new(1, 2, GraphEdgeKind.Taken),
            new(2, 1, GraphEdgeKind.Jump),
            new(1, 3, GraphEdgeKind.Fallthrough),
        });

    // ------------------------------------------------------------------
    // Shape
    // ------------------------------------------------------------------

    [Fact]
    public void ControlFlowsDownTheDrawing()
    {
        var (nodes, edges) = Diamond();
        var layout = LayeredLayout.Compute(nodes, edges, 0);

        var entry = layout.NodeFor(0)!;
        Assert.All(new[] { 1, 2 }, id => Assert.True(layout.NodeFor(id)!.Y >= entry.Bottom, $"node {id} is not below the entry"));
        Assert.True(layout.NodeFor(3)!.Y >= layout.NodeFor(1)!.Bottom);
    }

    [Fact]
    public void ArmsOfABranchSitSideBySide()
    {
        var (nodes, edges) = Diamond();
        var layout = LayeredLayout.Compute(nodes, edges, 0);

        var a = layout.NodeFor(1)!;
        var b = layout.NodeFor(2)!;

        Assert.Equal(a.Y, b.Y);                                   // same layer
        Assert.True(a.Right < b.X || b.Right < a.X, "the arms overlap");
    }

    [Fact]
    public void TheEdgeThatClosesALoopIsMarkedAsOne()
    {
        var (nodes, edges) = Loop();
        var layout = LayeredLayout.Compute(nodes, edges, 0);

        var back = layout.Edges.Where(e => e.Kind == GraphEdgeKind.Back).ToList();

        var single = Assert.Single(back);
        Assert.Equal(2, single.From);
        Assert.Equal(1, single.To);

        // Its kind is replaced whatever the caller said, because only the layout knows the graph is
        // cyclic; the caller only sees one branch at a time.
        Assert.DoesNotContain(layout.Edges.Where(e => e.From == 2 && e.To == 1), e => e.Kind == GraphEdgeKind.Jump);
    }

    [Fact]
    public void ALoopEdgeLeavesTheBottomAndArrivesAtTheTop()
    {
        // Which way round this goes is the whole reason loop edges are routed by hand: control leaves
        // the end of the block and re-enters the head of the loop, and the picture has to say so.
        var (nodes, edges) = Loop();
        var layout = LayeredLayout.Compute(nodes, edges, 0);

        var back = layout.Edges.Single(e => e.Kind == GraphEdgeKind.Back);
        var from = layout.NodeFor(2)!;
        var to = layout.NodeFor(1)!;

        Assert.Equal(from.Bottom, back.Points[0].Y, 3);
        Assert.Equal(to.Y, back.Points[^1].Y, 3);
        Assert.Equal(to.CenterX, back.Points[^1].X, 3);
    }

    [Fact]
    public void ABlockThatJumpsToItselfIsStillDrawn()
    {
        var nodes = new List<GraphNode> { Node(0), Node(1) };
        var edges = new List<GraphEdge> { new(0, 1, GraphEdgeKind.Fallthrough), new(1, 1, GraphEdgeKind.Taken) };

        var layout = LayeredLayout.Compute(nodes, edges, 0);

        Assert.Equal(2, layout.Nodes.Count);
        Assert.Contains(layout.Edges, e => e.From == 1 && e.To == 1 && e.Kind == GraphEdgeKind.Back);
    }

    [Fact]
    public void ReducingCrossingsActuallyReducesThem()
    {
        // Two layers wired in reverse: without the ordering sweep every pair of edges crosses.
        var nodes = Enumerable.Range(0, 9).Select(i => Node(i, 60, 30)).ToList();
        var edges = new List<GraphEdge> { new(0, 1, GraphEdgeKind.Fallthrough), new(0, 2, GraphEdgeKind.Taken), new(0, 3, GraphEdgeKind.Taken), new(0, 4, GraphEdgeKind.Taken) };
        for (int i = 0; i < 4; i++)
        {
            edges.Add(new GraphEdge(1 + i, 8 - i, GraphEdgeKind.Jump));
        }

        int sorted = LayeredLayout.CountCrossings(LayeredLayout.Compute(nodes, edges, 0));
        int unsorted = LayeredLayout.CountCrossings(LayeredLayout.Compute(nodes, edges, 0, new GraphLayoutOptions { OrderingPasses = 0 }));

        Assert.True(sorted < unsorted, $"ordering left {sorted} crossings against {unsorted} without it");
    }

    [Fact]
    public void TheSameGraphIsAlwaysDrawnTheSameWay()
    {
        // A layout that shifted between runs would move a block out from under the pointer on every
        // redraw, and would make every assertion here a coin toss.
        var (nodes, edges) = Diamond();

        var a = LayeredLayout.Compute(nodes, edges, 0);
        var b = LayeredLayout.Compute(nodes, edges, 0);

        Assert.Equal(a.Nodes, b.Nodes);

        // Compared point by point: EdgeRoute is a record holding a list, and record equality compares
        // that list by reference, so two identical routes are never equal to each other.
        Assert.Equal(a.Edges.Count, b.Edges.Count);
        foreach (var (left, right) in a.Edges.Zip(b.Edges))
        {
            Assert.Equal((left.From, left.To, left.Kind), (right.From, right.To, right.Kind));
            Assert.Equal(left.Points, right.Points);
        }
    }

    [Fact]
    public void NothingToDrawIsNotAnError()
    {
        var empty = LayeredLayout.Compute(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), 0);

        Assert.Empty(empty.Nodes);
        Assert.Equal(0, empty.Width);
    }

    [Fact]
    public void AGraphTooBigToReadIsRefusedRatherThanDrawn()
    {
        var nodes = Enumerable.Range(0, LayeredLayout.MaxNodes + 1).Select(i => Node(i)).ToList();

        Assert.Throws<ArgumentException>(() => LayeredLayout.Compute(nodes, Array.Empty<GraphEdge>(), 0));
    }

    [Fact]
    public void AnEdgeToANodeThatIsNotThereIsIgnored()
    {
        var layout = LayeredLayout.Compute(
            new List<GraphNode> { Node(0) },
            new List<GraphEdge> { new(0, 99, GraphEdgeKind.Jump) },
            0);

        Assert.Single(layout.Nodes);
        Assert.Empty(layout.Edges);
    }

    // ------------------------------------------------------------------
    // The same invariants over every function of a real binary
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(Corpus.NotepadX64)]
    [InlineData(Corpus.NotepadX86)]
    public void EveryFunctionOfARealBinaryDrawsCorrectly(string path)
    {
        if (!Corpus.Has(path))
        {
            return;
        }

        var analysis = Corpus.Analysed(path);

        int drawn = 0, boxes = 0;
        // Bounded by block count, not just by how many functions: checking that no line crosses a box
        // compares every segment against every box, so one enormous function costs more than the
        // hundreds of differently-shaped ones that actually make this worth running.
        foreach (var function in analysis.Functions.OrderBy(f => f.EntryVa)
                     .Where(f => FunctionGraphs.CanDraw(f) && f.Blocks.Count <= 60).Take(300))
        {
            var graph = FunctionGraphs.Build(function);
            drawn++;
            boxes += graph.Layout.Nodes.Count;
            AssertWellFormed(graph, function);
        }

        Assert.True(drawn > 200, $"only {drawn} functions were drawn");
        Assert.True(boxes > 400, $"only {boxes} boxes over {drawn} functions");
    }

    private static void AssertWellFormed(FunctionGraph graph, Function function)
    {
        var layout = graph.Layout;
        string where = function.Name;

        Assert.Equal(function.Blocks.Count, layout.Nodes.Count);
        Assert.Equal(function.Blocks.Count, layout.Nodes.Select(n => n.Id).Distinct().Count());

        // No box sits on another: a covered block cannot be read or clicked.
        var nodes = layout.Nodes.ToList();
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                Assert.False(Overlap(nodes[i], nodes[j]), $"{where}: boxes {nodes[i].Id} and {nodes[j].Id} overlap");
            }
        }

        var byId = layout.Nodes.ToDictionary(n => n.Id);
        foreach (var edge in layout.Edges)
        {
            var from = byId[edge.From];
            var to = byId[edge.To];

            // An edge starts on the block it leaves and ends on the block it reaches, so an arrowhead
            // always points at a box rather than into space.
            Assert.Equal(from.CenterX, edge.Points[0].X, 3);
            Assert.Equal(from.Bottom, edge.Points[0].Y, 3);
            Assert.Equal(to.CenterX, edge.Points[^1].X, 3);
            Assert.Equal(to.Y, edge.Points[^1].Y, 3);

            if (edge.Kind != GraphEdgeKind.Back)
            {
                Assert.True(to.Y >= from.Bottom, $"{where}: edge {edge.From}->{edge.To} does not run downwards");
            }

            // A line through a box hides instructions and suggests flow that is not there.
            for (int i = 0; i + 1 < edge.Points.Count; i++)
            {
                foreach (var box in layout.Nodes)
                {
                    if (box.Id == edge.From || box.Id == edge.To)
                    {
                        continue;
                    }

                    Assert.False(
                        SegmentEntersBox(edge.Points[i], edge.Points[i + 1], box),
                        $"{where}: edge {edge.From}->{edge.To} runs through box {box.Id}");
                }
            }
        }

        Assert.True(layout.Width > 0 && layout.Height > 0, where);
        Assert.All(layout.Nodes, n => Assert.True(n.X >= 0 && n.Y >= 0, $"{where}: box {n.Id} is off the canvas"));
    }

    private static bool Overlap(NodePlacement a, NodePlacement b)
        => a.X < b.Right - 0.01 && b.X < a.Right - 0.01 && a.Y < b.Bottom - 0.01 && b.Y < a.Bottom - 0.01;

    /// <summary>
    /// Whether a segment passes through the inside of a box. The box is shrunk slightly first: a line
    /// running along the gap beside a box grazes its edge, and that is not the same as crossing it.
    /// </summary>
    private static bool SegmentEntersBox(GraphPoint a, GraphPoint b, NodePlacement box)
    {
        const double inset = 0.5;
        double left = box.X + inset;
        double right = box.Right - inset;
        double top = box.Y + inset;
        double bottom = box.Bottom - inset;
        if (right <= left || bottom <= top)
        {
            return false;
        }

        // Liang-Barsky: does the segment's parameter range survive clipping to the box?
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double t0 = 0, t1 = 1;

        return Clip(-dx, a.X - left) && Clip(dx, right - a.X) && Clip(-dy, a.Y - top) && Clip(dy, bottom - a.Y) && t0 < t1;

        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-9)
            {
                return q >= 0;
            }

            double r = q / p;
            if (p < 0)
            {
                if (r > t1)
                {
                    return false;
                }

                t0 = Math.Max(t0, r);
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }

                t1 = Math.Min(t1, r);
            }

            return true;
        }
    }
}
