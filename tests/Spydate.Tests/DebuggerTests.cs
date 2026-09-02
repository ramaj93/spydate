using System.Runtime.Versioning;
using Spydate.Disassembly;
using Spydate.Debugger;

namespace Spydate.Tests;

/// <summary>
/// The debug loop, against real processes.
///
/// These start something and let it finish. The debuggee is a Windows utility chosen for being dull:
/// <c>timeout.exe</c> and <c>where.exe</c> do nothing, exit on their own, and are on every machine.
/// Nothing here runs anything from the corpus, and nothing runs anything that outlives the test.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DebuggerTests
{
    private static readonly string Trivial = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");

    private static bool Available => OperatingSystem.IsWindows() && File.Exists(Trivial);

    [Fact]
    public void TheDebuggeeUsedByTheseTestsIsActuallyPresent()
    {
        // Every live test above returns early without it, and would pass having done nothing.
        Assert.True(Available, $"{Trivial} is missing, so the debugger was never exercised");
    }

    [Fact]
    public void AProcessRunsToCompletionUnderTheDebugger()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession();
        var seen = new List<DebugEvent>();
        var exited = new ManualResetEventSlim();

        session.Reported += (_, e) =>
        {
            lock (seen)
            {
                seen.Add(e);
            }

            if (e.Kind == "exited")
            {
                exited.Set();
            }
        };

        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");

        // The loader break comes first, before any of the program's own code.
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");
        session.Continue();

        Assert.True(exited.Wait(TimeSpan.FromSeconds(20)), "the process never exited");

        lock (seen)
        {
            Assert.Contains(seen, e => e.Kind == "started");
            Assert.Contains(seen, e => e.Kind == "stopped");
            Assert.Contains(seen, e => e.Kind == "exited");

            // Modules load before the program runs; seeing none would mean the loop is missing events.
            Assert.Contains(seen, e => e.Kind == "module");
        }
    }

    [Fact]
    public void TheImageIsFoundWhereItWasActuallyLoaded()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession();
        session.Start(Trivial, imageBase: 0x140000000, imageSize: 0x100000, arguments: "where.exe");

        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        // Windows relocates, so this is the number every breakpoint address depends on.
        Assert.NotEqual(0ul, session.LoadedBase);

        // And the mapping is a real translation rather than an identity.
        ulong runtime = session.ToRuntime(0x140001000);
        Assert.Equal(session.LoadedBase + 0x1000, runtime);
        Assert.Equal(0x140001000ul, session.ToStatic(runtime));

        session.Stop();
    }

    [Fact]
    public void RegistersAndMemoryCanBeReadWhileItIsStopped()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        var registers = session.Registers();
        Assert.NotNull(registers);
        Assert.Contains(registers!, r => r.Name == "rip" && r.Value != 0);
        Assert.Contains(registers!, r => r.Name == "rsp" && r.Value != 0);

        // The first two bytes of a loaded PE are still "MZ" in memory.
        byte[] header = session.ReadMemory(session.LoadedBase, 2);
        Assert.Equal(2, header.Length);
        Assert.Equal((byte)'M', header[0]);
        Assert.Equal((byte)'Z', header[1]);

        session.Stop();
    }

    [Fact]
    public void SteppingMovesOneInstruction()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        ulong before = session.Registers()!.Single(r => r.Name == "rip").Value;

        session.StepInstruction();
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped after the step");

        ulong after = session.Registers()!.Single(r => r.Name == "rip").Value;

        Assert.NotEqual(before, after);

        session.Stop();
    }

    [Fact]
    public void RegistersAreRefusedWhileItIsRunning()
    {
        using var session = new DebugSession();

        // Nothing has been started, so there is nothing to report and no guess is offered.
        Assert.Equal(DebugState.NotStarted, session.State);
        Assert.Null(session.Registers());
        Assert.Empty(session.ReadMemory(0x140000000, 16));
    }

    [Fact]
    public void NothingRunsUntilItIsAskedTo()
    {
        // The property the whole design rests on: constructing a session starts no process. Opening,
        // analysing and patching a binary all leave it inert.
        using var session = new DebugSession();

        Assert.Equal(DebugState.NotStarted, session.State);
        Assert.Equal(0ul, session.LoadedBase);
        Assert.Empty(session.Breakpoints);
    }

    [Fact]
    public void ABreakpointIsRememberedBeforeThereIsAProcessToPlantItIn()
    {
        using var session = new DebugSession();

        Assert.True(session.AddBreakpoint(0x140001000));
        Assert.False(session.AddBreakpoint(0x140001000));   // already there
        Assert.Single(session.Breakpoints);

        Assert.True(session.RemoveBreakpoint(0x140001000));
        Assert.Empty(session.Breakpoints);
        Assert.False(session.RemoveBreakpoint(0x140001000));
    }

    [Fact]
    public void AnAddressOutsideTheImageIsNotTranslated()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession();
        session.Start(Trivial, imageBase: 0x140000000, imageSize: 0x10000, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        // Inside: translated both ways.
        ulong inside = session.LoadedBase + 0x100;
        Assert.True(session.InsideImage(inside));
        Assert.Equal(0x140000100ul, session.ToStatic(inside));

        // Outside: left alone. The loader break is in ntdll, and biasing that by this image's load
        // offset produces a number that looks like an address here and belongs to nothing.
        ulong elsewhere = session.LoadedBase + 0x40000000;
        Assert.False(session.InsideImage(elsewhere));
        Assert.Equal(elsewhere, session.ToStatic(elsewhere));

        session.Stop();
    }

    [Fact]
    public void SteppingOverACallLandsAfterItRatherThanInsideIt()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        // Step until a call is the next instruction, so the case is actually exercised rather than
        // assumed to come up.
        ulong callAt = 0, after = 0;
        for (int i = 0; i < 400 && callAt == 0; i++)
        {
            ulong rip = session.Registers()!.Single(r => r.Name == "rip").Value;
            byte[] code = session.ReadMemory(rip, 16);
            if (code.Length == 16)
            {
                var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(code), rip);
                var instruction = decoder.Decode();
                if (!instruction.IsInvalid
                    && instruction.FlowControl is Iced.Intel.FlowControl.Call or Iced.Intel.FlowControl.IndirectCall)
                {
                    callAt = rip;
                    after = instruction.NextIP;
                    break;
                }
            }

            session.StepInstruction();
            if (!Wait(() => session.State == DebugState.Stopped, 5))
            {
                break;
            }
        }

        // Not a silent return: a program's startup calls things, and if 400 instructions went by
        // without one then the stepping itself is broken and this test would pass having proved it.
        Assert.True(callAt != 0, "no call was reached in 400 instructions");

        session.StepOver();
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped after stepping over");

        ulong landed = session.Registers()!.Single(r => r.Name == "rip").Value;

        // The instruction after the call, not its target. Stepping into it would land somewhere else
        // entirely, and single-stepping through it would take millions of round trips.
        Assert.Equal(after, landed);

        session.Stop();
    }

    private static bool Wait(Func<bool> until, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (until())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
    }
}

