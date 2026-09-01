using Spydate.Core.PE;
using Spydate.Disassembly;
using Spydate.Mcp;
using Spydate.Mcp.Rendering;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// The session an agent works against, and the one screen it reads before it can ask anything else.
/// The tool methods are ordinary public methods returning strings — the MCP attributes are metadata —
/// so these call them directly, with no client and no protocol in the way.
/// </summary>
public class McpSessionTests
{
    private static BinarySession Session(string path)
        => new(path, Corpus.Image(path), Corpus.Analysed(path), null, new DiscoveryState(Corpus.Analysed(path).FunctionCount, true, TimeSpan.FromSeconds(1)));

    private static SessionStore StoreWith(string path)
    {
        var store = new SessionStore();
        store.Set(Session(path));
        return store;
    }

    // ------------------------------------------------------------------
    // Orientation
    // ------------------------------------------------------------------

    [Fact]
    public void TheOverviewAnswersWhatAnAgentNeedsBeforeItCanAskAnything()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        string text = new SessionTools(StoreWith(Corpus.NotepadX64), McpOptions.Default).GetOverview();

        // Every one of these is a round trip the agent does not have to spend.
        Assert.Contains("notepad.exe", text, StringComparison.Ordinal);
        Assert.Contains("Amd64", text, StringComparison.Ordinal);
        Assert.Contains("entry     0x", text, StringComparison.Ordinal);
        Assert.Contains(".text", text, StringComparison.Ordinal);
        Assert.Contains("modules,", text, StringComparison.Ordinal);
        Assert.Contains("functions, discovery complete", text, StringComparison.Ordinal);
        Assert.Contains("next      list_functions", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverviewIsSmallEnoughToBeWorthReading()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        // It is the first thing in the agent's context and stays there. A screen, not a chapter.
        string text = new SessionTools(StoreWith(Corpus.NotepadX64), McpOptions.Default).GetOverview();

        Assert.True(text.Length < 1400, $"the overview is {text.Length} characters");
    }

