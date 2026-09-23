using System.Globalization;
using System.Text;
using Spydate.Core.Archive;
using Spydate.Core.Jvm;
using Spydate.Decompiler.Jvm;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

public sealed record JarEntryRow(int Index, string Name, string Size, string Packed, string Method, string Crc, string Modified, string Kind, ArchiveEntry Entry);

public sealed record JarStringRow(string Text, string In, string At, JvmType Type, JvmMember Member);

/// <summary>
/// The documents a JAR has: the files in its archive, and the string constants its code loads. Double-clicking a
/// class file opens the class; any other file opens its contents; a string opens the method that loads it.
/// </summary>
public static class JarDocuments
{
    /// <summary>How much of a file inside the archive is shown when opened: a large resource is a hex dump, not a read.</summary>
    public const int MaxShown = 256 * 1024;

    /// <summary>The most a file is inflated to be shown; a larger one is refused by size rather than read into memory.</summary>
    private const int MaxRead = 64 * 1024 * 1024;

    public static RecordsDocumentViewModel Entries(JarImage jar, Action<ArchiveEntry> open)
    {
        var rows = jar.Archive.Entries.Where(e => !e.IsDirectory).Select(e => new JarEntryRow(
            e.Index,
            e.Name,
            e.Size.ToString("N0", CultureInfo.InvariantCulture),
            e.CompressedSize.ToString("N0", CultureInfo.InvariantCulture),
            e.MethodName + (e.IsEncrypted ? ", encrypted" : string.Empty),
            e.Crc32.ToString("X8", CultureInfo.InvariantCulture),
            e.Modified?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
            KindOf(e.Name) + (jar.Archive.Duplicates.Contains(e.Name) ? " (name appears twice)" : string.Empty),
            e)).ToList();

        return new RecordsDocumentViewModel(
            "entries",
            "Entries",
            SymbolRegular.FolderZip24,
            "every file in the archive, in directory order; double-click a class to read it, anything else to see its contents",
            [
                new("#", nameof(JarEntryRow.Index), 48), new("Name", nameof(JarEntryRow.Name), 420), new("Size", nameof(JarEntryRow.Size), 90),
                new("Packed", nameof(JarEntryRow.Packed), 90), new("Method", nameof(JarEntryRow.Method), 80), new("CRC-32", nameof(JarEntryRow.Crc), 80),
                new("Modified", nameof(JarEntryRow.Modified), 120), new("Kind", nameof(JarEntryRow.Kind), 0),
            ],
            rows,
            row => open(((JarEntryRow)row).Entry));
    }

    public static RecordsDocumentViewModel Strings(JvmReading reading, Action<JvmType, JvmMember> open)
    {
        static string OneLine(string text)
        {
            string flat = text.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);
            return flat.Length > 300 ? flat[..300] + "…" : flat;
        }

        var rows = reading.References.Strings.Select(s => new JarStringRow(
            OneLine(s.Text),
            $"{s.Type.FullName}::{s.Member.Signature}",
            s.Offset < 0 ? "constant" : s.Offset.ToString("X4", CultureInfo.InvariantCulture),
            s.Type,
            s.Member)).ToList();

        return new RecordsDocumentViewModel(
            "strings",
            "Strings",
            SymbolRegular.TextT24,
            "every string constant the code loads (ldc), and each static final String's value; double-click to read the member",
            [new("String", nameof(JarStringRow.Text), 480), new("In", nameof(JarStringRow.In), 0), new("At", nameof(JarStringRow.At), 70)],
            rows,
            row => open(((JarStringRow)row).Type, ((JarStringRow)row).Member));
    }

    /// <summary>
    /// A file from inside the archive as text: itself when it reads as text, a hex dump when it does not, and the
    /// reason when it cannot be read at all.
    /// </summary>
    public static string Contents(JarImage jar, ArchiveEntry entry)
    {
        byte[] bytes;
        try
        {
            bytes = jar.Archive.Read(entry, MaxRead);
        }
        catch (ArchiveException ex)
        {
            return ex.Message;
        }

        var shown = bytes.AsSpan(0, Math.Min(bytes.Length, MaxShown));
        string cut = bytes.Length > MaxShown ? $"\n\n… {bytes.Length - MaxShown:N0} more bytes not shown" : string.Empty;
        if (LooksLikeText(shown))
        {
            return Encoding.UTF8.GetString(shown) + cut;
        }

        var sb = new StringBuilder();
        for (int line = 0; line < shown.Length; line += 16)
        {
            var chunk = shown.Slice(line, Math.Min(16, shown.Length - line));
            sb.Append(CultureInfo.InvariantCulture, $"{line:X8}  ");
            for (int i = 0; i < 16; i++)
            {
                sb.Append(i < chunk.Length ? chunk[i].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
            }

            sb.Append(' ');
            foreach (byte b in chunk)
            {
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.Append('\n');
        }

        return sb.Append(cut).ToString();
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        int control = 0;
        foreach (byte b in bytes[..Math.Min(bytes.Length, 4096)])
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 0x20 && b is not (9 or 10 or 13))
            {
                control++;
            }
        }

        return control * 50 < Math.Max(1, Math.Min(bytes.Length, 4096));
    }

    private static string KindOf(string name)
    {
        string extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".class" => name.StartsWith("META-INF/versions/", StringComparison.Ordinal) ? "class (multi-release)" : "class",
            ".jar" or ".war" or ".zip" => "nested archive",
            ".so" or ".dll" or ".dylib" or ".jnilib" => "native library",
            ".mf" => "manifest",
            ".sf" or ".rsa" or ".dsa" or ".ec" => "signature",
            ".properties" or ".xml" or ".json" or ".yml" or ".yaml" or ".txt" or ".md" or ".html" => "text",
            _ => extension.Length > 0 ? extension[1..] : "file",
        };
    }
}
