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
[Collection(Debugging.Name)]
public sealed class DebuggerTests
{
    private static readonly string Trivial = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");

    private static bool Available => OperatingSystem.IsWindows() && File.Exists(Trivial);

    /// <summary>
    /// A session whose debuggee gets no console window.
    ///
    /// There are two dozen live tests below and each starts a process. A console window takes the
    /// foreground as it appears, so a run went off like a strobe and took the keyboard away from
    /// whoever was working. The debuggee still has a console and behaves exactly as it would; only
    /// the window is withheld.
    /// </summary>
    private static DebugSession Headless() => new() { ShowConsole = false };

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

        using var session = Headless();
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

            // How it ended, not merely that it did. A crash exit code is the single most useful
            // fact a dying process leaves behind, and it used to be dropped on the way out.
            Assert.Contains(seen, e => e.Kind == "exited" && e.Text.Contains("exited", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AProcessThatCrashesSaysWhatKilledItInHexAsWellAsDecimal()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        string? ending = null;
        var exited = new ManualResetEventSlim();

        session.Reported += (_, e) =>
        {
            if (e.Kind == "exited")
            {
                ending = e.Text;
                exited.Set();
            }
        };

        // where.exe with a name it cannot find exits non-zero without crashing, which is enough to
        // prove the code travels: a zero exit takes the other branch and says so in words.
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "no-such-program-anywhere.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");
        session.Continue();

        Assert.True(exited.Wait(TimeSpan.FromSeconds(20)), "the process never exited");
        Assert.NotNull(ending);

        // Either it exited cleanly, or it said how - in both forms, because only one of them is
        // readable for an NTSTATUS.
        if (!ending!.Contains("normally", StringComparison.Ordinal))
        {
            Assert.Contains("0x", ending, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheImageIsFoundWhereItWasActuallyLoaded()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
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

        using var session = Headless();
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

        using var session = Headless();
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
        using var session = Headless();

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
        using var session = Headless();

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

        using var session = Headless();
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

        using var session = Headless();
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

        using var session = Headless();
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
        using var session = Headless();

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

        using var session = Headless();
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

        using var session = Headless();
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
    /// <summary>
    /// Stepping a thread the analyst picked, rather than whichever one Windows last reported.
    ///
    /// Continuing lets the whole process run, and one instruction is long enough for another thread
    /// to move - so the others are held for the duration and let go afterwards. Nothing else in the
    /// session may be left suspended once the step is over, or continuing afterwards runs a program
    /// with most of it frozen.
    /// </summary>
    [Fact]
    public void SteppingFollowsTheChosenThreadAndLetsTheRestGoAfterwards()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        // Whichever one reported, until something says otherwise.
        Assert.NotEqual(0u, session.CurrentThreadId);
        Assert.Equal(session.CurrentThreadId, session.SelectedThreadId);

        ulong before = session.RegistersOf(session.SelectedThreadId)!.Single(r => r.Name == "rip").Value;
        session.StepInstruction();
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped after the step");

        Assert.NotEqual(before, session.RegistersOf(session.SelectedThreadId)!.Single(r => r.Name == "rip").Value);

        // And it runs on afterwards, which it could not if anything were still held.
        var exited = new ManualResetEventSlim();
        session.Reported += (_, e) =>
        {
            if (e.Kind == "exited")
            {
                exited.Set();
            }
        };

        session.Continue();
        Assert.True(exited.Wait(TimeSpan.FromSeconds(20)), "it never finished, so a thread was left suspended");
    }

    /// <summary>
    /// Each thread's stack starts at that thread's own rsp. The stack used to be the reporting
    /// thread's whichever thread was picked, so the panel showed one thread's registers above
    /// another's stack.
    /// </summary>
    [Fact]
    public void EveryThreadsStackIsReadFromItsOwnStackPointer()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        Assert.NotEmpty(session.Threads);
        foreach (var thread in session.Threads)
        {
            ulong rsp = session.RegistersOf(thread.Id)!.Single(r => r.Name == "rsp").Value;
            var stack = session.StackOf(thread.Id);

            Assert.NotEmpty(stack);
            Assert.Equal(rsp, stack[0].Address);
        }

        Assert.Equal(session.Stack(), session.StackOf(session.CurrentThreadId));
    }

    /// <summary>
    /// Pause is the only way back from running that does not end the run. ping keeps going for
    /// seconds, so it is still running when asked; the process must stop, show a thread worth looking
    /// at rather than the one Windows started to break in with, and then carry on to its end.
    /// </summary>
    [Fact]
    public void APausedProcessStopsWhereItIsAndCarriesOnAfterwards()
    {
        string ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
        if (!Available || !File.Exists(ping))
        {
            return;
        }

        using var session = Headless();
        DebugEvent? paused = null;
        var exited = new ManualResetEventSlim();
        session.Reported += (_, e) =>
        {
            if (e.Kind == "stopped" && e.Text == "paused")
            {
                paused = e;
            }
            else if (e.Kind == "exited")
            {
                exited.Set();
            }
        };

        session.Start(ping, imageBase: 0, imageSize: 0, arguments: "-n 5 127.0.0.1");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");
        session.Continue();
        Thread.Sleep(500);

        Assert.True(session.Pause(), "a running process refused to pause");
        Assert.True(Wait(() => paused is not null && session.State == DebugState.Stopped), "it never paused");

        // The break-in thread reported it; the one shown is another, and it can be read.
        Assert.NotEqual(session.CurrentThreadId, session.SelectedThreadId);
        Assert.NotNull(session.RegistersOf(session.SelectedThreadId));

        session.Continue();
        Assert.True(exited.Wait(TimeSpan.FromSeconds(30)), "it never finished after being paused");
    }

    /// <summary>
    /// Setting and clearing a breakpoint at a stop are edits, not instructions to go. Both used to be
    /// posted, and a posted command is run and then continued - so F9 while stopped let the program
    /// run on, and a breakpoint cleared while it ran left its int3 behind in the code.
    /// </summary>
    [Fact]
    public void ChangingABreakpointWhileStoppedLeavesItStoppedAndTheCodeAsItWas()
    {
        if (!Available)
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial);
        ulong entry = image.ImageBase + image.EntryPointRva;

        using var session = Headless();
        session.Start(Trivial, image.ImageBase, image.OptionalHeader.SizeOfImage, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        // The loader break is before the entry point runs, so what is there now is the program's own.
        ulong runtime = session.ToRuntime(entry);
        byte original = session.ReadMemory(runtime, 1)[0];
        Assert.NotEqual(0xCC, original);

        Assert.True(session.AddBreakpoint(entry));
        Thread.Sleep(300);
        Assert.Equal(DebugState.Stopped, session.State);
        Assert.Equal(0xCC, session.ReadMemory(runtime, 1)[0]);

        Assert.True(session.RemoveBreakpoint(entry));
        Thread.Sleep(300);
        Assert.Equal(DebugState.Stopped, session.State);
        Assert.Equal(original, session.ReadMemory(runtime, 1)[0]);
    }

    // ------------------------------------------------------------------
    // 32-bit, which runs under WOW64 and is read with different calls
    // ------------------------------------------------------------------

    private static readonly string Trivial32 = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "where.exe");

