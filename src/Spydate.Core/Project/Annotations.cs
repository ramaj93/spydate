using System.Text;

namespace Spydate.Core.Project;

/// <summary>Who decided an annotation. Not a permission — a record, so a set of them can be reviewed.</summary>
public enum AnnotationSource
{
    /// <summary>A person typed it. The default, including for files written before this existed.</summary>
    User,

    /// <summary>Something automated set it — today, an agent driving the MCP server.</summary>
    Agent,
}

/// <summary>What the user has said about one address: what to call it, and what to note about it.</summary>
public sealed record Annotation
{
    /// <summary>User-chosen name, replacing the generated <c>sub_</c> / <c>data_</c> / <c>loc_</c> one.</summary>
    public string? Name { get; init; }

    /// <summary>Free text shown beside the address in listings.</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Names for this function's stack slots, keyed by the generated one (<c>arg_0</c>, <c>local_18</c>).
    /// They belong to the function rather than to an address of their own, which is also how they read.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Locals { get; init; }

    /// <summary>
    /// Who set this. An agent naming forty callers after one misread function is a set worth being
    /// able to list and undo, and that is only possible if the answer was recorded when it happened.
    /// </summary>
    public AnnotationSource Source { get; init; } = AnnotationSource.User;

    /// <summary>When it was last set, UTC. Null for entries written before this was recorded.</summary>
    public DateTimeOffset? Modified { get; init; }

    public bool IsEmpty => string.IsNullOrEmpty(Name) && string.IsNullOrEmpty(Comment) && Locals is not { Count: > 0 };
}

/// <summary>What changed, so views can refresh only what they need to.</summary>
public sealed record AnnotationChange(ulong Va, Annotation? Before, Annotation? After)
{
    public bool NameChanged => Before?.Name != After?.Name;
}

/// <summary>
/// The user's annotations for one image, keyed by virtual address. Held separately from the
/// <c>SymbolTable</c>, which is what analysis produced: a name typed by hand outranks a generated one and
/// must survive re-analysis, so the two are kept apart and the store is applied on top.
/// </summary>
public sealed class AnnotationStore
{
    /// <summary>Longest name accepted; anything beyond is a paste accident, not a name.</summary>
    public const int MaxNameLength = 255;

    private readonly SortedDictionary<ulong, Annotation> _byVa = new();
    private readonly HashSet<ulong> _changed = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Stamped on everything this store writes. Set it once, before use: the window leaves it at
    /// <see cref="AnnotationSource.User"/>, and the MCP server sets <see cref="AnnotationSource.Agent"/>.
    /// </summary>
    public AnnotationSource Source { get; set; } = AnnotationSource.User;

    /// <summary>
    /// Addresses this store has changed since it was last loaded or saved. It is what makes saving a
    /// merge rather than an overwrite: everything else in the file on disk belongs to somebody else
    /// and must survive.
    /// </summary>
    public IReadOnlyCollection<ulong> ChangedAddresses
    {
        get
        {
            lock (_lock)
            {
                return _changed.ToList();
            }
        }
    }

