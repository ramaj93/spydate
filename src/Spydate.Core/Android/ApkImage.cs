using System.Globalization;
using System.Xml.Linq;
using Spydate.Core.Archive;
using Spydate.Core.Binary;
using Spydate.Core.Dex;

namespace Spydate.Core.Android;

/// <summary>One DEX file of an APK, parsed, with the entry it came from.</summary>
public sealed record ApkDex(ArchiveEntry Entry, DexFile File);

/// <summary>One class of an APK: its definition and the DEX file that holds it.</summary>
public sealed record ApkClass(DexClass Class, ApkDex Source);

/// <summary>A native library an APK carries for an ABI: <c>lib/arm64-v8a/libfoo.so</c>.</summary>
public sealed record NativeLibrary(ArchiveEntry Entry, string Abi, string Name);

/// <summary>An app component the manifest declares: an activity, service, receiver or provider, by its class name.</summary>
public sealed record AndroidComponent(string Kind, string Name, bool Exported);

/// <summary>What the manifest says about the app, as far as a reader wants it at a glance.</summary>
public sealed record ApkManifest(
    string? Package,
    string? VersionCode,
    string? VersionName,
    string? MinSdk,
    string? TargetSdk,
    string? CompileSdk,
    string? Application,
    bool Debuggable,
    string? MainActivity,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<AndroidComponent> Components)
{
    /// <summary>Reads the facts from a decoded manifest; class names written <c>.Main</c> are completed with the package.</summary>
    public static ApkManifest From(XDocument document)
    {
        XNamespace android = BinaryXml.AndroidNamespace;
        var root = document.Root!;
        string? package = (string?)root.Attribute("package");
        string? Name(XElement e) => (string?)e.Attribute(android + "name") is { } name ? Qualify(name, package) : null;

        var sdk = root.Element("uses-sdk");
        var application = root.Element("application");
        var components = new List<AndroidComponent>();
        string? main = null;
        foreach (var element in application?.Elements() ?? [])
        {
            string kind = element.Name.LocalName;
            if (kind is not ("activity" or "activity-alias" or "service" or "receiver" or "provider") || Name(element) is not { } name)
            {
                continue;
            }

            components.Add(new AndroidComponent(kind, name, (string?)element.Attribute(android + "exported") == "true"));
            bool launcher = element.Elements("intent-filter").Any(f =>
                f.Elements("action").Any(a => (string?)a.Attribute(android + "name") == "android.intent.action.MAIN")
                && f.Elements("category").Any(c => (string?)c.Attribute(android + "name") == "android.intent.category.LAUNCHER"));
            if (launcher && main is null)
            {
                main = kind == "activity-alias" && (string?)element.Attribute(android + "targetActivity") is { } target ? Qualify(target, package) : name;
            }
        }

        return new ApkManifest(
            package,
            (string?)root.Attribute(android + "versionCode"),
            (string?)root.Attribute(android + "versionName"),
            (string?)sdk?.Attribute(android + "minSdkVersion"),
            (string?)sdk?.Attribute(android + "targetSdkVersion"),
            (string?)root.Attribute(android + "compileSdkVersion") ?? (string?)root.Attribute("platformBuildVersionCode"),
            application is null ? null : Name(application),
            (string?)application?.Attribute(android + "debuggable") == "true",
            main,
            root.Elements().Where(e => e.Name.LocalName is "uses-permission" or "uses-permission-sdk-23")
                .Select(e => (string?)e.Attribute(android + "name")).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
            components);
    }

    private static string Qualify(string name, string? package)
        => name.StartsWith('.') && package is not null ? package + name : !name.Contains('.', StringComparison.Ordinal) && package is not null ? $"{package}.{name}" : name;
}

/// <summary>
/// An Android package: a zip holding the app's code as DEX files, its manifest and resources compiled to binary
/// XML and a resource table, and native libraries per ABI — or one DEX file on its own, which is the same program
/// without the package around it (<see cref="IsPackage"/> false, no <see cref="Archive"/>).
///
/// Like a JAR, it is an <see cref="IBinaryImage"/> without an address space — the architecture is
/// <see cref="Architecture.Unknown"/>, which keeps native analysis from starting — and its program is its
/// bytecode: the classes of <c>classes.dex</c>, <c>classes2.dex</c> and so on, read on first use. A native library
/// inside is listed, and opens as the ELF it is.
/// </summary>
public sealed class ApkImage : IBinaryImage
{
    /// <summary>A DEX file is bounded by 16-bit method and field counts per file; a quarter gigabyte is far past any real one.</summary>
    public const int MaxDexSize = 256 * 1024 * 1024;

    /// <summary>A manifest is tens of kilobytes; eight megabytes still bounds a hostile one.</summary>
    private const int MaxManifestSize = 8 * 1024 * 1024;

