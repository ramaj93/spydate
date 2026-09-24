using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Spydate.Core.Binary;

namespace Spydate.Core.Android;

/// <summary>Thrown when bytes cannot be read as an Android resource table.</summary>
public sealed class ResourceTableException : BinaryParseException
{
    public ResourceTableException(string message) : base(message)
    {
    }
}

/// <summary>A package of resources: <c>0x7F</c> is the app's own, <c>0x01</c> the framework's.</summary>
public sealed record ResourcePackage(int Id, string Name, int Types, int Entries);

/// <summary>
/// One value of one resource in one configuration: <c>0x7F0F001E string/app_name</c> in the default configuration,
/// or again in <c>fr</c>. A style, array or plural is a bag: a parent and items, each an attribute and a value.
/// </summary>
public sealed record ResourceEntry(uint Id, string Type, string Name, string Config, byte DataType, uint Data, string? String)
{
    /// <summary>A bag's parent resource (0 when none) and items; null for a simple value.</summary>
    public (uint Parent, IReadOnlyList<(uint Name, byte DataType, uint Data, string? String)> Items)? Bag { get; init; }

    /// <summary><c>string/app_name</c>: the name a reference to it is written with, after the <c>@</c>.</summary>
    public string FullName => $"{Type}/{Name}";
}

/// <summary>
/// An APK's <c>resources.arsc</c>: every resource the app defines — its id, type, name, and a value per configuration
/// (language, density, API level…) — and the names references to them are written with. Read chunk by chunk, each
/// bounded by the one around it; a damaged table is a <see cref="ResourceTableException"/>, never a crash, and a
/// damaged part of a table is skipped with a warning.
/// </summary>
public sealed class ResourceTable
{
    private const ushort TableType = 0x0002;
    private const ushort StringPoolType = 0x0001;
    private const ushort PackageType = 0x0200;
    private const ushort TypeType = 0x0201;

    /// <summary>A real table has tens of thousands of entries; this bounds a hostile one's offsets tables.</summary>
    private const int MaxEntriesPerType = 1 << 16;

    private readonly Dictionary<uint, string> _names = [];
    private readonly List<string> _warnings = [];

    private ResourceTable()
    {
    }

    public IReadOnlyList<ResourcePackage> Packages { get; private set; } = [];

    /// <summary>Every value, by package, type and configuration as the table stores them.</summary>
    public IReadOnlyList<ResourceEntry> Entries { get; private set; } = [];

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Whether bytes start like a resource table: its chunk header of twelve bytes.</summary>
    public static bool IsResourceTable(ReadOnlySpan<byte> head)
        => head.Length >= 12 && BinaryPrimitives.ReadUInt16LittleEndian(head) == TableType && BinaryPrimitives.ReadUInt16LittleEndian(head[2..]) == 12;

    /// <summary>The name a resource id is written with, <c>string/app_name</c> — or, for another package than the app's, <c>pkg:string/name</c>; null when the table does not have it.</summary>
    public string? NameOf(uint id) => _names.GetValueOrDefault(id);

    /// <summary>A value as text: a string as itself, a reference by its name when the table has it, anything else as aapt writes it.</summary>
    public string Format(byte dataType, uint data, string? text) => dataType switch
    {
        0x03 => text ?? string.Empty,
        0x01 or 0x07 when data != 0 && NameOf(data) is { } name => "@" + name,
        0x02 when NameOf(data) is { } attribute => "?" + attribute,
        _ => BinaryXml.Typed(dataType, data),
    };

    /// <summary>An entry's value as one line: a simple value formatted, a bag as its parent and items.</summary>
    public string ValueText(ResourceEntry entry)
    {
        if (entry.Bag is not { } bag)
        {
            return Format(entry.DataType, entry.Data, entry.String);
        }

        var sb = new StringBuilder();
        if (bag.Parent != 0)
        {
            sb.Append("parent ").Append(Format(0x01, bag.Parent, null)).Append("; ");
        }

        sb.Append(bag.Items.Count.ToString(CultureInfo.InvariantCulture)).Append(bag.Items.Count == 1 ? " item" : " items");
        if (bag.Items.Count > 0)
        {
            sb.Append(": ").AppendJoin(", ", bag.Items.Take(8).Select(i => $"{ItemName(i.Name)}={Format(i.DataType, i.Data, i.String)}"));
            if (bag.Items.Count > 8)
            {
                sb.Append(", …");
            }
        }

        return sb.ToString();
    }

