using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Spydate.Agent.Providers;

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

    /// <summary>
    /// The model's own message history, without the system message — what the next agent is rebuilt
    /// from, so a restored conversation is a memory and not just a transcript to read. Absent in a
    /// file written before histories were kept; <see cref="ChatLog.LoadBook"/> fills it from the
    /// entries in that case so every reader sees one shape.
    /// </summary>
    [JsonPropertyName("history")]
    public IReadOnlyList<ChatMessage> History { get; init; } = [];

    /// <summary>Which provider produced <see cref="History"/>. The replay decision reads it.</summary>
    [JsonPropertyName("provider")]
    public ProviderKind Provider { get; init; } = ProviderKind.Anthropic;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;
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
/// Two things are stored side by side: the transcript as it was displayed, which is what a person
/// reads coming back to it, and the model's own message history — the tool calls and their results
/// included — which is what the next agent is rebuilt from, so it carries on rather than re-deriving
/// everything it already knew. The display transcript alone was what used to be kept, on the theory
/// that a restored chat is only a record to read; but that left the model re-reading functions it
/// had read minutes earlier and repeating conclusions it had already reached, which is the opposite
/// of what a memory is for. What no longer fits the model's context is dropped oldest-first at the
/// start of the next turn (<see cref="AnalysisAgent.Trim"/>), so the file grows only to the width of
/// one conversation's window.
///
/// Kept beside the other per-user state rather than in the <c>.spydate</c> project file, which is
/// meant to be shared and diffed and has no business carrying a chat log.
/// </summary>
public static class ChatLog
{
    /// <summary>
    /// Carries the model's message history as well as the plain records.
    ///
    /// A <see cref="ChatMessage"/> and its content parts are polymorphic — a text, a tool call, a
    /// tool result are different types under one list — and only Microsoft.Extensions.AI's own type
    /// resolver knows the <c>$type</c> discriminators that tell them apart on the way back in. So the
    /// options are built from <see cref="AIJsonUtilities.DefaultOptions"/> (resolver and converters
    /// copied), with a reflection resolver appended for this file's own records and a string enum
    /// converter so a provider name reads as a word rather than a number.
    /// </summary>
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

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
    /// A text-only history built from the display transcript, for a file written before histories
    /// were kept — and the building block <see cref="Flatten"/> uses.
    ///
    /// Only what a person said and what the assistant said, markup stripped. Tool lines are the call
    /// with never the result, so replaying them would be a question with its answer missing; they are
    /// dropped, and what the tools found is gone with them. That is the cost of the old format having
    /// kept the transcript and not the history, and it is paid once, on the first open after this.
    /// </summary>
    public static IReadOnlyList<ChatMessage> HistoryFrom(IReadOnlyList<ChatEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var messages = new List<ChatMessage>();
        foreach (var entry in entries)
        {
            var role = entry.Kind switch
            {
                "you" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                _ => (ChatRole?)null,   // tool, note, problem: no counterpart in a replayed history
            };

            if (role is not { } r)
            {
                continue;
            }

            string text = WithoutMarkup(entry.Text).Trim();
            if (text.Length > 0)
            {
                messages.Add(new ChatMessage(r, text));
            }
        }

        return messages;
    }

    /// <summary>
    /// A history rewritten so any provider can take it: every tool call and its result flattened to
    /// text inside a plain record message, with the prose kept as it was.
    ///
    /// For when the provider that produced the history is not the one about to be handed it — its
    /// call ids and tool-message shapes do not carry across, and its reasoning fields were not saved
    /// (see <see cref="AnalysisAgent.Replayable"/>). A record message is user-role on purpose: the
    /// window fights models that write tool-call markup as prose, and an assistant-role message full
    /// of "you called X and it returned Y" would be teaching exactly that.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Flatten(IReadOnlyList<ChatMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        var messages = new List<ChatMessage>();

        foreach (var message in history)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                calls[call.CallId] = $"{call.Name}({DescribeArguments(call.Arguments)})";
            }

            string prose = Text.ToolCallMarkup.Without(
                string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text))).Trim();

            if (prose.Length > 0 && (message.Role == ChatRole.User || message.Role == ChatRole.Assistant))
            {
                messages.Add(new ChatMessage(message.Role, prose));
            }

            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                string called = calls.TryGetValue(result.CallId, out var c) ? c : "a tool";
                messages.Add(new ChatMessage(ChatRole.User,
                    $"[Spydate record — you called {called} and it returned:\n{result.Result}]"));
            }
        }

        return messages;
    }

    private static string DescribeArguments(IEnumerable<KeyValuePair<string, object?>>? arguments)
        => arguments is null
            ? string.Empty
            : string.Join(", ", arguments.Select(a => $"{a.Key}={a.Value}"));

    /// <summary>
    /// Puts a deserialised history back into the shape it had in memory.
    ///
    /// System.Text.Json reads a tool result and a call's arguments back as <see cref="JsonElement"/>,
    /// because their declared type is <c>object</c>. Left that way a string result would be re-sent
    /// quoted and escaped, and its length would be measured with the quotes on. So each element is
    /// unwrapped to the value it holds — a string to a string, a number to a number — before the
    /// history is handed on.
    /// </summary>
    private static IReadOnlyList<ChatMessage> Thaw(IReadOnlyList<ChatMessage> history)
    {
        foreach (var message in history)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionResultContent result when result.Result is JsonElement element:
                        result.Result = Unwrap(element);
                        break;

                    case FunctionCallContent call when call.Arguments is { } arguments:
                        foreach (string key in arguments.Keys.ToList())
                        {
                            if (arguments[key] is JsonElement value)
                            {
                                arguments[key] = Unwrap(value);
                            }
                        }

                        break;
                }
            }
        }

        return history;
    }

    private static object? Unwrap(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),   // an object or array: keep it as the JSON text it was
    };

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
                    : new ChatBook { Sessions = [Ready(new ChatSession { At = entries[0].At, Entries = entries })] };
            }

            var book = JsonSerializer.Deserialize<ChatBook>(text, Options) ?? new ChatBook();
            return book with { Sessions = book.Sessions.Select(Ready).ToList() };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ChatBook();
        }
    }

    /// <summary>
    /// A loaded session with a history ready to replay: unwrapped from its JSON elements, or built
    /// from the entries when the file predates histories being kept, so a caller never has to tell
    /// the two cases apart.
    /// </summary>
    private static ChatSession Ready(ChatSession session)
        => session.History.Count > 0
            ? session with { History = Thaw(session.History) }
            : session with { History = HistoryFrom(session.Entries) };

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
