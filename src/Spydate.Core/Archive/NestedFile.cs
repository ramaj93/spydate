namespace Spydate.Core.Archive;

/// <summary>
/// A file inside an archive, named the way the JVM names one — <c>app.apk!/lib/x86_64/libfoo.so</c> — and taken out
/// to a file of its own so that it opens like any other: an APK's native library opens as the ELF it is.
///
/// Extracted files go under <see cref="CacheRoot"/>, one folder per archive keyed by the hash of its zip directory,
/// so the same library of the same package is the same path every time (and its project is found again). An entry's
/// name comes from the archive and is not trusted: a name that would climb out of that folder is refused.
/// </summary>
public static class NestedFile
{
    public const string Separator = "!/";

    /// <summary>The most a nested file is inflated to: an APK's largest native libraries are tens of megabytes.</summary>
    public const int MaxSize = 512 * 1024 * 1024;

    /// <summary>Where extracted files are kept: the user's temp folder, under Spydate\extracted.</summary>
    public static string CacheRoot => Path.Combine(Path.GetTempPath(), "Spydate", "extracted");

    /// <summary>Splits <c>archive!/entry</c> into the archive's path and the entry's name; false for a plain path.</summary>
    public static bool TrySplit(string path, out string archive, out string entry)
    {
        int bang = path.IndexOf(Separator, StringComparison.Ordinal);
        if (bang <= 0 || bang + Separator.Length >= path.Length)
        {
            archive = entry = string.Empty;
            return false;
        }

        archive = path[..bang];
        entry = path[(bang + Separator.Length)..];
        return true;
    }

    /// <summary>Takes an entry out of the zip at a path on disk; see <see cref="Extract(ZipArchiveFile, ArchiveEntry, string?)"/>.</summary>
    public static string Extract(string archivePath, string entryName, string? cacheRoot = null)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(archivePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveException($"Cannot read '{archivePath}': {ex.Message}", ex);
        }

        var archive = ZipArchiveFile.Open(bytes);
        var entry = archive.Find(entryName) ?? throw new ArchiveException($"{Path.GetFileName(archivePath)} has no entry {entryName}.");
        return Extract(archive, entry, cacheRoot);
    }

    /// <summary>
    /// The entry as a file of its own, written on first use and found again after: its path under the archive's
    /// folder in the cache, the entry's own folders kept (<c>lib\x86_64\libfoo.so</c>).
    /// </summary>
    public static string Extract(ZipArchiveFile archive, ArchiveEntry entry, string? cacheRoot = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
        {
            throw new ArchiveException($"{entry.Name} is a folder, not a file.");
        }

        string folder = Path.GetFullPath(Path.Combine(cacheRoot ?? CacheRoot, archive.DirectoryHash()[..16]));
        string path = Path.GetFullPath(Path.Combine([folder, .. Segments(entry.Name)]));
        if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveException($"The entry name {entry.Name} would be written outside its folder.");
        }

        if (File.Exists(path) && new FileInfo(path).Length == entry.Size)
        {
            return path;
        }

        byte[] bytes = archive.Read(entry, MaxSize);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveException($"Cannot write {entry.Name} to {path}: {ex.Message}", ex);
        }

        return path;
    }

    /// <summary>An entry name's folders and file name, each one a name Windows can hold; a name that climbs is refused.</summary>
    private static string[] Segments(string name)
    {
        var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            throw new ArchiveException($"The entry name {name} is not a path inside the archive.");
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        return segments.Select(s => new string(s.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).TrimEnd('.', ' ') is { Length: > 0 } clean ? clean : "_").ToArray();
    }
}
