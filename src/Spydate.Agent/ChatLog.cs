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
/// One conversation about a binary: what was said, and when it started.
///
/// Several of these live alongside each other because one binary is rarely one question. Tracking
/// down two unrelated faults in the same program means two lines of reasoning, and holding both in
/// a single transcript costs the model context on whichever one it is not being asked about.
/// </summary>
public sealed record ChatSession
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("entries")]
    public IReadOnlyList<ChatEntry> Entries { get; init; } = [];
}

/// <summary>Every conversation about one binary, and which was last in front.</summary>
public sealed record ChatBook
{
    [JsonPropertyName("sessions")]
    public IReadOnlyList<ChatSession> Sessions { get; init; } = [];

    [JsonPropertyName("active")]
    public string? Active { get; init; }
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

    /// <summary>Kept for callers; the rule itself lives in ToolCallMarkup, which the agent shares.</summary>
    public static string WithoutMarkup(string? text) => Text.ToolCallMarkup.Without(text);

    /// <summary>
    /// A name for a conversation, for the picker: the question it opened with, shortened.
    ///
    /// The first question and not the last, because it is what the reader went in to find out and so
    /// is what they will look for when coming back. Cut on a word rather than mid-way through one —
    /// a list of titles is scanned, not read, and a severed word costs more than the space it saves.
    /// </summary>
    public static string TitleOf(IReadOnlyList<ChatEntry> entries, int maxChars = 44)
    {
        ArgumentNullException.ThrowIfNull(entries);

        string? asked = null;
        foreach (var entry in entries)
        {
            if (entry.Kind == "you")
            {
                asked = entry.Text;
                break;
            }
        }

        // Newlines and runs of spaces would make a one-line picker entry ragged.
        string clean = string.Join(' ', WithoutMarkup(asked).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (clean.Length == 0)
        {
            return "New chat";
        }

        if (clean.Length <= maxChars)
        {
            return clean;
        }

        int cut = clean.LastIndexOf(' ', maxChars - 1);
        return string.Concat(clean.AsSpan(0, cut < maxChars / 2 ? maxChars - 1 : cut).TrimEnd(), "…");
    }

    /// <summary>
    /// Every conversation about this binary. Never throws, for the same reason as <see cref="Load"/>.
    ///
    /// A file written before there were sessions is a bare array of entries, and is read back as the
    /// single conversation it was. Doing otherwise would quietly discard everything already on disk
    /// at the moment this feature arrived, which is the one outcome nobody would forgive.
    /// </summary>
    public static ChatBook LoadBook(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ChatBook();
            }

            string text = File.ReadAllText(path);

            if (text.AsSpan().TrimStart().StartsWith("["))
            {
                var entries = JsonSerializer.Deserialize<List<ChatEntry>>(text, Options) ?? [];
                return entries.Count == 0
                    ? new ChatBook()
                    : new ChatBook { Sessions = [new ChatSession { At = entries[0].At, Entries = entries }] };
            }

            return JsonSerializer.Deserialize<ChatBook>(text, Options) ?? new ChatBook();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ChatBook();
        }
    }

    /// <summary>Writes every conversation, dropping the empty ones. Never throws, as above.</summary>
    public static void SaveBook(string path, ChatBook book)
    {
        ArgumentNullException.ThrowIfNull(book);

        // An untouched new chat is not worth a line in the file, nor a row in the picker next time.
        var kept = book.Sessions.Where(session => session.Entries.Count > 0).ToList();

        try
        {
            if (kept.Count == 0)
            {
                File.Delete(path);
                return;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temporary = $"{path}.{Environment.ProcessId:X}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(book with { Sessions = kept }, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
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
