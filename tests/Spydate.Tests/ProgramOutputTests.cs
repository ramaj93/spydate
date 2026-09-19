using System.Runtime.Versioning;
using Spydate.Debugger;

namespace Spydate.Tests;

/// <summary>
/// The debuggee's own stdout and stderr, and writing its registers.
///
/// Both are things a panel shows and nothing below the panel could check until now. They are
/// checked here rather than in the window because the window has no tests at all, and because what
/// is actually in doubt is the plumbing — whether a pipe reaches end-of-file, whether a context
/// written back takes — rather than whether a list control displays a string.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(Debugging.Name)]
public sealed class ProgramOutputTests
{
    private static string System32(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    /// <summary>
    /// <c>where.exe</c> asked about itself: it prints one line to stdout and exits. Dull, present on
    /// every machine, and its output is a path this test can recognise.
    /// </summary>
    private static readonly string Trivial = System32("where.exe");

    private static bool Available => OperatingSystem.IsWindows() && File.Exists(Trivial);

    [Fact]
    public void TheDebuggeeUsedByTheseTestsIsActuallyPresent()
        => Assert.True(File.Exists(Trivial), $"{Trivial} is missing, so nothing below was exercised");

    [Fact]
    public void WhatTheProgramPrintsIsCaptured()
    {
        if (!Available)
        {
            return;
        }

        var lines = new List<DebugSession.ProgramOutput>();
        var exited = new ManualResetEventSlim();

        using var session = new DebugSession { ShowConsole = false, CaptureOutput = true };
        session.Wrote += (_, line) =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        };

        session.Reported += (_, e) =>
        {
            if (e.Kind == "exited")
            {
                exited.Set();
            }
        };

        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never reached the loader break");
        session.Continue();
        Assert.True(exited.Wait(TimeSpan.FromSeconds(20)), "the process never exited");

        // The reader runs on its own thread and the pipe drains after the process goes, so the last
        // line can arrive a moment after the exit event.
        Assert.True(Wait(() => { lock (lines) { return lines.Count > 0; } }, 10), "nothing was captured");

        lock (lines)
        {
            Assert.Contains(lines, l => l.Text.Contains("where.exe", StringComparison.OrdinalIgnoreCase));

            // On stdout, and said so. A capture that reported everything as an error would be worse
            // than none: stderr is how a program says it failed.
            Assert.Contains(lines, l => !l.IsError);
        }

        session.Stop();
    }

    [Fact]
    public void NothingIsCapturedWhenItWasNotAskedFor()
    {
        if (!Available)
        {
            return;
        }

        var lines = new List<DebugSession.ProgramOutput>();
        using var session = new DebugSession { ShowConsole = false };
        session.Wrote += (_, line) =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        };

        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");
        session.Continue();
        Thread.Sleep(2000);

        // Off by default, because redirecting a program's handles changes how it runs and that is
        // not something to do to everything that is ever debugged.
        lock (lines)
        {
            Assert.Empty(lines);
        }

        session.Stop();
    }

    [Fact]
    public void ARegisterCanBeWrittenAndReadsBackChanged()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession { ShowConsole = false };
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        uint thread = session.CurrentThreadId;
        const ulong written = 0x1234_5678_9ABC_DEF0;
        var before = session.Registers()!.ToDictionary(r => r.Name, r => r.Value);
        Assert.NotEqual(written, before["rax"]);

        Assert.Null(session.SetRegister(thread, "rax", written));

        var after = session.Registers()!.ToDictionary(r => r.Name, r => r.Value);
        Assert.Equal(written, after["rax"]);

        // And nothing else moved. A context goes back whole, so the failure worth guarding against
        // is one field's write smearing over its neighbours — which a check on rax alone would miss
        // entirely.
        foreach (var (name, value) in before.Where(r => r.Key is not ("rax" or "rflags")))
        {
            Assert.Equal(value, after[name]);
        }

        // rflags is excepted, and not because it is allowed to drift.
        //
        // Writing any register writes the whole context, and the kernel sanitises EFLAGS on the way
        // in to the bits a thread is allowed to set. Bit 1 is reserved and reads as 1 on a live
        // thread, so it survives the read and not the write: 0x246 goes in and 0x244 comes back.
        // Nothing depends on it — it is ignored by the processor — but the difference is real, and
        // masking it here is more honest than excluding the register and calling the rest untouched.
        const ulong reserved = 0x2;
        Assert.Equal(before["rflags"] | reserved, after["rflags"] | reserved);

        session.Stop();
    }

    [Fact]
    public void TheInstructionPointerIsWritableToo()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession { ShowConsole = false };
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        ulong rip = session.Registers()!.Single(r => r.Name == "rip").Value;

        // Moving execution is the reason people want writable registers — skipping a call, retrying
        // a branch — so the one register it would be tempting to protect is the one that must work.
        Assert.Null(session.SetRegister(session.CurrentThreadId, "rip", rip + 1));
        Assert.Equal(rip + 1, session.Registers()!.Single(r => r.Name == "rip").Value);

        Assert.Null(session.SetRegister(session.CurrentThreadId, "rip", rip));
        Assert.Equal(rip, session.Registers()!.Single(r => r.Name == "rip").Value);

        session.Stop();
    }

    [Fact]
    public void ANameThatIsNotARegisterIsRefusedRatherThanWrittenSomewhere()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession { ShowConsole = false };
        session.Start(Trivial, imageBase: 0, imageSize: 0, arguments: "where.exe");
        Assert.True(Wait(() => session.State == DebugState.Stopped), "never stopped");

        string? problem = session.SetRegister(session.CurrentThreadId, "notaregister", 1);

        Assert.NotNull(problem);
        Assert.Contains("notaregister", problem, StringComparison.Ordinal);

        session.Stop();
    }

    [Fact]
    public void WritingIsRefusedWhileItIsRunning()
    {
        using var session = new DebugSession { ShowConsole = false };

        // Nothing started, so there are no threads and no context to write. Refused with a reason
        // rather than throwing at whoever asked.
        string? problem = session.SetRegister(1, "rax", 0);

        Assert.NotNull(problem);
        Assert.Contains("stopped", problem, StringComparison.OrdinalIgnoreCase);
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

        return until();
    }

    /// <summary>
    /// A launch that fails says why.
    ///
    /// It did not. The last Win32 error was read after the pipe write ends were closed, and a
    /// successful CloseHandle sets that error to zero — so every refused launch, whatever the
    /// reason, reported itself as "The operation completed successfully". Capturing output is what
    /// creates those handles, so it has to be on for this to be the old bug rather than a new test.
    /// </summary>
    [Fact]
    public void AFailedLaunchSaysWhyRatherThanSaying_ItWorked()
    {
        if (!Available)
        {
            return;
        }

        using var session = new DebugSession { ShowConsole = false, CaptureOutput = true };

        var thrown = Assert.ThrowsAny<Exception>(
            () => session.Start(System32("there-is-no-such-program.exe"), imageBase: 0, imageSize: 0));

        Assert.DoesNotContain("operation completed successfully", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot find the file", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }
}
