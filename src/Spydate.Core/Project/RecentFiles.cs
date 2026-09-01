using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spydate.Core.Project;

/// <summary>A binary that was open, and when.</summary>
public sealed record RecentFile
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; }

    /// <summary>Just the file name, for a menu.</summary>
    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    /// <summary>Where it is, for telling two files of the same name apart.</summary>
    [JsonIgnore]
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>
/// The binaries opened lately, newest first.
///
/// The list is of binaries rather than of <c>.spydate</c> files, because the binary is what gets
/// opened and the project is found from it — a recent list of project files would be a list of
/// things the user never opens directly.
///
/// Kept with the other per-user state and never in a project file: which binaries someone has been
/// looking at is about them, not about the program being analysed, and a project file is meant to be
/// shareable.
/// </summary>
public static class RecentFiles
{
    /// <summary>Enough to find last week's work in, few enough to read at a glance.</summary>
    public const int Max = 12;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spydate",
        "recent.json");

    /// <summary>
    /// What was opened lately. Never throws: a damaged list is worth losing silently, and is
    /// certainly not worth failing to start the window over.
    /// </summary>
    public static IReadOnlyList<RecentFile> Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<RecentFile>>(File.ReadAllText(path), Options) is { } read
                    ? read.Where(r => r.Path.Length > 0).Take(Max).ToList()
                    : []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Puts one at the top and returns the new list. An entry already there moves rather than
    /// repeating, compared without case because Windows paths do not have one.
    /// </summary>
    public static IReadOnlyList<RecentFile> Add(string binaryPath, string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);

        string full = Full(binaryPath);
        var kept = Load(path)
            .Where(r => !string.Equals(Full(r.Path), full, StringComparison.OrdinalIgnoreCase))
            .Take(Max - 1);

        var all = new List<RecentFile> { new() { Path = full, At = DateTimeOffset.Now } };
        all.AddRange(kept);

        Write(path ?? DefaultPath, all);
        return all;
    }

    /// <summary>Takes one out, for a file that has since been moved or deleted.</summary>
    public static IReadOnlyList<RecentFile> Remove(string binaryPath, string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);

        string full = Full(binaryPath);
        var all = Load(path)
            .Where(r => !string.Equals(Full(r.Path), full, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Write(path ?? DefaultPath, all);
        return all;
    }

    public static void Clear(string? path = null) => Write(path ?? DefaultPath, []);

    /// <summary>
    /// A relative path recorded today means something else tomorrow, once the working directory has
    /// moved — and the command line is one of the ways a binary gets opened.
    /// </summary>
    private static string Full(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return path;
        }
    }

    private static void Write(string path, IReadOnlyList<RecentFile> entries)
    {
        try
        {
            if (entries.Count == 0)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            // Written beside and moved into place, the way every other file this program owns is.
            string temporary = $"{path}.{Environment.ProcessId:X}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