    /// <summary>A bag item's key: an attribute by name, or the quantities and indices plurals and arrays use.</summary>
    private string ItemName(uint name) => name switch
    {
        0x01000000 => "^type",
        0x01000001 => "^min",
        0x01000002 => "^max",
        0x01000003 => "^l10n",
        0x01000004 => "other",
        0x01000005 => "zero",
        0x01000006 => "one",
        0x01000007 => "two",
        0x01000008 => "few",
        0x01000009 => "many",
        _ when NameOf(name) is { } known => known,
        _ when BinaryXml.AndroidAttributeName(name) is { } android => "android:attr/" + android,
        _ => $"0x{name:X8}",
    };

    /// <summary>The whole table as text: a line per value, id, name, configuration and value.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var package in Packages)
        {
            sb.Append(CultureInfo.InvariantCulture, $"package 0x{package.Id:X2} {package.Name}: {package.Types} types, {package.Entries} values\n");
        }

        foreach (var entry in Entries)
        {
            sb.Append(CultureInfo.InvariantCulture, $"0x{entry.Id:X8} {entry.FullName}{(entry.Config.Length == 0 ? string.Empty : $" [{entry.Config}]")} = {ValueText(entry)}\n");
        }

        foreach (string warning in _warnings)
        {
            sb.Append("warning: ").Append(warning).Append('\n');
        }

