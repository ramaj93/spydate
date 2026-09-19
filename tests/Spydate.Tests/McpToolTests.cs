using Spydate.Core.PE;
using Spydate.Decompiler.Managed;
using Spydate.Mcp;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// The tools an agent uses to find something worth reading, and to read it. Called directly — the MCP
/// attributes are metadata, so none of this needs a client or the protocol.
/// </summary>
public class McpToolTests
{
    private static SessionStore Store(string path)
    {
        var analysis = Corpus.Analysed(path);
        var store = new SessionStore();
        store.Set(new BinarySession(path, Corpus.Image(path), analysis, null, new DiscoveryState(analysis.FunctionCount, true, TimeSpan.FromSeconds(1))));
        return store;
    }

    private static NavigationTools Nav(string path) => new(Store(path));

    private static CodeTools Code(string path) => new(Store(path));

    [Fact]
    public void ReadFunctionReadsAMethodInAReferencedAssembly()
    {
        // The test assembly references Spydate.Core, so PeImage::Load is not in it but is one
        // reference away — the exact shape of reading a framework or dependency method by name.
        string testAssembly = typeof(McpToolTests).Assembly.Location;
        var store = new SessionStore();
        store.Set(new BinarySession(
            testAssembly, Corpus.Image(testAssembly), null, null, DiscoveryState.None,
            managed: ManagedAssembly.Load(testAssembly)));

        string text = new CodeTools(store).ReadFunction("Spydate.Core.PE.PeImage::Load", view: "csharp");

        // Read through the assembly that has it, not refused as absent from the opened one.
        Assert.Contains("Load", text, StringComparison.Ordinal);
        Assert.Contains("Spydate.Core", text, StringComparison.Ordinal);
        Assert.DoesNotContain("is not a", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindSymbolSurfacesAPInvokeByItsNativeTarget()
    {
        // Spydate.Debugger P/Invokes into kernel32. A managed image never shows that in its PE import
        // table, so an agent hunting a native call has to find it through the metadata — which is what
        // find_symbol now does, rather than leaving it to parse the ImplMap table out of the bytes.
        string debugger = typeof(Spydate.Debugger.Managed.ManagedDebugSession).Assembly.Location;
        var store = new SessionStore();
        store.Set(new BinarySession(
            debugger, Corpus.Image(debugger), null, null, DiscoveryState.None,
            managed: ManagedAssembly.Load(debugger)));

        string text = new NavigationTools(store).FindSymbol("kernel32");

        Assert.Contains("pinvoke", text, StringComparison.Ordinal);
        Assert.Contains("kernel32", text, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // The worklist
    // ------------------------------------------------------------------

    [Fact]
    public void TheWorklistIsUnnamedFunctionsMostUsedFirst()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        // "What should I name next, by payoff" is the question that starts the loop. Address order
        // over hundreds of functions answers a different, useless question.
        string text = Nav(Corpus.NotepadX64).ListFunctions(named: "unnamed", sort: "refs", limit: 10);
        var refs = Rows(text).Select(r => int.Parse(r.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1], System.Globalization.CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(10, refs.Count);
        Assert.Equal(refs.OrderByDescending(r => r), refs);
        Assert.All(Rows(text), r => Assert.Contains("sub_", r, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryRowNamesAnAddressThatIsReallyAFunction()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        string text = Nav(Corpus.NotepadX64).ListFunctions(limit: 30);

        foreach (string row in Rows(text))
        {
            ulong va = Convert.ToUInt64(row.Split(' ')[0][2..], 16);
            Assert.True(analysis.TryGetFunction(va, out _), $"0x{va:X} came back from list_functions but is not a function");
        }
    }

    [Fact]
    public void APageSaysHowToGetTheNextOne()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var tools = Nav(Corpus.NotepadX64);
        string first = tools.ListFunctions(limit: 5);
        string cursor = Rows(first)[^1].Split(' ')[0];

        // A cursor, not an offset: the function set can grow mid-session, and an offset walk would
        // silently repeat and skip rows as everything below it shifted.
        Assert.Contains($"after_va=\"{cursor}\"", first, StringComparison.Ordinal);

        string second = tools.ListFunctions(afterVa: cursor, limit: 5);
        Assert.Empty(Rows(first).Intersect(Rows(second), StringComparer.Ordinal));
    }

    [Fact]
    public void EveryListSaysHowMuchOfTheWholeItShowed()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        string text = Nav(Corpus.NotepadX64).ListFunctions(named: "unnamed", limit: 5);

        Assert.Contains($"(of {analysis.FunctionCount} functions)", text, StringComparison.Ordinal);
        Assert.Contains("5 of ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALimitBeyondTheCapIsClampedRatherThanObeyed()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        string text = Nav(Corpus.NotepadX64).ListFunctions(limit: 100_000);

        Assert.True(Rows(text).Count <= 200, $"{Rows(text).Count} rows came back");
        Assert.True(text.Length <= Spydate.Mcp.Rendering.Budget.MaxChars, $"{text.Length} characters came back");
    }

    // ------------------------------------------------------------------
    // Following references, which is the other half of the loop
    // ------------------------------------------------------------------

    [Fact]
    public void WhoCallsAnImportIsAnswerableFromItsName()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        var slot = analysis.Symbols.All.First(s => s.Name.EndsWith("!CreateFileW", StringComparison.Ordinal));

        string text = Nav(Corpus.NotepadX64).Xrefs(target: $"0x{slot.Va:X}", limit: 5);

        Assert.Contains("CreateFileW", text, StringComparison.Ordinal);
        Assert.Contains("indirectcall", text, StringComparison.Ordinal);

        // Every site names the function it is in, which is the address worth reading next.
        foreach (string row in Rows(text).Where(r => r.StartsWith("0x", StringComparison.Ordinal)))
        {
            Assert.Contains("+0x", row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnImportCarriesWhatTheDllSaidItTakes()
    {
        if (!Corpus.Has(Corpus.NotepadX86))
        {
            return;
        }

        // 32-bit stdcall states its argument count exactly, in its own ret N.
        string text = Nav(Corpus.NotepadX86).ListImports(filter: "CreateFileW");

        Assert.Contains("CreateFileW", text, StringComparison.Ordinal);
        Assert.Contains("7 args", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyTakesColumnIsExplainedRatherThanLeftAmbiguous()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        // Blank means "nobody could read the DLL", not "takes nothing" — an agent that confuses the
        // two writes a call with no arguments and believes it.
        string text = Nav(Corpus.NotepadX64).ListImports(limit: 60);

        Assert.Contains("takes", text, StringComparison.Ordinal);
        if (text.Contains("an empty 'takes'", StringComparison.Ordinal))
        {
            Assert.Contains("unknown, not none", text, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    [Fact]
    public void ReadingAFunctionAnswersWhatItIsCalledByAndWhatItCalls()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        var function = analysis.Functions.First(f => f.Blocks.Count is > 3 and < 20 && f.CallTargets.Count > 0);

        string text = Code(Corpus.NotepadX64).ReadFunction($"0x{function.EntryVa:X}");

        // Three round trips the agent does not have to spend.
        Assert.Contains($"0x{function.EntryVa:X}", text, StringComparison.Ordinal);
        Assert.Contains("calls       ", text, StringComparison.Ordinal);
        Assert.Contains("--- lines 1-", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressInsideAFunctionReadsTheFunctionAndSaysSo()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        var function = analysis.Functions.First(f => f.InstructionCount > 6);
        ulong inside = function.Instructions.Skip(3).First().Va;

        string text = Code(Corpus.NotepadX64).ReadFunction($"0x{inside:X}");

        Assert.Contains($"(0x{inside:X} is inside this function)", text, StringComparison.Ordinal);
        Assert.Contains($"0x{function.EntryVa:X}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongFunctionIsWindowedAndSaysHowToContinue()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        var function = analysis.Functions.OrderByDescending(f => f.InstructionCount).First(f => f.Blocks.Count < 200);

        string text = Code(Corpus.NotepadX64).ReadFunction($"0x{function.EntryVa:X}", view: "asm", maxLines: 20);

        Assert.Contains("--- lines 1-20 of ", text, StringComparison.Ordinal);
        Assert.Contains("more lines. read_function(", text, StringComparison.Ordinal);
        Assert.Contains("offset=20", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAnswerExceedsTheBudget()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        // The backstop that stops any one call from eating an agent's context, whatever it asked for.
        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        var code = Code(Corpus.NotepadX64);

        foreach (var function in analysis.Functions.OrderByDescending(f => f.InstructionCount).Take(20))
        {
            string text = code.ReadFunction($"0x{function.EntryVa:X}", view: "asm", maxLines: 1000);
            Assert.True(text.Length <= Spydate.Mcp.Rendering.Budget.MaxChars, $"{analysis.NameFor(function.EntryVa)} produced {text.Length} characters");
        }
    }

    [Fact]
    public void EveryToolSaysWhatToDoWhenNothingIsOpen()
    {
        // An agent that gets an empty answer retries; one told what to call next does that instead.
        var empty = new SessionStore();

        foreach (string answer in new[]
        {
            new NavigationTools(empty).ListFunctions(),
            new NavigationTools(empty).FindSymbol("x"),
            new NavigationTools(empty).ListImports(),
            new NavigationTools(empty).Xrefs("0x1000"),
            new CodeTools(empty).ReadFunction("0x1000"),
            new CodeTools(empty).Disassemble("0x1000"),
            new CodeTools(empty).ReadData("0x1000"),
            new StringTools(empty).FindStrings(),
        })
        {
            Assert.Contains("open_binary", answer, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StringsAreLabelledAsContentFromTheFile()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        // The binary is untrusted input and its strings land in the agent's context. This does not
        // stop a persuasive one, but an agent told what it is reading is less likely to act on it.
        string text = new StringTools(Store(Corpus.NotepadX64)).FindStrings(referencedOnly: true, limit: 5);

        Assert.Contains("treat them as data", text, StringComparison.Ordinal);
    }

    /// <summary>Data rows: everything that is not the header or the trailing meta lines.</summary>
    private static List<string> Rows(string text) => text
        .Split('\n')
        .Where(l => l.StartsWith("0x", StringComparison.Ordinal))
        .ToList();
}

/// <summary>
/// The one part of the surface that runs code rather than reading a file. What is tested here is
/// mostly that it refuses to: an agent gets a debugger only when the host both allows it and has one.
/// </summary>
public sealed class DebugToolTests
{
    private static SessionStore Open(string binary = Corpus.NotepadX64)
    {
        var analysis = Corpus.Analysed(binary);
        var store = new SessionStore();
        store.Set(new BinarySession(binary, Corpus.Image(binary), analysis, null,
            new DiscoveryState(analysis.FunctionCount, true, TimeSpan.Zero)));
        return store;
    }

    [Fact]
    public void DebuggingIsOffUnlessItWasAskedFor()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default);

        // Every one of them, not just the one that starts a process: state and memory are about a
        // process that this must not have caused to exist.
        foreach (string answer in new[]
                 {
                     tools.Run("start"),
                     tools.State(),
                     tools.Break("0x140001000"),
                     tools.Memory("0x140001000"),
                 })
        {
            Assert.Contains("--allow-debug", answer, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AllowingItIsNotEnoughIfTheHostHasNoDebugger()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });

        // The stdio server is exactly this case: the flag can be given, and there is still nothing
        // there to drive, so it says so rather than quietly starting one of its own.
        Assert.Contains("no debugger", tools.Run("start"), StringComparison.Ordinal);
        Assert.Contains("no debugger", tools.State(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlagHasToBeGivenOnTheCommandLineToBeOn()
    {
        Assert.False(McpOptions.Default.AllowDebug);
        Assert.False(McpOptions.Parse([]).AllowDebug);
        Assert.False(McpOptions.Parse(["--read-only"]).AllowDebug);
        Assert.True(McpOptions.Parse(["--allow-debug"]).AllowDebug);
    }

    [Fact]
    public void AnActionNobodyDefinedIsRefusedByName()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true }) ;
        store.Debug = new StubDebug();

        string answer = tools.Run("detonate");

        Assert.Contains("no such action", answer, StringComparison.Ordinal);
        Assert.Contains("step_over", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void RunToWithoutSomewhereToRunToSaysSoRatherThanRunning()
    {
        using var store = Open();
        var stub = new StubDebug();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        Assert.Contains("needs an address", tools.Run("run_to"), StringComparison.Ordinal);
        Assert.Empty(stub.Done);
    }

    [Fact]
    public void EachActionDrivesTheHostsOwnDebuggerAndReportsWhereItGot()
    {
        using var store = Open();
        var stub = new StubDebug();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        tools.Run("continue");
        tools.Run("step");
        tools.Run("step_over");

        Assert.Equal(new[] { "continue", "step", "step_over" }, stub.Done);

        // And what comes back is the state after it settled, not the state when it was asked.
        string state = tools.State();
        Assert.Contains("stopped at 0x140001000", state, StringComparison.Ordinal);
        Assert.Contains("rax=", state, StringComparison.Ordinal);
        Assert.Contains("ZF 1", state, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryComesBackAsHexAndAscii()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = new StubDebug();

        string dump = tools.Memory("0x140001000", 8);

        Assert.Contains("4D 5A", dump, StringComparison.Ordinal);
        Assert.Contains("MZ", dump, StringComparison.Ordinal);
    }

    /// <summary>A debugger that records what it was told to do and reports a fixed stop.</summary>
    /// <summary>
    /// The loop that got reported: the process had run, loaded the DLL and exited, and the agent
    /// went on asking it to continue. Each request did nothing, came back saying "still running",
    /// and left asking again as the only thing to try.
    /// </summary>
    [Fact]
    public void ContinuingAProcessThatHasExitedSaysSoInsteadOfInvitingAnotherGo()
    {
        using var store = Open();
        var stub = new StubDebug { State = "exited", Status = "Exited with code 3221225477 (0xC0000005)." };
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        foreach (string action in new[] { "continue", "step", "step_over" })
        {
            string answer = tools.Run(action);

            Assert.Contains("exited", answer, StringComparison.Ordinal);
            Assert.Contains("0xC0000005", answer, StringComparison.Ordinal);
            Assert.Contains("start", answer, StringComparison.Ordinal);
        }

        // And it did not touch the debugger at all, rather than asking it to do something it cannot.
        Assert.Empty(stub.Done);
    }

    [Fact]
    public void AProcessStillRunningIsNotAskedToContinueEither()
    {
        using var store = Open();
        var stub = new StubDebug { State = "running", Status = "Running." };
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        Assert.Contains("has not stopped", tools.Run("continue"), StringComparison.Ordinal);
        Assert.Empty(stub.Done);
    }

    /// <summary>
    /// What actually happened, not just what state it ended in. The module arriving, the breakpoint
    /// firing and the exit were all in front of the analyst and none of it reached the agent.
    /// </summary>
    [Fact]
    public void TheSnapshotCarriesWhatTheProcessHasBeenDoing()
    {
        using var store = Open();
        var stub = new StubDebug
        {
            Recent =
            [
                "10:38:11  dcsxpdf.dll loaded at 0x7FFA21F70000 (file says 0x180000000); 1 breakpoint armed",
                "10:38:12  breakpoint at 0x1800040C0",
            ],
        };

        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        string state = tools.State();

        Assert.Contains("dcsxpdf.dll loaded", state, StringComparison.Ordinal);
        Assert.Contains("breakpoint at 0x1800040C0", state, StringComparison.Ordinal);
    }

    /// <summary>
    /// A breakpoint reached after the settle window has closed. The DLL under a host in the run this
    /// came from loaded over a minute in, so the hit lands long after "continue" has answered — and
    /// there was no action that meant "be there when it does".
    /// </summary>
    [Fact]
    public void WaitingIsHowABreakpointHitLaterIsSeenAtAll()
    {
        using var store = Open();
        // Running when asked, and stopped by the time the wait comes back — which is the only
        // arrangement in which waiting means anything.
        var stub = new StubDebug
        {
            State = "running",
            Status = "Stopped at 0x1800040C0.",
            BecomesOnWait = "stopped",
            At = 0x1800040C0,
            Recent = ["10:38:11  dcsxpdf.dll loaded at 0x7FFA21F70000; 1 breakpoint armed", "10:38:12  breakpoint at 0x1800040C0"],
        };

        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        string answer = tools.Run("wait");

        // Waiting asks the process for nothing; it only reports what it found.
        Assert.Empty(stub.Done);
        Assert.Contains("breakpoint at 0x1800040C0", answer, StringComparison.Ordinal);
        Assert.Contains("stopped at 0x1800040C0", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Waiting means the next stop. Asked while it is already stopped there is no next one — nothing
    /// happens until something lets it run — and returning the state it already had read exactly
    /// like having waited and found nothing had changed.
    /// </summary>
    [Fact]
    public void WaitingWhileAlreadyStoppedSaysSoRatherThanLookingLikeItWaited()
    {
        using var store = Open();
        var stub = new StubDebug { State = "stopped", Status = "Stopped at 0x1800040C0." };
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        string answer = tools.Run("wait");

        Assert.Contains("already stopped", answer, StringComparison.Ordinal);
        Assert.Contains("continue or step", answer, StringComparison.Ordinal);
        Assert.Empty(stub.Done);
    }

    [Fact]
    public void WaitingOnSomethingThatHasGoneSaysThereIsNothingToWaitFor()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });

        store.Debug = new StubDebug { State = "exited", Status = "Exited with code 0 (normally)." };
        Assert.Contains("nothing left to wait for", tools.Run("wait"), StringComparison.Ordinal);

        store.Debug = new StubDebug { State = "not running", Status = "Not running." };
        Assert.Contains("nothing to wait for", tools.Run("wait"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDeadEndPointsAtWaitRatherThanAtTryingTheSameThing()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });

        // Running and refusing to continue.
        store.Debug = new StubDebug { State = "running", Status = "Running." };
        Assert.Contains("wait", tools.Run("continue"), StringComparison.Ordinal);

        // And the timeout, which is the reply an agent gets most often on a real program.
        store.Debug = new StubDebug { State = "running", Status = "Running.", Settles = false };
        string timedOut = tools.Run("wait");
        Assert.Contains("Use wait to keep waiting", timedOut, StringComparison.Ordinal);
        Assert.Contains("host loads the module", timedOut, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Patches tried in the running process and never written down
    // ------------------------------------------------------------------

    private static ulong EntryOf(SessionStore store)
        => store.Current!.Image.RvaToVa(store.Current.Image.EntryPointRva);

    /// <summary>
    /// A hypothesis goes into the process and nowhere else. Most guesses are wrong, and a project
    /// holding a history of reverted guesses is a worse record than one holding only what was true.
    /// </summary>
    [Fact]
    public void APatchTriedLiveReachesTheProcessAndNotTheProject()
    {
        using var store = Open();
        var stub = new StubDebug();
        store.Debug = stub;
        var tools = new PatchTools(store, McpOptions.Default with { AllowDebug = true });
        ulong va = EntryOf(store);

        string answer = tools.Patch($"0x{va:X}", "nop", "does this check matter?", keep: false);

        Assert.Contains(stub.Done, d => d.StartsWith("try:", StringComparison.Ordinal));
        Assert.Contains("Nothing is recorded", answer, StringComparison.Ordinal);
        Assert.Equal(0, store.Current!.Patches.Count);
    }

    /// <summary>
    /// Trying one changes a running process, which is the thing this server does not do unless it was
    /// asked to — the same gate the debug tools are behind, for the same reason.
    /// </summary>
    [Fact]
    public void TryingAPatchLiveNeedsDebuggingTurnedOn()
    {
        using var store = Open();
        var tools = new PatchTools(store, McpOptions.Default);
        ulong va = EntryOf(store);

        string answer = tools.Patch($"0x{va:X}", "nop", keep: false);

        Assert.Contains("--allow-debug", answer, StringComparison.Ordinal);
        Assert.Equal(0, store.Current!.Patches.Count);
    }

    /// <summary>
    /// Undoing is the same word whichever kind it was, so revert_patch answers for both rather than
    /// saying "no patch covers that" about something it can see in the process.
    /// </summary>
    [Fact]
    public void RevertingTakesBackSomethingOnlyTriedLive()
    {
        using var store = Open();
        var stub = new StubDebug();
        store.Debug = stub;
        var tools = new PatchTools(store, McpOptions.Default with { AllowDebug = true });
        ulong va = EntryOf(store);
        tools.Patch($"0x{va:X}", "nop", keep: false);

        string answer = tools.RevertPatch($"0x{va:X}");

        Assert.Contains("took the live patch", answer, StringComparison.Ordinal);
        Assert.Contains(stub.Done, d => d.StartsWith("undo:", StringComparison.Ordinal));
        Assert.Empty(stub.Tried);
    }

    [Fact]
    public void ListingKeepsWhatWasTriedApartFromWhatWasRecorded()
    {
        using var store = Open();
        store.Debug = new StubDebug();
        var tools = new PatchTools(store, McpOptions.Default with { AllowDebug = true });
        ulong va = EntryOf(store);
        tools.Patch($"0x{va:X}", "nop", keep: false);

        string list = tools.ListPatches();

        Assert.Contains("tried live, not recorded", list, StringComparison.Ordinal);
        Assert.Contains("no recorded patches", list, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Threads, which the agent could not see at all
    // ------------------------------------------------------------------

    [Fact]
    public void TheStateNamesEveryThreadAndWhoseRegistersTheseAre()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = new StubDebug();

        string state = tools.State();

        Assert.Contains("threads: 1368 (stopped it, shown), 4412", state, StringComparison.Ordinal);
        Assert.Contains("registers of thread 1368:", state, StringComparison.Ordinal);
        Assert.Contains("stack of thread 1368:", state, StringComparison.Ordinal);
    }

    [Fact]
    public void SteppingAChosenThreadPicksItBeforeMovingIt()
    {
        using var store = Open();
        var stub = new StubDebug();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        string answer = tools.Run("step", thread: 4412);

        // Picked first, then stepped: the other order steps whichever thread was shown before.
        Assert.Equal(["select:4412", "step"], stub.Done);
        Assert.Contains("registers of thread 4412:", answer, StringComparison.Ordinal);

        // And the stop is still said to be the other thread's, so rip below is not read as it.
        Assert.Contains("in thread 1368", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void LookingAtAThreadMovesNothing()
    {
        using var store = Open();
        var stub = new StubDebug();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        string state = tools.State(thread: 4412);

        Assert.Equal(["select:4412"], stub.Done);
        Assert.Contains("1368 (stopped it), 4412 (shown)", state, StringComparison.Ordinal);
    }

    [Fact]
    public void AThreadWaitingInTheKernelIsMarkedAsSuch()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = new StubDebug();

        Assert.Contains("5852 (waiting in the kernel)", tools.State(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Most threads at any stop are parked in a wait. Stepping one lands only when the wait ends, and
    /// the general timeout answer - a DLL not loaded yet - pointed somewhere unrelated.
    /// </summary>
    [Fact]
    public void SteppingAThreadThatIsWaitingSaysWhyItHasNotLanded()
    {
        using var store = Open();
        var stub = new StubDebug { Settles = false };
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        string answer = tools.Run("step", thread: 5852);

        Assert.Contains("thread 5852 is waiting in the kernel", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("host loads the module", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AThreadThatIsNotThereIsRefusedWithTheOnesThatAre()
    {
        using var store = Open();
        var stub = new StubDebug();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        string answer = tools.Run("step", thread: 9);

        Assert.Contains("no thread 9", answer, StringComparison.Ordinal);
        Assert.Contains("1368, 4412", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("step", stub.Done);
    }

    [Fact]
    public void AThreadCannotBePickedWhileTheProcessRuns()
    {
        using var store = Open();
        var stub = new StubDebug { State = "running", Status = "Running." };
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        Assert.Contains("only be picked while it is stopped", tools.State(thread: 4412), StringComparison.Ordinal);
        Assert.Empty(stub.Done);
    }

    [Fact]
    public void PausingARunningProcessStopsItWhereItIs()
    {
        using var store = Open();
        var stub = new StubDebug { State = "running", Status = "Paused.", BecomesOnWait = "stopped" };
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        string answer = tools.Run("pause");

        Assert.Equal(["pause"], stub.Done);
        Assert.StartsWith("stopped", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void PausingWhatIsNotRunningAsksNothingOfIt()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });

        var stopped = new StubDebug();
        store.Debug = stopped;
        Assert.Contains("already stopped", tools.Run("pause"), StringComparison.Ordinal);
        Assert.Empty(stopped.Done);

        var idle = new StubDebug { State = "not running", Status = "Not running." };
        store.Debug = idle;
        Assert.Contains("nothing to pause", tools.Run("pause"), StringComparison.Ordinal);
        Assert.Empty(idle.Done);
    }

    [Fact]
    public void ARunningProcessThatCannotBeContinuedPointsAtPause()
    {
        using var store = Open();
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = new StubDebug { State = "running", Status = "Running." };

        Assert.Contains("pause to stop it where it is", tools.Run("continue"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A breakpoint in a module the open binary merely calls into.
    ///
    /// The address read off that module's own listing cannot work and is the thing to stop anyone
    /// reaching for: two DLLs in one process routinely prefer the same base, so the number alone is
    /// a real place in several of them, and planting it translates against the wrong module's rebase.
    /// The module has to be named, and the tool takes it the way the panel writes it.
    /// </summary>
    [Fact]
    public void ABreakpointCanNameAModuleOtherThanTheOneBeingRead()
    {
        using var store = Open();
        var stub = new StubDebug { State = "stopped", At = 0x1800040C0 };
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        string answer = tools.Break("Mingus.dll+0x6F10");

        Assert.Contains("Mingus.dll+0x6F10", answer, StringComparison.Ordinal);
        Assert.Contains("Mingus.dll+0x6F10", stub.ModuleBreaks);

        // And it is not mistaken for an address in the open listing, which is where it used to go.
        Assert.DoesNotContain(stub.Done, d => d.StartsWith("break:6F10:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The two things that are not a module and an RVA, so neither is taken for one: a .NET method
    /// with an IL offset, and a name with arithmetic on it. Both end in "+something" and neither
    /// names a file.
    /// </summary>
    [Theory]
    [InlineData("Program::Main+IL_7")]
    [InlineData("sub_1000+8")]
    [InlineData("0x140001000")]
    public void WhatIsNotAModuleAndAnRvaIsNotTakenForOne(string target)
    {
        using var store = Open();
        var stub = new StubDebug { State = "stopped" };
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });
        store.Debug = stub;

        tools.Break(target);

        Assert.Empty(stub.ModuleBreaks);
    }

    private sealed class StubDebug : IDebugControl
    {
        public List<string> Done { get; } = [];

        /// <summary>False to stand in for a process that has not stopped inside the window.</summary>
        public bool Settles { get; init; } = true;

        public string State { get; set; } = "stopped";

        /// <summary>What it becomes once a wait comes back, for a process that stops while waited on.</summary>
        public string? BecomesOnWait { get; init; }

        /// <summary>Where it is stopped.</summary>
        public ulong At { get; init; } = 0x140001000;

        public string Status { get; init; } = "Stopped at 0x140001000.";

        public IReadOnlyList<string> Recent { get; init; } = [];

        public IReadOnlyList<(uint Id, ulong Start, bool Waiting)> Threads { get; init; } =
            [(1368, 0x7FF600001000, false), (4412, 0x7FFA21F81230, false), (5852, 0x7FFAACE80000, true)];

        /// <summary>The one whose event stopped it.</summary>
        public uint Current { get; init; } = 1368;

        /// <summary>The one shown and stepped; moves when something is picked.</summary>
        public uint Selected { get; set; } = 1368;

        public bool SelectThread(uint threadId)
        {
            Done.Add($"select:{threadId}");
            if (!Threads.Any(t => t.Id == threadId))
            {
                return false;
            }

            Selected = threadId;
            return true;
        }

        public string? Start()
        {
            Done.Add("start");
            return null;
        }

        public void Stop() => Done.Add("stop");

        public void Continue() => Done.Add("continue");

        public void Pause() => Done.Add("pause");

        /// <summary>Hypotheses the stub is holding, as the window's list would.</summary>
        public List<(uint Rva, ulong Va, string Was, string Now, string? Comment)> Tried { get; } = [];

        /// <summary>Why a live try fails, when the test wants it to.</summary>
        public string? TryRefusal { get; init; }

        public string? TryPatch(ulong va, string instruction, string? comment)
        {
            Done.Add($"try:{va:X}:{instruction}");
            if (TryRefusal is { } no)
            {
                return no;
            }

            Tried.Add((0, va, "4831C0", "90909090", comment));
            return null;
        }

        /// <summary>
        /// Takes back the last thing tried. Which patch covers an address is decided in the window,
        /// not here, so a stub that matched on one would be testing an answer it made up itself.
        /// </summary>
        public bool UndoPatch(uint rva)
        {
            Done.Add($"undo:{rva:X}");
            if (Tried.Count == 0)
            {
                return false;
            }

            Tried.RemoveAt(Tried.Count - 1);
            return true;
        }

        public void StepInstruction() => Done.Add("step");

        public void StepOver() => Done.Add("step_over");

        public void RunTo(ulong staticVa) => Done.Add($"run_to:{staticVa:X}");

        public bool SetBreakpoint(ulong staticVa, bool on)
        {
            Done.Add($"break:{staticVa:X}:{on}");
            return on;
        }

        public string? SetModuleBreakpoint(string module, uint rva, bool on)
        {
            Done.Add($"break:{module}+{rva:X}:{on}");
            ModuleBreaks.Add($"{module}+0x{rva:X}");
            return null;
        }

        public List<string> ModuleBreaks { get; } = new();

        public DebugSnapshot Snapshot() => new()
        {
            State = State,
            Status = Status,
            Recent = Recent,
            Address = At,
            TargetLoaded = true,
            Threads = Threads,
            CurrentThread = Current,
            SelectedThread = Selected,
            Registers = [("rax", Selected), ("rbx", 2), ("rcx", 3), ("rdx", 4), ("rip", 0x140001000)],
            Flags = "CF 0  PF 1  AF 0  ZF 1  SF 0  TF 0  IF 1  DF 0  OF 0",
            Stack = [(0x1000, 0xDEAD)],
            Modules = [("notepad.exe", 0x140000000, true)],
            Breakpoints = [0x140001000],
            LivePatches = Tried,
        };

        public byte[] ReadMemory(ulong staticVa, int length)
            => "MZ\0\0PE\0\0"u8.ToArray().AsSpan(0, Math.Min(length, 8)).ToArray();

        public bool WaitUntilStopped(TimeSpan timeout)
        {
            if (BecomesOnWait is { } next)
            {
                State = next;
            }

            return Settles;
        }
    }
}
