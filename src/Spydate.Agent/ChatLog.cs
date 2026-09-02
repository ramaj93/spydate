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
    /// The end of a stored conversation, small enough to hand to a model that is starting again.
    ///
    /// The end, not the beginning: what a restored session is asked next is nearly always about the
    /// last thing that was said. A transcript that ends "want me to do that?" is answered with
    /// "yes", and a model given no part of it has to guess what it just agreed to — which, holding
    /// sixteen tools that write to the project, is the one thing it must not do.
    ///
    /// Only what a person said and what the assistant said. Tool lines are the call and never the
    /// result, so they would spend the budget describing questions whose answers are missing.
    /// </summary>
    public static string Recap(IReadOnlyList<ChatEntry> entries, int maxChars = 3000)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var kept = new List<string>();
        int budget = maxChars;

        for (int i = entries.Count - 1; i >= 0 && budget > 0; i--)
        {
            if (entries[i].Kind is not ("you" or "assistant"))
            {
                continue;
            }

            string said = WithoutMarkup(entries[i].Text).Trim();
            if (said.Length == 0)
            {
                continue;
            }

            string line = $"{(entries[i].Kind == "you" ? "user" : "assistant")}: {said}";

            if (line.Length > budget)
            {
                // Too big to keep whole, so keep its end — the pending question is the last
                // sentence of it, and half a paragraph from the middle answers nothing.
                if (budget < 80)
                {
                    break;
                }

                line = $"…{line[^(budget - 1)..]}";
            }

            kept.Add(line);
            budget -= line.Length;
        }

        kept.Reverse();
        return string.Join("\n\n", kept);
    }

    /// <summary>
    /// A message with any leaked tool-call template cut off the end of it.
    ///
    /// A model that fails to call a tool properly writes its own template out as prose instead, and
    /// the panel stores what it displayed, so that markup ends up in the log. Handing it back is the
    /// worst possible use of it: it is a picture of the failure, and putting it in the prompt as an
    /// example of what an assistant says here invites the same failure again.
    ///
    /// Cut rather than picked apart, because a model only ever starts emitting one of these at the
    /// end of a message — whatever prose came first is kept, and everything from the first sentinel
    /// goes. The list is sentinels rather than a grammar: providers each have their own shape, they
    /// change, and none of them occurs in a sentence about a binary.
    /// </summary>
    public static string WithoutMarkup(string? text)
    {
        if (text is null or { Length: 0 })
        {
            return string.Empty;
        }

        int cut = -1;
        foreach (string marker in Sentinels)
        {
            int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (cut < 0 || at < cut))
            {
                cut = at;
            }
        }

        if (cut < 0)
        {
            return text;
        }

        // The fullwidth bar sits inside an opening tag — "<｜｜DSML｜｜tool_calls>" — so the angle
        // bracket in front of it belongs to the markup as well, and cutting on the bar alone leaves
        // it dangling on the end of the prose.
        string head = text[..cut].TrimEnd();
        return head.EndsWith('<') ? head[..^1].TrimEnd() : head;
    }

    private static readonly string[] Sentinels =
    [
        "｜",           // the fullwidth bar these are built out of: <｜tool▁calls▁begin｜>
        "<|",
        "<tool_call", "</tool_call",
        "<function_call", "</function_call",
        "<invoke", "</invoke",
        "<parameter", "</parameter",
    ];

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
