namespace Spydate.Agent.Text;

/// <summary>
/// Spotting a tool call a model wrote out as prose instead of making.
///
/// Tools are called through the provider's own mechanism, and the model never sees that mechanism —
/// it emits the chat template it was trained on and something upstream turns that into a structured
/// call. When that something fails, the template arrives as ordinary text:
///
/// <code>
/// &lt;｜｜DSML｜｜tool_calls&gt;&lt;｜｜DSML｜｜invoke name="debug_state"&gt;…
/// </code>
///
/// Nothing runs, the turn ends mid-thought, and it reads as the model being stupid rather than as a
/// call that was dropped in transit. It happens far more with streaming on, because the parser is
/// reassembling the call from fragments.
///
/// This is a list of sentinels rather than a grammar on purpose. Every provider has its own shape,
/// the shapes change between releases, and a parser would be wrong in a new way each time — whereas
/// none of these character sequences occurs in a sentence about a binary.
/// </summary>
public static class ToolCallMarkup
{
    private static readonly string[] Sentinels =
    [
        "｜",           // the fullwidth bar these are built out of: <｜tool▁calls▁begin｜>
        "<|",
        "<tool_call", "</tool_call",
        "<function_call", "</function_call",
        "<invoke", "</invoke",
        "<parameter", "</parameter",
    ];

    /// <summary>Where the markup starts, or -1. The first sentinel wins, wherever it is.</summary>
    public static int IndexIn(string? text)
    {
        if (text is null or { Length: 0 })
        {
            return -1;
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

        return cut;
    }

    /// <summary>Whether an answer contains a tool call that was written rather than made.</summary>
    public static bool Present(string? text) => IndexIn(text) >= 0;

    /// <summary>
    /// The text with any leaked template cut off the end.
    ///
    /// Cut rather than picked apart, because a model only ever starts emitting one of these at the
    /// end of a message — whatever prose came first is worth keeping and everything after it is not.
    /// </summary>
    public static string Without(string? text)
    {
        if (text is null or { Length: 0 })
        {
            return string.Empty;
        }

        int cut = IndexIn(text);
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

    /// <summary>
    /// The tool name the leaked markup was trying to call, or null.
    ///
    /// Read only to say it back — "you wrote find_strings as text" is a far more useful correction
    /// than "something went wrong", and it costs one substring. It is deliberately not used to run
    /// anything: the provider's own parser refused this text, strings out of the binary reach the
    /// model's context, and a path that executes tool calls the parser rejected is a path that runs
    /// whatever a hostile binary can persuade the model to echo.
    /// </summary>
    public static string? NameIn(string? text)
    {
        if (text is null)
        {
            return null;
        }

        const string marker = "name=";
        int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        int from = at + marker.Length;
        if (from < text.Length && text[from] is '"' or '\'')
        {
            from++;
        }

        int to = from;
        while (to < text.Length && (char.IsLetterOrDigit(text[to]) || text[to] == '_'))
        {
            to++;
        }

        return to > from ? text[from..to] : null;
    }
}
