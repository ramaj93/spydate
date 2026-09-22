using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Spydate.Core.Binary;
using Spydate.Core.PE;

namespace Spydate.Core.Project;

/// <summary>
/// Enough of the image to tell whether a project belongs to the file being opened. The same reasoning as
/// the PDB check: annotations from a different build land at the wrong addresses, which is worse than
/// having none.
///
/// The build is told apart by the image's <see cref="IBinaryImage.Fingerprint"/> — whatever the format says
/// distinguishes one build from another. For a PE that is the link timestamp and checksum as
/// <c>"TTTTTTTT-CCCCCCCC"</c>, exactly the string the per-user store has always used in its file names, so
/// every project written before formats were generalised still matches.
/// </summary>
public sealed partial record ProjectIdentity(string FileName, long FileSize, string Fingerprint)
{
    public static ProjectIdentity Of(IBinaryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new ProjectIdentity(image.FileName, image.Data.Length, image.Fingerprint);
    }

    /// <summary>
    /// Whether the two describe the same file. The name is compared case-insensitively and only as a last
    /// resort - a renamed copy of the same bytes is still the same binary.
    /// </summary>
    public bool Matches(ProjectIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return FileSize == other.FileSize && string.Equals(Fingerprint, other.Fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A PE's fingerprint is spelled out as the stamp and checksum it is made of; any other is shown as it is.</summary>
    public string Describe() => PeFingerprint().Match(Fingerprint) is { Success: true } pe
        ? $"{FileName}, {FileSize} bytes, stamp 0x{pe.Groups[1].Value}, checksum 0x{pe.Groups[2].Value}"
        : $"{FileName}, {FileSize} bytes, build {Fingerprint}";

    [GeneratedRegex("^([0-9A-F]{8})-([0-9A-F]{8})$", RegexOptions.IgnoreCase)]
    private static partial Regex PeFingerprint();
}

/// <summary>Outcome of looking for the project file that belongs to an image.</summary>
public sealed record ProjectLoadResult
{
    public required bool Loaded { get; init; }
    public string? Path { get; init; }
    /// <summary>Annotations applied to the store.</summary>
    public int Applied { get; init; }
    /// <summary>Annotations skipped because their address is not in this image.</summary>
    public int Skipped { get; init; }
    /// <summary>Why nothing was loaded: no file, a different build, unreadable JSON.</summary>
    public string? Reason { get; init; }

    /// <summary>Patches read back from the file.</summary>
    public int PatchesApplied { get; init; }

    /// <summary>Patches in the file that could not be used — bad hex, or a length that does not match.</summary>
    public int PatchesSkipped { get; init; }

    /// <summary>Breakpoints read back from the file.</summary>
    public int BreakpointsApplied { get; init; }

    /// <summary>Note sections read back from the file.</summary>
    public int NotesApplied { get; init; }

    public override string ToString() => Loaded
        ? $"{Applied} annotation(s) from {Path}"
        : Reason ?? "no project file";
}

/// <summary>
/// Reading and writing the <c>.spydate</c> file that holds a session's renames and comments.
///
/// Addresses are stored as RVAs, not VAs: an RVA is what the file itself says, so a project keeps working
/// if the image is ever examined at a different base. The file is JSON, indented, with hex strings for
/// addresses - it is meant to be readable, diffable and hand-editable, because a rename list is exactly
/// the kind of thing people want to keep in version control.
/// </summary>
public static class SpydateProject
{
    public const int FormatVersion = 1;

    public const string Extension = ".spydate";

    /// <summary>
    /// Where a project for this image may live, most preferred first: beside the binary, then a per-user
    /// store. The second exists because the interesting binaries are in places one cannot write to.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths(IBinaryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var paths = new List<string>(2);
        if (image.Path is { } imagePath)
        {
            paths.Add(imagePath + Extension);
        }

        paths.Add(UserStorePath(image));
        return paths;
    }

    /// <summary>Per-user location, named so two files with the same name do not collide.</summary>
    public static string UserStorePath(IBinaryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var identity = ProjectIdentity.Of(image);
        string key = $"{identity.Fingerprint}-{identity.FileSize:X}";
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spydate",
            "Projects",
            $"{image.FileName}.{key}{Extension}");
    }