        return sb.ToString();
    }

    public static ResourceTable Parse(ReadOnlySpan<byte> data)
    {
        if (!IsResourceTable(data))
        {
            throw new ResourceTableException("Not an Android resource table: it does not start with a table chunk.");
        }

        var table = new ResourceTable();
        int end = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[4..]), (uint)data.Length);
        string[] values = [];
        var packages = new List<ResourcePackage>();
        var entries = new List<ResourceEntry>();
        int at = 12;
        while (at + 8 <= end)
        {
            var (type, headerSize, size) = Header(data, at, end);
            var chunk = data.Slice(at, size);
            try
            {
                switch (type)
                {
                    case StringPoolType:
                        values = BinaryXml.StringPool(chunk);
                        break;
                    case PackageType:
                        packages.Add(table.Package(chunk, headerSize, values, entries));
                        break;
                }
            }
            catch (BinaryParseException ex)
            {
                table._warnings.Add($"The chunk at 0x{at:X} could not be read: {ex.Message}");
            }

            at += size;
        }

        table.Packages = packages;
        table.Entries = entries;
        return table;
    }

    private ResourcePackage Package(ReadOnlySpan<byte> chunk, int headerSize, string[] values, List<ResourceEntry> entries)
    {
        if (headerSize < 284)
        {
            throw new ResourceTableException($"A package header of {headerSize} bytes is too short to hold one.");
        }

        int id = (int)U4(chunk, 8);
        string name = Encoding.Unicode.GetString(chunk.Slice(12, 256)).Split('\0')[0];
        uint typeStringsAt = U4(chunk, 268);
        uint keyStringsAt = U4(chunk, 276);
        int typeIdOffset = headerSize >= 288 ? (int)U4(chunk, 284) : 0;
        string[] typeNames = [];
        string[] keys = [];
        int before = entries.Count;
        var types = new HashSet<int>();

        int at = headerSize;
        while (at + 8 <= chunk.Length)
        {
            var (type, subHeader, size) = Header(chunk, at, chunk.Length);
            var sub = chunk.Slice(at, size);
            try
            {
                switch (type)
                {
                    case StringPoolType when at == typeStringsAt:
                        typeNames = BinaryXml.StringPool(sub);
                        break;
                    case StringPoolType when at == keyStringsAt:
                        keys = BinaryXml.StringPool(sub);
                        break;
                    case TypeType:
                        types.Add(Type(sub, subHeader, id, typeIdOffset, typeNames, keys, values, entries));
                        break;
                }
            }
            catch (BinaryParseException ex)
            {
                _warnings.Add($"Package 0x{id:X2}: the chunk at 0x{at:X} could not be read: {ex.Message}");
            }

            at += size;
        }

        return new ResourcePackage(id, name, types.Count, entries.Count - before);
    }

    /// <summary>One type's values in one configuration; returns the type's id.</summary>
    private int Type(ReadOnlySpan<byte> chunk, int headerSize, int packageId, int typeIdOffset, string[] typeNames, string[] keys, string[] values, List<ResourceEntry> entries)
    {
        if (headerSize < 20)
        {
            throw new ResourceTableException("A type chunk's header is cut off.");
        }

        int typeId = chunk[8];
        byte flags = chunk[9];
        uint count = U4(chunk, 12);
        uint entriesStart = U4(chunk, 16);
        if (count > MaxEntriesPerType || entriesStart > chunk.Length)
        {
            throw new ResourceTableException($"Type {typeId} claims {count} entries starting at 0x{entriesStart:X}, which its chunk cannot hold.");
        }

        string typeName = typeId - 1 - typeIdOffset is var t and >= 0 && t < typeNames.Length ? typeNames[t] : $"type{typeId}";
        string config = Config(chunk[20..headerSize]);
        bool sparse = (flags & 0x01) != 0;
        bool offset16 = (flags & 0x02) != 0;

        for (int i = 0; i < count; i++)
        {
            int index;
            uint offset;
            if (sparse)
            {
                index = U2(chunk, headerSize + (i * 4));
                offset = (uint)U2(chunk, headerSize + (i * 4) + 2) * 4;
            }
            else if (offset16)
            {
                index = i;
                int packed = U2(chunk, headerSize + (i * 2));
                if (packed == 0xFFFF)
                {
                    continue;
                }

                offset = (uint)packed * 4;
            }
            else
            {
                index = i;
                offset = U4(chunk, headerSize + (i * 4));
                if (offset == 0xFFFFFFFF)
                {
                    continue;
                }
            }

            long entryAt = entriesStart + (long)offset;
            if (entryAt + 8 > chunk.Length)
            {
                throw new ResourceTableException($"Entry {index} of type {typeName} lies past its chunk.");
            }

            uint id = ((uint)packageId << 24) | ((uint)typeId << 16) | (uint)index;
            entries.Add(Entry(chunk, (int)entryAt, id, typeName, config, keys, values));
            string full = $"{typeName}/{entries[^1].Name}";
            _names.TryAdd(id, packageId == 0x7F || packageId == 0 ? full : $"{PackageName(packageId)}:{full}");
        }

        return typeId;
    }

    private static string PackageName(int id) => id == 0x01 ? "android" : $"0x{id:X2}";

    private static ResourceEntry Entry(ReadOnlySpan<byte> chunk, int at, uint id, string type, string config, string[] keys, string[] values)
    {
        int size = U2(chunk, at);
        int flags = U2(chunk, at + 2);
        string Key(uint k) => k < keys.Length ? keys[k] : $"0x{id:X8}";

        // Android 14's compact entry: a 16-bit key, the value's type in the flags' high byte, and the value itself.
        if ((flags & 0x0008) != 0)
        {
            byte compactType = (byte)(flags >> 8);
            uint compactData = U4(chunk, at + 4);
            return new ResourceEntry(id, type, Key((uint)size), config, compactType, compactData, StringOf(compactType, compactData, values));
        }

        string name = Key(U4(chunk, at + 4));
        if ((flags & 0x0001) == 0)
        {
            byte dataType = chunk.Length > at + size + 3 ? chunk[at + size + 3] : throw new ResourceTableException($"The value of {type}/{name} lies past its chunk.");
            uint data = U4(chunk, at + size + 4);
            return new ResourceEntry(id, type, name, config, dataType, data, StringOf(dataType, data, values));
        }

        // A bag: its parent, then items of an attribute id and a value.
        uint parent = U4(chunk, at + 8);
        uint count = U4(chunk, at + 12);
        if (count > MaxEntriesPerType || at + size + ((long)count * 12) > chunk.Length)
        {
            throw new ResourceTableException($"{type}/{name} claims {count} items, more than its chunk holds.");
        }

        var items = new List<(uint, byte, uint, string?)>((int)count);
        for (int i = 0; i < count; i++)
        {
            int item = at + size + (i * 12);
            byte dataType = chunk[item + 7];
            uint data = U4(chunk, item + 8);
            items.Add((U4(chunk, item), dataType, data, StringOf(dataType, data, values)));
        }

        return new ResourceEntry(id, type, name, config, 0, 0, null) { Bag = (parent, items) };
    }

    private static string? StringOf(byte dataType, uint data, string[] values) => dataType == 0x03 && data < values.Length ? values[data] : null;

    /// <summary>
    /// A configuration as its qualifiers, in the order resource directories write them: <c>fr-rCA</c>, <c>land</c>,
    /// <c>xhdpi</c>, <c>v21</c>. The default configuration is empty.
    /// </summary>
    internal static string Config(ReadOnlySpan<byte> bytes)
    {
        byte[] config = bytes.ToArray();
        if (config.Length < 8)
        {
            return string.Empty;
        }

        int size = Math.Min((int)BinaryPrimitives.ReadUInt32LittleEndian(config), config.Length);
        byte Byte(int at) => at < size ? config[at] : (byte)0;
        int Short(int at) => at + 2 <= size ? BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(at)) : 0;
        var parts = new List<string>();

        if (Short(4) is var mcc and not 0)
        {
            parts.Add($"mcc{mcc:D3}");
        }

        if (Short(6) is var mnc and not 0)
        {
            parts.Add(mnc == 0xFFFF ? "mnc00" : $"mnc{mnc:D2}");
        }

        string language = Packed(Byte(8), Byte(9), 'a');
        if (language.Length > 0)
        {
            string region = Packed(Byte(10), Byte(11), '0');
            parts.Add(region.Length > 0 ? $"{language}-r{region}" : language);
        }

        int screenLayout = Byte(28);
        parts.AddRange((screenLayout & 0xC0) switch { 0x40 => ["ldltr"], 0x80 => ["ldrtl"], _ => [] });
        if (Short(30) is var sw and not 0)
        {
            parts.Add($"sw{sw}dp");
        }

        if (Short(32) is var w and not 0)
        {
            parts.Add($"w{w}dp");
        }

        if (Short(34) is var h and not 0)
        {
            parts.Add($"h{h}dp");
        }

        parts.AddRange((screenLayout & 0x0F) switch { 1 => ["small"], 2 => ["normal"], 3 => ["large"], 4 => ["xlarge"], _ => [] });
        parts.AddRange((screenLayout & 0x30) switch { 0x10 => ["notlong"], 0x20 => ["long"], _ => [] });
        parts.AddRange((Byte(48) & 0x03) switch { 1 => ["notround"], 2 => ["round"], _ => [] });
        parts.AddRange(Byte(12) switch { 1 => ["port"], 2 => ["land"], 3 => ["square"], _ => [] });
        int uiMode = Byte(29);
        parts.AddRange((uiMode & 0x0F) switch { 2 => ["desk"], 3 => ["car"], 4 => ["television"], 5 => ["appliance"], 6 => ["watch"], 7 => ["vrheadset"], _ => [] });
        parts.AddRange((uiMode & 0x30) switch { 0x10 => ["notnight"], 0x20 => ["night"], _ => [] });
        parts.AddRange(Short(14) switch
        {
            0 => [],
            120 => ["ldpi"],
            160 => ["mdpi"],
            213 => ["tvdpi"],
            240 => ["hdpi"],
            320 => ["xhdpi"],
            480 => ["xxhdpi"],
            640 => ["xxxhdpi"],
            0xFFFE => ["anydpi"],
            0xFFFF => ["nodpi"],
            var dpi => [$"{dpi}dpi"],
        });
        parts.AddRange(Byte(13) switch { 1 => ["notouch"], 3 => ["finger"], 2 => ["stylus"], _ => [] });
        parts.AddRange((Byte(18) & 0x03) switch { 1 => ["keysexposed"], 2 => ["keyshidden"], 3 => ["keyssoft"], _ => [] });
        parts.AddRange(Byte(16) switch { 1 => ["nokeys"], 2 => ["qwerty"], 3 => ["12key"], _ => [] });
        parts.AddRange(Byte(17) switch { 1 => ["nonav"], 2 => ["dpad"], 3 => ["trackball"], 4 => ["wheel"], _ => [] });
        if (Short(24) is var sdk and not 0)
        {
            parts.Add($"v{sdk}");
        }

        return string.Join('-', parts);
    }

    /// <summary>A language or region: two letters, or three packed into two bytes when the first byte's high bit is set.</summary>
    private static string Packed(byte first, byte second, char zero)
    {
        if (first == 0)
        {
            return string.Empty;
        }

        if ((first & 0x80) == 0)
        {
            return new string([(char)first, (char)second]).TrimEnd('\0');
        }

        int a = second & 0x1F;
        int b = ((second & 0xE0) >> 5) | ((first & 0x03) << 3);
        int c = (first & 0x7C) >> 2;
        return new string([(char)(zero + a), (char)(zero + b), (char)(zero + c)]);
    }

    private static (ushort Type, int HeaderSize, int Size) Header(ReadOnlySpan<byte> data, int at, int end)
    {
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
        if (size < 8 || headerSize < 8 || headerSize > size || at + (long)size > end)
        {
            throw new ResourceTableException($"The chunk at 0x{at:X} claims {size} bytes, which the table does not have.");
        }

        return (type, headerSize, (int)size);
    }

    private static uint U4(ReadOnlySpan<byte> chunk, int at)
        => at >= 0 && at + 4 <= chunk.Length ? BinaryPrimitives.ReadUInt32LittleEndian(chunk[at..]) : throw new ResourceTableException($"A read at 0x{at:X} runs past its chunk.");

    private static int U2(ReadOnlySpan<byte> chunk, int at)
        => at >= 0 && at + 2 <= chunk.Length ? BinaryPrimitives.ReadUInt16LittleEndian(chunk[at..]) : throw new ResourceTableException($"A read at 0x{at:X} runs past its chunk.");
}
