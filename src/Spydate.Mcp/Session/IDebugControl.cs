namespace Spydate.Mcp.Session;

/// <summary>Where execution is and what is around it, as one readable snapshot.</summary>
public sealed record DebugSnapshot
{
    /// <summary>not started, running, stopped, or exited.</summary>
    public required string State { get; init; }

    /// <summary>A line saying what it is doing, the same one the panel shows.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Where it stopped, as the listing states it. Null while running or outside the image.</summary>
    public ulong? Address { get; init; }

    /// <summary>Whether the module the listing is about has been loaded yet.</summary>
    public bool TargetLoaded { get; init; }

    public IReadOnlyList<(string Name, ulong Value)> Registers { get; init; } = [];

    /// <summary>The flags spelled out, because reading them off a hex RFLAGS is arithmetic.</summary>
    public string Flags { get; init; } = string.Empty;

    public IReadOnlyList<(ulong Address, ulong Value)> Stack { get; init; } = [];

    public IReadOnlyList<(string Name, ulong Base, bool IsTarget)> Modules { get; init; } = [];

    /// <summary>Breakpoints by static address — the ones in the listing.</summary>
    public IReadOnlyList<ulong> Breakpoints { get; init; } = [];
}

/// <summary>
/// Driving a debugger, as much of it as an agent is given.
///
/// It is an interface rather than the debug session itself for two reasons. The tools must act on
/// the <em>same</em> debugger the window is showing — a second process started behind the analyst's
/// back, with its own breakpoints, would be worse than none — and <c>Spydate.Mcp</c> has no business
/// referencing <c>Spydate.Debugger</c> just to describe what it wants done.
///
/// A host that does not offer one is a host where the agent cannot debug, which is the default: see
/// <see cref="McpOptions.AllowDebug"/> for why starting a process is not something a client gets
/// merely by connecting.
/// </summary>
public interface IDebugControl
{
    /// <summary>
    /// Starts it, using whatever host and arguments were configured for this binary. Returns null on
    /// success, or why it did not start — a DLL with no host, a 32-bit image, a refused confirmation.
    /// </summary>
    string? Start();

    void Stop();

    /// <summary>Lets it run on. Does nothing unless it is stopped.</summary>
    void Continue();

    /// <summary>One instruction, into a call.</summary>
    void StepInstruction();

    /// <summary>One instruction, over a call.</summary>
    void StepOver();

    /// <summary>Runs until it reaches a static address, without leaving a breakpoint there.</summary>
    void RunTo(ulong staticVa);

    /// <summary>Sets or clears a breakpoint at a static address. Returns whether it is now set.</summary>
    bool SetBreakpoint(ulong staticVa, bool on);

    /// <summary>Everything readable right now.</summary>
    DebugSnapshot Snapshot();

    /// <summary>Reads the debuggee's memory at a static address. Empty when it cannot be read.</summary>
    byte[] ReadMemory(ulong staticVa, int length);

    /// <summary>
    /// Waits for it to come to a stop, or gives up. Every command here is asynchronous — the debug
    /// loop is on its own thread and the process runs until it hits something — so a tool that
    /// returned the moment it asked would report the state before anything happened.
    /// </summary>
    bool WaitUntilStopped(TimeSpan timeout);
}