/// <summary>Breakpoints as the listing shows them, and as the caret still reads them.</summary>
public sealed class BreakpointListingTests
{
    [Fact]
    public void ABreakpointIsMarkedInTheGutter()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        ulong va = analysis.Image.RvaToVa(analysis.Image.EntryPointRva);
        Assert.True(analysis.TryGetFunction(va, out var function));

        string plain = AsmListing.ForFunction(analysis, function!);
        string marked = AsmListing.ForFunction(analysis, function!, null, new HashSet<ulong> { va });

        var line = marked.Split('\n').First(l => l.Contains($"{va:X16}", StringComparison.Ordinal));
        Assert.StartsWith("*", line, StringComparison.Ordinal);

        // Only that line. A gutter on every line would be a gutter that says nothing.
        Assert.Equal(1, marked.Split('\n').Count(l => l.StartsWith("*", StringComparison.Ordinal)));
        Assert.DoesNotContain(plain.Split('\n'), l => l.StartsWith("*", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoBreakpointsTheListingIsUnchanged()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        ulong va = analysis.Image.RvaToVa(analysis.Image.EntryPointRva);
        Assert.True(analysis.TryGetFunction(va, out var function));

        // No gutter column at all: everything that reads a listing, the MCP tools included, sees
        // exactly what it saw before a debugger existed.
        Assert.Equal(
            AsmListing.ForFunction(analysis, function!),
            AsmListing.ForFunction(analysis, function!, null, new HashSet<ulong>()));
    }

    [Fact]
    public void TheCaretStillFindsTheAddressOnAMarkedLine()
    {
        // The marker is not whitespace, so it survives TrimStart. Left unhandled it would make every
        // line with a breakpoint report no address — exactly the lines a debug session cares about,
        // which would disable Rename, Comment and the patch commands precisely there.
        Assert.Equal(0x140001A20ul, Spydate.Core.Text.AddressText.FromLine("* 0000000140001A20  4883EC28  sub rsp, 0x28"));
        Assert.Equal(0x140001A20ul, Spydate.Core.Text.AddressText.FromLine("  0000000140001A20  4883EC28  sub rsp, 0x28"));
    }
}
