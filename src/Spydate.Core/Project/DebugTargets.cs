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

    /// <summary>Whether anything has actually been set, so an empty one can be dropped rather than stored.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Host)
                           && string.IsNullOrWhiteSpace(Arguments)
                           && string.IsNullOrWhiteSpace(WorkingDirectory);
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
