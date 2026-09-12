namespace Spydate.Mcp.Session;

/// <summary>One local or argument of a stopped managed frame.</summary>
public readonly record struct ManagedSlot(int Index, string Kind, string Text)
{
    public override string ToString() => $"[{Index}] {Kind} = {Text}";
}

/// <summary>
/// Where a managed program is and what it is holding.
///
/// Deliberately not <see cref="DebugSnapshot"/>. Almost none of that type's fields mean anything
/// here — there are no registers to report, the stack words are the JIT's business, and the address
/// a method has is whatever it was compiled to this time — and a snapshot whose every field is
/// meaningful in one mode and misleading in the other is worse than two types.
/// </summary>
public sealed record ManagedSnapshot
{
    /// <summary>not started, running, stopped, or exited.</summary>
    public required string State { get; init; }

    public string Status { get; init; } = string.Empty;

    /// <summary>The method and IL offset it stopped in, written the way a listing writes them.</summary>
    public string? Where { get; init; }

    /// <summary>Whether that offset is exact. A prologue or reordered code maps approximately.</summary>
    public string Mapping { get; init; } = string.Empty;

    public IReadOnlyList<ManagedSlot> Arguments { get; init; } = [];

    public IReadOnlyList<ManagedSlot> Locals { get; init; } = [];

    public IReadOnlyList<string> Modules { get; init; } = [];

    /// <summary>Breakpoints asked for, each saying whether it is in the process yet.</summary>
    public IReadOnlyList<string> Breakpoints { get; init; } = [];

    public IReadOnlyList<string> Recent { get; init; } = [];
}

/// <summary>
/// Driving a debugger for a .NET process, as much of it as an agent is given.
///
/// A second interface rather than more of <see cref="IDebugControl"/>, for the same reason
/// <c>ManagedDebugSession</c> is a second session: ICorDebug takes the process's native debug port
/// and means to be the only debugger attached, so which of the two is running is decided when a
/// binary is opened and never both at once. They also take different things. Nothing here has an
/// address in it — a breakpoint is a method and an offset into its IL, which is what the listing
/// prints and what survives the method being recompiled.
/// </summary>
public interface IManagedDebugControl
{
    /// <summary>
    /// Starts it. Null on success, or why it did not start.
    ///
    /// <paramref name="holdAtStart"/> stops before any managed code runs, which is the only moment a
    /// breakpoint is certainly in place before the code it is about — setting one afterwards races
    /// the program.
    /// </summary>
    string? Start(bool holdAtStart);

    void Stop();

    /// <summary>Lets it run on. Does nothing unless it is stopped.</summary>
    void Continue();

    /// <summary>One IL instruction, into a call or over it. Null when the step was armed.</summary>
    string? Step(bool into);

    /// <summary>Runs to the end of the current method and stops in whatever called it.</summary>
    string? StepOut();

    /// <summary>
    /// Sets or clears a breakpoint at an IL offset in a method. Works before the module is loaded,
    /// which is the normal case. Null when it was taken.
    /// </summary>
    string? SetBreakpoint(string module, uint methodToken, uint ilOffset, bool on);

    /// <summary>Everything readable right now.</summary>
    ManagedSnapshot Snapshot();

    /// <summary>Waits for it to come to a stop, or gives up.</summary>
    bool WaitUntilStopped(TimeSpan timeout);
}
