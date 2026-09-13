using System.Diagnostics;
using Spydate.Debugger;
using Spydate.Debugger.Managed;
using Xunit;

namespace Spydate.Tests;

/// <summary>
/// Which runtime a program will start under, and what happens when the answer is .NET Framework.
///
/// Worth testing because getting it wrong is silent. A .NET Framework program debugged as though it
/// were .NET is launched, runs to the end, exits — and thirty seconds later the debugger says its
/// runtime never became debuggable, having watched the whole thing happen. No exception, no failing
/// HRESULT, no breakpoint. That is what every .NET Framework binary did here.
/// </summary>
[Collection(Debugging.Name)]
public class ManagedTargetTests
{
    /// <summary>A .NET Framework program. See <see cref="FrameworkFixture"/> for why it is built.</summary>
    private static string? FrameworkProgram => FrameworkFixture.Build("Legacy", """
        using System;
        class Legacy
        {
            static int Main(string[] argv)
            {
                int total = 0;
                for (int i = 0; i < 3; i++)
                {
                    total += i;
                }

                Console.WriteLine(total);
                return total;
            }
        }
        """);

    /// <summary>The .NET program the rest of the debugger tests use.</summary>
    private static string? CoreProgram
    {
        get
        {
            string guess = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "Spydate.Mcp", "bin", "Debug", "net10.0", "spydate-mcp.exe"));
            return File.Exists(guess) ? guess : null;
        }
    }

    [SkippableFact]
    public void AProgramBuiltForTheFrameworkIsRecognisedAsOne()
    {
        Skip.If(FrameworkProgram is null, "no .NET Framework compiler on this machine");

        var target = ManagedTarget.Of(FrameworkProgram!);

        Assert.True(target.Framework, "a .NET Framework program was read as .NET");
        Assert.True(target.Wide, "an x64 build was read as 32-bit");
        Assert.Equal("v4.0.30319", target.Runtime);
    }

    [SkippableFact]
    public void AProgramBuiltForDotNetIsNot()
    {
        Skip.If(CoreProgram is null, "this build produced no .NET program");

        // An apphost has no metadata of its own to ask, so the runtimeconfig beside it is the
        // answer — and it is the only one there is for a program published as a single file.
        Assert.False(ManagedTarget.Of(CoreProgram!).Framework);
    }

    [Fact]
    public void SomethingWithNoManagedCodeInItIsNotTheFrameworkEither()
    {
        string native = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll");

        Assert.False(ManagedTarget.Of(native).Framework);
    }

    /// <summary>
    /// The point of the whole exercise: a .NET Framework program stops before it runs.
    ///
    /// This failed before in the way that is hardest to see — it did not fail. The program started,
    /// ran to completion, and returned its own exit code, and the debugger reported afterwards that
    /// no runtime had ever become debuggable. So the assertion that matters is not that Start
    /// succeeded but that the program is sitting there not having run: exit code 3 means it got all
    /// the way to the end.
    /// </summary>
    [SkippableFact]
    public void ADotNetFrameworkProgramIsHeldBeforeItRunsAnything()
    {
        Skip.If(FrameworkProgram is null, "no .NET Framework compiler on this machine");

        using var session = new ManagedDebugSession { ShowConsole = false };
        string? problem = session.Start(FrameworkProgram!, holdAtStart: true, timeout: TimeSpan.FromSeconds(30));

        Assert.True(problem is null, problem + "\n" + string.Join("\n", session.Recent));
        Assert.Equal(DebugState.Stopped, session.State);
        Assert.NotEqual(0u, session.ProcessId);
    }

    /// <summary>
    /// And a breakpoint in it is hit, which is the thing a debugger is for.
    ///
    /// The token is 0x06000001: <c>Main</c> is the first and only method the compiler emits here.
    /// It is set while the program is held and before its own module has loaded, so this also
    /// covers the part of the Framework route that plants a waiting breakpoint when the module
    /// arrives.
    /// </summary>
    [SkippableFact]
    public void ABreakpointInADotNetFrameworkProgramIsHit()
    {
        Skip.If(FrameworkProgram is null, "no .NET Framework compiler on this machine");

        using var session = new ManagedDebugSession { ShowConsole = false };
        Assert.Null(session.Start(FrameworkProgram!, holdAtStart: true, timeout: TimeSpan.FromSeconds(30)));

        Assert.Null(session.SetBreakpoint("Legacy.exe", 0x06000001));
        session.Continue();

        Assert.True(
            session.WaitUntilStopped(TimeSpan.FromSeconds(30)),
            "it never stopped:\n" + string.Join("\n", session.Recent));

        Assert.Equal(ManagedStopKind.Breakpoint, session.StoppedBy);
        Assert.Equal("Legacy.exe", session.StoppedAt?.Module);
        Assert.Equal(0x06000001u, session.StoppedAt?.MethodToken);
    }

    /// <summary>
    /// Locals read as values in a .NET Framework frame too.
    ///
    /// Not a given: everything downstream of getting the interface is shared code, and this is what
    /// says so rather than assuming it. <c>Main</c>'s argument is the empty array it was launched
    /// with, and that is a reference the debugger has to follow rather than print the address of.
    /// </summary>
    [SkippableFact]
    public void AFrameworkFrameReadsItsValuesTheSameWay()
    {
        Skip.If(FrameworkProgram is null, "no .NET Framework compiler on this machine");

        using var session = new ManagedDebugSession { ShowConsole = false };
        Assert.Null(session.Start(FrameworkProgram!, holdAtStart: true, timeout: TimeSpan.FromSeconds(30)));
        Assert.Null(session.SetBreakpoint("Legacy.exe", 0x06000001));
        session.Continue();
        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(30)));

        var arguments = session.Values(arguments: true);

        Assert.Single(arguments);
        Assert.Equal("string[]", arguments[0].Kind);
    }
}