    /// <summary>
    /// Writes the annotations to the first path that accepts them. Returns where they went, or null when
    /// there was nothing to write and no file to update.
    /// </summary>
    public static string? Save(IBinaryImage image, AnnotationStore annotations, PatchStore? patches = null, BreakpointStore? breakpoints = null, NoteStore? notes = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(annotations);

        var candidates = CandidatePaths(image);
        // Keep updating a file that already exists rather than starting a second one elsewhere.
        string? existing = candidates.FirstOrDefault(File.Exists);
        if (existing is null && annotations.Count == 0 && (patches?.Count ?? 0) == 0 && (breakpoints?.Count ?? 0) == 0 && (notes?.Count ?? 0) == 0)
        {
            return null;
        }

        var order = existing is null ? candidates : new[] { existing }.Concat(candidates.Where(p => p != existing)).ToList();
        Exception? failure = null;
        foreach (string path in order)
        {
            try
            {
                SaveTo(path, image, annotations, patches, breakpoints, notes);
                annotations.MarkSaved();
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failure = ex;   // usually "the binary lives somewhere unwritable"; try the next candidate
            }
        }

        throw new IOException($"Could not save the project: {failure?.Message}", failure);
    }

    /// <summary>
    /// Writes the annotations to an explicit path, merging rather than overwriting.
    ///
    /// Two processes hold this file open — the window and an agent driving the MCP server, or two
    /// agents — and each has its own store loaded at its own moment. Serialising a whole snapshot
    /// would make every save delete whatever the other one had recorded since, and the direction
    /// that hurts is the agent erasing names a person typed. So a save re-reads the file, keeps
    /// every entry this store has not touched, and overlays only the addresses it actually changed
    /// (a cleared one being removed). Entries collide only when both sides edited the same address,
    /// which is rare and resolves in favour of the writer, since that is the more recent decision.
    /// </summary>
    public static void SaveTo(string path, IBinaryImage image, AnnotationStore annotations, PatchStore? patches = null, BreakpointStore? breakpoints = null, NoteStore? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(annotations);

        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var _ = Advisory.Lock(path);

        var identity = ProjectIdentity.Of(image);
        var mine = annotations.Snapshot().ToDictionary(e => e.Key, e => e.Value);

        // With nothing to merge into — no file, or one belonging to another build — the store is the
        // only account of this binary there is, so all of it goes down. Only when there is a file
        // worth keeping does the change set matter, and then it is the whole point.
        var existing = ExistingEntries(path, identity);
        var entries = existing ?? new Dictionary<string, AnnotationDto>(StringComparer.OrdinalIgnoreCase);
        var overlay = existing is null ? mine.Keys : annotations.ChangedAddresses;

        foreach (ulong va in overlay)
        {
            if (image.VaToRva(va) is not { } rva)
            {
                continue; // an address outside the image cannot be described in a portable way
            }

            string key = Hex(rva);
            if (mine.TryGetValue(va, out var annotation))
            {
                entries[key] = new AnnotationDto
                {
                    Rva = key,
                    Name = annotation.Name,
                    Comment = annotation.Comment,
                    Locals = annotation.Locals is { Count: > 0 } locals ? new Dictionary<string, string>(locals, StringComparer.Ordinal) : null,
                    Source = annotation.Source,
                    Modified = annotation.Modified,
                };
            }
            else
            {
                entries.Remove(key);   // cleared here, so it goes from the file too
            }
        }

        var file = new ProjectFile
        {
            Format = FormatVersion,
            Image = new ImageDto
            {
                Name = identity.FileName,
                Size = identity.FileSize,
                // A PE keeps writing the stamp and checksum its fingerprint is made of, so its project file is
                // unchanged and an older build still reads it; any other format writes the fingerprint itself.
                TimeDateStamp = image is PeImage stamped ? Hex(stamped.FileHeader.TimeDateStamp) : null,
                CheckSum = image is PeImage summed ? Hex(summed.OptionalHeader.CheckSum) : null,
                Fingerprint = image is PeImage ? null : identity.Fingerprint,
                ImageBase = Hex(image.ImageBase),
            },
            Annotations = entries.Values.OrderBy(e => ParseHex32(e.Rva)).ToList(),
            Patches = MergePatches(path, identity, patches),
            Breakpoints = MergeBreakpoints(path, identity, breakpoints),
            Notes = MergeNotes(path, identity, notes),
        };

        // Write beside the target and move into place, so a failure cannot truncate the previous
        // project. The name carries the process id: two writers sharing one ".tmp" would corrupt
        // each other's half-written file, which is exactly the accident the temp file exists to stop.
        string temporary = $"{path}.{Environment.ProcessId:X}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, Options));
        File.Move(temporary, path, overwrite: true);

        // The store now agrees with this file, so the next save merges from here. Without this, an
        // address changed once would be re-applied by every later save, undoing anybody who removed
        // it in between.
        annotations.MarkSaved();
        patches?.MarkSaved();
        breakpoints?.MarkSaved();
        notes?.MarkSaved();
    }

