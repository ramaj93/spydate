using System.Text;

namespace Spydate.Core.Project;

/// <summary>
/// One section of what has been learned about a binary as a whole — how its strings are encoded,
/// what a subsystem is for, a dead end not worth chasing again. Unlike an <see cref="Annotation"/> it
/// belongs to no address; it is the analyst's running notes, the way a project keeps a CLAUDE.md.
/// </summary>
public sealed record Note
{
    /// <summary>The text, Markdown, possibly many lines. Newlines are LF, folded on the way in.</summary>
    public required string Text { get; init; }

    /// <summary>Who wrote it, the same record <see cref="Annotation.Source"/> keeps and for the same reason.</summary>
    public AnnotationSource Source { get; init; } = AnnotationSource.User;

    /// <summary>When it was last set, UTC. Null for entries written before this was recorded.</summary>
    public DateTimeOffset? Modified { get; init; }
}

/// <summary>What changed about one section, so a view refreshes only the row that moved.</summary>
public sealed record NoteChange(string Key, Note? Before, Note? After);

/// <summary>
/// The analyst's notes for one image, keyed by a short section name the writer chooses (<c>overview</c>,
/// <c>string-xor</c>, <c>dead-ends</c>). Held in the same project file as the annotations and saved the
/// same way — a merge, not an overwrite — so the window and an agent writing at the same time each keep
/// their own sections. Sections rather than one document is exactly what makes that merge safe: two
/// writers collide only when they edit the same key, which is rare, instead of every save of a single
/// blob deleting whatever the other one had added since.
/// </summary>
public sealed class NoteStore
{
    /// <summary>Longest key accepted; past this it is a paste, not a heading.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>
    /// Longest section accepted. A note is knowledge, so this is refused rather than truncated — unlike
    /// a name, where a 300-character value is a mistake and clipping it loses nothing. Cutting a note in
    /// half would drop what was learned and leave no sign it happened, which is the worst thing this
    /// store could do; the writer is told the overage and splits the section instead.
    /// </summary>
    public const int MaxTextLength = 4_000;

    private readonly SortedDictionary<string, Note> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Stamped on everything this store writes. Set it once, before use: the window leaves it at
    /// <see cref="AnnotationSource.User"/>, and the agent sets <see cref="AnnotationSource.Agent"/>.
    /// </summary>
    public AnnotationSource Source { get; set; } = AnnotationSource.User;

    /// <summary>
    /// Keys this store has changed since it was last loaded or saved — what makes a save a merge: every
    /// other section in the file on disk belongs to somebody else and must survive.
    /// </summary>
    public IReadOnlyCollection<string> ChangedKeys
    {
        get
        {
            lock (_lock)
            {
                return _changed.ToList();
            }
        }
    }

