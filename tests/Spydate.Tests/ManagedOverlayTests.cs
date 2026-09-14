using System.Runtime.Versioning;
using Spydate.Debugger;

namespace Spydate.Tests;

/// <summary>
/// The managed overlay: ClrMD reading the managed world while <see cref="DebugSession"/> owns the one
/// OS debug port. The whole point of the mixed-mode design is that these two coexist, so the tests
/// prove exactly that — a managed stack walked, statics read, a native address named in managed terms,
/// all while the native loop holds the process stopped.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(Debugging.Name)]
public sealed class ManagedOverlayTests
{
    /// <summary>
    /// The managed debuggee built beside the tests. It spins in a known method and holds a known
    /// static, so a pause always finds managed frames and a static read has something to check.
    /// Null when this build has not produced it.
    /// </summary>
    private static string? Fixture
    {
        get
        {
            string here = AppContext.BaseDirectory;
            string guess = Path.GetFullPath(Path.Combine(
                here, "..", "..", "..", "..", "Spydate.Tests.ManagedDebuggee", "bin", "Debug", "net10.0", "ManagedDebuggee.exe"));
            return File.Exists(guess) ? guess : null;
        }
    }

    private const string SpinFrame = "ManagedDebuggee.Program.Spin";

    private static bool Wait(Func<bool> until, int seconds = 20)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (until())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return until();
    }

    /// <summary>
    /// Runs the fixture and stops it with its own code on a managed stack, ready to read.
    ///
    /// A pause the instant a CLR appears can land before the fixture's own type is loaded — the
    /// runtime brings itself up first, and <c>Program</c> and its statics arrive a moment later when
    /// Main runs. So this pauses, checks that <c>Spin</c> is on a managed stack, and if not lets the
    /// process run on and pauses again. Once Spin is there the type is loaded and its static
    /// constructor has run, so statics are readable too.
    /// </summary>
    private static ManagedOverlay PauseInManagedCode(DebugSession session)
    {
        Assert.True(Wait(() => session.Managed?.HasClr == true), "no CLR appeared in the debuggee");
        var overlay = session.Managed!;

        for (int attempt = 0; attempt < 40; attempt++)
        {
            Assert.True(session.Pause(), "could not pause the running debuggee");
            Assert.True(Wait(() => session.State == DebugState.Stopped), "the pause never stopped it");

            if (overlay.Threads().SelectMany(t => t.Frames).Any(f => f.Method?.Contains(SpinFrame, StringComparison.Ordinal) == true))
            {
                return overlay;
            }

            session.Continue();
            Assert.True(Wait(() => session.State == DebugState.Running), "the debuggee never resumed");
            Thread.Sleep(150);
        }

        return overlay;   // let the caller's assertions report what did not turn up
    }

    [Fact]
    public void AManagedStackIsWalkedWhileTheNativeLoopHoldsThePort()
    {
        if (Fixture is not { } fixture)
        {
            return;
        }

        using var session = new DebugSession { ShowConsole = false };

        // Let it run: the CLR is not up at the loader break, and the overlay needs a live runtime.
        // Then the native loop takes the process, with the fixture's own code on a managed stack.
        session.Start(fixture, imageBase: 0, imageSize: 0, entryStop: EntryStop.DontBreak);
        var overlay = PauseInManagedCode(session);

        var threads = overlay.Threads();
        Assert.NotEmpty(threads);

        // The spinner's own frames are on a managed stack, walked while DebugSession owns the port.
        var frames = threads.SelectMany(t => t.Frames).ToList();
        Assert.Contains(frames, f => f.Method?.Contains("ManagedDebuggee.Program.Spin", StringComparison.Ordinal) == true);
        Assert.Contains(frames, f => f.Method?.Contains("ManagedDebuggee.Program.Main", StringComparison.Ordinal) == true);

        // The frames name their module, so a caller can tell the debuggee's own code from the runtime.
        Assert.Contains(frames, f => f.Module is { } m && m.Equals("ManagedDebuggee.dll", StringComparison.OrdinalIgnoreCase));

        session.Stop();
    }

    [Fact]
    public void StaticsAndAManagedLocationAreReadableAtANativeStop()
    {
        if (Fixture is not { } fixture)
        {
            return;
        }

        using var session = new DebugSession { ShowConsole = false };
        session.Start(fixture, imageBase: 0, imageSize: 0, entryStop: EntryStop.DontBreak);
        var overlay = PauseInManagedCode(session);

        // A static string, read straight out of memory, with the value the fixture set.
        var statics = overlay.Statics("ManagedDebuggee.Program");
        var label = Assert.Single(statics, s => s.Name == "Label");
        Assert.Contains("spydate-overlay", label.Value, StringComparison.Ordinal);

        // A native instruction pointer, said in managed terms: the frame is in Spin, and the DAC gives
        // the method back with a real metadata token.
        var spin = overlay.Threads()
            .SelectMany(t => t.Frames)
            .First(f => f.Method?.Contains("ManagedDebuggee.Program.Spin", StringComparison.Ordinal) == true);

        var location = overlay.LocationOf(spin.InstructionPointer);
        Assert.NotNull(location);
        Assert.Contains("Spin", location!.Method, StringComparison.Ordinal);
        Assert.Equal("ManagedDebuggee.dll", location.Module);
        Assert.NotEqual(0, location.MethodToken);

        session.Stop();
    }

    [Fact]
    public void ANativeProcessHasNoManagedOverlay()
    {
        string where = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(where))
        {
            return;
        }

        using var session = new DebugSession { ShowConsole = false };
        session.Start(where, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");

        // A native program has no CLR, so the overlay is present but empty — not throwing, not
        // pretending. This is the answer for the whole native half of Spydate's corpus.
        var overlay = session.Managed!;
        Assert.False(overlay.HasClr);
        Assert.Empty(overlay.Threads());

        session.Stop();
    }
}
