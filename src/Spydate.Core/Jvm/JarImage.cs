using System.Text;
using Spydate.Core.Archive;
using Spydate.Core.Binary;

namespace Spydate.Core.Jvm;

/// <summary>One class file in a JAR, parsed, with the entry it came from.</summary>
public sealed record JarClass(ArchiveEntry Entry, ClassFile File);

/// <summary>
/// A Java archive: a zip of class files and resources, with a manifest that may say how it runs.
///
/// It is an <see cref="IBinaryImage"/> so that it opens, is fingerprinted and keeps a project file the way every
/// other file does — but it has no address space. There are no sections, no entry address and no native symbols;
/// the address members answer "nothing here", and the architecture is <see cref="Architecture.Unknown"/>, which is
/// what keeps native analysis from ever starting on one. Its program is its bytecode, read through the classes
/// here.
/// </summary>
public sealed class JarImage : IBinaryImage
{
    /// <summary>A class file is bounded by the format's own u2 counts; anything this large is a zip bomb, not a class.</summary>
    public const int MaxClassSize = 16 * 1024 * 1024;

    /// <summary>A manifest is a few hundred bytes; a megabyte is generous and still bounds a hostile one.</summary>
    private const int MaxManifestSize = 1024 * 1024;

    private const string VersionedPrefix = "META-INF/versions/";

    private readonly List<string> _warnings = [];
    private readonly Lazy<ClassSet> _classes;

    private JarImage(string? path, ReadOnlyMemory<byte> data, ZipArchiveFile archive)
    {
        Path = path;
        FileName = path is null ? "(memory)" : System.IO.Path.GetFileName(path);
        Data = data;
        Archive = archive;
        _warnings.AddRange(archive.Warnings);
        Fingerprint = archive.DirectoryHash();

        if (archive.Find("META-INF/MANIFEST.MF") is { } manifest)
        {
            try
            {
                Manifest = JarManifest.Parse(Encoding.UTF8.GetString(archive.Read(manifest, MaxManifestSize)));
            }
            catch (ArchiveException ex)
            {
                _warnings.Add($"The manifest could not be read: {ex.Message}");
            }
        }

        _classes = new Lazy<ClassSet>(ReadClasses, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static JarImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveException($"Cannot read '{path}': {ex.Message}", ex);
        }

        return new JarImage(path, bytes, ZipArchiveFile.Open(bytes));
    }

    /// <summary>
    /// The Java archive inside another file, when there is one: a Windows launcher (launch4j, jpackage's stub) is a
    /// PE with the application's JAR appended, so the file is both at once — the PE is only what starts the JVM,
    /// and the program is the archive. Recognised by a zip directory at the end that lists class files. Null for
    /// any file that is not such a pair, which costs a scan of its last 64 KB.
    /// </summary>
    public static JarImage? Embedded(IBinaryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is JarImage || image.Data.Length < 22)
        {
            return null;
        }

