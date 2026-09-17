using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spydate.Core.Project;

/// <summary>
/// How to run a binary under the debugger.
///
/// A DLL cannot be started, so something has to load it: <see cref="Host"/> is that something, and
/// the DLL is then a module inside a process that is running somebody else's executable. For an EXE
/// there is no host — it is its own — and the arguments and directory still apply.
/// </summary>
/// <summary>
/// Which debugger drives the process.
///
/// Only ever a question for an IL-only assembly: everything else has native code and no choice to
/// make. <see cref="Native"/> on a .NET program is what makes a breakpoint or a patch in one of the
/// native DLLs it loads possible, at the cost of there being no managed frames or locals — nothing
/// is talking to the runtime then.
/// </summary>
public enum DebugEngine
{
    /// <summary>Decided from the file, which is what happened before there was anything to decide.</summary>
    Auto = 0,

    /// <summary>The Win32 debug loop.</summary>
    Native,

    /// <summary>The CLR debugging interface.</summary>
    Clr,
}

/// <summary>
/// Where a run should stop of its own accord, before anything else is asked of it.
///
/// The names are dnSpy's, because the choice is the same one and an analyst who knows the other
/// program should not have to learn a second vocabulary for it.
/// </summary>
public enum DebugBreakAt
{
    /// <summary>Run on. Nothing stops it but a breakpoint somebody set.</summary>
    None = 0,

    /// <summary>The first moment there is a process at all, before it has run its own code.</summary>
    CreateProcess,

    /// <summary>The executable's entry point.</summary>
    EntryPoint,

    /// <summary>The module's static constructor if it has one, and its entry point if it does not.</summary>
    ModuleCctorOrEntryPoint,
}

public sealed record DebugTarget
{
    /// <summary>The executable to start. Null or empty means the binary itself, which needs it to be one.</summary>
    [JsonPropertyName("host")]
    public string? Host { get; init; }

    /// <summary>Command line for it, after the program name.</summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    /// <summary>Where to start it. Null lets it inherit this process's, which is rarely what is wanted.</summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Which debugger to use, or null for whatever the file implies.
    ///
    /// Written as a name rather than a number: this file is indented and meant to be readable, and a
    /// number would also silently change meaning if the enum were ever reordered.
    /// </summary>
    [JsonPropertyName("engine")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DebugEngine? Engine { get; init; }

    /// <summary>Where to stop on starting, or null for the default.</summary>
    [JsonPropertyName("breakAt")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DebugBreakAt? BreakAt { get; init; }

    /// <summary>
    /// Whether anything has actually been set, so an empty one can be dropped rather than stored.
    ///
    /// The two settings count as unset when they are null, and the caller is expected to write null
    /// for its own defaults. Otherwise merely opening a binary would give it an entry here, and a
    /// file of remembered targets would fill up with binaries nobody ever chose to run.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Host)
                           && string.IsNullOrWhiteSpace(Arguments)
                           && string.IsNullOrWhiteSpace(WorkingDirectory)
                           && Engine is null
                           && BreakAt is null;
}

/// <summary>
/// The host, arguments and directory remembered for each binary.
///
/// Kept with the other per-user state and deliberately not in the <c>.spydate</c> project file. A
/// host path is a path on one machine — <c>D:\Programs\…\Test.exe</c> — and the project file is
/// meant to be shared, diffed and committed. Somebody else opening the same DLL wants its names and
/// comments, not a pointer to a program they do not have. It is also a way of running the thing
/// rather than anything about what the thing means, which is what that file is for.
///
/// Never throws. A debug target is a convenience, and failing to read one is not a reason to fail to
/// open a binary.
/// </summary>
public static class DebugTargets
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spydate",
        "debug.json");

    /// <summary>What was last set for this binary, or null.</summary>
    public static DebugTarget? For(string binaryPath, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        return All(path).TryGetValue(Key(binaryPath), out var target) ? target : null;
    }

    /// <summary>Remembers a target, or forgets it when there is nothing left in it.</summary>
    public static void Set(string binaryPath, DebugTarget? target, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return;
        }

        var all = new Dictionary<string, DebugTarget>(All(path), StringComparer.OrdinalIgnoreCase);
        string key = Key(binaryPath);

        if (target is null || target.IsEmpty)
        {
            if (!all.Remove(key))
            {
                return;      // nothing stored and nothing to store
            }
        }
        else
        {
            all[key] = target;
        }

        Write(all, path ?? DefaultPath);
    }

    public static IReadOnlyDictionary<string, DebugTarget> All(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, DebugTarget>>(File.ReadAllText(path), Options)
                  is { } map
                    ? new Dictionary<string, DebugTarget>(map, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, DebugTarget>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DebugTarget>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Dictionary<string, DebugTarget>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The full path, lowercased. Two spellings of one path are one binary.</summary>
    private static string Key(string binaryPath) => binaryPath.Trim().ToLowerInvariant();

    private static void Write(Dictionary<string, DebugTarget> all, string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            if (all.Count == 0)
            {
                File.Delete(path);
                return;
            }

            // Written beside and moved into place, the way every other file this program owns is.
            string temporary = $"{path}.{Environment.ProcessId:X}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
