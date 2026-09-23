namespace Spydate.Core.Project;

/// <summary>What changed about one member, so views can refresh only what they need to.</summary>
public sealed record MemberAnnotationChange(string Key, Annotation? Before, Annotation? After);

/// <summary>
/// Annotations for a file whose program has no addresses — a JAR — keyed on the member they are about instead.
///
/// The key is whatever the reading says identifies a type or member for good across builds of the same code: for
/// the JVM, the internal name, plus a method's or field's name and descriptor
/// (<c>com/example/Greeter.greet(Ljava/lang/String;)Ljava/lang/String;</c>). It is opaque here; the store only
/// keeps, merges and reports on it, the way <see cref="AnnotationStore"/> does for addresses, and with the same
/// cleaning, provenance and change tracking.
/// </summary>
public sealed class MemberAnnotationStore
{
    private readonly SortedDictionary<string, Annotation> _byKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Stamped on everything this store writes: the window leaves it at User, the MCP server sets Agent.</summary>
    public AnnotationSource Source { get; set; } = AnnotationSource.User;

    /// <summary>Keys changed since the last load or save — what makes saving a merge rather than an overwrite.</summary>
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

    public event EventHandler<MemberAnnotationChange>? Changed;

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

    public bool IsDirty { get; private set; }

    public Annotation? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            return _byKey.TryGetValue(key, out var annotation) ? annotation : null;
        }
    }

    /// <summary>Sets or clears a member's name. Returns the name stored, or null if cleared.</summary>
    public string? SetName(string key, string? name) => Update(key, a => a with { Name = AnnotationStore.CleanName(name) })?.Name;

    /// <summary>Sets or clears a member's comment. Returns the comment stored, or null if cleared.</summary>
    public string? SetComment(string key, string? comment) => Update(key, a => a with { Comment = AnnotationStore.CleanComment(comment) })?.Comment;

    /// <summary>Restores what a project file recorded, provenance included, without stamping this store's own source over it.</summary>
    public void Set(string key, Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Update(
            key,
            _ => new Annotation
            {
                Name = AnnotationStore.CleanName(annotation.Name),
                Comment = AnnotationStore.CleanComment(annotation.Comment),
                Source = annotation.Source,
                Modified = annotation.Modified,
            },
            stamp: false);
    }

    /// <summary>Every annotation, in key order.</summary>
    public IReadOnlyList<KeyValuePair<string, Annotation>> Snapshot()
    {
        lock (_lock)
        {
            return _byKey.ToList();
        }
    }

    /// <summary>Forgets everything, announcing each removal, so a reload from the file replaces rather than merges.</summary>
    public void Clear()
    {
        List<KeyValuePair<string, Annotation>> removed;
        lock (_lock)
        {
            removed = _byKey.ToList();
            _byKey.Clear();
            _changed.Clear();
            IsDirty = false;
        }

        foreach (var (key, before) in removed)
        {
            Changed?.Invoke(this, new MemberAnnotationChange(key, before, null));
        }
    }

    public void MarkSaved()
    {
        lock (_lock)
        {
            _changed.Clear();
            IsDirty = false;
        }
    }

    private Annotation? Update(string key, Func<Annotation, Annotation> change, bool stamp = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Annotation? before;
        Annotation? after;
        lock (_lock)
        {
            before = _byKey.TryGetValue(key, out var existing) ? existing : null;
            after = change(before ?? new Annotation());
            if (stamp && !after.IsEmpty)
            {
                after = after with { Source = Source, Modified = DateTimeOffset.UtcNow };
            }

            if (after.IsEmpty)
            {
                after = null;
                _byKey.Remove(key);
            }
            else
            {
                _byKey[key] = after;
            }

            if (before?.Name == after?.Name && before?.Comment == after?.Comment && before?.Source == after?.Source)
            {
                return after;
            }

            _changed.Add(key);
            IsDirty = true;
        }

        Changed?.Invoke(this, new MemberAnnotationChange(key, before, after));
        return after;
    }
}