    /// <summary>Raised after any change, including one that removed a section.</summary>
    public event EventHandler<NoteChange>? Changed;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _byKey.Count;
            }
        }
    }

    /// <summary>True when something has changed since the last <see cref="MarkSaved"/>.</summary>
    public bool IsDirty { get; private set; }

    public Note? Get(string key)
    {
        string? clean = CleanKey(key);
        if (clean is null)
        {
            return null;
        }

        lock (_lock)
        {
            return _byKey.TryGetValue(clean, out var note) ? note : null;
        }
    }

    /// <summary>Every section, in key order.</summary>
    public IReadOnlyList<KeyValuePair<string, Note>> Snapshot()
    {
        lock (_lock)
        {
            return _byKey.ToList();
        }
    }

    /// <summary>
    /// Writes or clears one section, stamping this store's source and the time. Empty text removes the
    /// section. Returns the stored note, or null when it was cleared. Throws
    /// <see cref="ArgumentException"/> when the text is longer than <see cref="MaxTextLength"/>, so the
    /// caller can report the overage rather than lose the tail of it.
    /// </summary>
    public Note? Set(string key, string? text)
    {
        string? cleanKey = CleanKey(key);
        if (cleanKey is null)
        {
            throw new ArgumentException("a section needs a short key, such as overview or string-xor", nameof(key));
        }

        string? cleanText = CleanText(text);
        if (cleanText is { Length: > MaxTextLength })
        {
            throw new ArgumentException(
                $"the note is {cleanText.Length - MaxTextLength} characters over the {MaxTextLength} limit; "
                + "split it into two sections", nameof(text));
        }

        return Update(cleanKey, cleanText is null
            ? null
            : new Note { Text = cleanText, Source = Source, Modified = DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// Replaces one section with exactly what is given, provenance included. This is the shape loading a
    /// project file wants — restoring what someone else recorded, not deciding it — so unlike
    /// <see cref="Set"/> it keeps the note's own source and time instead of stamping this store's.
    /// </summary>
    public void Restore(string key, Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        string? cleanKey = CleanKey(key);
        if (cleanKey is null)
        {
            return;
        }

        string? cleanText = CleanText(note.Text);
        if (cleanText is null || cleanText.Length > MaxTextLength)
        {
            return;   // a hand-edited file with an empty or over-long section: keep the good ones
        }

        Update(cleanKey, note with { Text = cleanText });
    }

    /// <summary>Forgets every section, announcing each removal so a bound view empties with it.</summary>
    public void Clear()
    {
        List<KeyValuePair<string, Note>> removed;
        lock (_lock)
        {
            removed = _byKey.ToList();
            _byKey.Clear();
            _changed.Clear();
            IsDirty = false;
        }

        foreach (var (key, before) in removed)
        {
            Changed?.Invoke(this, new NoteChange(key, before, null));
        }
    }

    /// <summary>
    /// Called after a successful save or load. Forgetting what changed is what makes the next save merge
    /// against a file this store now agrees with.
    /// </summary>
    public void MarkSaved()
    {
        lock (_lock)
        {
            _changed.Clear();
            IsDirty = false;
        }
    }

    private Note? Update(string key, Note? after)
    {
        Note? before;
        lock (_lock)
        {
            before = _byKey.TryGetValue(key, out var existing) ? existing : null;

            if (after is null)
            {
                _byKey.Remove(key);
            }
            else
            {
                _byKey[key] = after;
            }

            if (Same(before, after))
            {
                return after;
            }

            _changed.Add(key);
            IsDirty = true;
        }

        Changed?.Invoke(this, new NoteChange(key, before, after));
        return after;
    }

    /// <summary>Value equality on the parts that count. Modified does not, or every rewrite would look changed.</summary>
    private static bool Same(Note? a, Note? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        // Source counts, the way it does for annotations: a section a person retypes over an agent's is
        // the same text but no longer the agent's, and that difference is worth a save.
        return a is not null && b is not null && a.Text == b.Text && a.Source == b.Source;
    }

    /// <summary>
    /// Cleans a section key: trimmed, lower-cased, and every run of whitespace or underscores folded to a
    /// single hyphen, so <c>Dead Ends</c>, <c>dead_ends</c> and <c>dead-ends</c> are one section rather
    /// than three near-duplicates in the index. Null when nothing usable is left.
    /// </summary>
    public static string? CleanKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        string trimmed = key.Trim().ToLowerInvariant();
        var sb = new StringBuilder(trimmed.Length);
        bool pendingSeparator = false;
        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c) || c == '_' || c == '-')
            {
                pendingSeparator = sb.Length > 0;   // never lead with a hyphen, and collapse runs
                continue;
            }

            if (pendingSeparator)
            {
                sb.Append('-');
                pendingSeparator = false;
            }

            sb.Append(char.IsControl(c) ? '-' : c);
        }

        if (sb.Length == 0)
        {
            return null;
        }

        if (sb.Length > MaxKeyLength)
        {
            sb.Length = MaxKeyLength;
        }

        return sb.ToString().TrimEnd('-');
    }

    /// <summary>
    /// Trims a note and normalises its newlines to LF, keeping the paragraph structure a name would fold
    /// away. Null when it is blank. It does not truncate — that is <see cref="Set"/>'s refusal to make.
    /// </summary>
    public static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }
}