    /// <summary>Raised after any change, including one that cleared an annotation.</summary>
    public event EventHandler<AnnotationChange>? Changed;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _byVa.Count;
            }
        }
    }

    /// <summary>True when something has changed since the last <see cref="MarkSaved"/>.</summary>
    public bool IsDirty { get; private set; }

    public Annotation? Get(ulong va)
    {
        lock (_lock)
        {
            return _byVa.TryGetValue(va, out var annotation) ? annotation : null;
        }
    }

    public string? NameFor(ulong va) => Get(va)?.Name;

    public string? CommentFor(ulong va) => Get(va)?.Comment;

    /// <summary>What the user calls one of a function's stack slots, if anything.</summary>
    public string? LocalNameFor(ulong functionVa, string slot)
    {
        ArgumentException.ThrowIfNullOrEmpty(slot);
        return Get(functionVa)?.Locals is { } locals && locals.TryGetValue(slot, out string? name) ? name : null;
    }

    /// <summary>Every slot the user has named in a function, keyed by the generated name.</summary>
    public IReadOnlyDictionary<string, string> LocalNamesFor(ulong functionVa)
        => Get(functionVa)?.Locals ?? EmptyLocals;

    private static readonly Dictionary<string, string> EmptyLocals = new(StringComparer.Ordinal);

    /// <summary>Names or un-names one stack slot. Returns the name that was stored, or null if cleared.</summary>
    public string? SetLocalName(ulong functionVa, string slot, string? name)
    {
        ArgumentException.ThrowIfNullOrEmpty(slot);
        string? clean = CleanName(name);
        var updated = Update(functionVa, current =>
        {
            var locals = new Dictionary<string, string>(current.Locals ?? EmptyLocals, StringComparer.Ordinal);
            if (clean is null)
            {
                locals.Remove(slot);
            }
            else
            {
                locals[slot] = clean;
            }

            return current with { Locals = locals.Count == 0 ? null : locals };
        });

        return updated?.Locals is { } after && after.TryGetValue(slot, out string? stored) ? stored : null;
    }

    /// <summary>Sets or clears the name at an address. Returns the name that was stored, or null if cleared.</summary>
    public string? SetName(ulong va, string? name) => Update(va, a => a with { Name = CleanName(name) })?.Name;

    /// <summary>Sets or clears the comment at an address.</summary>
    public string? SetComment(ulong va, string? comment) => Update(va, a => a with { Comment = CleanComment(comment) })?.Comment;

    /// <summary>
    /// Replaces everything known about an address, provenance included. This is the shape loading a
    /// project file wants — it is restoring what someone else recorded, not deciding it — so unlike
    /// the setters above it does not stamp the store's own source over what it is given.
    /// </summary>
    public void Set(ulong va, Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Update(
            va,
            _ => new Annotation
            {
                Name = CleanName(annotation.Name),
                Comment = CleanComment(annotation.Comment),
                Locals = CleanLocals(annotation.Locals),
                Source = annotation.Source,
                Modified = annotation.Modified,
            },
            stamp: false);
    }

    /// <summary>Every annotation a given source is responsible for, in address order.</summary>
    public IReadOnlyList<KeyValuePair<ulong, Annotation>> SnapshotOf(AnnotationSource source)
        => Snapshot().Where(e => e.Value.Source == source).ToList();

    /// <summary>Every annotation, in address order.</summary>
    public IReadOnlyList<KeyValuePair<ulong, Annotation>> Snapshot()
    {
        lock (_lock)
        {
            return _byVa.ToList();
        }
    }

    /// <summary>
    /// Forgets everything, announcing each removal. The announcement is the point: names reach the
    /// symbol table through <see cref="Changed"/>, so clearing silently would leave every one of them
    /// still showing in listings with nothing behind it.
    /// </summary>
    public void Clear()
    {
        List<KeyValuePair<ulong, Annotation>> removed;
        lock (_lock)
        {
            removed = _byVa.ToList();
            _byVa.Clear();
            _changed.Clear();
            IsDirty = false;
        }

        foreach (var (va, before) in removed)
        {
            Changed?.Invoke(this, new AnnotationChange(va, before, null));
        }
    }

    /// <summary>
    /// Called after a successful save or load. Forgetting what changed is what makes the next save
    /// merge against a file this store now agrees with.
    /// </summary>
    public void MarkSaved()
    {
        lock (_lock)
        {
            _changed.Clear();
            IsDirty = false;
        }
    }

    private Annotation? Update(ulong va, Func<Annotation, Annotation> change, bool stamp = true)
    {
        Annotation? before;
        Annotation? after;
        lock (_lock)
        {
            before = _byVa.TryGetValue(va, out var existing) ? existing : null;
            after = change(before ?? new Annotation());
            if (stamp && !after.IsEmpty)
            {
                after = after with { Source = Source, Modified = DateTimeOffset.UtcNow };
            }

            if (after.IsEmpty)
            {
                after = null;
                _byVa.Remove(va);
            }
            else
            {
                _byVa[va] = after;
            }

            if (Same(before, after))
            {
                return after;
            }

            _changed.Add(va);
            IsDirty = true;
        }

        Changed?.Invoke(this, new AnnotationChange(va, before, after));
        return after;
    }

    /// <summary>Cleans every slot name, dropping any that cleans away to nothing.</summary>
    private static IReadOnlyDictionary<string, string>? CleanLocals(IReadOnlyDictionary<string, string>? locals)
    {
        if (locals is not { Count: > 0 })
        {
            return null;
        }

        var cleaned = new Dictionary<string, string>(locals.Count, StringComparer.Ordinal);
        foreach (var (slot, name) in locals)
        {
            if (!string.IsNullOrWhiteSpace(slot) && CleanName(name) is { } clean)
            {
                cleaned[slot] = clean;
            }
        }

        return cleaned.Count == 0 ? null : cleaned;
    }

    /// <summary>Value equality, including the slot names a record's default comparison would miss.</summary>
    private static bool Same(Annotation? a, Annotation? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        // Source counts: a name a person retypes over an agent's is the same text but no longer the
        // agent's, and a revert-what-the-agent-did should not take it back. Modified does not, or
        // every no-op write would look like a change.
        if (a is null || b is null || a.Name != b.Name || a.Comment != b.Comment || a.Source != b.Source)
        {
            return false;
        }

        var left = a.Locals ?? EmptyLocals;
        var right = b.Locals ?? EmptyLocals;
        return left.Count == right.Count && left.All(e => right.TryGetValue(e.Key, out string? v) && v == e.Value);
    }

    /// <summary>
    /// Trims a name and replaces what would break a listing. Whitespace and control characters become
    /// underscores; everything else is kept, because real symbol names are full of punctuation
    /// (<c>?Foo@Bar@@QEAAXXZ</c>, <c>kernel32!CreateFileW</c>).
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            trimmed = trimmed[..MaxNameLength];
        }

        var sb = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            sb.Append(char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c);
        }

        return sb.ToString();
    }

    /// <summary>Trims a comment and folds it to one line, which is how listings show it.</summary>
    public static string? CleanComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        var sb = new StringBuilder(comment.Length);
        foreach (char c in comment.Trim())
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }
}