    /// <summary>
    /// Patches for the file being written, merged the way annotations are: only what this session
    /// touched is overlaid, so a second writer's patches survive. Null when there is nothing to say,
    /// which keeps the member out of the file entirely for projects that have never had a patch.
    /// </summary>
    private static List<PatchDto>? MergePatches(string path, ProjectIdentity identity, PatchStore? patches)
    {
        var existing = ExistingPatches(path, identity);

        if (patches is null)
        {
            // Not that there are none — that this caller does not know about them. Anything already
            // in the file stays, rather than being dropped by a save that never considered it.
            return existing?.Values.OrderBy(e => ParseHex32(e.Rva)).ToList();
        }

        var mine = patches.Snapshot().ToDictionary(p => p.Rva, p => p);
        var entries = existing ?? new Dictionary<string, PatchDto>(StringComparer.OrdinalIgnoreCase);
        var overlay = existing is null ? mine.Keys : patches.ChangedAddresses;

        foreach (uint rva in overlay)
        {
            string key = Hex(rva);
            if (mine.TryGetValue(rva, out var patch))
            {
                entries[key] = new PatchDto
                {
                    Rva = key,
                    Bytes = patch.Hex,
                    Original = patch.OriginalHex,
                    Comment = patch.Comment,
                    Enabled = patch.Enabled ? null : false,
                    Source = patch.Source,
                    Modified = patch.Modified,
                };
            }
            else
            {
                entries.Remove(key);
            }
        }

        return entries.Count == 0 ? null : entries.Values.OrderBy(e => ParseHex32(e.Rva)).ToList();
    }

    /// <summary>
    /// Breakpoints for the file being written, merged the way patches are: only the addresses this
    /// session touched are overlaid, so a second window's breakpoints survive. Null when there is
    /// nothing to say, which keeps the member out of the file for a project that never had one.
    /// </summary>
    private static List<BreakpointDto>? MergeBreakpoints(string path, ProjectIdentity identity, BreakpointStore? breakpoints)
    {
        var existing = ExistingBreakpoints(path, identity);

        if (breakpoints is null)
        {
            return existing?.Values.OrderBy(e => ParseHex32(e.Rva)).ToList();
        }

        var mine = breakpoints.Snapshot().ToHashSet();
        var entries = existing ?? new Dictionary<string, BreakpointDto>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<uint> overlay = existing is null ? mine : breakpoints.ChangedAddresses;

        foreach (uint rva in overlay)
        {
            string key = Hex(rva);
            if (mine.Contains(rva))
            {
                entries[key] = new BreakpointDto { Rva = key };
            }
            else
            {
                entries.Remove(key);   // cleared here, so it goes from the file too
            }
        }

        return entries.Count == 0 ? null : entries.Values.OrderBy(e => ParseHex32(e.Rva)).ToList();
    }

