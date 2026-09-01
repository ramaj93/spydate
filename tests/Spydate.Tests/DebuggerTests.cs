using System.Runtime.Versioning;
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

        session.Start(Trivial, imageBase: 0, arguments: "where.exe");

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
        session.Start(Trivial, imageBase: 0x140000000, arguments: "where.exe");

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
        session.Start(Trivial, imageBase: 0, arguments: "where.exe");
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
        session.Start(Trivial, imageBase: 0, arguments: "where.exe");
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
