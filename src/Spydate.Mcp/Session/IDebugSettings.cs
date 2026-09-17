namespace Spydate.Mcp.Session;

/// <summary>
/// How the next run will start — the Debug Program settings — as one readable snapshot. The same
/// fields the window's dialog holds, so an agent can read and change them without a person opening it.
/// </summary>
public sealed record DebugSettingsSnapshot
{
    /// <summary>"managed" (the .NET CLR engine) or "native" (the native loop; mixed mode for a .NET target).</summary>
    public required string Engine { get; init; }

    /// <summary>Whether the target is a .NET assembly, and so both engines are available. A native
    /// binary is native only, and asking for the managed engine on one is refused.</summary>
    public required bool TargetIsManaged { get; init; }

    /// <summary>Whether a run is in progress. The settings are settled once it is, and cannot be changed
    /// until it stops.</summary>
    public required bool Running { get; init; }

    /// <summary>Whether the executable can be set — off for a native EXE, which is its own program.</summary>
    public required bool ExecutableEditable { get; init; }

    /// <summary>The program to launch: a host for a DLL or .NET assembly, or empty for a native EXE that
    /// is its own program.</summary>
    public string Executable { get; init; } = string.Empty;

    public string Arguments { get; init; } = string.Empty;

    /// <summary>Empty means the host's own folder.</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>Where the run stops of its own accord, as one of <see cref="BreakAtOptions"/>.</summary>
    public required string BreakAt { get; init; }

    /// <summary>The engines that may be set for this target.</summary>
    public IReadOnlyList<string> EngineOptions { get; init; } = [];

    /// <summary>The break points that may be set.</summary>
    public IReadOnlyList<string> BreakAtOptions { get; init; } = [];
}

/// <summary>
/// Reads and changes the run configuration — engine, executable, arguments, working directory and
/// where it breaks — the fields a person would otherwise set in the Debug Program dialog. It exists so
/// an agent can settle how a binary runs, and switch a .NET target between the managed and native
/// engines, without a human in the loop. A host that does not offer one is one where those settings
/// are not the agent's to change; see <see cref="SessionStore.DebugSettings"/>.
/// </summary>
public interface IDebugSettings
{
    /// <summary>The settings as they stand.</summary>
    DebugSettingsSnapshot Read();

    /// <summary>
    /// Changes the given fields and leaves the rest; a null argument is left alone. Returns null on
    /// success, or why it could not — a run in progress, the managed engine asked of a native binary,
    /// an executable that cannot be set, an unknown engine or break point.
    /// </summary>
    string? Apply(string? engine, string? executable, string? arguments, string? workingDirectory, string? breakAt);
}
