using Spydate.Debugger;
using Spydate.Debugger.Managed;

namespace Spydate.Tests;

/// <summary>
/// Debugging a .NET process through the CLR debugging interface.
///
/// These run a program. Every one of them launches something, watches it, and kills it, so each is
/// written to leave nothing behind whether it passes or fails — and each skips rather than fails
/// when the target it wants is not built, so the suite still runs on a fresh clone.
///
/// The target is Spydate's own MCP server: a real .NET console application that starts, initialises
/// a host, and then blocks reading stdin, which is exactly what a debuggee should do while it is
/// being looked at.
/// </summary>
[Collection(Debugging.Name)]
public class ManagedDebuggerTests
{
    /// <summary>
    /// A session whose debuggee gets no console window.
    ///
    /// Every test here starts a process, and a console window takes the foreground as it appears —
    /// so a suite run while somebody is typing took the keyboard off them a dozen times. The
    /// debuggee still has a console and still runs exactly as it would; only the window is withheld.
    /// </summary>
    private static ManagedDebugSession Headless() => new() { ShowConsole = false };

    /// <summary>A managed program to debug, or null when this build has not produced one.</summary>
    private static string? Target
    {
        get
        {
            string here = AppContext.BaseDirectory;
            string guess = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "..", "src", "Spydate.Mcp", "bin", "Debug", "net10.0", "spydate-mcp.exe"));
            return File.Exists(guess) ? guess : null;
        }
    }

    [Fact]
    public void ARuntimeIsFoundAndAttachedTo()
    {
        if (Target is not { } target)
        {
            return;
        }

        using var session = Headless();
        string? problem = session.Start(target);

        Assert.True(problem is null, problem + "\n" + string.Join("\n", session.Recent));
        Assert.Equal(DebugState.Running, session.State);

        // Attaching is the whole of the hard part: the process had to be launched suspended, the
        // debugger registered against it, the runtime given a chance to come up, and the right
        // mscordbi loaded for whichever runtime it picked.
        Assert.Contains(session.Recent, line => line.Contains("runtime is attached", StringComparison.Ordinal));
    }

    [Fact]
    public void StoppingItEndsTheProcess()
    {
        if (Target is not { } target)
        {
            return;
        }

        using var session = Headless();
        Assert.Null(session.Start(target));

        uint pid = session.ProcessId;
        Assert.True(pid != 0, "the session reported no process id");

        session.Stop();

        Assert.Equal(DebugState.Exited, session.State);

        // Asked of the operating system, not of the session. A debugger that believes it has killed
        // something and has not leaves a process running under a dead debugger, and the only place
        // that shows is the machine.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && Alive(pid))
        {
            Thread.Sleep(100);
        }

        Assert.False(Alive(pid), $"process {pid} is still running after Stop()\n" + string.Join("\n", session.Recent));
    }

    private static bool Alive(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void ItBreaksOnAMethodNamedOnlyByItsMetadataToken()
    {
        if (Target is not { } target)
        {
            return;
        }

        // The server opens whatever binary it is given as an argument, and opening one goes through
        // PeImage.Load. So this breaks on a method chosen from the metadata, in a library the
        // debuggee has not loaded yet, and lets the program walk into it.
        uint token = TokenOf("Spydate.Core.PE.PeImage", "Load");

        using var session = Headless();
        string? problem = session.Start(target, arguments: @"C:\Windows\System32\where.exe", holdAtStart: true);
        Assert.True(problem is null, problem);

        // Held before anything managed ran, which is what makes this a test rather than a race.
        Assert.Equal(DebugState.Stopped, session.State);
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", token));
        Assert.Contains(session.Breakpoints, b => !b.Planted);

        session.Continue();

        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)),
            "it never stopped\n" + string.Join("\n", session.Recent));

        Assert.Equal(ManagedStopKind.Breakpoint, session.StoppedBy);

        var at = session.StoppedAt;
        Assert.NotNull(at);
        Assert.Equal(token, at!.MethodToken);
        Assert.Equal("Spydate.Core.dll", at.Module);
        Assert.Equal(0u, at.Offset);

        // The word beside the number, which is the whole point of carrying it: a stop on the first
        // instruction of a method is exactly there. This read the mapping as a list of values when
        // it is a set of flags, so every ordinary stop called itself "unmapped" — the offset right,
        // and a warning attached to it telling the reader not to believe the line it was on.
        Assert.Equal("exact", at.Mapping);
    }

    [Fact]
    public void SteppingMovesOneIlInstructionAtATime()
    {
        if (Target is not { } target)
        {
            return;
        }

        uint token = TokenOf("Spydate.Core.PE.PeImage", "Load");

        using var session = Headless();
        Assert.Null(session.Start(target, arguments: @"C:\Windows\System32\where.exe", holdAtStart: true));
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", token));
        session.Continue();

        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)), string.Join("\n", session.Recent.TakeLast(8)));
        Assert.Equal(0u, session.StoppedAt!.Offset);

        // Stepped over rather than into, so the walk stays inside one method and the offsets can be
        // compared with each other. Stepping in is the same machinery and would be a different
        // assertion: at the first call it lands at offset zero of somewhere else, which is correct
        // and says nothing about whether stepping works.
        var seen = new List<uint> { session.StoppedAt.Offset };
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(session.Step(into: false));
            Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(20)), $"step {i + 1} never landed");
            Assert.Equal(ManagedStopKind.Step, session.StoppedBy);
            Assert.Equal(token, session.StoppedAt!.MethodToken);
            seen.Add(session.StoppedAt.Offset);
        }

        // Forwards, one IL instruction at a time, never twice on the same one. These are the
        // offsets the listing prints, and they are the same on every run — which the addresses the
        // JIT produced are not.
        string walked = string.Join(" -> ", seen.Select(o => $"IL_{o:X4}"));
        Assert.True(seen.Count == seen.Distinct().Count(), "it stepped onto the same instruction twice: " + walked);
        Assert.True(seen.SequenceEqual(seen.OrderBy(o => o)), $"the walk went backwards: {walked}");
    }

    [Fact]
    public void AStoppedFrameSaysWhatItsArgumentsActuallyAre()
    {
        if (Target is not { } target)
        {
            return;
        }

        // This is the thing the native debugger cannot do. PeImage.Load takes a path, and stopping
        // at its first instruction should be able to say which path - not a register, not a stack
        // word to be chased by hand, the string itself.
        const string Opening = @"C:\Windows\System32\where.exe";
        uint token = TokenOf("Spydate.Core.PE.PeImage", "Load");

        using var session = Headless();
        Assert.Null(session.Start(target, arguments: Opening, holdAtStart: true));
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", token));
        session.Continue();

        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)), string.Join("\n", session.Recent.TakeLast(8)));

        var arguments = session.Values(arguments: true);
        Assert.NotEmpty(arguments);

        var path = arguments[0];
        Assert.Equal("string", path.Kind);
        Assert.Contains("where.exe", path.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrimitiveLocalsReadAsValuesRatherThanAsBytes()
    {
        if (Target is not { } target)
        {
            return;
        }

        // PeImage.Load declares its locals with .locals init, so at its first instruction they are
        // all zero - and a bool that reads "false" is a bool that was really read, where one that
        // reads "not available" is an interface this could not reach.
        uint token = TokenOf("Spydate.Core.PE.PeImage", "Load");

        using var session = Headless();
        Assert.Null(session.Start(target, arguments: @"C:\Windows\System32\where.exe", holdAtStart: true));
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", token));
        session.Continue();

        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)), string.Join("\n", session.Recent.TakeLast(8)));

        var locals = session.Values();
        Assert.NotEmpty(locals);

        var bools = locals.Where(l => l.Kind == "bool").ToList();
        Assert.NotEmpty(bools);
        Assert.All(bools, b => Assert.Equal("false", b.Text));
    }

    [Fact]
    public void ValuesAreOnlyReadableWhileItIsStopped()
    {
        if (Target is not { } target)
        {
            return;
        }

        using var session = Headless();
        Assert.Null(session.Start(target));

        // A running program has no frame to read, and inventing one would be inventing values.
        Assert.Empty(session.Values());
        Assert.Empty(session.Values(arguments: true));
    }

    [Fact]
    public void SteppingSomethingThatIsNotStoppedSaysSo()
    {
        if (Target is not { } target)
        {
            return;
        }

        using var session = Headless();
        Assert.Null(session.Start(target));

        // Running, not stopped. A step accepted here would arm a stepper against nothing and the
        // caller would wait for a landing that never comes.
        Assert.Contains("not stopped", session.Step()!, StringComparison.Ordinal);
    }

    [Fact]
    public void AClearedBreakpointStopsFiringInTheProcessThatIsAlreadyRunning()
    {
        if (Target is not { } target)
        {
            return;
        }

        // A method called over and over while one file is parsed, which is what makes this provable:
        // a breakpoint in something called once cannot tell a clear that worked from a clear that
        // did nothing. So the test watches it fire twice first, and only then removes it.
        uint token = TokenOf("Spydate.Core.PE.PeImage", "RvaToOffset");

        using var session = Headless();
        Assert.Null(session.Start(target, arguments: @"C:\Windows\System32\where.exe", holdAtStart: true));
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", token));
        session.Continue();

        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)), string.Join("\n", session.Recent.TakeLast(8)));
        Assert.Equal(ManagedStopKind.Breakpoint, session.StoppedBy);
        Assert.Equal(token, session.StoppedAt!.MethodToken);

        // Twice, so what follows is measured against a breakpoint that was demonstrably still live.
        session.Continue();
        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)), "it only ever hit it once, so clearing proves nothing");
        Assert.Equal(ManagedStopKind.Breakpoint, session.StoppedBy);

        Assert.Null(session.ClearBreakpoint("Spydate.Core.dll", token));
        Assert.Empty(session.Breakpoints);

        // The part that releasing the pointer alone does not do. The runtime holds a reference of
        // its own, so a breakpoint merely let go of goes on stopping the program — which reads as a
        // debugger that reported success and changed nothing.
        session.Continue();
        Assert.False(
            session.WaitUntilStopped(TimeSpan.FromSeconds(15)),
            "it stopped again after the breakpoint was cleared: " + session.Status);
    }

    [Fact]
    public void OneCanBeClearedAfterTheProgramHasAlreadyGone()
    {
        if (Target is not { } target)
        {
            return;
        }

        // The breakpoint object outlives the process it was in, and asking a dead one to deactivate
        // fails. Reporting that as a refusal would leave a breakpoint from a finished run impossible
        // to drop — the marker stuck in the listing over a program that is not running at all.
        uint token = TokenOf("Spydate.Core.PE.PeImage", "Load");

        using var session = Headless();
        Assert.Null(session.Start(target, arguments: @"C:\Windows\System32\where.exe", holdAtStart: true));
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", token));
        session.Continue();

        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)), string.Join("\n", session.Recent.TakeLast(8)));
        Assert.Contains(session.Breakpoints, b => b.Planted);

        session.Stop();
        Assert.Equal(DebugState.Exited, session.State);

        Assert.Null(session.ClearBreakpoint("Spydate.Core.dll", token));
        Assert.Empty(session.Breakpoints);
    }

    [Fact]
    public void ClearingOneThatIsStillWaitingForItsModuleSimplyForgetsIt()
    {
        if (Target is not { } target)
        {
            return;
        }

        using var session = Headless();
        Assert.Null(session.Start(target, holdAtStart: true));
        Assert.Null(session.SetBreakpoint("SomethingNotLoaded.dll", 0x06000001));
        Assert.Single(session.Breakpoints);

        // Nothing was ever planted, so there is nothing to deactivate and forgetting the note is the
        // whole of removing it. Refusing here would leave the only breakpoints that can be set
        // before a run — which is most of them — as the ones that cannot be taken back.
        Assert.Null(session.ClearBreakpoint("SomethingNotLoaded.dll", 0x06000001));
        Assert.Empty(session.Breakpoints);
    }

    [Fact]
    public void ClearingOneThatWasNeverSetSaysSoRatherThanReportingSuccess()
    {
        if (Target is not { } target)
        {
            return;
        }

        using var session = Headless();
        Assert.Null(session.Start(target, holdAtStart: true));
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", 0x06000001, ilOffset: 4));

        // The right method at the wrong offset is the way this is got wrong in practice, and a
        // cheerful "cleared" would leave the caller waiting to not stop at something it still stops
        // at. Both halves are named so the mismatch is visible.
        string? problem = session.ClearBreakpoint("Spydate.Core.dll", 0x06000001);
        Assert.NotNull(problem);
        Assert.Contains("no breakpoint", problem!, StringComparison.Ordinal);
        Assert.Contains("IL_0000", problem!, StringComparison.Ordinal);

        // And the one that is really there is untouched by the failed attempt.
        Assert.Single(session.Breakpoints);
    }

    [Fact]
    public void OneCanBeClearedWithoutStoppingTheProgramFirst()
    {
        if (Target is not { } target)
        {
            return;
        }

        // The case the panel is actually in. Somebody watching a program run clicks the dot off, and
        // a debugger that answered "stop it first" would be asking them to interrupt the thing they
        // are watching in order to stop watching part of it. Most of ICorDebug does refuse a running
        // process — Terminate does, which is why Stop() synchronises first — so this is worth
        // pinning rather than assuming.
        uint token = TokenOf("Spydate.Core.PE.PeImage", "Load");

        using var session = Headless();
        Assert.Null(session.Start(target, arguments: @"C:\Windows\System32\where.exe", holdAtStart: true));
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", token));
        session.Continue();

        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)), string.Join("\n", session.Recent.TakeLast(8)));

        // Let go, and given long enough to get past the file it was opening and back to waiting on
        // its input. The assertion below is what says it really is running.
        session.Continue();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && session.State != DebugState.Running)
        {
            Thread.Sleep(100);
        }

        Assert.Equal(DebugState.Running, session.State);
        Assert.Null(session.ClearBreakpoint("Spydate.Core.dll", token));
        Assert.Empty(session.Breakpoints);
    }

    [Fact]
    public void ABreakpointWaitsForTheModuleItIsIn()
    {
        if (Target is not { } target)
        {
            return;
        }

        using var session = Headless();
        Assert.Null(session.Start(target, holdAtStart: true));

        // Nothing has loaded this yet, and it never will - the point is that asking is not an error.
        // Most breakpoints worth setting are in libraries that load later, and a debugger that could
        // only set them once loaded could not set the interesting ones at all.
        Assert.Null(session.SetBreakpoint("SomethingNotLoaded.dll", 0x06000001));

        var waiting = Assert.Single(session.Breakpoints);
        Assert.False(waiting.Planted);
        Assert.Contains("waiting for the module", waiting.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMethodThatIsNotThereIsRefusedRatherThanSilentlyNeverHit()
    {
        if (Target is not { } target)
        {
            return;
        }

        using var session = Headless();
        Assert.Null(session.Start(target, arguments: @"C:\Windows\System32\where.exe", holdAtStart: true));

        // Nothing is loaded yet at this point, so the answer cannot come now - it comes when the
        // module arrives. It has to come at all, though: a breakpoint accepted on a method that does
        // not exist is one that never fires and never says why.
        Assert.Empty(session.Modules);
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", 0x06FFFFFF));

        session.Continue();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !session.Recent.Any(l => l.Contains("0x06FFFFFF", StringComparison.Ordinal)))
        {
            Thread.Sleep(100);
        }

        Assert.Contains(session.Recent, l => l.Contains("0x06FFFFFF", StringComparison.Ordinal));
        Assert.Contains(session.Recent, l => l.Contains("no method with token", StringComparison.Ordinal));
    }

    /// <summary>The metadata token of a method, which is how a managed breakpoint names one.</summary>
    private static uint TokenOf(string type, string method)
    {
        using var assembly = Spydate.Decompiler.Managed.ManagedAssembly.Load(typeof(Spydate.Core.PE.PeImage).Assembly.Location);
        var member = assembly.Namespaces
            .SelectMany(n => n.Types)
            .First(t => t.FullName == type)
            .Members.First(m => m.Name == method);

        return (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(member.Handle);
    }

    [Fact]
    public void SomethingThatIsNotManagedSaysSoRatherThanWaitingForever()
    {
        // A native program never publishes a runtime, so the wait is the only thing that can end
        // this - and it has to end, with a sentence that names the likely reason.
        string native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        if (!File.Exists(native))
        {
            return;
        }

        using var session = Headless();
        string? problem = session.Start(native, timeout: TimeSpan.FromSeconds(6));

        Assert.NotNull(problem);
        Assert.Contains("not .NET", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotThereIsRefusedBeforeAnythingIsLaunched()
    {
        using var session = Headless();

        Assert.Contains("there is no file at", session.Start(@"C:\nothing\here.exe")!, StringComparison.Ordinal);
        Assert.Equal(DebugState.NotStarted, session.State);
    }
}
