using System.Xml.Linq;
using Spydate.Core.Graph;
using Spydate.Core.PE;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// The exported drawing. It matters twice over: it is what "Export SVG" writes, and it is the only form
/// of the graph that can be looked at outside the window, which is how the layout was judged readable
/// in the first place.
/// </summary>
public class GraphSvgTests
{
    private static Function Build(byte[] code, ulong baseVa = 0x1000, int bitness = 32)
    {
        var symbols = new Core.Symbols.SymbolTable();
        var source = new MemoryCodeSource(code, baseVa, bitness);
        return new FunctionDiscovery(source, new X86Disassembler(bitness, symbols), symbols).Discover(baseVa);
    }

    /// <summary>cmp/jl over two arms, so there is a branch, a join and three boxes.</summary>
    private static Function Branchy() => Build(new byte[]
    {
        0x83, 0xF9, 0x0A,             // 1000 cmp ecx, 0xa
        0x7C, 0x06,                   // 1003 jl 100b
        0xB8, 0x01, 0x00, 0x00, 0x00, // 1005 mov eax, 1
        0xC3,                         // 100a ret
        0xB8, 0x02, 0x00, 0x00, 0x00, // 100b mov eax, 2
        0xC3,                         // 1010 ret
    });

    [Fact]
    public void TheExportIsWellFormedXml()
    {
        string svg = FunctionGraphs.Build(Branchy()).ToSvg();

        var root = XDocument.Parse(svg).Root!;

        Assert.Equal("svg", root.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", root.Name.NamespaceName);
    }

    [Fact]
    public void EveryBlockAndEveryEdgeIsDrawn()
    {
        var graph = FunctionGraphs.Build(Branchy());
        var root = XDocument.Parse(graph.ToSvg()).Root!;
        var ns = root.Name.Namespace;

        // One background rectangle plus one per box.
        Assert.Equal(graph.Blocks.Count + 1, root.Descendants(ns + "rect").Count());
        Assert.Equal(graph.Layout.Edges.Count, root.Descendants(ns + "polyline").Count());
    }

    [Fact]
    public void TheInstructionsAreInTheDrawing()
    {
        string svg = FunctionGraphs.Build(Branchy()).ToSvg();

        Assert.Contains("cmp ecx", svg, StringComparison.Ordinal);
        Assert.Contains("mov eax, 2", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void TextThatLooksLikeMarkupIsEscaped()
    {
        // Operands carry <, > and & — "cmp byte ptr [eax], 3Ch" is harmless, but a symbol name with a
        // template in it is not, and an unescaped one makes the whole file unparseable.
        var boxes = new List<GraphBoxText> { new(0, "head", new[] { "call std::vector<int>::at & co" }, true) };
        var layout = LayeredLayout.Compute(new List<GraphNode> { new(0, 200, 40) }, Array.Empty<GraphEdge>(), 0);

        string svg = GraphSvg.Render(layout, boxes, 7, 15);

        Assert.DoesNotContain("<int>", svg, StringComparison.Ordinal);
        Assert.Contains("&lt;int&gt;", svg, StringComparison.Ordinal);
        _ = XDocument.Parse(svg);
    }

    [Fact]
    public void TheEntryBlockIsMarkedOut()
    {
        var graph = FunctionGraphs.Build(Branchy());
        var theme = GraphSvgTheme.Dark;

        string svg = graph.ToSvg(theme);

        // Exactly one box is drawn in the entry colour: the one the function starts at.
        Assert.Single(graph.Blocks, b => b.IsEntry);
        Assert.Equal(0x1000UL, graph.Blocks.Single(b => b.IsEntry).StartVa);
        Assert.Contains($"stroke=\"{theme.EntryStroke}\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongBlockKeepsItsEndsAndSaysWhatWasLeftOut()
    {
        // 200 single-byte nops then a ret: a box with every line would be metres tall.
        var code = Enumerable.Repeat((byte)0x90, 200).Append((byte)0xC3).ToArray();

        var graph = FunctionGraphs.Build(Build(code));
        var block = graph.Blocks.Single();

        Assert.True(block.Lines.Count <= GraphMetrics.Default.MaxLines, $"{block.Lines.Count} lines");
        Assert.Contains(block.Lines, l => l.Contains("more instructions", StringComparison.Ordinal));
        Assert.StartsWith("1000", block.Lines[0], StringComparison.Ordinal);
        Assert.Contains("ret", block.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFunctionOfARealBinaryExportsAsValidXml()
    {
        const string path = @"C:\Windows\System32\notepad.exe";
        if (!File.Exists(path))
        {
            return;
        }

        var analysis = new BinaryAnalysis(PeImage.Load(path));
        analysis.DiscoverAll();

        int exported = 0;
        foreach (var function in analysis.Functions.OrderBy(f => f.EntryVa).Where(f => f.Blocks.Count is > 1 and < 60).Take(120))
        {
            // Real operands carry the characters that break XML, and real symbol names carry more.
            _ = XDocument.Parse(FunctionGraphs.Build(function).ToSvg());
            exported++;
        }

        Assert.True(exported > 60, $"only {exported} functions were exported");
    }
}