        try
        {
            var archive = ZipArchiveFile.Open(image.Data);
            if (!archive.Entries.Any(e => e.Name.EndsWith(".class", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return new JarImage(image.Path, image.Data, archive);
        }
        catch (ArchiveException)
        {
            return null;   // no zip at the end, or not a readable one: an ordinary binary
        }
    }

    /// <summary>A JAR held in memory, as tests build them.</summary>
    public static JarImage FromBytes(byte[] bytes, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new JarImage(path, bytes, ZipArchiveFile.Open(bytes));
    }

    public ZipArchiveFile Archive { get; }

    /// <summary>Null when the archive has no manifest, or it could not be read.</summary>
    public JarManifest? Manifest { get; }

    /// <summary>
    /// Every class file in the archive that parsed, in directory order. Read on first use, since a large JAR
    /// holds thousands, and whoever asks first — the explorer, an agent — should pay for it off the UI thread.
    /// A class that does not parse is left out and named in <see cref="Warnings"/>.
    /// </summary>
    public IReadOnlyList<JarClass> Classes => _classes.Value.Classes;

    /// <summary>Class files under <c>META-INF/versions/</c>: a multi-release JAR's alternatives, shadowing a base class on newer runtimes.</summary>
    public int VersionedClasses => _classes.Value.Versioned;

    /// <summary>The module <c>module-info.class</c> declares, when there is one.</summary>
    public string? ModuleName => _classes.Value.Module;

    /// <summary>The class with this internal name, or null.</summary>
    public JarClass? FindClass(string internalName) => _classes.Value.ByName.GetValueOrDefault(internalName);

    public BinaryFormat Format => BinaryFormat.Jar;

    public Architecture Architecture => Architecture.Unknown;

    public string? Path { get; }

    public string FileName { get; }

    public ReadOnlyMemory<byte> Data { get; }

    public long Length => Data.Length;

    public int Bitness => 0;

    public bool Is64Bit => false;

    public ulong ImageBase => 0;

    public ulong ImageSize => 0;

    public uint EntryPointRva => 0;

    public ulong EntryPointVa => 0;

    /// <summary>A library unless its manifest names a class to run.</summary>
    public bool IsLibrary => Manifest?.MainClass is null;

    public IReadOnlyList<IBinarySection> Sections => [];

    public IBinarySection? SectionFromRva(uint rva) => null;

    public IBinarySection? SectionFromVa(ulong va) => null;

    public ulong RvaToVa(uint rva) => rva;

    public uint? VaToRva(ulong va) => null;

    public uint? RvaToOffset(uint rva) => null;

    public uint? OffsetToRva(uint offset) => null;

    public uint? VaToOffset(ulong va) => null;

    public ReadOnlyMemory<byte> ReadAtVa(ulong va, int length) => ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> ReadAtRva(uint rva, int length) => ReadOnlyMemory<byte>.Empty;

    public ulong? ReadPointerAtRva(uint rva) => null;

    public IReadOnlyList<ExportedSymbol> Exports => [];

    public IReadOnlyList<ImportedSymbol> Imports => [];

    /// <summary>What the archive and its classes tolerated. Reading this reads the classes.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            _ = _classes.Value;
            return _warnings;
        }
    }

    /// <summary>A hash of the zip's central directory, which holds every entry's name, size and CRC.</summary>
    public string Fingerprint { get; }

    private ClassSet ReadClasses()
    {
        var set = new ClassSet();
        var classWarnings = new List<string>();
        int failed = 0;
        foreach (var entry in Archive.Entries)
        {
            if (entry.IsDirectory || !entry.Name.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Name.StartsWith(VersionedPrefix, StringComparison.Ordinal))
            {
                set.Versioned++;
                continue;
            }

            if (!ReferenceEquals(Archive.Find(entry.Name), entry))
            {
                continue;   // the second of a duplicated name: the JVM never sees it, and the archive already said so
            }

            ClassFile file;
            try
            {
                file = ClassFile.Parse(Archive.Read(entry, MaxClassSize));
            }
            catch (BinaryParseException ex)
            {
                failed++;
                if (classWarnings.Count < 20)
                {
                    classWarnings.Add($"{entry.Name}: {ex.Message}");
                }

                continue;
            }

            if (file.IsModule)
            {
                set.Module = file.Name == "module-info" ? ModuleNameOf(file) : file.Name;
                continue;
            }

            if (!set.ByName.TryAdd(file.Name, new JarClass(entry, file)))
            {
                classWarnings.Add($"{entry.Name} declares {file.Name}, which {set.ByName[file.Name].Entry.Name} already does; the first is kept.");
                continue;
            }

            set.Classes.Add(set.ByName[file.Name]);
        }

        if (failed > 0)
        {
            lock (_warnings)
            {
                _warnings.Add($"{failed} class file(s) could not be read and are left out{(failed > 20 ? "; the first 20 are named below" : string.Empty)}.");
            }
        }

        lock (_warnings)
        {
            _warnings.AddRange(classWarnings);
        }

        return set;
    }

    /// <summary>The name in a <c>module-info</c> class's <c>Module</c> attribute, which the parser keeps only by name.</summary>
    private static string? ModuleNameOf(ClassFile file)
    {
        // The Module attribute starts with the module's own Module constant; the parser does not read module
        // bodies, so find the one Module constant a module-info holds for itself — the first in the pool.
        for (int i = 1; i < file.Pool.Count; i++)
        {
            if (file.Pool.Get(i) is { Tag: ConstantTag.Module, A: var name })
            {
                return file.Pool.Utf8(name);
            }
        }

        return null;
    }

    private sealed class ClassSet
    {
        public List<JarClass> Classes { get; } = [];

        public Dictionary<string, JarClass> ByName { get; } = new(StringComparer.Ordinal);

        public int Versioned { get; set; }

        public string? Module { get; set; }
    }
}
