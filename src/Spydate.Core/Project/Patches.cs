using Spydate.Core.Binary;

namespace Spydate.Core.Project;

/// <summary>
/// A change to the bytes at one address, and what was there before.
///
/// The original is kept, not derived. A patch is written down so it can be reverted, reviewed and
/// argued with months later, and re-reading the file to find out what it replaced only works while
/// the file is still to hand and still unpatched — neither of which is safe to assume about a
/// record that outlives the session.
/// </summary>
public sealed record Patch
{
    /// <summary>Where, as an RVA. Not a file offset: those move when a file is rebuilt.</summary>
    public uint Rva { get; init; }

    /// <summary>What is to be written.</summary>
    public IReadOnlyList<byte> Bytes { get; init; } = [];

    /// <summary>What was there, for reverting and for noticing that the file has changed underneath.</summary>
    public IReadOnlyList<byte> Original { get; init; } = [];

    /// <summary>Why. A patch without a reason is a mystery to whoever reads it next, including you.</summary>
    public string? Comment { get; init; }

    /// <summary>Whether to include it when a patched file is written. Off is how a patch is tried out.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Who made it, on the same terms as an annotation's source.</summary>
    public AnnotationSource Source { get; init; } = AnnotationSource.User;

    /// <summary>When it was last set, UTC.</summary>
    public DateTimeOffset? Modified { get; init; }

    /// <summary>How many bytes it covers. Always the same as the original's length.</summary>
    public int Length => Bytes.Count;

    /// <summary>A patch that writes back exactly what was there is not a patch.</summary>
    public bool IsEmpty => Bytes.Count == 0 || Bytes.SequenceEqual(Original);

    /// <summary>The bytes, as they read in a listing.</summary>
    public string Hex => Convert.ToHexString(Bytes.ToArray());

    public string OriginalHex => Convert.ToHexString(Original.ToArray());
}

/// <summary>What changed, so views can refresh only what they need to.</summary>
public sealed record PatchChange(uint Rva, Patch? Before, Patch? After);

/// <summary>
/// The patches for one image, keyed by RVA.
///
/// Every patch replaces exactly as many bytes as it covers. That is not a limitation of the storage
/// but the rule that makes the rest of the program keep working: every function boundary, every
/// cross-reference and every address the user has written down stays where it was, so a patch never
/// forces a re-analysis and never silently moves something the analyst has already named. Anything
/// shorter is padded to length by whoever builds the patch, which is why <see cref="Set"/> refuses a
/// mismatch rather than accepting one and truncating later.
/// </summary>
public sealed class PatchStore
{
    private readonly SortedDictionary<uint, Patch> _byRva = new();
    private readonly HashSet<uint> _changed = new();
    private readonly Lock _lock = new();

    /// <summary>Stamped onto anything set from here on, the way the annotation store does it.</summary>
    public AnnotationSource Source { get; set; } = AnnotationSource.User;

    /// <summary>Addresses touched this session, so a save can merge rather than overwrite.</summary>
    public IReadOnlyCollection<uint> ChangedAddresses
    {
        get
        {
            lock (_lock)
            {
                return _changed.ToArray();
            }
        }
    }