    public const string ManifestEntry = "AndroidManifest.xml";

    private readonly List<string> _warnings = [];
    private readonly Lazy<CodeSet> _code;

    private ApkImage(string? path, ReadOnlyMemory<byte> data, ZipArchiveFile? archive)
    {
        Path = path;
        FileName = path is null ? "(memory)" : System.IO.Path.GetFileName(path);
        Data = data;
        Archive = archive;
        _code = new Lazy<CodeSet>(ReadCode, LazyThreadSafetyMode.ExecutionAndPublication);
        _classFiles = new Lazy<IReadOnlyList<Jvm.JarClass>>(
            () => Classes.Select(c => new Jvm.JarClass(c.Source.Entry, DexClasses.ToClassFile(c.Class))).ToList(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _resources = new Lazy<ResourceTable?>(ReadResources, LazyThreadSafetyMode.ExecutionAndPublication);

        // A DEX file on its own: its code, and a hash of it to key its project by.
        if (archive is null)
        {
            Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.Span)[..16]);
            NativeLibraries = [];
            return;
        }

        _warnings.AddRange(archive.Warnings);
        Fingerprint = archive.DirectoryHash();

        if (archive.Find(ManifestEntry) is { } manifest)
        {
            try
            {
                byte[] bytes = archive.Read(manifest, MaxManifestSize);
                ManifestDocument = BinaryXml.Read(bytes);
                Manifest = ApkManifest.From(ManifestDocument);
            }
            catch (BinaryParseException ex)
            {
                _warnings.Add($"The manifest could not be read: {ex.Message}");
            }
        }
        else
        {
            _warnings.Add("The package has no AndroidManifest.xml.");
        }

        NativeLibraries = archive.Entries
            .Where(e => !e.IsDirectory && e.Name.StartsWith("lib/", StringComparison.Ordinal) && e.Name.EndsWith(".so", StringComparison.Ordinal) && e.Name.Count(c => c == '/') == 2)
            .Select(e => new NativeLibrary(e, e.Name.Split('/')[1], e.Name.Split('/')[2]))
            .ToList();
    }

    private readonly Lazy<IReadOnlyList<Jvm.JarClass>> _classFiles;
    private readonly Lazy<ResourceTable?> _resources;

    public static ApkImage Load(string path)
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