    /// <summary>
    /// Note sections for the file being written, merged the way annotations are: only the keys this
    /// session touched are overlaid, so a section a second writer added survives. Null when there is
    /// nothing to say, which keeps the member out of the file for a project that never had a note.
    /// </summary>
    private static List<NoteDto>? MergeNotes(string path, ProjectIdentity identity, NoteStore? notes)
    {
        var existing = ExistingNotes(path, identity);

        if (notes is null)
        {
            // Not that there are none — that this caller does not know about them. Anything already in
            // the file stays, rather than being dropped by a save that never considered it.
            return existing?.Values.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var mine = notes.Snapshot().ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
        var entries = existing ?? new Dictionary<string, NoteDto>(StringComparer.OrdinalIgnoreCase);
        var overlay = existing is null ? mine.Keys : notes.ChangedKeys;

        foreach (string key in overlay)
        {
            if (mine.TryGetValue(key, out var note))
            {
                entries[key] = new NoteDto
                {
                    Key = key,
                    Text = note.Text,
                    Order = note.Order == 0 ? null : note.Order,
                    Source = note.Source,
                    Modified = note.Modified,
                };
            }
            else
            {
                entries.Remove(key);   // cleared here, so it goes from the file too
            }
        }

        return entries.Count == 0 ? null : entries.Values.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, NoteDto>? ExistingNotes(string path, ProjectIdentity identity)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var file = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options);
            if (file is null || file.Format > FormatVersion || !IdentityOf(file).Matches(identity))
            {
                return null;
            }

            var entries = new Dictionary<string, NoteDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var note in file.Notes ?? [])
            {
                if (note.Key is { Length: > 0 } key)
                {
                    entries[key] = note;   // last wins if a hand-edit left two keys differing only in case
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, BreakpointDto>? ExistingBreakpoints(string path, ProjectIdentity identity)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var file = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options);
            if (file is null || file.Format > FormatVersion || !IdentityOf(file).Matches(identity))
            {
                return null;
            }

            return (file.Breakpoints ?? [])
                .Where(b => b.Rva is { Length: > 0 })
                .ToDictionary(b => b.Rva!, b => b, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, PatchDto>? ExistingPatches(string path, ProjectIdentity identity)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var file = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options);
            if (file is null || file.Format > FormatVersion || !IdentityOf(file).Matches(identity))
            {
                return null;
            }

            return (file.Patches ?? [])
                .Where(p => p.Rva is { Length: > 0 })
                .ToDictionary(p => p.Rva!, p => p, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What is already in the file, keyed by RVA, or null when there is nothing worth merging into:
    /// no file, an unreadable one, or one belonging to a different build. That last case is not an
    /// error — merging would mix two binaries' annotations at addresses that mean different things
    /// in each — so the stale file is replaced rather than joined.
    /// </summary>
    private static Dictionary<string, AnnotationDto>? ExistingEntries(string path, ProjectIdentity identity)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options);
            if (file?.Annotations is not { } stored || file.Format > FormatVersion || !IdentityOf(file).Matches(identity))
            {
                return null;
            }

            var entries = new Dictionary<string, AnnotationDto>(stored.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in stored)
            {
                if (entry.Rva is { Length: > 0 } rva)
                {
                    entries[rva] = entry;
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;   // unreadable: better to write a good file than to refuse to save
        }
    }

    /// <summary>Finds the project belonging to <paramref name="image"/> and applies it.</summary>
    public static ProjectLoadResult LoadFor(IBinaryImage image, AnnotationStore annotations, PatchStore? patches = null, BreakpointStore? breakpoints = null, NoteStore? notes = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        string? mismatch = null;
        foreach (string path in CandidatePaths(image))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var result = Load(path, image, annotations, patches, breakpoints, notes);
            if (result.Loaded)
            {
                return result;
            }

            mismatch ??= result.Reason;
        }

        return new ProjectLoadResult { Loaded = false, Reason = mismatch ?? "no project file" };
    }

    /// <summary>Applies one project file, rejecting it if it was made for a different build.</summary>
    public static ProjectLoadResult Load(string path, IBinaryImage image, AnnotationStore annotations, PatchStore? patches = null, BreakpointStore? breakpoints = null, NoteStore? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(annotations);

        ProjectFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ProjectLoadResult { Loaded = false, Path = path, Reason = $"{path} could not be read: {ex.Message}" };
        }

        if (file is null || file.Image is null)
        {
            return new ProjectLoadResult { Loaded = false, Path = path, Reason = $"{path} is not a Spydate project." };
        }

        if (file.Format > FormatVersion)
        {
            return new ProjectLoadResult { Loaded = false, Path = path, Reason = $"{path} is format {file.Format}; this build understands {FormatVersion}." };
        }

        var stored = IdentityOf(file);
        var actual = ProjectIdentity.Of(image);
        if (!stored.Matches(actual))
        {
            return new ProjectLoadResult
            {
                Loaded = false,
                Path = path,
                Reason = $"{path} was made for a different build ({stored.Describe()}), not this one ({actual.Describe()}).",
            };
        }

