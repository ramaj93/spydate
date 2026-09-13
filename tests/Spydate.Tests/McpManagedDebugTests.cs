using System.Reflection.Metadata.Ecma335;
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
    public void ClearingOneNamesTheSameMethodAndSaysItIsGone()
    {
        var (tools, debug, _) = Open();

        tools.Break("Spydate.Core.PE.PeImage::Load+IL_000F");
        string text = tools.Break("Spydate.Core.PE.PeImage::Load+IL_000F", on: false);

        // The same three things it was set with, because that is all a managed breakpoint is: no
        // address is involved in either direction, so clearing one cannot miss by an address.
        Assert.Equal("Spydate.Core.dll", debug.Module);
        Assert.Equal(0x0Fu, debug.Offset);
        Assert.False(debug.Wanted);
        Assert.Contains("cleared", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABreakpointCanBeSetByAListingAddressToo()
    {
        var (tools, debug, store) = Open();

        // The token the name form produces, so the address form can be held to the same answer.
        tools.Break("Spydate.Core.PE.PeImage::Load");
        uint token = debug.Token;

        // The address the IL view would print against the first instruction of that method: the image
        // base plus where its IL sits. An agent reads exactly this off the listing.
        var body = store.Current!.Bodies!.All.Single(b => (uint)MetadataTokens.GetToken(b.Method) == token);
        ulong va = store.Current.Image.ImageBase + body.IlRva;

        string text = tools.Break($"0x{va:X}");

        // Same method, resolved through the body map rather than a name — and the offset comes out of
        // the address, not a +IL_ suffix.
        Assert.Equal(token, debug.Token);
        Assert.Equal(0u, debug.Offset);
        Assert.Contains("PeImage::Load", text, StringComparison.Ordinal);
        Assert.Contains($"0x{va:X}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressPartWayIntoAMethodBecomesThatMethodAndAnOffset()
    {
        var (tools, debug, store) = Open();

        tools.Break("Spydate.Core.PE.PeImage::Load");
        uint token = debug.Token;
        var body = store.Current!.Bodies!.All.Single(b => (uint)MetadataTokens.GetToken(b.Method) == token);

        tools.Break($"0x{store.Current.Image.ImageBase + body.IlRva + 4:X}");

        Assert.Equal(token, debug.Token);
        Assert.Equal(4u, debug.Offset);
    }

    [Fact]
    public void AnAddressInAnotherModuleIsSentToTheByNameForm()
    {
        // An address well above the opened image is in some other module. Because managed assemblies
        // share image bases, an address cannot say which — so rather than guess, the tool points at the
        // unambiguous by-name form. (A referenced module that uniquely claims the address does resolve;
        // that path is exercised live, where the module layout is real.)
        var (tools, debug, store) = Open();
        ulong far = store.Current!.Image.ImageBase + 0x80000000;   // past the end of the opened image

        string text = tools.Break($"0x{far:X}");

        Assert.Contains("another assembly", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Type::Method", text, StringComparison.Ordinal);
        Assert.Null(debug.Module);   // nothing was set
    }

    [Fact]
    public void AnAddressThatIsNotInAnyMethodsIlIsRefusedAsAnAddress()
    {
        var (tools, debug, store) = Open();

        // A real address inside the image but before any code — the PE header — so it parses but lands
        // in no method's IL. It is refused as an address, not mistaken for a name.
        ulong va = store.Current!.Image.ImageBase + 0x40;
        string text = tools.Break($"0x{va:X}");

        Assert.Contains("not inside any method's IL", text, StringComparison.Ordinal);
        Assert.Null(debug.Module);   // nothing was set
    }

    [Fact]
    public void AMethodInAnotherAssemblyIsBrokenOnByName()
    {
        var (tools, debug, _) = Open();

        // Not in the opened assembly, but shaped like a method — handed to the resolver that waits for
        // its assembly to load rather than refused as "not in this assembly".
        string text = tools.Break("System.Environment::Exit");

        Assert.Equal(("System.Environment", "Exit", 0u, true), debug.Named);
        Assert.Contains("Environment", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AByNameBreakpointCarriesItsOffsetAndItsClear()
    {
        var (tools, debug, _) = Open();

        tools.Break("System.Environment::Exit+IL_0007");
        Assert.Equal(("System.Environment", "Exit", 0x7u, true), debug.Named);

        tools.Break("System.Windows.Application::Shutdown", on: false);
        Assert.Equal(("System.Windows.Application", "Shutdown", 0u, false), debug.Named);
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

    // ------------------------------------------------------------------
    // Threads
    // ------------------------------------------------------------------

    [Fact]
    public void AStopListsItsThreads()
    {
        var (tools, debug, _) = Open();
        debug.State = "stopped";
        debug.Threads = ["1 #1 Main Thread — Shapes.exe!Program::Main (stopped it, shown)", "2 Worker Thread — [not in managed code]"];

        string text = tools.State();

        Assert.Contains("threads:", text, StringComparison.Ordinal);
        Assert.Contains("Main Thread", text, StringComparison.Ordinal);
        Assert.Contains("Worker Thread", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowingAThreadPicksIt()
    {
        var (tools, debug, _) = Open();
        debug.State = "stopped";

        tools.State(thread: 2);

        // The agent works from ids, and the point of asking for one is that values and the stack then
        // follow it — so the id is passed straight to the selection the window uses.
        Assert.Equal(2u, debug.Selected);
    }

    [Fact]
    public void SteppingAGivenThreadSelectsItFirst()
    {
        var (tools, debug, _) = Open();
        debug.State = "stopped";

        tools.Run("step", thread: 3);

        Assert.Equal(3u, debug.Selected);
        Assert.Equal(1, debug.Steps);
    }

    [Fact]
    public void ASessionThatDoesNotOwnItsAssemblyLeavesItUsableAfterDisposal()
    {
        var assembly = ManagedAssembly.Load(CoreAssembly);
        var session = new BinarySession(
            CoreAssembly, Corpus.Image(CoreAssembly), null, null, DiscoveryState.None,
            managed: assembly, ownsManaged: false);

        session.Dispose();

        // The window opened the assembly and its views are still on it, so the assistant's session
        // going away must not take it with them. Reading the bodies is exactly what those views do,
        // and it would throw on a disposed assembly.
        Assert.NotEmpty(ManagedBodies.Build(assembly).All);
        assembly.Dispose();
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

        /// <summary>Whether the last breakpoint call was setting one or clearing one.</summary>
        public bool? Wanted { get; private set; }

        public string? Where { get; set; }

        public string Mapping { get; set; } = "exact";

        public IReadOnlyList<ManagedSlot> Arguments { get; set; } = [];

        public IReadOnlyList<ManagedSlot> Locals { get; set; } = [];

        public IReadOnlyList<string> Threads { get; set; } = [];

        /// <summary>The last thread asked to be looked at, or null if none was.</summary>
        public uint? Selected { get; private set; }

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
            Wanted = on;
            return null;
        }

        /// <summary>The last Type::Method asked for by name, and its offset.</summary>
        public (string Type, string Method, uint Offset, bool On)? Named { get; private set; }

        public string SetBreakpointByName(string type, string method, uint ilOffset, bool on)
        {
            Named = (type, method, ilOffset, on);
            return $"recorded {type}::{method}";
        }

        public ManagedSnapshot Snapshot() => new()
        {
            State = State,
            Status = "as it was",
            Where = Where,
            Mapping = Mapping,
            Arguments = Arguments,
            Locals = Locals,
            Threads = Threads,
        };

        public string? SelectThread(uint threadId)
        {
            Selected = threadId;
            return null;
        }

        public bool WaitUntilStopped(TimeSpan timeout) => State == "stopped";
    }
}
