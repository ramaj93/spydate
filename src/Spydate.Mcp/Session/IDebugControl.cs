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

    /// <summary>
    /// Every thread, by id, with where it began and whether it is waiting in the kernel. A waiting
    /// thread cannot finish a step until whatever it is waiting for happens.
    /// </summary>
    public IReadOnlyList<(uint Id, ulong Start, bool Waiting)> Threads { get; init; } = [];

    /// <summary>The thread whose event stopped it. <see cref="Address"/> is where this one is.</summary>
    public uint CurrentThread { get; init; }

    /// <summary>
    /// The thread the registers, flags and stack belong to, and the one a step will move. The
    /// current one until something picks another.
    /// </summary>
    public uint SelectedThread { get; init; }

    /// <summary>The selected thread's registers.</summary>
    public IReadOnlyList<(string Name, ulong Value)> Registers { get; init; } = [];

    /// <summary>The flags spelled out, because reading them off a hex RFLAGS is arithmetic.</summary>
    public string Flags { get; init; } = string.Empty;

    public IReadOnlyList<(ulong Address, ulong Value)> Stack { get; init; } = [];

    public IReadOnlyList<(string Name, ulong Base, bool IsTarget)> Modules { get; init; } = [];

    /// <summary>
    /// Patches that are in the running process but not in the project — hypotheses, tried before
    /// anyone commits to them. Recording one at the same address is what keeps it.
    /// </summary>
    public IReadOnlyList<(uint Rva, ulong Va, string Was, string Now, string? Comment)> LivePatches { get; init; } = [];

    /// <summary>Breakpoints by static address — the ones in the listing.</summary>
    public IReadOnlyList<ulong> Breakpoints { get; init; } = [];

    /// <summary>
    /// The managed call stack, innermost first, when the native engine is driving a .NET target —
    /// mixed mode. Empty for a purely native target and for the managed (.NET CLR) engine, which has
    /// its own snapshot. Its presence is how a snapshot says it is a .NET process under the native
    /// loop: managed breakpoints and this stack are available, but not local variables.
    /// </summary>
    public IReadOnlyList<string> ManagedFrames { get; init; } = [];

    /// <summary>
    /// The last things the debuggee did, oldest first — modules loading, breakpoints being hit,
    /// exceptions, how it ended.
    ///
    /// Without it a snapshot is a state and nothing else, and everything that actually happened
    /// between two questions is invisible: the module arriving, the breakpoint firing, the access
    /// violation. The panel had all of it in front of the analyst the whole time.
    /// </summary>
    public IReadOnlyList<string> Recent { get; init; } = [];
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

    /// <summary>Stops it where it is, without ending it. Does nothing unless it is running.</summary>
    void Pause();

    /// <summary>One instruction, into a call.</summary>
    void StepInstruction();

    /// <summary>One instruction, over a call.</summary>
    void StepOver();

    /// <summary>Runs until it reaches a static address, without leaving a breakpoint there.</summary>
    void RunTo(ulong staticVa);

    /// <summary>Sets or clears a breakpoint at a static address. Returns whether it is now set.</summary>
    bool SetBreakpoint(ulong staticVa, bool on);

    /// <summary>
    /// Makes a thread the one looked at and stepped — for the panel as well, since there is one
    /// debugger and one selection. False when there is no such thread.
    /// </summary>
    bool SelectThread(uint threadId);

    /// <summary>Everything readable right now.</summary>
    DebugSnapshot Snapshot();

    /// <summary>
    /// Assembles an instruction into the running process without recording it. Returns null on
    /// success, or why not.
    ///
    /// The point of it is to be undoable and to leave no trace in the project: a guess about what a
    /// check does is worth trying before it is worth writing down. It shows up in the window beside
    /// the analyst's own hypotheses, because an agent quietly changing a running program where
    /// nobody can see it is the one way this should not work.
    /// </summary>
    string? TryPatch(ulong va, string instruction, string? comment);

    /// <summary>Takes a hypothesis back out of the process. False when none covers that address.</summary>
    bool UndoPatch(uint rva);

    /// <summary>Reads the debuggee's memory at a static address. Empty when it cannot be read.</summary>
    byte[] ReadMemory(ulong staticVa, int length);

    /// <summary>
    /// Waits for it to come to a stop, or gives up. Every command here is asynchronous — the debug
    /// loop is on its own thread and the process runs until it hits something — so a tool that
    /// returned the moment it asked would report the state before anything happened.
    /// </summary>
    bool WaitUntilStopped(TimeSpan timeout);
}