    [Fact]
    public void AskingAboutNothingSaysWhatToDoAboutIt()
    {
        string text = new SessionTools(new SessionStore(), McpOptions.Default).GetOverview();

        Assert.Contains("open_binary", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningSomethingThatIsNotAPeFileExplainsItselfRatherThanThrowing()
    {
        // A tool that throws gives the agent a stack trace; one that answers gives it a next move.
        var tools = new SessionTools(new SessionStore(), McpOptions.Default);
        string missing = await tools.OpenBinaryAsync(Path.Combine(Path.GetTempPath(), "no-such-spydate-file.exe"));
        string notPe = await tools.OpenBinaryAsync(typeof(McpSessionTests).Assembly.Location.Replace(".dll", ".runtimeconfig.json", StringComparison.Ordinal));

        Assert.Contains("no file at", missing, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", notPe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARootedServerWillNotOpenOutsideIt()
    {
        var options = new McpOptions { Root = Path.GetTempPath() };
        var tools = new SessionTools(new SessionStore(), options);

        string text = await tools.OpenBinaryAsync(@"C:\Windows\System32\notepad.exe");

        Assert.Contains("--root", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", false)]
    [InlineData(@"C:\Windows\Temp\..\System32\notepad.exe", false)]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", true)]
    public void ARootConfinesWhatCanBeOpened(string path, bool allowed)
    {
        // The traversal case is the one that matters: comparing full paths is what stops "..".
        var options = new McpOptions { Root = @"C:\Windows\System32\drivers" };

        Assert.Equal(allowed, options.Allows(path));
    }

    // ------------------------------------------------------------------
    // Resolving what an agent writes into an address
    // ------------------------------------------------------------------

    [Fact]
    public void EveryFormAnAgentMightUseResolvesToTheSameAddress()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var session = Session(Corpus.NotepadX64);
        var function = session.Functions.First(f => f.Name.StartsWith("sub_", StringComparison.Ordinal));

        foreach (string form in new[] { $"0x{function.EntryVa:X}", $"{function.EntryVa:X}", function.Name })
        {
            var resolved = Targets.Resolve(session, form);
            Assert.True(resolved.Found, form);
            Assert.Equal(function.EntryVa, resolved.Va);
        }
    }

    [Fact]
    public void AMissedNameOffersTheOnesItMightHaveMeant()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var result = Targets.Resolve(Session(Corpus.NotepadX64), "CreateFil");

        Assert.False(result.Found);
        Assert.Contains("Did you mean", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressInsideAFunctionResolvesToTheFunction()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        // The guard that matters. GetOrDiscoverFunction would happily invent a function at a call
        // site or a search hit and cache a junk symbol for it; agents hand back exactly those
        // addresses, because those are the ones they were just shown.
        var session = Session(Corpus.NotepadX64);
        var function = session.Functions.First(f => f.InstructionCount > 4);
        ulong inside = function.Instructions.Skip(2).First().Va;

        int before = session.Analysis!.FunctionCount;
        var (target, resolved, redirected) = Targets.ResolveFunction(session, $"0x{inside:X}");

        Assert.True(redirected);
        Assert.Equal(function.EntryVa, target.Va);
        Assert.Equal(function.EntryVa, resolved!.EntryVa);
        Assert.Equal(before, session.Analysis.FunctionCount);   // nothing was invented
    }

    [Fact]
    public void AnAddressInNoFunctionIsRefusedRatherThanDiscovered()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var session = Session(Corpus.NotepadX64);
        int before = session.Analysis!.FunctionCount;

        var (target, function, _) = Targets.ResolveFunction(session, "0x1");

        Assert.False(target.Found);
        Assert.Null(function);
        Assert.Equal(before, session.Analysis.FunctionCount);
    }

    // ------------------------------------------------------------------
    // The budget, which is enforced centrally so no tool can forget it
    // ------------------------------------------------------------------

    [Fact]
    public void ClippingCutsWholeLinesAndSaysHowManyItDropped()
    {
        string text = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"line {i} bbbbbbbbbb"));

        string clipped = Budget.Clip(text, 200);

        Assert.True(clipped.Length < text.Length);
        Assert.DoesNotContain("bbbbbbbbb\n", clipped.Replace("bbbbbbbbbb", "x", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("more lines were cut", clipped, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowSaysWhereItIsAndHowToGetTheRest()
    {
        string text = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i}"));

        string window = Budget.Window(text, offset: 10, maxLines: 5, continuation: "read_function(offset=15)");

        Assert.Contains("lines 11-15 of 50", window, StringComparison.Ordinal);
        Assert.Contains("line 11", window, StringComparison.Ordinal);
        Assert.Contains("line 15", window, StringComparison.Ordinal);
        Assert.DoesNotContain("line 16", window, StringComparison.Ordinal);
        Assert.Contains("35 more lines. read_function(offset=15)", window, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessiveWindowsReconstructTheWholeThing()
    {
        // The invariant behind paging a function body: an agent that walks it sees all of it, with
        // nothing dropped and nothing repeated at the seams.
        var lines = Enumerable.Range(1, 137).Select(i => $"line {i}").ToList();
        string text = string.Join('\n', lines);

        var rebuilt = new List<string>();
        for (int offset = 0; offset < lines.Count; offset += 40)
        {
            string window = Budget.Window(text, offset, 40, "next");
            rebuilt.AddRange(window.Split('\n').Where(l => !l.StartsWith("---", StringComparison.Ordinal) && l.Length > 0));
        }

        Assert.Equal(lines, rebuilt);
    }

    [Fact]
    public void AnOverlongValueKeepsBothEnds()
    {
        // Mangled C++ names run to hundreds of characters, and the informative parts are the front
        // and the back; a plain truncation throws away the half that says what it returns.
        string elided = Budget.Elide("?Foo@Bar@@QEAAXXZ_and_a_great_deal_more_besides_here", 20);

        Assert.Equal(20, elided.Length);
        Assert.StartsWith("?Foo", elided, StringComparison.Ordinal);
        Assert.EndsWith("here", elided, StringComparison.Ordinal);
        Assert.Contains('\u2026', elided);
    }

    [Fact]
    public void AListSaysHowMuchItDidNotShow()
    {
        // Three numbers, and the third is the one usually missing: without the total an agent cannot
        // tell whether its filter narrowed anything or matched everything.
        string meta = TextTable.Meta(returned: 40, matching: 862, total: 1284, subject: "functions", next: "after_va=0x1400034A0");

        Assert.Contains("40 of 862 matching (of 1284 functions)", meta, StringComparison.Ordinal);
        Assert.Contains("next: after_va=0x1400034A0", meta, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyTableSaysSoRatherThanReturningNothing()
    {
        var table = new TextTable(("name", 20), ("va", 18));

        Assert.Equal("nothing matched", table.Render());
    }

    [Fact]
    public void ColumnsLineUpAndOverlongCellsAreCut()
    {
        var table = new TextTable(("name", 8), ("va", 18));
        table.Add("short", "0x1000");
        table.Add("a_very_long_name_indeed", "0x2000");

        var lines = table.Render().Split('\n');

        Assert.Equal(3, lines.Length);

        // A ragged table is unreadable and, worse, ambiguous about which value is in which column.
        int column = lines[0].IndexOf("va", StringComparison.Ordinal);
        Assert.Equal(column, lines[1].IndexOf("0x1000", StringComparison.Ordinal));
        Assert.Equal(column, lines[2].IndexOf("0x2000", StringComparison.Ordinal));

        // The over-long name was cut to the column's limit rather than pushing everything sideways.
        Assert.Equal(8, lines[2][..column].TrimEnd().Length);
    }
}
