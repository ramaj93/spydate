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

        // Modules load before the program runs; seeing none would mean the loop is missing events.
        // They are collected rather than announced one line each — a process loads dozens, and
        // "loaded a module at 0x7FF…" with no name was noise that answered nothing.
        Assert.NotEmpty(session.Modules);

        session.Continue();

        Assert.True(exited.Wait(TimeSpan.FromSeconds(20)), "the process never exited");

        lock (seen)
        {
            Assert.Contains(seen, e => e.Kind == "started");
            Assert.Contains(seen, e => e.Kind == "stopped");
            Assert.Contains(seen, e => e.Kind == "exited");
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

    /// <summary>
    /// Every module is named, not just counted. Knowing which one loaded where is what lets a
    /// breakpoint on a DLL be translated at all, and it used to be thrown away: the load event was
    /// reported as "loaded a module at 0x…" and the base went nowhere.
    /// </summary>
    [Fact]
    public void EveryModuleThatLoadsIsRecordedWithItsNameAndItsBase()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        var modules = session.Modules;

        // ntdll is in every process on this platform, and is the one module that is always there.
        Assert.Contains(modules, m => m.Name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(modules, m => m.Name.Equals("where.exe", StringComparison.OrdinalIgnoreCase));
        Assert.All(modules, m => Assert.NotEqual(0ul, m.Base));
        Assert.All(modules, m => Assert.NotEmpty(m.Path));

        session.Stop();
    }

    /// <summary>
    /// Naming a module makes it, rather than the executable, the thing addresses are about. This is
    /// what debugging a DLL rests on; here it is pointed at ntdll, which is loaded by everything, so
    /// the mechanism can be tested without needing a host and a DLL to go in it.
    /// </summary>
    [Fact]
    public void TheModuleTheListingIsAboutCanBeSomethingOtherThanTheExecutable()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession();
        session.Start(Trivial, imageBase: 0x180000000, imageSize: 0x200000, arguments: "where.exe", module: "ntdll.dll");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        Assert.True(session.TargetLoaded);

        ulong ntdll = session.Modules.Single(m => m.Name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase)).Base;
        Assert.Equal(ntdll, session.LoadedBase);

        // The executable's own base is emphatically not what addresses now translate against.
        ulong exe = session.Modules.Single(m => m.Name.Equals("where.exe", StringComparison.OrdinalIgnoreCase)).Base;
        Assert.NotEqual(exe, session.LoadedBase);

        Assert.Equal(ntdll + 0x1000, session.ToRuntime(0x180001000));

        // And it really is that module: a mapped PE still starts "MZ".
        byte[] header = session.ReadMemory(session.LoadedBase, 2);
        Assert.Equal(new[] { (byte)'M', (byte)'Z' }, header);

        session.Stop();
    }

    /// <summary>
    /// The bug this keying change fixes. A breakpoint set before the process exists was translated
    /// there and then — with no load address to translate against, so it kept the static address and
    /// was later planted at it. Windows relocates, so the int3 went to an address in nothing.
    /// </summary>
    [Fact]
    public void ABreakpointSetBeforeStartingIsPlantedWhereTheModuleActuallyLanded()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession();
        var seen = new List<DebugEvent>();
        session.Reported += (_, e) =>
        {
            lock (seen)
            {
                seen.Add(e);
            }
        };

        // Set first, exactly as the window does: read the listing, mark the place, then run.
        const ulong Static = 0x140001000;
        Assert.True(session.AddBreakpoint(Static));

        session.Start(Trivial, imageBase: 0x140000000, imageSize: 0x100000, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        string log;
        lock (seen)
        {
            log = string.Join("; ", seen.Select(e => $"{e.Kind}: {e.Text}"));
        }

        // Still held under the address in the listing, whatever the process did with it.
        var breakpoint = Assert.Single(session.Breakpoints);
        Assert.Equal(Static, breakpoint.Address);

        // And the int3 is where the module is, not where the file said it would be.
        Assert.True(breakpoint.Planted, $"the breakpoint was never planted. {log}");
        Assert.Equal(0xCC, session.ReadMemory(session.LoadedBase + 0x1000, 1).Single());
        Assert.NotEqual(Static, session.LoadedBase + 0x1000);

        session.Stop();
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