        int applied = 0;
        int skipped = 0;
        foreach (var entry in file.Annotations ?? new List<AnnotationDto>())
        {
            uint rva = ParseHex32(entry.Rva);
            if (rva == 0 && !string.Equals(entry.Rva, "0x0", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            annotations.Set(image.RvaToVa(rva), new Annotation
            {
                Name = entry.Name,
                Comment = entry.Comment,
                Locals = entry.Locals is { Count: > 0 } ? entry.Locals : null,
                Source = entry.Source ?? AnnotationSource.User,
                Modified = entry.Modified,
            });
            applied++;
        }

        int patchesApplied = 0;
        int patchesSkipped = 0;
        if (patches is not null)
        {
            foreach (var entry in file.Patches ?? [])
            {
                if (ReadPatch(entry) is not { } patch)
                {
                    patchesSkipped++;
                    continue;
                }

                try
                {
                    patches.Set(patch.Rva, patch);
                    patchesApplied++;
                }
                catch (ArgumentException)
                {
                    // Hand-edited into an overlap or a length mismatch. One bad entry is not a reason
                    // to refuse the project, and the store's rules are not negotiable.
                    patchesSkipped++;
                }
            }

            patches.MarkSaved();
        }

        int breakpointsApplied = 0;
        if (breakpoints is not null)
        {
            foreach (var entry in file.Breakpoints ?? [])
            {
                if (entry.Rva is not { Length: > 0 } text)
                {
                    continue;
                }

                uint rva = ParseHex32(text);
                if (rva == 0 && !string.Equals(text, "0x0", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (breakpoints.Add(rva))
                {
                    breakpointsApplied++;
                }
            }

            breakpoints.MarkSaved();
        }

        int notesApplied = 0;
        if (notes is not null)
        {
            foreach (var entry in file.Notes ?? [])
            {
                if (entry.Key is not { Length: > 0 } key || entry.Text is not { Length: > 0 } text)
                {
                    continue;
                }

                notes.Restore(key, new Note
                {
                    Text = text,
                    Order = entry.Order ?? 0,
                    Source = entry.Source ?? AnnotationSource.User,
                    Modified = entry.Modified,
                });
                notesApplied++;
            }

            notes.MarkSaved();
        }

        annotations.MarkSaved();
        return new ProjectLoadResult
        {
            Loaded = true,
            Path = path,
            Applied = applied,
            Skipped = skipped,
            PatchesApplied = patchesApplied,
            PatchesSkipped = patchesSkipped,
            BreakpointsApplied = breakpointsApplied,
            NotesApplied = notesApplied,
        };
    }

    /// <summary>One stored patch, or null when it does not describe a usable one.</summary>
    private static Patch? ReadPatch(PatchDto entry)
    {
        if (entry.Rva is not { Length: > 0 } rvaText || entry.Bytes is not { Length: > 0 } bytesText)
        {
            return null;
        }

        try
        {
            byte[] bytes = Convert.FromHexString(bytesText);
            byte[] original = entry.Original is { Length: > 0 } originalText
                ? Convert.FromHexString(originalText)
                : [];

            return bytes.Length == 0 || bytes.Length != original.Length
                ? null
                : new Patch
                {
                    Rva = ParseHex32(rvaText),
                    Bytes = bytes,
                    Original = original,
                    Comment = entry.Comment,
                    Enabled = entry.Enabled ?? true,
                    Source = entry.Source ?? AnnotationSource.User,
                    Modified = entry.Modified,
                };
        }
        catch (FormatException)
        {
            return null;   // not hex
        }
    }

    /// <summary>The build a parsed project file says it belongs to.</summary>
    /// <summary>
    /// The build a parsed project file says it belongs to. A file with a fingerprint is taken at its word; one
    /// without was written for a PE, and its stamp and checksum are the fingerprint, spelled as the image spells it.
    /// </summary>
    private static ProjectIdentity IdentityOf(ProjectFile file) => new(
        file.Image?.Name ?? string.Empty,
        file.Image?.Size ?? 0,
        file.Image?.Fingerprint is { Length: > 0 } fingerprint
            ? fingerprint
            : $"{ParseHex32(file.Image?.TimeDateStamp):X8}-{ParseHex32(file.Image?.CheckSum):X8}");

    private static string Hex(ulong value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    private static uint ParseHex32(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : 0;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// The format is not bumped for provenance. A reader that predates it ignores members it does not
    /// know, so an older build opens an agent-written project and simply sees the names — which is a
    /// far better failure than <see cref="Load"/> refusing the file outright for being from the future.
    /// </summary>

    // --- file shape ---------------------------------------------------

    private sealed class ProjectFile
    {
        [JsonPropertyName("format")] public int Format { get; set; }
        [JsonPropertyName("image")] public ImageDto? Image { get; set; }
        [JsonPropertyName("annotations")] public List<AnnotationDto>? Annotations { get; set; }

        /// <summary>
        /// Still format 1. A reader that predates patches ignores a member it does not know, and this
        /// one treats an absent list as no patches — so a project written by either version opens in
        /// the other, losing nothing it understood. Bumping the version would have refused the file
        /// outright, over a member that costs nothing to skip.
        /// </summary>
        [JsonPropertyName("patches")] public List<PatchDto>? Patches { get; set; }

        /// <summary>
        /// Still format 1, on the same reasoning as patches: a reader that predates breakpoints skips
        /// a member it does not know, and an absent list means none, so a project written by either
        /// version opens in the other losing only what it never understood.
        /// </summary>
        [JsonPropertyName("breakpoints")] public List<BreakpointDto>? Breakpoints { get; set; }

        /// <summary>
        /// Still format 1, on the same reasoning as patches and breakpoints: a reader that predates
        /// notes skips a member it does not know, and an absent list means none, so a project written
        /// by either version opens in the other losing only what it never understood.
        /// </summary>
        [JsonPropertyName("notes")] public List<NoteDto>? Notes { get; set; }
    }

    private sealed class ImageDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("timeDateStamp")] public string? TimeDateStamp { get; set; }
        [JsonPropertyName("checkSum")] public string? CheckSum { get; set; }
        [JsonPropertyName("imageBase")] public string? ImageBase { get; set; }

        /// <summary>Written for formats other than PE, whose build is told apart some other way than a stamp and checksum.</summary>
        [JsonPropertyName("fingerprint")] public string? Fingerprint { get; set; }
    }

    private sealed class AnnotationDto
    {
        [JsonPropertyName("rva")] public string? Rva { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("comment")] public string? Comment { get; set; }
        [JsonPropertyName("locals")] public Dictionary<string, string>? Locals { get; set; }

        /// <summary>Absent in files written before provenance existed, which means a person wrote it.</summary>
        [JsonPropertyName("source")] public AnnotationSource? Source { get; set; }

        [JsonPropertyName("modified")] public DateTimeOffset? Modified { get; set; }
    }

    private sealed class BreakpointDto
    {
        /// <summary>Where, as an RVA. A breakpoint is only a place, so this is all it needs.</summary>
        [JsonPropertyName("rva")] public string? Rva { get; set; }
    }

    private sealed class PatchDto
    {
        [JsonPropertyName("rva")] public string? Rva { get; set; }

        /// <summary>Hex, so the file stays readable and a patch can be checked by eye.</summary>
        [JsonPropertyName("bytes")] public string? Bytes { get; set; }

        [JsonPropertyName("original")] public string? Original { get; set; }
        [JsonPropertyName("comment")] public string? Comment { get; set; }

        /// <summary>Absent means on, so a hand-written entry does not need it.</summary>
        [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

        [JsonPropertyName("source")] public AnnotationSource? Source { get; set; }
        [JsonPropertyName("modified")] public DateTimeOffset? Modified { get; set; }
    }

    private sealed class NoteDto
    {
        /// <summary>The section key, the short heading the writer chose (<c>overview</c>, <c>string-xor</c>).</summary>
        [JsonPropertyName("key")] public string? Key { get; set; }

        /// <summary>The section text, Markdown with LF newlines. Multi-line, unlike a comment.</summary>
        [JsonPropertyName("text")] public string? Text { get; set; }

        /// <summary>Where it sits in the document, lower first. Absent means zero, so an old file needs no change.</summary>
        [JsonPropertyName("order")] public int? Order { get; set; }

        [JsonPropertyName("source")] public AnnotationSource? Source { get; set; }
        [JsonPropertyName("modified")] public DateTimeOffset? Modified { get; set; }
    }
}