    /// <summary>
    /// The registers of a 32-bit thread, which are not the ones the plain call answers with. Asking
    /// the 64-bit way succeeds on a WOW64 thread and hands back the wow64 layer's own state, so the
    /// test that matters is not "did it work" but "are these the program's registers": EIP has to be
    /// inside the 32-bit image, and a 32-bit process's addresses all fit in four bytes.
    /// </summary>
    [Fact]
    public void AThirtyTwoBitThreadIsReadAsThirtyTwoBit()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Trivial32))
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial32);
        using var session = Headless();
        session.Start(Trivial32, image.ImageBase, image.OptionalHeader.SizeOfImage, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        Assert.True(session.Is32Bit, "a SysWOW64 program was not seen as 32-bit");

        var registers = session.Registers();
        Assert.NotNull(registers);
        Assert.Contains(registers!, r => r.Name == "eip" && r.Value != 0);
        Assert.Contains(registers!, r => r.Name == "esp" && r.Value != 0);
        Assert.DoesNotContain(registers!, r => r.Name == "rip");

        // Nothing in a 32-bit process lives above four gigabytes. The 64-bit call on this thread
        // answers with values that do, which is how a wrong context gives itself away.
        Assert.All(registers!, r => Assert.True(r.Value <= uint.MaxValue, $"{r.Name} = 0x{r.Value:X}"));
    }

    /// <summary>A 32-bit stack is read four bytes at a time, and starts at that thread's own ESP.</summary>
    [Fact]
    public void AThirtyTwoBitStackIsReadInFourByteWords()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Trivial32))
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial32);
        using var session = Headless();
        session.Start(Trivial32, image.ImageBase, image.OptionalHeader.SizeOfImage, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        ulong esp = session.StackPointerOf(session.CurrentThreadId)!.Value;
        var stack = session.StackOf(session.CurrentThreadId);

        Assert.NotEmpty(stack);
        Assert.Equal(esp, stack[0].Address);
        Assert.Equal(esp + 4, stack[1].Address);
        Assert.All(stack, row => Assert.True(row.Value <= uint.MaxValue));
    }

    /// <summary>
    /// Stepping a 32-bit thread, which needs the trap flag written back through the WOW64 call. The
    /// breakpoint byte is the same 0xCC either way; what differs is every call used to read and write
    /// the thread around it.
    /// </summary>
    [Fact]
    public void AThirtyTwoBitThreadSteps()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Trivial32))
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial32);
        using var session = Headless();
        session.Start(Trivial32, image.ImageBase, image.OptionalHeader.SizeOfImage, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        ulong before = session.InstructionPointerOf(session.CurrentThreadId)!.Value;
        session.StepInstruction();
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped after the step");

        Assert.NotEqual(before, session.InstructionPointerOf(session.CurrentThreadId)!.Value);
    }

    /// <summary>
    /// A breakpoint in 32-bit code: hit it, step off it, and go on to a normal end.
    ///
    /// Under WOW64 an int3 is reported as STATUS_WX86_BREAKPOINT and a step as STATUS_WX86_SINGLE_STEP.
    /// Neither was recognised, so a breakpoint was handed back to the program unhandled and killed it.
    /// The tell is the exit code: the process died with 0x4000001F - the breakpoint itself - instead
    /// of finishing, which is why a 32-bit binary allowed exactly one resume per launch.
    /// </summary>
    [Fact]
    public void AThirtyTwoBitBreakpointIsHitSteppedAndResumed()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Trivial32))
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial32);
        ulong entry = image.ImageBase + image.EntryPointRva;

        using var session = Headless();
        var said = new List<string>();
        string ending = string.Empty;
        var exited = new ManualResetEventSlim();
        int stops = 0;

        session.Reported += (_, e) =>
        {
            lock (said)
            {
                said.Add($"{e.Kind}:{e.Text}");
            }

            if (e.Kind == "stopped")
            {
                Interlocked.Increment(ref stops);
            }
            else if (e.Kind == "exited")
            {
                ending = e.Text;
                exited.Set();
            }
        };

        bool StopAfter(int seen) => Wait(() => Volatile.Read(ref stops) > seen && session.State == DebugState.Stopped);

        session.AddBreakpoint(entry);
        session.Start(Trivial32, image.ImageBase, image.OptionalHeader.SizeOfImage, arguments: "where.exe");
        Assert.True(StopAfter(0), "never reached the loader break");

        // On to the breakpoint, which is in the program's own 32-bit code.
        int seen = Volatile.Read(ref stops);
        session.Continue();
        Assert.True(StopAfter(seen), "the breakpoint was never reached");

        lock (said)
        {
            Assert.Contains(said, t => t.StartsWith("stopped:breakpoint at", StringComparison.Ordinal));

            // As a breakpoint, that is - not as an exception the program is left to survive.
            Assert.DoesNotContain(said, t => t.Contains("4000001F", StringComparison.OrdinalIgnoreCase));
        }

        // And it steps off it, which needs the 32-bit single step recognised as well.
        ulong before = session.InstructionPointerOf(session.CurrentThreadId)!.Value;
        seen = Volatile.Read(ref stops);
        session.StepInstruction();
        Assert.True(StopAfter(seen), "never stopped after the step");
        Assert.NotEqual(before, session.InstructionPointerOf(session.CurrentThreadId)!.Value);

        session.Continue();
        Assert.True(exited.Wait(TimeSpan.FromSeconds(30)), "it never finished after the breakpoint");
        Assert.DoesNotContain("4000001F", ending, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A 32-bit program runs to its end under the debugger, second loader break and all.</summary>
    [Fact]
    public void AThirtyTwoBitProcessRunsToCompletion()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Trivial32))
        {
            return;
        }

        using var session = Headless();
        var exited = new ManualResetEventSlim();
        session.Reported += (_, e) =>
        {
            if (e.Kind == "exited")
            {
                exited.Set();
            }
        };

        session.Start(Trivial32, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");
        session.Continue();

        Assert.True(exited.Wait(TimeSpan.FromSeconds(30)), "it never finished");
    }

    [Fact]
    public void NothingRunningCannotBePaused()
    {
        using var session = Headless();
        Assert.False(session.Pause());
    }

    /// <summary>
    /// A live patch goes into the running process and its file bytes come back out — the whole of
    /// runtime patching, minus the file. The loader break is before the program's own code, so what
    /// is at the entry then is the file's byte, which is what a removal must restore.
    /// </summary>
    [Fact]
    public void ALivePatchIsWrittenIntoTheProcessAndTakenBackOut()
    {
        if (!Available)
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial);
        uint rva = image.EntryPointRva;
        ulong entry = image.ImageBase + rva;

        using var session = Headless();
        session.Start(Trivial, image.ImageBase, image.OptionalHeader.SizeOfImage, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        ulong runtime = session.ToRuntime(entry);
        byte original = session.ReadMemory(runtime, 1)[0];
        Assert.NotEqual(0x90, original);

        Assert.Null(session.SetPatch(new LivePatch(rva, [0x90], [original])));
        Assert.Equal(0x90, session.ReadMemory(runtime, 1)[0]);
        Assert.Contains(session.LivePatches, p => p.Rva == rva);

        Assert.Null(session.ClearPatch(rva));
        Assert.Equal(original, session.ReadMemory(runtime, 1)[0]);
        Assert.DoesNotContain(session.LivePatches, p => p.Rva == rva);
    }

    /// <summary>
    /// A breakpoint and a patch on the same bytes have no single answer — one wants an int3 there,
    /// the other its own byte — so a live patch over a planted breakpoint is refused rather than
    /// clobbering it. The entry breakpoint is planted at the loader break, before the patch is tried.
    /// </summary>
    [Fact]
    public void ALivePatchOverAPlantedBreakpointIsRefused()
    {
        if (!Available)
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial);
        uint rva = image.EntryPointRva;
        ulong entry = image.ImageBase + rva;

        using var session = Headless();
        session.AddBreakpoint(entry);
        session.Start(Trivial, image.ImageBase, image.OptionalHeader.SizeOfImage, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        string? refused = session.SetPatch(new LivePatch(rva, [0x90], [0xCC]));
        Assert.NotNull(refused);
        Assert.Contains("breakpoint", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void AThreadThatHasGoneIsNotTheOneStepped()
    {
        using var session = Headless();

        // Nothing running, so no thread of that id exists and the choice cannot stand.
        session.SelectedThreadId = 999999;
        Assert.Equal(session.CurrentThreadId, session.SelectedThreadId);
    }

    /// <summary>
    /// The loader break happens in ntdll, never in the image being read. Reported as an address the
    /// listing knows, the window opens a document for it - a fabricated function of nought blocks
    /// and nought instructions - and that is what the analyst then watches for a marker that cannot
    /// move, because there is nothing there to move over.
    /// </summary>
    [Fact]
    public void AStopOutsideTheImageIsNotOfferedAsAPlaceInTheListing()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        DebugEvent? stop = null;
        var reached = new ManualResetEventSlim();

        session.Reported += (_, e) =>
        {
            if (e.Kind == "stopped" && stop is null)
            {
                stop = e;
                reached.Set();
            }
        };

        // A real base and size, so the translation is live and its bounds actually apply.
        session.Start(Trivial, imageBase: 0x140000000, imageSize: 0x100000, arguments: "where.exe");
        Assert.True(reached.Wait(TimeSpan.FromSeconds(20)), "never reached the loader break");

        Assert.NotNull(stop);
        Assert.Contains("loader break", stop!.Text, StringComparison.Ordinal);

        // It says where it is in words, and offers no address at all - because the one it has
        // belongs to another module and would be read as belonging to this one.
        Assert.Null(stop.Address);

        session.Stop();
    }

    /// <summary>
    /// Stepping while sitting on a breakpoint, which is where stepping is nearly always done.
    ///
    /// Getting off a breakpoint uses the trap flag too, and the handler treated the resulting
    /// single-step as its own - so it re-armed, carried on, and the step became a continue. The
    /// existing step test never saw it because it steps from the loader break, where nothing is
    /// re-armed.
    /// </summary>
    [Fact]
    public void SteppingOffABreakpointMovesOneInstructionRatherThanRunningOn()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        // A breakpoint on the very next instruction, so it is reached at once and stepped off.
        ulong at = session.Registers()!.Single(r => r.Name == "rip").Value;
        var decoded = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(session.ReadMemory(at, 16)), at);
        ulong next = decoded.Decode().NextIP;

        Assert.True(session.AddBreakpoint(next), "the breakpoint was not taken");
        session.Continue();
        Assert.True(Wait(() => session.State == DebugState.Stopped && session.CurrentAddress == next),
            "never stopped on the breakpoint");

        session.StepInstruction();
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped after the step");

        ulong after = session.Registers()!.Single(r => r.Name == "rip").Value;

        // One instruction on, and specifically not back where it started: running on would have
        // carried it away entirely, and a swallowed step would have left it where it was.
        Assert.NotEqual(next, after);
        Assert.Equal(next, session.ToRuntime(next));

        session.Stop();
    }

    /// <summary>
    /// Continuing the instant the stop is announced, which is what anything reacting in code does.
    /// The state has to already say stopped by then, or Post drops the command and the loop waits
    /// for one that never comes - a session where nothing works but Stop.
    /// </summary>
    [Fact]
    public void ContinuingTheMomentAStopIsAnnouncedIsNotDropped()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        var exited = new ManualResetEventSlim();
        int stops = 0;

        session.Reported += (_, e) =>
        {
            if (e.Kind == "exited")
            {
                exited.Set();
                return;
            }

            if (e.Kind != "stopped")
            {
                return;
            }

            Interlocked.Increment(ref stops);

            // From inside the report, with no pause at all. This is the race: the engine has told
            // the world it stopped, and the world is answering before the line after the telling.
            Assert.Equal(DebugState.Stopped, session.State);
            session.Continue();
        };

        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");

        Assert.True(exited.Wait(TimeSpan.FromSeconds(25)),
            "it never exited, so a continue sent the instant the stop was announced was dropped");
        Assert.True(stops > 0, "it never stopped at all");
    }

    /// <summary>
    /// The process id is available as soon as the session is.
    ///
    /// Something other than this loop may need to read the debuggee — the CLR data access layer
    /// attaches by process id, which is how managed state is read while this loop holds the debug
    /// port — and a caller that has to work out for itself which process was just launched is a
    /// caller that will eventually pick the wrong one.
    /// </summary>
    [Fact]
    public void TheProcessIdIsKnownAsSoonAsItHasStarted()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        Assert.Equal(0u, session.ProcessId);

        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        Assert.NotEqual(0u, session.ProcessId);

        // The id of the thing that was actually started, rather than merely a number that is not zero.
        // It is stopped at the loader break, so it is certainly still there to be asked.
        using var running = System.Diagnostics.Process.GetProcessById((int)session.ProcessId);
        Assert.Equal("where", running.ProcessName, ignoreCase: true);

        session.Stop();
    }

    /// <summary>
    /// A breakpoint that could not be put into the process does not claim to be planted, and says why.
    ///
    /// The failure this guards against is the quiet one: a breakpoint that lists as planted, never
    /// fires, and offers no reason for it. Whoever set it then reads the absence of a stop as a fact
    /// about the program — that the code was never reached — when it is a fact about the debugger.
    /// </summary>
    [Fact]
    public void ABreakpointThatCouldNotBeWrittenIsNotReportedAsPlanted()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        var problems = new List<string>();
        session.Reported += (_, e) =>
        {
            if (e.Kind == "problem")
            {
                lock (problems)
                {
                    problems.Add(e.Text);
                }
            }
        };

        // The lowest 64KB of a process is never mapped, so no byte can be put here. With no image base
        // given the translation is the identity, so the address is tried exactly as written.
        const ulong Nowhere = 0x1000;
        Assert.True(session.AddBreakpoint(Nowhere));

        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        // Still held, because it was asked for — but not pretending to be in the process.
        var breakpoint = Assert.Single(session.Breakpoints);
        Assert.False(breakpoint.Planted, "it claims to be planted where nothing can be written");

        lock (problems)
        {
            Assert.Contains(problems, p => p.Contains("1000", StringComparison.Ordinal));
        }

        session.Stop();
    }

    /// <summary>
    /// A live patch that could not be written reports that, instead of reporting success.
    ///
    /// This one used to come back null. The write went out through a helper that looked at the result
    /// of <c>WriteProcessMemory</c> only to decide whether to flush the instruction cache, and threw
    /// it away otherwise — so a patch onto memory that does not exist was indistinguishable from one
    /// that had been applied, and the caller went on to believe the program had been changed.
    /// </summary>
    [Fact]
    public void APatchThatCouldNotBeWrittenSaysSoInsteadOfReportingSuccess()
    {
        if (!Available)
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial);

        using var session = Headless();
        session.Start(Trivial, image.ImageBase, image.OptionalHeader.SizeOfImage, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        // Far past the end of the image, so whatever address this works out to is mapped by nothing.
        uint beyond = image.OptionalHeader.SizeOfImage + 0x100000;
        string? refused = session.SetPatch(new LivePatch(beyond, [0x90], [0x00]));

        Assert.NotNull(refused);
        Assert.Contains("could not be written", refused, StringComparison.Ordinal);

        session.Stop();
    }

    /// <summary>
    /// Breakpoints in two different modules at once, which is the whole point of naming a module.
    ///
    /// They are kept apart by (module, RVA) rather than by a static address, because a static address
    /// cannot say which module is meant: 0x180000000 is the default base for an x64 DLL and most of
    /// them keep it, so in a real process several modules claim the same numbers. Aimed at each
    /// module's first byte — the "MZ" of its mapped header, which is read-only and never executed — so
    /// this proves the bookkeeping without depending on either module running anything.
    /// </summary>
    [Fact]
    public void BreakpointsSitInTwoModulesAtOnce()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        Assert.True(session.AddBreakpoint("where.exe", 0));
        Assert.True(session.AddBreakpoint("ntdll.dll", 0));

        Assert.Equal(2, session.Breakpoints.Count);
        Assert.All(session.Breakpoints, b => Assert.True(b.Planted, $"{b.Module} was not planted"));

        ulong exe = session.Modules.Single(m => m.Name.Equals("where.exe", StringComparison.OrdinalIgnoreCase)).Base;
        ulong ntdll = session.Modules.Single(m => m.Name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase)).Base;
        Assert.NotEqual(exe, ntdll);

        Assert.Equal(0xCC, session.ReadMemory(exe, 1).Single());
        Assert.Equal(0xCC, session.ReadMemory(ntdll, 1).Single());

        // And each comes out on its own, restoring that module's own byte rather than the other's.
        Assert.True(session.RemoveBreakpoint("where.exe", 0));
        Assert.Equal((byte)'M', session.ReadMemory(exe, 1).Single());
        Assert.Equal(0xCC, session.ReadMemory(ntdll, 1).Single());

        Assert.True(session.RemoveBreakpoint("ntdll.dll", 0));
        Assert.Equal((byte)'M', session.ReadMemory(ntdll, 1).Single());

        session.Stop();
    }

    /// <summary>
    /// A breakpoint named by module and RVA before the run fires when that module's code reaches it,
    /// and is reported the way it was set.
    ///
    /// Set on the executable's own entry point, which always runs. Named before anything is launched,
    /// when the module does not exist and there is no address to put a byte at — the loader event is
    /// what redeems it.
    /// </summary>
    [Fact]
    public void ABreakpointNamedByModuleAndRvaFires()
    {
        if (!Available)
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial);

        using var session = Headless();
        var stops = new List<string>();
        session.Reported += (_, e) =>
        {
            if (e.Kind == "stopped")
            {
                lock (stops)
                {
                    stops.Add(e.Text);
                }
            }
        };

        Assert.True(session.AddBreakpoint("where.exe", image.EntryPointRva));

        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        ulong exe = session.Modules.Single(m => m.Name.Equals("where.exe", StringComparison.OrdinalIgnoreCase)).Base;
        ulong expected = exe + image.EntryPointRva;

        // Waited for by where it landed, not merely by it having stopped: continuing takes a moment to
        // leave the stopped state, and "is it stopped" is true again the instant before it moves.
        session.Continue();
        Assert.True(
            Wait(() => session.State == DebugState.Stopped && session.CurrentAddress == expected),
            "the entry-point breakpoint never hit");

        lock (stops)
        {
            Assert.Contains(stops, s => s.Contains("where.exe+0x", StringComparison.OrdinalIgnoreCase));
        }

        session.Stop();
    }

    /// <summary>
    /// "Entry Point" runs past the loader break and stops at the launched program's own entry, with
    /// nothing planted by anyone — the stop is the break-at itself.
    /// </summary>
    [Fact]
    public void EntryPointBreakStopsAtTheProcessEntryAndNotTheLoaderBreak()
    {
        if (!Available)
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial);
        using var session = Headless();
        var stops = new List<string>();
        session.Reported += (_, e) =>
        {
            if (e.Kind == "stopped")
            {
                lock (stops) { stops.Add(e.Text); }
            }
        };

        session.Start(Trivial, image.ImageBase, image.OptionalHeader.SizeOfImage,
            arguments: "where.exe", entryStop: EntryStop.ProcessEntry);

        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped at the entry point");

        ulong exe = session.Modules.Single(m => m.Name.Equals("where.exe", StringComparison.OrdinalIgnoreCase)).Base;
        Assert.Equal(exe + image.EntryPointRva, session.CurrentAddress);

        // The loader break was run past, not reported: the only stop is the entry.
        lock (stops)
        {
            Assert.DoesNotContain(stops, s => s.Contains("loader break", StringComparison.OrdinalIgnoreCase));
        }

        session.Stop();
    }

    /// <summary>
    /// "Module cctor or Entry Point" for native code — which has no static constructor — stops at the
    /// entry point of the module being read. For a standalone target that module is the main image.
    /// </summary>
    [Fact]
    public void ModuleEntryBreakStopsAtTheModuleEntryPoint()
    {
        if (!Available)
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial);
        using var session = Headless();

        session.Start(Trivial, image.ImageBase, image.OptionalHeader.SizeOfImage,
            arguments: "where.exe", entryStop: EntryStop.ModuleEntry, entryRva: image.EntryPointRva);

        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped at the module entry");

        ulong exe = session.Modules.Single(m => m.Name.Equals("where.exe", StringComparison.OrdinalIgnoreCase)).Base;
        Assert.Equal(exe + image.EntryPointRva, session.CurrentAddress);

        session.Stop();
    }

    /// <summary>
    /// "Don't break" lets go of the loader break the instant it arrives and never stops of its own
    /// accord: the process runs to its own exit with no stop reported.
    /// </summary>
    [Fact]
    public void DontBreakRunsToExitWithoutStopping()
    {
        if (!Available)
        {
            return;
        }

        var image = Spydate.Core.PE.PeImage.Load(Trivial);
        using var session = Headless();
        var stops = new List<string>();
        var exited = new ManualResetEventSlim();
        session.Reported += (_, e) =>
        {
            if (e.Kind == "stopped")
            {
                lock (stops) { stops.Add(e.Text); }
            }
            else if (e.Kind == "exited")
            {
                exited.Set();
            }
        };

        session.Start(Trivial, image.ImageBase, image.OptionalHeader.SizeOfImage,
            arguments: "where.exe", entryStop: EntryStop.DontBreak);

        Assert.True(exited.Wait(TimeSpan.FromSeconds(20)), "the process never exited");
        lock (stops)
        {
            Assert.Empty(stops);
        }
    }

    /// <summary>
    /// A breakpoint named by a module that is not present at startup but arrives partway through the
    /// run — the loader-event path — fires at that module's entry point, before its own code runs, with
    /// the DllMain reason code readable at the stop.
    ///
    /// This is the case the whole mixed-mode design turned on: a DLL that loads late (a protection
    /// module, say) and runs code in its own entry point. It needs no custom fixture — rundll32 does a
    /// LoadLibrary of winmm.dll from its command line, and winmm is not statically linked into it, so
    /// the load is a genuine LOAD_DLL event after the process is already up. The entry argument is never
    /// reached: the stop is at winmm's DllMain during the load, and the process is terminated there,
    /// long before rundll32 looks for the export that does not exist.
    /// </summary>
    [Fact]
    public void ABreakpointInALateLoadingDllFiresAtItsEntryWithTheDllMainReason()
    {
        string rundll32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe");
        string winmm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "winmm.dll");
        if (!OperatingSystem.IsWindows() || !File.Exists(rundll32) || !File.Exists(winmm))
        {
            return;
        }

        uint entryRva = Spydate.Core.PE.PeImage.Load(winmm).EntryPointRva;

        using var session = Headless();
        var stops = new List<string>();
        session.Reported += (_, e) =>
        {
            if (e.Kind == "stopped")
            {
                lock (stops) { stops.Add(e.Text); }
            }
        };

        // Named by the module, not by a static address: winmm is nowhere at the loader break, so its
        // breakpoint waits and is planted when the module lands.
        Assert.True(session.AddBreakpoint("winmm.dll", entryRva));

        // Don't break at the loader break: let it run on so the one stop is winmm's own entry, not
        // rundll32's start. The named breakpoint is still planted when winmm lands.
        session.Start(rundll32, imageBase: 0, imageSize: 0,
            arguments: "winmm.dll,SpydateProbeEntryThatDoesNotExist", entryStop: EntryStop.DontBreak);

        Assert.True(
            Wait(() => session.State == DebugState.Stopped
                       && session.Modules.Any(m => m.Name.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase))),
            "the late-loading DLL's entry breakpoint never fired");

        ulong winmmBase = session.Modules.Single(m => m.Name.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase)).Base;
        Assert.Equal(winmmBase + entryRva, session.CurrentAddress);

        // DllMain(hinstance, reason, reserved): on x64 the reason is the second argument and sits in
        // rdx at the entry point the loader jumps to. DLL_PROCESS_ATTACH is 1.
        ulong rdx = session.Registers()!.Single(r => r.Name == "rdx").Value;
        Assert.Equal(1u, (uint)rdx);

        lock (stops)
        {
            Assert.Contains(stops, s => s.Contains("winmm.dll+0x", StringComparison.OrdinalIgnoreCase));
        }

        session.Stop();
    }

    /// <summary>
    /// A breakpoint naming a module the process never loads waits, and does not claim to be anywhere.
    /// </summary>
    [Fact]
    public void ABreakpointNamingAModuleThatNeverLoadsStaysUnplanted()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        Assert.True(session.AddBreakpoint("no-such-module-of-ours.dll", 0x1000));

        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        var waiting = Assert.Single(session.Breakpoints);
        Assert.Equal("no-such-module-of-ours.dll", waiting.Module);
        Assert.False(waiting.Planted, "it claims to be planted in a module that is not loaded");

        session.Stop();
    }

    /// <summary>
    /// A live patch goes into a module other than the one being read.
    ///
    /// This is the half of patching that was missing: an RVA was always module-relative, but there was
    /// no way to say which module, so every patch went into the program itself. Aimed at ntdll's first
    /// byte — the "MZ" of its mapped header, read-only and never executed — so it proves the addressing
    /// without changing anything the process will run.
    /// </summary>
    [Fact]
    public void APatchGoesIntoAModuleOtherThanTheOneBeingRead()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        ulong ntdll = session.Modules.Single(m => m.Name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase)).Base;
        byte original = session.ReadMemory(ntdll, 1).Single();
        Assert.Equal((byte)'M', original);

        Assert.Null(session.SetPatch(new LivePatch(0, [0x90], [original]) { Module = "ntdll.dll" }));
        Assert.Equal(0x90, session.ReadMemory(ntdll, 1).Single());
        Assert.Contains(session.LivePatches, p => p.Module == "ntdll.dll" && p.Rva == 0);

        Assert.Null(session.ClearPatch("ntdll.dll", 0));
        Assert.Equal(original, session.ReadMemory(ntdll, 1).Single());
        Assert.DoesNotContain(session.LivePatches, p => p.Module == "ntdll.dll");

        session.Stop();
    }

    /// <summary>
    /// Two modules hold a patch at the same RVA at once, and clearing one leaves the other alone.
    ///
    /// The case a table keyed by RVA alone could not represent at all: the second patch was the first
    /// one, and removing either restored whichever bytes happened to be recorded. RVA 0 in both, so the
    /// two keys differ only by module — which is the whole of what was added.
    /// </summary>
    [Fact]
    public void PatchesAtTheSameRvaInTwoModulesDoNotCollide()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        ulong exe = session.Modules.Single(m => m.Name.Equals("where.exe", StringComparison.OrdinalIgnoreCase)).Base;
        ulong ntdll = session.Modules.Single(m => m.Name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase)).Base;
        Assert.NotEqual(exe, ntdll);

        byte exeWas = session.ReadMemory(exe, 1).Single();
        byte ntdllWas = session.ReadMemory(ntdll, 1).Single();

        Assert.Null(session.SetPatch(new LivePatch(0, [0x90], [exeWas]) { Module = "where.exe" }));
        Assert.Null(session.SetPatch(new LivePatch(0, [0x91], [ntdllWas]) { Module = "ntdll.dll" }));

        Assert.Equal(2, session.LivePatches.Count);
        Assert.Equal(0x90, session.ReadMemory(exe, 1).Single());
        Assert.Equal(0x91, session.ReadMemory(ntdll, 1).Single());

        // Each comes out on its own, restoring that module's own byte and leaving the other patched.
        Assert.Null(session.ClearPatch("where.exe", 0));
        Assert.Equal(exeWas, session.ReadMemory(exe, 1).Single());
        Assert.Equal(0x91, session.ReadMemory(ntdll, 1).Single());

        Assert.Null(session.ClearPatch("ntdll.dll", 0));
        Assert.Equal(ntdllWas, session.ReadMemory(ntdll, 1).Single());
        Assert.Empty(session.LivePatches);

        session.Stop();
    }

    /// <summary>
    /// A patch naming a module the process never loads is held rather than refused, and nothing is
    /// written anywhere on its behalf. Held is the same answer a patch gets before its module arrives,
    /// which is the normal case for a DLL.
    /// </summary>
    [Fact]
    public void APatchNamingAModuleThatNeverLoadsIsHeldAndNotWritten()
    {
        if (!Available)
        {
            return;
        }

        using var session = Headless();
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        Assert.Null(session.SetPatch(new LivePatch(0x1000, [0x90], [0x00]) { Module = "no-such-module-of-ours.dll" }));

        var held = Assert.Single(session.LivePatches);
        Assert.Equal("no-such-module-of-ours.dll", held.Module);
        Assert.Equal(0x1000u, held.Rva);

        session.Stop();
    }
}

