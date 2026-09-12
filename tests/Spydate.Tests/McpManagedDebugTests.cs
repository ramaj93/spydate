using Spydate.Core.PE;
using Spydate.Decompiler.Managed;
using Spydate.Mcp;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// The debug tools answering for a .NET process.
///
/// Driven against a stand-in rather than a real debugger, deliberately. What is being tested here is
/// the surface — which actions are accepted, how a method is turned into a breakpoint, what a stop
/// reads like — and running a program to find that out would make these tests slow, flaky and about
/// something else. The real debugger has its own tests, which do run programs.
/// </summary>
public class McpManagedDebugTests
{
    private static string CoreAssembly => typeof(PeImage).Assembly.Location;

    private static (DebugTools Tools, Fake Debug, SessionStore Store) Open()
    {
        var image = Corpus.Image(CoreAssembly);
        var managed = ManagedAssembly.Load(CoreAssembly);
        var store = new SessionStore();
        store.Set(new BinarySession(CoreAssembly, image, null, null, DiscoveryState.None, managed: managed));

        var debug = new Fake();
        store.ManagedDebug = debug;
        return (new DebugTools(store, McpOptions.Default with { AllowDebug = true }), debug, store);
    }

    // ------------------------------------------------------------------
    // Moving it
    // ------------------------------------------------------------------

    [Fact]
    public void StartingHoldsItBeforeItRunsAnything()
    {
        var (tools, debug, _) = Open();

        tools.Run("start");

        // Always held, and it costs nothing: the agent gets one continue to spend, and in exchange
        // every breakpoint it sets is certainly in place before the code it is about runs.
        Assert.True(debug.Held);
    }

    [Fact]
    public void MovingSomethingThatIsNotStoppedSaysSoRatherThanDoingNothing()
    {
        var (tools, debug, _) = Open();
        debug.State = "running";

        string text = tools.Run("step");

        Assert.Contains("running and has not stopped", text, StringComparison.Ordinal);
        Assert.Equal(0, debug.Steps);
    }

    [Fact]
    public void TheTwoActionsWithNoManagedMeaningAreNamedRatherThanMissing()
    {
        var (tools, _, _) = Open();

        // Both are real gaps. An agent that asked for one needs to be told it did not happen, not to
        // read back a state that looks as though it did.
        Assert.Contains("not implemented", tools.Run("pause"), StringComparison.Ordinal);
        Assert.Contains("debug_break", tools.Run("run_to"), StringComparison.Ordinal);
    }

    [Fact]
    public void SteppingOutIsOfferedHereBecauseTheRuntimeKnowsWhereToStop()
    {
        var (tools, debug, _) = Open();
        debug.State = "stopped";

        tools.Run("step_out");

        Assert.True(debug.SteppedOut);
    }

    // ------------------------------------------------------------------
    // Breaking on a method
    // ------------------------------------------------------------------

    [Fact]
    public void ABreakpointIsSetByNamingAMethodRatherThanAnAddress()
    {
        var (tools, debug, _) = Open();

        string text = tools.Break("Spydate.Core.PE.PeImage::Load");

        Assert.Contains("breakpoint", text, StringComparison.Ordinal);
        Assert.Equal("Spydate.Core.dll", debug.Module);
        Assert.Equal(0u, debug.Offset);

        // The token is what the runtime is actually given, and it has to be the metadata's own.
        Assert.Equal(0x06u, debug.Token >> 24);
    }

    [Fact]
    public void AnOffsetIntoTheMethodIsReadTheWayAListingWritesIt()
    {
        var (tools, debug, _) = Open();

        tools.Break("Spydate.Core.PE.PeImage::Load+IL_000F");

        Assert.Equal(0x0Fu, debug.Offset);
    }

    [Fact]
    public void BreakingOnATypeSaysWhyThatIsNotAPlace()
    {
        var (tools, _, _) = Open();

        // A breakpoint goes in code. Accepting one on a type would be accepting something that can
        // never fire, and never saying why.
        string text = tools.Break("Spydate.Core.PE.PeImage");

        Assert.Contains("not a method", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMethodThatIsNotThereIsRefusedWithASuggestion()
    {
        var (tools, _, _) = Open();

        string text = tools.Break("Spydate.Core.PE.PeImagg::Load");

        Assert.Contains("PeImage", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Reading a stop
    // ------------------------------------------------------------------

    [Fact]
    public void AStopReadsAsAMethodAndWhatTheFrameIsHolding()
    {
        var (tools, debug, _) = Open();
        debug.State = "stopped";
        debug.Where = "Spydate.Core.dll!0x06000123+IL_0000";
        debug.Arguments = [new ManagedSlot(0, "string", "\"C:\\\\Windows\\\\notepad.exe\"")];
        debug.Locals = [new ManagedSlot(0, "bool", "false")];

        string text = tools.State();

        Assert.Contains("IL_0000", text, StringComparison.Ordinal);
        Assert.Contains("arguments:", text, StringComparison.Ordinal);
        Assert.Contains("notepad.exe", text, StringComparison.Ordinal);
        Assert.Contains("locals:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOffsetThatIsNotExactSaysSoEveryTime()
    {
        var (tools, debug, _) = Open();
        debug.State = "stopped";
        debug.Where = "Spydate.Core.dll!0x06000123+IL_0007";
        debug.Mapping = "approximate";

        // A frame in a prologue or in code the JIT reordered maps approximately, and an offset read
        // as exact when it is not points at the wrong line with nothing to show that it is wrong.
        Assert.Contains("approximate mapping", tools.State(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingMemoryByAddressIsRefusedWithTheReason()
    {
        var (tools, _, _) = Open();

        // In an IL-only assembly a listing address names a byte of IL in the file, and the bytes at
        // that address in the process are the JIT's output for something else entirely.
        string text = tools.Memory("0x10001000");

        Assert.Contains("debug_state", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes at", text, StringComparison.Ordinal);
    }

    /// <summary>A debugger that records what it was asked rather than doing any of it.</summary>
    private sealed class Fake : IManagedDebugControl
    {
        public string State { get; set; } = "not started";

        public bool Held { get; private set; }

        public int Steps { get; private set; }

        public bool SteppedOut { get; private set; }

        public string? Module { get; private set; }

        public uint Token { get; private set; }

        public uint Offset { get; private set; }

        public string? Where { get; set; }

        public string Mapping { get; set; } = "exact";

        public IReadOnlyList<ManagedSlot> Arguments { get; set; } = [];

        public IReadOnlyList<ManagedSlot> Locals { get; set; } = [];

        public string? Start(bool holdAtStart)
        {
            Held = holdAtStart;
            State = "stopped";
            return null;
        }

        public void Stop() => State = "exited";

        public void Continue() => State = "running";

        public string? Step(bool into)
        {
            Steps++;
            return null;
        }

        public string? StepOut()
        {
            SteppedOut = true;
            return null;
        }

        public string? SetBreakpoint(string module, uint methodToken, uint ilOffset, bool on)
        {
            Module = module;
            Token = methodToken;
            Offset = ilOffset;
            return null;
        }

        public ManagedSnapshot Snapshot() => new()
        {
            State = State,
            Status = "as it was",
            Where = Where,
            Mapping = Mapping,
            Arguments = Arguments,
            Locals = Locals,
        };

        public bool WaitUntilStopped(TimeSpan timeout) => State == "stopped";
    }
}
