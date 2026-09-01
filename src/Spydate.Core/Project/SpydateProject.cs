using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spydate.Core.PE;

namespace Spydate.Core.Project;

/// <summary>
/// Enough of the image to tell whether a project belongs to the file being opened. The same reasoning as
/// the PDB check: annotations from a different build land at the wrong addresses, which is worse than
/// having none.
/// </summary>
public sealed record ProjectIdentity(string FileName, long FileSize, uint TimeDateStamp, uint CheckSum)
{
    public static ProjectIdentity Of(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new ProjectIdentity(image.FileName, image.Data.Length, image.FileHeader.TimeDateStamp, image.OptionalHeader.CheckSum);
    }

    /// <summary>
    /// Whether the two describe the same file. The name is compared case-insensitively and only as a last
    /// resort - a renamed copy of the same bytes is still the same binary.
    /// </summary>
    public bool Matches(ProjectIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return FileSize == other.FileSize && TimeDateStamp == other.TimeDateStamp && CheckSum == other.CheckSum;
    }

    public string Describe() => $"{FileName}, {FileSize} bytes, stamp 0x{TimeDateStamp:X8}, checksum 0x{CheckSum:X8}";
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
    public static IReadOnlyList<string> CandidatePaths(PeImage image)
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
    public static string UserStorePath(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var identity = ProjectIdentity.Of(image);
        string key = $"{identity.TimeDateStamp:X8}-{identity.CheckSum:X8}-{identity.FileSize:X}";
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
    public static string? Save(PeImage image, AnnotationStore annotations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(annotations);

        var candidates = CandidatePaths(image);
        // Keep updating a file that already exists rather than starting a second one elsewhere.
        string? existing = candidates.FirstOrDefault(File.Exists);
        if (existing is null && annotations.Count == 0)
        {
            return null;
        }

        var order = existing is null ? candidates : new[] { existing }.Concat(candidates.Where(p => p != existing)).ToList();
        Exception? failure = null;
        foreach (string path in order)
        {
            try
            {
                SaveTo(path, image, annotations);
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
    public static void SaveTo(string path, PeImage image, AnnotationStore annotations)
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
                TimeDateStamp = Hex(identity.TimeDateStamp),
                CheckSum = Hex(identity.CheckSum),
                ImageBase = Hex(image.ImageBase),
            },
            Annotations = entries.Values.OrderBy(e => ParseHex32(e.Rva)).ToList(),
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
    public static ProjectLoadResult LoadFor(PeImage image, AnnotationStore annotations)
    {
        ArgumentNullException.ThrowIfNull(image);

        string? mismatch = null;
        foreach (string path in CandidatePaths(image))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var result = Load(path, image, annotations);
            if (result.Loaded)
            {
                return result;
            }

            mismatch ??= result.Reason;
        }

        return new ProjectLoadResult { Loaded = false, Reason = mismatch ?? "no project file" };
    }

    /// <summary>Applies one project file, rejecting it if it was made for a different build.</summary>
    public static ProjectLoadResult Load(string path, PeImage image, AnnotationStore annotations)
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

        annotations.MarkSaved();
        return new ProjectLoadResult { Loaded = true, Path = path, Applied = applied, Skipped = skipped };
    }

    /// <summary>The build a parsed project file says it belongs to.</summary>
    private static ProjectIdentity IdentityOf(ProjectFile file) => new(
        file.Image?.Name ?? string.Empty,
        file.Image?.Size ?? 0,
        ParseHex32(file.Image?.TimeDateStamp),
        ParseHex32(file.Image?.CheckSum));

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
    }

    private sealed class ImageDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("timeDateStamp")] public string? TimeDateStamp { get; set; }
        [JsonPropertyName("checkSum")] public string? CheckSum { get; set; }
        [JsonPropertyName("imageBase")] public string? ImageBase { get; set; }
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
}