    public event EventHandler<PatchChange>? Changed;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _byRva.Count;
            }
        }
    }

    /// <summary>How many would actually be written. The rest are switched off.</summary>
    public int EnabledCount
    {
        get
        {
            lock (_lock)
            {
                return _byRva.Values.Count(p => p.Enabled);
            }
        }
    }

    public bool IsDirty { get; private set; }

    public Patch? Get(uint rva)
    {
        lock (_lock)
        {
            return _byRva.TryGetValue(rva, out var patch) ? patch : null;
        }
    }

    /// <summary>
    /// Records a patch, replacing any at the same address. Throws when the replacement is a different
    /// length from what it replaces, or when it would overlap a patch already recorded: two patches
    /// covering one byte have no defined result, and finding that out when the file is written is
    /// far too late.
    /// </summary>
    public void Set(uint rva, Patch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (patch.Bytes.Count != patch.Original.Count)
        {
            throw new ArgumentException(
                $"a patch must replace exactly as many bytes as it covers: {patch.Bytes.Count} for {patch.Original.Count}",
                nameof(patch));
        }

        if (patch.Bytes.Count == 0)
        {
            throw new ArgumentException("a patch with no bytes changes nothing", nameof(patch));
        }

        PatchChange change;
        lock (_lock)
        {
            if (Overlapping(rva, patch.Bytes.Count) is { } clash)
            {
                throw new ArgumentException(
                    $"a patch at 0x{clash:X} already covers some of 0x{rva:X}..0x{rva + patch.Bytes.Count - 1:X}",
                    nameof(rva));
            }

            _byRva.TryGetValue(rva, out var before);
            var stored = patch with { Rva = rva, Source = Source, Modified = DateTimeOffset.UtcNow };
            _byRva[rva] = stored;
            _changed.Add(rva);
            IsDirty = true;
            change = new PatchChange(rva, before, stored);
        }

        Changed?.Invoke(this, change);
    }

    /// <summary>Takes one back out. The bytes revert because the original was kept.</summary>
    public bool Remove(uint rva)
    {
        PatchChange change;
        lock (_lock)
        {
            if (!_byRva.TryGetValue(rva, out var before))
            {
                return false;
            }

            _byRva.Remove(rva);
            _changed.Add(rva);
            IsDirty = true;
            change = new PatchChange(rva, before, null);
        }

        Changed?.Invoke(this, change);
        return true;
    }

    /// <summary>Switches one on or off without forgetting it.</summary>
    public bool SetEnabled(uint rva, bool enabled)
    {
        PatchChange change;
        lock (_lock)
        {
            if (!_byRva.TryGetValue(rva, out var before) || before.Enabled == enabled)
            {
                return false;
            }

            var stored = before with { Enabled = enabled, Modified = DateTimeOffset.UtcNow };
            _byRva[rva] = stored;
            _changed.Add(rva);
            IsDirty = true;
            change = new PatchChange(rva, before, stored);
        }

        Changed?.Invoke(this, change);
        return true;
    }

    /// <summary>Every patch, in address order.</summary>
    public IReadOnlyList<Patch> Snapshot()
    {
        lock (_lock)
        {
            return _byRva.Values.ToList();
        }
    }

    /// <summary>The patch covering this RVA, if any — not only one that starts there.</summary>
    public Patch? Covering(uint rva)
    {
        lock (_lock)
        {
            foreach (var (at, patch) in _byRva)
            {
                if (at > rva)
                {
                    break;
                }

                if (rva < at + (uint)patch.Bytes.Count)
                {
                    return patch;
                }
            }

            return null;
        }
    }

    public void Clear()
    {
        List<PatchChange> changes;
        lock (_lock)
        {
            changes = _byRva.Select(e => new PatchChange(e.Key, e.Value, null)).ToList();
            foreach (var change in changes)
            {
                _changed.Add(change.Rva);
            }

            _byRva.Clear();
            IsDirty |= changes.Count > 0;
        }

        // One at a time, so anything tracking individual addresses unwinds them properly rather than
        // being told only that everything changed.
        foreach (var change in changes)
        {
            Changed?.Invoke(this, change);
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

    /// <summary>The start of a recorded patch overlapping this range, ignoring one at the same address.</summary>
    private uint? Overlapping(uint rva, int length)
    {
        uint end = rva + (uint)length;
        foreach (var (at, patch) in _byRva)
        {
            if (at == rva)
            {
                continue;
            }

            if (at < end && rva < at + (uint)patch.Bytes.Count)
            {
                return at;
            }
        }

        return null;
    }
}

/// <summary>What happened when a patched file was written.</summary>
public sealed record PatchWriteResult(int Applied, int Skipped, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>Writes a copy of an image with its patches applied.</summary>
public static class PatchWriter
{
    /// <summary>
    /// Writes <paramref name="image"/> to <paramref name="path"/> with every enabled patch applied.
    ///
    /// Always a copy, never the file that was opened. Overwriting the binary under analysis destroys
    /// the one reference for what it originally did, and does it at exactly the moment the analyst is
    /// least able to notice — so the caller must name somewhere else, and this refuses if they name
    /// the same place.
    /// </summary>
    public static PatchWriteResult Write(IBinaryImage image, PatchStore patches, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (image.Path is { Length: > 0 } source && SamePath(source, path))
        {
            throw new ArgumentException("write the patched copy somewhere other than the file it came from", nameof(path));
        }

        byte[] bytes = image.Data.ToArray();
        var problems = new List<string>();
        int applied = 0;
        int skipped = 0;

        foreach (var patch in patches.Snapshot())
        {
            if (!patch.Enabled)
            {
                skipped++;
                continue;
            }

            if (image.RvaToOffset(patch.Rva) is not { } offset)
            {
                problems.Add($"0x{patch.Rva:X} is not in any section of the file, so it has no bytes to change");
                continue;
            }

            if (offset + patch.Bytes.Count > bytes.Length)
            {
                problems.Add($"0x{patch.Rva:X} runs past the end of the file");
                continue;
            }

            // The file may not be the one the patch was made against. Saying so beats writing bytes
            // over something else that happens to live there now.
            var present = bytes.AsSpan((int)offset, patch.Original.Count);
            if (!present.SequenceEqual(patch.Original.ToArray()))
            {
                problems.Add(
                    $"0x{patch.Rva:X} holds {Convert.ToHexString(present)} where the patch expected {patch.OriginalHex}");
                continue;
            }

            patch.Bytes.ToArray().CopyTo(bytes.AsSpan((int)offset));
            applied++;
        }

        if (problems.Count > 0)
        {
            // Nothing is written when anything is wrong. A half-patched binary is worse than none:
            // it looks like it worked.
            return new PatchWriteResult(0, skipped, problems);
        }

        string temporary = $"{path}.{Environment.ProcessId:X}.tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);

        return new PatchWriteResult(applied, skipped, problems);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
