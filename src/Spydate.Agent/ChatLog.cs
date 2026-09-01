using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spydate.Agent;

/// <summary>One line of a conversation, as it was shown.</summary>
public sealed record ChatEntry
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "assistant";

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// The conversation about one binary, kept between runs.
///
/// What is stored is the transcript as it was displayed, not the model's own message history: the
/// tool calls and their results are large, provider-shaped, and of no use to a reader, while the
/// reasoning behind a name is exactly what is worth having six months later. A restored chat is
/// therefore something to read, not something the model remembers — it starts a new conversation
/// with a clean history, and the panel says so rather than implying otherwise.
///
/// Kept beside the other per-user state rather than in the <c>.spydate</c> project file, which is
/// meant to be shared and diffed and has no business carrying a chat log.
/// </summary>
public static class ChatLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spydate",
        "chats");

    /// <summary>
    /// A file per binary. The name carries the binary's own name so the folder can be read by a
    /// human, and a hash of the full path so two files called <c>setup.exe</c> stay apart.
    /// </summary>
    public static string PathFor(string binaryPath, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(binaryPath.ToLowerInvariant()));
        string stamp = Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();

        string name = Path.GetFileName(binaryPath);
        if (name.Length == 0 || name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            name = "binary";
        }

        return Path.Combine(directory ?? Directory, $"{name}-{stamp}.json");
    }

    /// <summary>
    /// What was said last time, or nothing. Never throws: a damaged log is worth losing silently,
    /// and is certainly not worth failing to open a binary over.
    /// </summary>
    public static IReadOnlyList<ChatEntry> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<ChatEntry>>(File.ReadAllText(path), Options) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the conversation, or deletes the file when there is nothing left to remember. Never
    /// throws, for the same reason as <see cref="Load"/>: this is a convenience, and a full disk
    /// should not take the window down with it.
    /// </summary>
    public static void Save(string path, IEnumerable<ChatEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var all = entries.ToList();

        try
        {
            if (all.Count == 0)
            {
                File.Delete(path);
                return;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

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
