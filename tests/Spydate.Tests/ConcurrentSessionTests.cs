using System.Runtime.Versioning;
using Spydate.Debugger;
using Spydate.Debugger.Managed;

namespace Spydate.Tests;

/// <summary>
/// Several debuggees at once, which is what file tabs need and what nothing here has ever done.
///
/// The claim being tested is the one in <c>docs/MULTI-FILE.md</c> §1. A Windows process has exactly
/// one debug port, and it is easy to read that as "a debugger may own one process" — it is not. The
/// rule binds the debuggee: one debugger per process, and nothing at all about how many processes a
/// debugger may hold.
///
/// What would actually break it is a level below. The debug object lives in the debugger *thread's*
/// TEB, created on that thread's first attach, and <c>WaitForDebugEvent</c> on it returns the events
/// of every process that thread debugs, interleaved. Two sessions sharing a pump thread would see
/// each other's stops and each would continue a process it does not own. <see cref="DebugSession"/>
/// starts a thread per instance and holds no mutable static state, so they do not share one — and
/// these tests are here because that was an argument from the API contract until something ran it.
///
/// The two debuggees are deliberately **different programs**. Two copies of one program stop at the
/// same loader break at the same address, so "the other session did not move" would hold just as
/// well if the test had read the wrong session — it would prove nothing while looking like proof.
/// Different images make each session's identity checkable: a module list naming its own executable
/// and not the other's.
///
/// They are written to leave nothing behind. Each session is disposed whether or not the assertions
/// hold, and each skips rather than fails when its debuggee is not on the machine.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(Debugging.Name)]
public sealed class ConcurrentSessionTests
{
    private static string System32(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    /// <summary>
    /// Two dull debuggees, in the spirit of the single-session tests: on every machine, doing
    /// nothing, exiting on their own if they are ever let go.
    /// </summary>
    private static readonly string First = System32("where.exe");

    private static readonly string Second = System32("hostname.exe");

    private static bool Available =>
        OperatingSystem.IsWindows() && File.Exists(First) && File.Exists(Second);

    /// <summary>A session whose debuggee gets no console window, so a run does not strobe.</summary>
    private static DebugSession Headless() => new() { ShowConsole = false };

    private static void Launch(DebugSession session, string path) =>
        session.Start(path, imageBase: 0, imageSize: 0, arguments: Path.GetFileName(path));

    private static ulong Rip(DebugSession session) =>
        session.Registers()!.Single(r => r.Name == "rip").Value;

    /// <summary>Whether this session's process is the one it was asked to debug, and not the other.</summary>
    private static void AssertOwns(DebugSession session, string mine, string theirs)
    {
        string me = Path.GetFileName(mine);
        string them = Path.GetFileName(theirs);

        Assert.Contains(session.Modules, m => string.Equals(m.Name, me, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Modules, m => string.Equals(m.Name, them, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BothDebuggeesUsedByTheseTestsAreActuallyPresent()
    {
        // Every test here returns early without them and would pass having proved nothing, which is
        // the failure mode that matters most in a file like this: the claim being tested is one
        // nobody can check by reading, so a silent skip reads exactly like a green light.
        Assert.True(File.Exists(First), $"{First} is missing, so concurrency was never exercised");
        Assert.True(File.Exists(Second), $"{Second} is missing, so concurrency was never exercised");
        Assert.NotNull(ManagedTarget);
    }

    [Fact]
    public void TwoProcessesCanBeDebuggedAtTheSameTime()
    {
        if (!Available)
        {
            return;
        }

        using var first = Headless();
        using var second = Headless();

        Launch(first, First);
        Assert.True(Wait(() => first.State == DebugState.Stopped), "the first never reached its loader break");

        // Started while the first is still held at its loader break. If one debug object were being
        // shared, this is where it would show: the second attach would either be refused or would
        // start delivering its events to the first loop.
        Launch(second, Second);
        Assert.True(Wait(() => second.State == DebugState.Stopped), "the second never reached its loader break");

        Assert.Equal(DebugState.Stopped, first.State);
        Assert.Equal(DebugState.Stopped, second.State);

        // Each holds its own process and only its own. This is the assertion that makes the rest of
        // the file mean anything: it is what distinguishes two independent sessions from one session
        // answering twice.
        AssertOwns(first, First, Second);
        AssertOwns(second, Second, First);

        first.Stop();
        second.Stop();
    }

    [Fact]
    public void SteppingOneLeavesTheOtherWhereItWas()
    {
        if (!Available)
        {
            return;
        }

        using var first = Headless();
        using var second = Headless();

        Launch(first, First);
        Assert.True(Wait(() => first.State == DebugState.Stopped), "the first never stopped");
        Launch(second, Second);
        Assert.True(Wait(() => second.State == DebugState.Stopped), "the second never stopped");

        AssertOwns(first, First, Second);
        AssertOwns(second, Second, First);

        ulong firstBefore = Rip(first);
        ulong secondBefore = Rip(second);

        first.StepInstruction();
        Assert.True(Wait(() => first.State == DebugState.Stopped), "the first never stopped after its step");

        // The whole question, in two assertions. A step is a continue with the trap flag set, and a
        // loop holding the wrong process would move that one instead — which fails the first — while
        // a loop handed both would move them both, which fails the second.
        Assert.NotEqual(firstBefore, Rip(first));
        Assert.Equal(secondBefore, Rip(second));
        Assert.Equal(DebugState.Stopped, second.State);

        // And the other way round, so this cannot pass by one session simply being inert.
        second.StepInstruction();
        Assert.True(Wait(() => second.State == DebugState.Stopped), "the second never stopped after its step");

        Assert.NotEqual(secondBefore, Rip(second));

        first.Stop();
        second.Stop();
    }

    [Fact]
    public void NeitherSessionIsToldAboutTheOthersProcess()
    {
        if (!Available)
        {
            return;
        }

        var firstEvents = new List<DebugEvent>();
        var secondEvents = new List<DebugEvent>();

        using var first = Headless();
        using var second = Headless();

        first.Reported += (_, e) =>
        {
            lock (firstEvents)
            {
                firstEvents.Add(e);
            }
        };

        second.Reported += (_, e) =>
        {
            lock (secondEvents)
            {
                secondEvents.Add(e);
            }
        };

        Launch(first, First);
        Assert.True(Wait(() => first.State == DebugState.Stopped), "the first never stopped");
        Launch(second, Second);
        Assert.True(Wait(() => second.State == DebugState.Stopped), "the second never stopped");

        first.Stop();
        second.Stop();

        // One process each. Shared event streams would show up as a doubled start — the tell that
        // one loop was being handed both processes' events — and that is worth asserting exactly
        // rather than merely asserting each saw a start at all.
        lock (firstEvents)
        {
            Assert.Equal(1, firstEvents.Count(e => e.Kind == "started"));
        }

        lock (secondEvents)
        {
            Assert.Equal(1, secondEvents.Count(e => e.Kind == "started"));
        }
    }

    /// <summary>
    /// A .NET program under <c>ICorDebug</c> beside a native one under the Win32 loop.
    ///
    /// The mixed-mode work established that these two cannot both hold the *same* process, because
    /// each calls the OS attach itself and the second loses. Different processes is the case that
    /// was never tried, and it is the one a tab per file depends on.
    /// </summary>
    [Fact]
    public void AManagedSessionAndANativeOneCoexist()
    {
        if (!Available || ManagedTarget is not { } target)
        {
            return;
        }

        using var native = Headless();
        using var managed = new ManagedDebugSession { ShowConsole = false };

        Launch(native, First);
        Assert.True(Wait(() => native.State == DebugState.Stopped), "the native session never stopped");

        // Started while the native debuggee is held. The runtime has to be found, registered against
        // and attached to, all with another debug loop already running in this process.
        string? problem = managed.Start(target);
        Assert.True(problem is null, problem + "\n" + string.Join("\n", managed.Recent));

        Assert.Equal(DebugState.Running, managed.State);
        Assert.Equal(DebugState.Stopped, native.State);

        // Attaching is the whole of the hard part, and this is the line that proves it happened
        // rather than that the test reached the end: the process had to be launched suspended, the
        // debugger registered against it, and the right mscordbi loaded — all with another debug
        // loop already live in this process.
        Assert.Contains(managed.Recent, line => line.Contains("runtime is attached", StringComparison.Ordinal));

        // The native one is still answerable — still holding its own process, still where it was,
        // rather than having been continued or collected by the managed attach.
        Assert.NotNull(native.Registers());
        Assert.Contains(native.Modules, m => string.Equals(m.Name, "where.exe", StringComparison.OrdinalIgnoreCase));

        managed.Stop();
        native.Stop();
    }

    /// <summary>Spydate's own MCP server: a real .NET console program, or null when unbuilt.</summary>
    private static string? ManagedTarget
    {
        get
        {
            string here = AppContext.BaseDirectory;
            string guess = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "..", "src", "Spydate.Mcp", "bin", "Debug", "net10.0", "spydate-mcp.exe"));
            return File.Exists(guess) ? guess : null;
        }
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