        return new ApkImage(path, bytes, ZipArchiveFile.Open(bytes));
    }

    /// <summary>An APK held in memory, as tests build them.</summary>
    public static ApkImage FromBytes(byte[] bytes, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new ApkImage(path, bytes, ZipArchiveFile.Open(bytes));
    }

    /// <summary>A DEX file on its own, as <c>d8</c> writes one or a tool drops one: the app's code without its package.</summary>
    public static ApkImage LoadDex(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DexFormatException($"Cannot read '{path}': {ex.Message}");
        }

        return FromDexBytes(bytes, path);
    }

    /// <summary>A DEX file held in memory.</summary>
    public static ApkImage FromDexBytes(byte[] bytes, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaxDexSize)
        {
            throw new DexFormatException($"The file is {bytes.Length:N0} bytes, more than a DEX file can be.");
        }

        // Read now: a file that says it is DEX and is not one is refused as it is opened, not when its code is shown.
        var image = new ApkImage(path, bytes, null);
        _ = image.Classes;
        return image;
    }

    /// <summary>The package's zip; null for a DEX file on its own.</summary>
    public ZipArchiveFile? Archive { get; }

    /// <summary>A package, rather than one DEX file on its own.</summary>
    public bool IsPackage => Archive is not null;

    /// <summary>
    /// The resource table, <c>resources.arsc</c>: every resource's name and values. Read on first use; null when the
    /// package has none or it could not be read (said in <see cref="Warnings"/>).
    /// </summary>
    public ResourceTable? Resources => _resources.Value;

    /// <summary>The name a resource id is written with, <c>string/app_name</c>, from the resource table; null without one.</summary>
    public string? ResourceName(uint id) => Resources?.NameOf(id);

    /// <summary>The manifest decoded from binary XML; null when it is missing or could not be read.</summary>
    public XDocument? ManifestDocument { get; }

    public ApkManifest? Manifest { get; }

    /// <summary>The DEX files in the order Android loads them: <c>classes.dex</c>, then <c>classes2.dex</c>, <c>classes3.dex</c>…</summary>
    public IReadOnlyList<ApkDex> DexFiles => _code.Value.Dex;

    /// <summary>Every class, the first definition of a name winning as the runtime's class loader has it.</summary>
    public IReadOnlyList<ApkClass> Classes => _code.Value.Classes;

    /// <summary>The class with this internal name (<c>com/example/Main</c>), or null.</summary>
    public ApkClass? FindClass(string internalName) => _code.Value.ByName.GetValueOrDefault(internalName);

    /// <summary>
    /// Every class as a Java class file (<see cref="DexClasses"/>), each with the DEX entry it came from, so a reading
    /// built for JARs reads it. Translated on first use.
    /// </summary>
    public IReadOnlyList<Jvm.JarClass> ClassFiles => _classFiles.Value;

    /// <summary>The DEX format version of the newest file: <c>035</c>, <c>038</c>…</summary>
    public int DexVersion => DexFiles.Count == 0 ? 0 : DexFiles.Max(d => d.File.Version);

    public IReadOnlyList<NativeLibrary> NativeLibraries { get; }

    /// <summary>The ABIs the native libraries are built for: <c>arm64-v8a</c>, <c>x86_64</c>…</summary>
    public IReadOnlyList<string> Abis => NativeLibraries.Select(l => l.Abi).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    public const string ResourceTableEntry = "resources.arsc";

    public bool HasResourceTable => Archive?.Find(ResourceTableEntry) is not null;

    public BinaryFormat Format => IsPackage ? BinaryFormat.Apk : BinaryFormat.Dex;

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

    /// <summary>A library (an AAR-like package, a feature split, a DEX file alone) unless the manifest names an activity that launches.</summary>
    public bool IsLibrary => Manifest?.MainActivity is null;

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

    /// <summary>What the archive, manifest, resource table and DEX files tolerated. Reading this reads the code and the resources.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            _ = _code.Value;
            _ = _resources.Value;
            lock (_warnings)
            {
                return [.. _warnings];
            }
        }
    }

    /// <summary>A hash of the zip's central directory, which holds every entry's name, size and CRC.</summary>
    public string Fingerprint { get; }

    private sealed class CodeSet
    {
        public List<ApkDex> Dex { get; } = [];

        public List<ApkClass> Classes { get; } = [];

        public Dictionary<string, ApkClass> ByName { get; } = new(StringComparer.Ordinal);
    }

    private ResourceTable? ReadResources()
    {
        if (Archive?.Find(ResourceTableEntry) is not { } entry)
        {
            return null;
        }

        try
        {
            var table = ResourceTable.Parse(Archive.Read(entry, MaxDexSize));
            lock (_warnings)
            {
                _warnings.AddRange(table.Warnings.Select(w => $"{ResourceTableEntry}: {w}"));
            }

            return table;
        }
        catch (BinaryParseException ex)
        {
            lock (_warnings)
            {
                _warnings.Add($"{ResourceTableEntry} could not be read: {ex.Message}");
            }

            return null;
        }
    }

    private CodeSet ReadCode()
    {
        var set = new CodeSet();

        // A DEX file on its own is the one entry of a package that is not there.
        var dexEntries = Archive is null
            ? [new ArchiveEntry(0, FileName, Data.Length, Data.Length, 0, 0, null, false, 0)]
            : Archive.Entries.Where(e => !e.IsDirectory && DexOrder(e.Name) is not null).OrderBy(e => DexOrder(e.Name)).ToList();
        foreach (var entry in dexEntries)
        {
            try
            {
                var dex = new ApkDex(entry, DexFile.Parse(Archive is null ? Data.ToArray() : Archive.Read(entry, MaxDexSize)));
                set.Dex.Add(dex);
                lock (_warnings)
                {
                    _warnings.AddRange(dex.File.Warnings.Select(w => $"{entry.Name}: {w}"));
                }

                int duplicates = 0;
                foreach (var definition in dex.File.Classes)
                {
                    var apkClass = new ApkClass(definition, dex);
                    if (set.ByName.TryAdd(definition.Name, apkClass))
                    {
                        set.Classes.Add(apkClass);
                    }
                    else
                    {
                        duplicates++;
                    }
                }

                if (duplicates > 0)
                {
                    lock (_warnings)
                    {
                        _warnings.Add($"{entry.Name}: {duplicates} class(es) defined again; the first definition is the one that loads.");
                    }
                }
            }
            catch (BinaryParseException ex)
            {
                // A DEX file alone that does not parse is not a DEX file: said as the error opening it, not a warning.
                if (Archive is null)
                {
                    throw;
                }

                lock (_warnings)
                {
                    _warnings.Add($"{entry.Name} could not be read: {ex.Message}");
                }
            }
        }

        return set;
    }

    /// <summary><c>classes.dex</c> is 1, <c>classesN.dex</c> is N; any other name is not loaded code.</summary>
    private static int? DexOrder(string name)
    {
        if (name == "classes.dex")
        {
            return 1;
        }

        return name.StartsWith("classes", StringComparison.Ordinal) && name.EndsWith(".dex", StringComparison.Ordinal)
               && int.TryParse(name.AsSpan(7, name.Length - 11), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 2
            ? n
            : null;
    }
}
