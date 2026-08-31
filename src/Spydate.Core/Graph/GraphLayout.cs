namespace Spydate.Core.Graph;

public readonly record struct GraphPoint(double X, double Y);

/// <summary>How control reaches the target, which is the only thing an edge's colour has to say.</summary>
public enum GraphEdgeKind
{
    /// <summary>Execution ran off the end of the block into the next one.</summary>
    Fallthrough,

    /// <summary>A branch was taken.</summary>
    Taken,

    /// <summary>An unconditional jump.</summary>
    Jump,

    /// <summary>Back to a block that dominates this one: the edge that closes a loop.</summary>
    Back,

    /// <summary>One arm of a recovered switch table.</summary>
    Switch,
}

/// <summary>A box to place. Size comes from the caller, which is the only thing that can measure text.</summary>
public sealed record GraphNode(int Id, double Width, double Height);

public sealed record GraphEdge(int From, int To, GraphEdgeKind Kind);

public sealed record NodePlacement(int Id, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public bool Contains(GraphPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
}

/// <summary>An edge as a polyline: the first point is on the source, the last on the target.</summary>
public sealed record EdgeRoute(int From, int To, GraphEdgeKind Kind, IReadOnlyList<GraphPoint> Points);

public sealed record GraphLayoutResult(
    IReadOnlyList<NodePlacement> Nodes,
    IReadOnlyList<EdgeRoute> Edges,
    double Width,
    double Height)
{
    public NodePlacement? NodeFor(int id) => Nodes.FirstOrDefault(n => n.Id == id);
}

public sealed record GraphLayoutOptions
{
    /// <summary>Space between the bottom of one layer and the top of the next.</summary>
    public double LayerGap { get; init; } = 46;

    /// <summary>Least horizontal space between two boxes on the same layer.</summary>
    public double NodeGap { get; init; } = 28;

    /// <summary>Width of a channel reserved down the left for one loop edge.</summary>
    public double BackEdgeLane { get; init; } = 22;

    public double Margin { get; init; } = 24;

    /// <summary>Passes of the crossing-reduction sweep. Four is where the improvement flattens out.</summary>
    public int OrderingPasses { get; init; } = 4;

    public static GraphLayoutOptions Default { get; } = new();
}

/// <summary>
/// Lays out a control-flow graph the way one is normally read: entry at the top, control flowing
/// downwards, one layer of boxes per step away from the entry.
///
/// The shape is the classic layered drawing — rank, order, position, route — with one deliberate
/// departure. A loop's back edge is not routed through the layers; it is taken out of the graph before
/// ranking and drawn in a channel down the left. Two reasons. Ranking needs a graph without cycles, so
/// the edge has to come out either way; and routing it back through the middle would have it leave the
/// top of the block it comes from, when what the reader needs to see is control leaving the bottom of
/// that block and returning to the top of the header.
///
/// Everything here is geometry over numbers: no text, no fonts, no drawing. That is what makes it
/// testable, which matters more than usual because the window it is drawn in cannot be inspected.
/// </summary>
public static class LayeredLayout
{
    /// <summary>
    /// A graph this size is not a picture anyone can read, and laying it out costs more than it returns.
    /// The caller is told to show something else rather than being handed an unusable drawing.
    /// </summary>
    public const int MaxNodes = 2000;

    public static GraphLayoutResult Compute(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        int entryId,
        GraphLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        options ??= GraphLayoutOptions.Default;

        if (nodes.Count == 0)
        {
            return new GraphLayoutResult(Array.Empty<NodePlacement>(), Array.Empty<EdgeRoute>(), 0, 0);
        }

        if (nodes.Count > MaxNodes)
        {
            throw new ArgumentException($"the graph has {nodes.Count} nodes, more than the {MaxNodes} this lays out", nameof(nodes));
        }

        var g = new Graph(nodes, edges, entryId, options);
        g.ClassifyBackEdges();
        g.AssignLayers();
        g.InsertDummies();
        g.OrderWithinLayers();
        g.AssignCoordinates();
        return g.Build();
    }

    /// <summary>
    /// How many times edges cross, over an ordering. Not needed to draw anything — it is how the
    /// ordering sweep knows whether it improved, and how a test can say the sweep is worth running.
    /// </summary>
    public static int CountCrossings(GraphLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        int crossings = 0;
        var segments = layout.Edges
            .SelectMany(e => e.Points.Zip(e.Points.Skip(1), (a, b) => (A: a, B: b)))
            .ToList();

        for (int i = 0; i < segments.Count; i++)
        {
            for (int j = i + 1; j < segments.Count; j++)
            {
                if (Crosses(segments[i].A, segments[i].B, segments[j].A, segments[j].B))
                {
                    crossings++;
                }
            }
        }

        return crossings;
    }

    private static bool Crosses(GraphPoint p1, GraphPoint p2, GraphPoint p3, GraphPoint p4)
    {
        // Segments sharing an endpoint meet, they do not cross.
        if (Same(p1, p3) || Same(p1, p4) || Same(p2, p3) || Same(p2, p4))
        {
            return false;
        }

        double d1 = Cross(p3, p4, p1);
        double d2 = Cross(p3, p4, p2);
        double d3 = Cross(p1, p2, p3);
        double d4 = Cross(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));

        static double Cross(GraphPoint a, GraphPoint b, GraphPoint c)
            => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

        static bool Same(GraphPoint a, GraphPoint b) => Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;
    }

    // ------------------------------------------------------------------
    // The working graph. Real nodes keep their index; dummies are appended.
    // ------------------------------------------------------------------

    private sealed class Graph
    {
        private readonly IReadOnlyList<GraphNode> _nodes;
        private readonly IReadOnlyList<GraphEdge> _edges;
        private readonly GraphLayoutOptions _o;
        private readonly Dictionary<int, int> _indexOf;
        private readonly int _entry;

        /// <summary>Edges that close a loop, by their position in the input.</summary>
        private readonly HashSet<int> _backEdges = new();

        private int[] _layer = Array.Empty<int>();
        private double[] _width = Array.Empty<double>();
        private double[] _height = Array.Empty<double>();
        private double[] _x = Array.Empty<double>();
        private double[] _y = Array.Empty<double>();
        private int _count;

        /// <summary>Chains of dummy indices for each forward edge, source layer first.</summary>
        private readonly Dictionary<int, List<int>> _chains = new();

        /// <summary>Who each node connects to on the layer above and below, dummies included.</summary>
        private List<int>[] _above = Array.Empty<List<int>>();
        private List<int>[] _below = Array.Empty<List<int>>();
        private List<List<int>> _layers = new();

        public Graph(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, int entryId, GraphLayoutOptions options)
        {
            _nodes = nodes;
            _edges = edges;
            _o = options;
            _indexOf = new Dictionary<int, int>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
            {
                _indexOf[nodes[i].Id] = i;
            }

            _entry = _indexOf.TryGetValue(entryId, out int e) ? e : 0;
        }

        private IEnumerable<(int Index, GraphEdge Edge)> Forward()
        {
            for (int i = 0; i < _edges.Count; i++)
            {
                if (!_backEdges.Contains(i) && Known(_edges[i]))
                {
                    yield return (i, _edges[i]);
                }
            }
        }

        private bool Known(GraphEdge e) => _indexOf.ContainsKey(e.From) && _indexOf.ContainsKey(e.To);

        /// <summary>
        /// An edge to a node still on the stack of the depth-first walk goes back into the path that
        /// reached it, which is what a loop is. The walk is iterative: a deeply nested function would
        /// otherwise overflow the stack, and untrusted input decides how deep it goes.
        /// </summary>
        public void ClassifyBackEdges()
        {
            var outgoing = new List<int>[_nodes.Count];
            for (int i = 0; i < _nodes.Count; i++)
            {
                outgoing[i] = new List<int>();
            }

            for (int i = 0; i < _edges.Count; i++)
            {
                if (Known(_edges[i]))
                {
                    outgoing[_indexOf[_edges[i].From]].Add(i);
                }
            }

            var state = new byte[_nodes.Count];   // 0 unvisited, 1 on the stack, 2 done
            var stack = new Stack<(int Node, int Next)>();

            for (int start = 0; start < _nodes.Count; start++)
            {
                int root = start == 0 ? _entry : start;
                if (state[root] != 0)
                {
                    continue;
                }

                state[root] = 1;
                stack.Push((root, 0));
                while (stack.Count > 0)
                {
                    var (node, next) = stack.Pop();
                    if (next >= outgoing[node].Count)
                    {
                        state[node] = 2;
                        continue;
                    }

                    stack.Push((node, next + 1));
                    int edge = outgoing[node][next];
                    int target = _indexOf[_edges[edge].To];
                    if (state[target] == 1)
                    {
                        _backEdges.Add(edge);
                    }
                    else if (state[target] == 0)
                    {
                        state[target] = 1;
                        stack.Push((target, 0));
                    }
                }
            }
        }

        /// <summary>
        /// Longest path from the entry: a node sits one layer below the lowest thing that reaches it, so
        /// every forward edge points downwards and none is horizontal.
        /// </summary>
        public void AssignLayers()
        {
            int n = _nodes.Count;
            _layer = new int[n];
            var incoming = new int[n];
            var below = new List<int>[n];
            for (int i = 0; i < n; i++)
            {
                below[i] = new List<int>();
            }

            foreach (var (_, edge) in Forward())
            {
                int from = _indexOf[edge.From];
                int to = _indexOf[edge.To];
                if (from == to)
                {
                    continue;   // a self-loop is a back edge already; this is belt and braces
                }

                below[from].Add(to);
                incoming[to]++;
            }

            var ready = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (incoming[i] == 0)
                {
                    ready.Enqueue(i);
                }
            }

            int settled = 0;
            while (ready.Count > 0)
            {
                int node = ready.Dequeue();
                settled++;
                foreach (int next in below[node])
                {
                    _layer[next] = Math.Max(_layer[next], _layer[node] + 1);
                    if (--incoming[next] == 0)
                    {
                        ready.Enqueue(next);
                    }
                }
            }

            if (settled < n)
            {
                // Removing back edges should leave no cycle; if one survives, layer what is left by
                // distance rather than looping forever.
                for (int i = 0; i < n; i++)
                {
                    if (incoming[i] > 0)
                    {
                        _layer[i] = Math.Max(_layer[i], 1);
                    }
                }
            }
        }

        /// <summary>
        /// An edge spanning more than one layer gets a stand-in node on each layer it passes, so that
        /// ordering and spacing treat the line as something occupying room rather than as nothing at all.
        /// </summary>
        public void InsertDummies()
        {
            int n = _nodes.Count;
            var layers = new List<int>(_layer);
            var widths = new List<double>(_nodes.Select(x => x.Width));
            var heights = new List<double>(_nodes.Select(x => x.Height));

            foreach (var (index, edge) in Forward())
            {
                int from = _indexOf[edge.From];
                int to = _indexOf[edge.To];
                var chain = new List<int>();
                for (int layer = _layer[from] + 1; layer < _layer[to]; layer++)
                {
                    chain.Add(layers.Count);
                    layers.Add(layer);
                    widths.Add(0);
                    heights.Add(0);
                }

                if (chain.Count > 0)
                {
                    _chains[index] = chain;
                }
            }

            _count = layers.Count;
            _layer = layers.ToArray();
            _width = widths.ToArray();
            _height = heights.ToArray();
            _x = new double[_count];
            _y = new double[_count];

            // Adjacency over the expanded graph: every link now joins neighbouring layers.
            _above = new List<int>[_count];
            _below = new List<int>[_count];
            for (int i = 0; i < _count; i++)
            {
                _above[i] = new List<int>();
                _below[i] = new List<int>();
            }

            foreach (var (index, edge) in Forward())
            {
                int from = _indexOf[edge.From];
                int to = _indexOf[edge.To];
                if (from == to)
                {
                    continue;
                }

                var path = PathOf(index, from, to);
                for (int i = 0; i + 1 < path.Count; i++)
                {
                    _below[path[i]].Add(path[i + 1]);
                    _above[path[i + 1]].Add(path[i]);
                }
            }

            _layers = new List<List<int>>();
            int maxLayer = _count == 0 ? 0 : _layer.Max();
            for (int i = 0; i <= maxLayer; i++)
            {
                _layers.Add(new List<int>());
            }

            for (int i = 0; i < _count; i++)
            {
                _layers[_layer[i]].Add(i);
            }
        }

        private List<int> PathOf(int index, int from, int to)
        {
            var path = new List<int> { from };
            if (_chains.TryGetValue(index, out var chain))
            {
                path.AddRange(chain);
            }

            path.Add(to);
            return path;
        }

        /// <summary>
        /// Crossing reduction by the median heuristic: a node wants to sit at the median position of the
        /// things it connects to on the layer just visited. Sweeps run down then up, and an ordering is
        /// only kept when it actually crossed fewer times, so a pass can never make the picture worse.
        /// </summary>
        public void OrderWithinLayers()
        {
            var best = _layers.Select(l => new List<int>(l)).ToList();
            int bestCrossings = Crossings(_layers);

            for (int pass = 0; pass < _o.OrderingPasses && bestCrossings > 0; pass++)
            {
                Sweep(downwards: pass % 2 == 0);
                int crossings = Crossings(_layers);
                if (crossings < bestCrossings)
                {
                    bestCrossings = crossings;
                    best = _layers.Select(l => new List<int>(l)).ToList();
                }
            }

            _layers = best;
        }

        private void Sweep(bool downwards)
        {
            var order = new int[_count];
            for (int l = 0; l < _layers.Count; l++)
            {
                for (int i = 0; i < _layers[l].Count; i++)
                {
                    order[_layers[l][i]] = i;
                }
            }

            var layerIndices = Enumerable.Range(0, _layers.Count);
            foreach (int l in downwards ? layerIndices : layerIndices.Reverse())
            {
                var neighbours = downwards ? _above : _below;
                var keyed = _layers[l]
                    .Select((node, position) => (Node: node, Key: Median(neighbours[node], order, position)))
                    .OrderBy(t => t.Key)
                    .ThenBy(t => t.Node)
                    .Select(t => t.Node)
                    .ToList();

                _layers[l] = keyed;
                for (int i = 0; i < keyed.Count; i++)
                {
                    order[keyed[i]] = i;
                }
            }
        }

        /// <summary>A node with nothing on the neighbouring layer keeps where it is, rather than jumping to the front.</summary>
        private static double Median(List<int> neighbours, int[] order, int fallback)
        {
            if (neighbours.Count == 0)
            {
                return fallback;
            }

            var positions = neighbours.Select(n => (double)order[n]).OrderBy(v => v).ToList();
            int mid = positions.Count / 2;
            return positions.Count % 2 == 1 ? positions[mid] : (positions[mid - 1] + positions[mid]) / 2;
        }

        private int Crossings(List<List<int>> layers)
        {
            int total = 0;
            for (int l = 0; l + 1 < layers.Count; l++)
            {
                var position = new Dictionary<int, int>();
                for (int i = 0; i < layers[l + 1].Count; i++)
                {
                    position[layers[l + 1][i]] = i;
                }

                var targets = new List<int>();
                foreach (int node in layers[l])
                {
                    var below = _below[node].Where(position.ContainsKey).Select(n => position[n]).OrderBy(v => v);
                    targets.AddRange(below);
                }

                for (int i = 0; i < targets.Count; i++)
                {
                    for (int j = i + 1; j < targets.Count; j++)
                    {
                        if (targets[i] > targets[j])
                        {
                            total++;
                        }
                    }
                }
            }

            return total;
        }

        /// <summary>
        /// Packs each layer left to right, then pulls every node towards the average of what it connects
        /// to and pushes overlaps apart again. Order within a layer never changes, so the crossing count
        /// the previous step settled on survives.
        /// </summary>
        public void AssignCoordinates()
        {
            foreach (var layer in _layers)
            {
                double x = 0;
                foreach (int node in layer)
                {
                    _x[node] = x;
                    x += Math.Max(_width[node], 1) + _o.NodeGap;
                }
            }

            for (int pass = 0; pass < 6; pass++)
            {
                var indices = Enumerable.Range(0, _layers.Count);
                foreach (int l in pass % 2 == 0 ? indices : indices.Reverse())
                {
                    var neighbours = pass % 2 == 0 ? _above : _below;
                    foreach (int node in _layers[l])
                    {
                        if (neighbours[node].Count == 0)
                        {
                            continue;
                        }

                        double desired = neighbours[node].Average(n => _x[n] + (_width[n] / 2));
                        _x[node] = desired - (_width[node] / 2);
                    }

                    Separate(_layers[l]);
                }
            }

            double y = 0;
            foreach (var layer in _layers)
            {
                double tallest = layer.Count == 0 ? 0 : layer.Max(n => _height[n]);
                foreach (int node in layer)
                {
                    _y[node] = y;
                }

                y += tallest + _o.LayerGap;
            }
        }

        /// <summary>Restores the minimum gap between neighbours without reordering them.</summary>
        private void Separate(List<int> layer)
        {
            for (int i = 1; i < layer.Count; i++)
            {
                double least = _x[layer[i - 1]] + Math.Max(_width[layer[i - 1]], 1) + _o.NodeGap;
                if (_x[layer[i]] < least)
                {
                    _x[layer[i]] = least;
                }
            }

            for (int i = layer.Count - 2; i >= 0; i--)
            {
                double most = _x[layer[i + 1]] - Math.Max(_width[layer[i]], 1) - _o.NodeGap;
                if (_x[layer[i]] > most)
                {
                    _x[layer[i]] = most;
                }
            }
        }

        /// <summary>
        /// The lowest point any box on a layer reaches. Edges bend here rather than at the bottom of the
        /// box they leave, because a short block beside a tall one would otherwise have its edge cut
        /// straight across its neighbour.
        /// </summary>
        private double[] LayerBottoms()
        {
            var bottoms = new double[_count];
            foreach (var layer in _layers)
            {
                double lowest = layer.Count == 0 ? 0 : layer.Max(n => _y[n] + _height[n]);
                foreach (int node in layer)
                {
                    bottoms[node] = lowest;
                }
            }

            return bottoms;
        }

        public GraphLayoutResult Build()
        {
            int lanes = _backEdges.Count(i => Known(_edges[i]));
            double laneRoom = lanes * _o.BackEdgeLane;
            var layerBottom = LayerBottoms();

            double minX = _nodes.Count == 0 ? 0 : Enumerable.Range(0, _nodes.Count).Min(i => _x[i]);
            double shiftX = _o.Margin + laneRoom - minX;
            double shiftY = _o.Margin;

            var placements = new List<NodePlacement>(_nodes.Count);
            for (int i = 0; i < _nodes.Count; i++)
            {
                placements.Add(new NodePlacement(_nodes[i].Id, _x[i] + shiftX, _y[i] + shiftY, _nodes[i].Width, _nodes[i].Height));
            }

            var byId = placements.ToDictionary(p => p.Id);
            var routes = new List<EdgeRoute>(_edges.Count);
            int lane = 0;

            for (int index = 0; index < _edges.Count; index++)
            {
                var edge = _edges[index];
                if (!Known(edge) || !byId.TryGetValue(edge.From, out var from) || !byId.TryGetValue(edge.To, out var to))
                {
                    continue;
                }

                int fromIndex = _indexOf[edge.From];
                if (_backEdges.Contains(index))
                {
                    routes.Add(BackRoute(edge, from, to, lane++, laneRoom, layerBottom[fromIndex] + shiftY));
                    continue;
                }

                // Every bend happens in the empty band between two layers, and every run through a layer
                // is vertical, in a column the ordering reserved. That is what keeps a line off the boxes
                // it passes rather than merely usually clear of them.
                var points = new List<GraphPoint>
                {
                    new(from.CenterX, from.Bottom),
                    new(from.CenterX, layerBottom[fromIndex] + shiftY),
                };

                if (_chains.TryGetValue(index, out var chain))
                {
                    foreach (int dummy in chain)
                    {
                        double x = _x[dummy] + shiftX;
                        points.Add(new GraphPoint(x, _y[dummy] + shiftY));
                        points.Add(new GraphPoint(x, layerBottom[dummy] + shiftY));
                    }
                }

                points.Add(new GraphPoint(to.CenterX, to.Y));
                routes.Add(new EdgeRoute(edge.From, edge.To, edge.Kind, Simplify(points)));
            }

            double width = placements.Count == 0 ? 0 : placements.Max(p => p.Right);
            double height = placements.Count == 0 ? 0 : placements.Max(p => p.Bottom);
            width = Math.Max(width, routes.SelectMany(r => r.Points).Select(p => p.X).DefaultIfEmpty(0).Max());
            height = Math.Max(height, routes.SelectMany(r => r.Points).Select(p => p.Y).DefaultIfEmpty(0).Max());

            return new GraphLayoutResult(placements, routes, width + _o.Margin, height + _o.Margin);
        }

        /// <summary>
        /// A loop edge leaves the bottom of the block it comes from, runs down its own channel to the
        /// left of everything, and comes back into the top of the header. Each one gets a channel of its
        /// own, so two loops closing on the same header stay apart.
        /// </summary>
        private EdgeRoute BackRoute(GraphEdge edge, NodePlacement from, NodePlacement to, int lane, double laneRoom, double sourceLayerBottom)
        {
            double laneX = _o.Margin + laneRoom - ((lane + 1) * _o.BackEdgeLane);

            // Both horizontal runs sit in the band between two layers, where there is nothing to cross:
            // below everything on the source's layer, and above everything on the target's.
            double under = sourceLayerBottom + (_o.LayerGap / 2);
            double over = to.Y - (_o.LayerGap / 2);

            return new EdgeRoute(edge.From, edge.To, GraphEdgeKind.Back, Simplify(new List<GraphPoint>
            {
                new(from.CenterX, from.Bottom),
                new(from.CenterX, under),
                new(laneX, under),
                new(laneX, over),
                new(to.CenterX, over),
                new(to.CenterX, to.Y),
            }));
        }

        /// <summary>Drops points a line already passes through, so a straight edge stays two points.</summary>
        private static List<GraphPoint> Simplify(List<GraphPoint> points)
        {
            var kept = new List<GraphPoint> { points[0] };
            for (int i = 1; i < points.Count; i++)
            {
                var previous = kept[^1];
                if (Math.Abs(points[i].X - previous.X) < 0.01 && Math.Abs(points[i].Y - previous.Y) < 0.01)
                {
                    continue;
                }

                // Three points on one straight line: the middle one says nothing.
                if (kept.Count >= 2 && Collinear(kept[^2], previous, points[i]))
                {
                    kept[^1] = points[i];
                    continue;
                }

                kept.Add(points[i]);
            }

            return kept;
        }

        private static bool Collinear(GraphPoint a, GraphPoint b, GraphPoint c)
            => Math.Abs(((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X))) < 0.01;
    }
}
