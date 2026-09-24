using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Spydate.Core.Binary;

namespace Spydate.Core.Android;

/// <summary>Thrown when bytes cannot be read as Android binary XML.</summary>
public sealed class BinaryXmlException : BinaryParseException
{
    public BinaryXmlException(string message) : base(message)
    {
    }
}

/// <summary>
/// Android's compiled XML (AXML), as <c>AndroidManifest.xml</c> and <c>res/**.xml</c> are stored in an APK: a
/// string pool, a map from attribute names to resource ids, and a stream of namespace, element and text chunks
/// whose attribute values are typed — strings, integers, booleans, colours, dimensions, references to resources.
/// It reads back as an <see cref="XDocument"/>, values written as aapt's dump writes them (<c>@0x7F040001</c> for a
/// reference, <c>16dp</c> for a dimension).
///
/// Every chunk is bounded by the one around it and every index checked, so a damaged or hostile file is a
/// <see cref="BinaryXmlException"/>, never a crash. Obfuscators empty attribute names in the pool; the resource
/// map still says which attribute each is, and the common <c>android:</c> ones are named from it.
/// </summary>
public static class BinaryXml
{
    public const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

    private const ushort XmlType = 0x0003;
    private const ushort StringPoolType = 0x0001;
    private const ushort ResourceMapType = 0x0180;
    private const ushort StartNamespaceType = 0x0100;
    private const ushort EndNamespaceType = 0x0101;
    private const ushort StartElementType = 0x0102;
    private const ushort EndElementType = 0x0103;
    private const ushort CDataType = 0x0104;

    /// <summary>How deep elements may nest before the file is taken for hostile.</summary>
    private const int MaxDepth = 256;

    /// <summary>Whether bytes start like compiled XML: an XML chunk header of eight bytes.</summary>
    public static bool IsBinaryXml(ReadOnlySpan<byte> head)
        => head.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(head) == XmlType && BinaryPrimitives.ReadUInt16LittleEndian(head[2..]) == 8;

    /// <summary>
    /// The document as text XML, indented, with the namespaces it declares. With the package's resource table, a
    /// reference is written by name — <c>@string/app_name</c> — rather than by id.
    /// </summary>
    public static string ToText(ReadOnlySpan<byte> data, Func<uint, string?>? names = null)
    {
        var document = Read(data, names);
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "    ", OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) };
        var sb = new StringBuilder();
        try
        {
            using var writer = XmlWriter.Create(new StringWriter(sb, CultureInfo.InvariantCulture), settings);
            document.Save(writer);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or InvalidOperationException)
        {
            throw new BinaryXmlException($"The document cannot be written as XML: {ex.Message}");
        }

        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }

    public static XDocument Read(ReadOnlySpan<byte> data, Func<uint, string?>? names = null)
    {
        try
        {
            return ReadDocument(data, names);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or InvalidOperationException)
        {
            // Names and namespaces a damaged file makes up that XML itself refuses.
            throw new BinaryXmlException($"The document is not valid XML: {ex.Message}");
        }
    }

    private static XDocument ReadDocument(ReadOnlySpan<byte> data, Func<uint, string?>? names)
    {
        if (!IsBinaryXml(data))
        {
            throw new BinaryXmlException("Not Android binary XML: it does not start with an XML chunk.");
        }

        int end = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[4..]), (uint)data.Length);
        string[] strings = [];
        uint[] resourceIds = [];
        var namespaces = new List<(string Prefix, string Uri)>();
        var pendingNamespaces = new List<(string Prefix, string Uri)>();
        var stack = new Stack<XElement>();
        var document = new XDocument();

        int at = 8;
        while (at + 8 <= end)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            if (size < 8 || headerSize < 8 || headerSize > size || at + size > end)
            {
                throw new BinaryXmlException($"The chunk at 0x{at:X} claims {size} bytes, which the file does not have.");
            }

            var chunk = data.Slice(at, (int)size);
            switch (type)
            {
                case StringPoolType:
                    strings = StringPool(chunk);
                    break;
                case ResourceMapType:
                    resourceIds = new uint[(size - headerSize) / 4];
                    for (int i = 0; i < resourceIds.Length; i++)
                    {
                        resourceIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(headerSize + (i * 4))..]);
                    }

                    break;
                case StartNamespaceType:
                {
                    var (prefix, uri) = (Text(strings, U4(chunk, headerSize)), Text(strings, U4(chunk, headerSize + 4)));
                    if (uri is not null)
                    {
                        namespaces.Add((prefix ?? string.Empty, uri));
                        pendingNamespaces.Add((prefix ?? string.Empty, uri));
                    }

                    break;
                }

                case EndNamespaceType:
                    if (namespaces.Count > 0)
                    {
                        namespaces.RemoveAt(namespaces.Count - 1);
                    }

                    break;
                case StartElementType:
                {
                    if (stack.Count >= MaxDepth)
                    {
                        throw new BinaryXmlException($"Elements nest more than {MaxDepth} deep.");
                    }

                    var element = Element(chunk, headerSize, strings, resourceIds, names);
                    // A namespace is declared by its prefix; one without a prefix gets a made-up one, since the element's
                    // own name decides the default namespace.
                    foreach (var (prefix, uri) in pendingNamespaces)
                    {
                        string declared = prefix.Length == 0 ? $"ns{namespaces.Count}" : XmlName(prefix);
                        if (element.Attribute(XNamespace.Xmlns + declared) is null)
                        {
                            element.SetAttributeValue(XNamespace.Xmlns + declared, uri);
                        }
                    }

                    pendingNamespaces.Clear();
                    if (stack.TryPeek(out var parent))
                    {
                        parent.Add(element);
                    }
                    else if (document.Root is null)
                    {
                        document.Add(element);
                    }
                    else
                    {
                        throw new BinaryXmlException("The file has a second root element.");
                    }

                    stack.Push(element);
                    break;
                }

                case EndElementType:
                    if (stack.Count == 0)
                    {
                        throw new BinaryXmlException($"An element ends at 0x{at:X} that never started.");
                    }

                    stack.Pop();
                    break;
                case CDataType:
                    if (stack.TryPeek(out var holder) && Text(strings, U4(chunk, headerSize)) is { } text)
                    {
                        holder.Add(new XText(text));
                    }

                    break;
            }

            at += (int)size;
        }

        if (document.Root is null)
        {
            throw new BinaryXmlException("The file has no element.");
        }

        return document;
    }

    private static XElement Element(ReadOnlySpan<byte> chunk, int headerSize, string[] strings, uint[] resourceIds, Func<uint, string?>? names)
    {
        string? ns = Text(strings, U4(chunk, headerSize));
        string name = XmlName(Text(strings, U4(chunk, headerSize + 4)) ?? "element");
        int attributeStart = U2(chunk, headerSize + 8);
        int attributeSize = U2(chunk, headerSize + 10);
        int count = U2(chunk, headerSize + 12);
        var element = new XElement(ns is null ? XName.Get(name) : XName.Get(name, ns));
        if (attributeSize < 20)
        {
            return element;
        }

        for (int i = 0; i < count; i++)
        {
            int a = headerSize + attributeStart + (i * attributeSize);
            if (a + 20 > chunk.Length)
            {
                throw new BinaryXmlException($"Attribute {i} of <{name}> runs past its element.");
            }

            string? attributeNs = Text(strings, U4(chunk, a));
            uint nameIndex = U4(chunk, a + 4);
            string? attributeName = Text(strings, nameIndex);
            if (string.IsNullOrEmpty(attributeName))
            {
                // An obfuscated manifest empties the name; the resource map still says which attribute it is.
                attributeName = nameIndex >= resourceIds.Length ? $"attr_{nameIndex}"
                    : AndroidAttributes.TryGetValue(resourceIds[nameIndex], out var known) ? known
                    : $"attr_0x{resourceIds[nameIndex]:X8}";
                attributeNs ??= nameIndex < resourceIds.Length && resourceIds[nameIndex] >> 16 == 0x0101 ? AndroidNamespace : null;
            }

            string? raw = Text(strings, U4(chunk, a + 8));
            byte dataType = chunk[a + 15];
            uint value = U4(chunk, a + 16);
            string text = dataType == 0x03 ? Text(strings, value) ?? raw ?? string.Empty : raw ?? Named(dataType, value, names) ?? Typed(dataType, value);
            var xname = attributeNs is null ? XName.Get(XmlName(attributeName)) : XName.Get(XmlName(attributeName), attributeNs);
            if (element.Attribute(xname) is null)
            {
                element.SetAttributeValue(xname, Clean(text));
            }
        }

        return element;
    }

    /// <summary>A reference or an attribute by the name the resource table gives it, when there is one.</summary>
    private static string? Named(byte type, uint data, Func<uint, string?>? names) => type switch
    {
        0x01 or 0x07 when data != 0 && names?.Invoke(data) is { } name => "@" + name,
        0x02 when names?.Invoke(data) is { } attribute => "?" + attribute,
        _ => null,
    };

    /// <summary>A typed attribute value, written as aapt writes it.</summary>
    internal static string Typed(byte type, uint data) => type switch
    {
        0x00 => string.Empty,
        0x01 => $"@0x{data:X8}",
        0x02 => $"?0x{data:X8}",
        0x04 => BitConverter.Int32BitsToSingle((int)data).ToString(CultureInfo.InvariantCulture),
        0x05 => Complex(data, ["px", "dp", "sp", "pt", "in", "mm"]),
        0x06 => Complex(data, ["%", "%p"], fraction: true),
        0x07 => $"@0x{data:X8}",
        0x10 => ((int)data).ToString(CultureInfo.InvariantCulture),
        0x11 => $"0x{data:X8}",
        0x12 => data != 0 ? "true" : "false",
        0x1C or 0x1D or 0x1E or 0x1F => $"#{data:X8}",
        _ => $"0x{data:X8}",
    };

    /// <summary>A dimension or fraction: a 24-bit mantissa with a radix, and a unit.</summary>
    private static string Complex(uint data, string[] units, bool fraction = false)
    {
        int mantissa = (int)(data & 0xFFFFFF00) >> 8;
        double[] radix = [1.0, 1.0 / (1 << 7), 1.0 / (1 << 15), 1.0 / (1 << 23)];
        double value = mantissa * radix[(data >> 4) & 3];
        if (fraction)
        {
            value *= 100;
        }

        int unit = (int)(data & 0xF);
        return value.ToString("0.######", CultureInfo.InvariantCulture) + (unit < units.Length ? units[unit] : $"(unit {unit})");
    }

    /// <summary>The string pool: UTF-16 or UTF-8 strings, found by offsets from where the strings start.</summary>
    internal static string[] StringPool(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < 28)
        {
            throw new BinaryXmlException("The string pool's header is cut off.");
        }

        uint count = U4(chunk, 8);
        uint flags = U4(chunk, 16);
        uint stringsStart = U4(chunk, 20);
        int headerSize = U2(chunk, 2);
        if ((long)headerSize + ((long)count * 4) > chunk.Length || stringsStart > chunk.Length)
        {
            throw new BinaryXmlException($"The string pool lists {count} strings, more than it could hold.");
        }

        bool utf8 = (flags & 0x100) != 0;
        var strings = new string[count];
        for (int i = 0; i < count; i++)
        {
            long at = stringsStart + (long)U4(chunk, headerSize + (i * 4));
            strings[i] = at < chunk.Length ? (utf8 ? Utf8String(chunk, (int)at) : Utf16String(chunk, (int)at)) : string.Empty;
        }

        return strings;
    }

    private static string Utf8String(ReadOnlySpan<byte> chunk, int at)
    {
        // Two lengths, each one or two bytes: UTF-16 units first, then the UTF-8 bytes.
        int Length(ref int p, ReadOnlySpan<byte> c)
        {
            if (p >= c.Length)
            {
                return 0;
            }

            int first = c[p++];
            return (first & 0x80) != 0 && p < c.Length ? ((first & 0x7F) << 8) | c[p++] : first;
        }

        _ = Length(ref at, chunk);
        int bytes = Length(ref at, chunk);
        return Encoding.UTF8.GetString(chunk.Slice(at, Math.Clamp(bytes, 0, chunk.Length - at)));
    }

    private static string Utf16String(ReadOnlySpan<byte> chunk, int at)
    {
        if (at + 2 > chunk.Length)
        {
            return string.Empty;
        }

        int length = U2(chunk, at);
        at += 2;
        if ((length & 0x8000) != 0 && at + 2 <= chunk.Length)
        {
            length = ((length & 0x7FFF) << 16) | U2(chunk, at);
            at += 2;
        }

        length = Math.Clamp(length, 0, (chunk.Length - at) / 2);
        return Encoding.Unicode.GetString(chunk.Slice(at, length * 2));
    }

    private static string? Text(string[] strings, uint index) => index < strings.Length ? strings[index] : null;

    /// <summary>A name XML allows: a damaged or obfuscated one keeps its letters and loses the rest.</summary>
    private static string XmlName(string name)
    {
        try
        {
            return XmlConvert.VerifyNCName(name);
        }
        catch (XmlException)
        {
            string cleaned = new(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.').ToArray());
            return cleaned.Length == 0 || !char.IsAsciiLetter(cleaned[0]) && cleaned[0] != '_' ? "_" + cleaned : cleaned;
        }
    }

    /// <summary>Text XML cannot hold every character a string pool can: those it cannot are escaped as \\uXXXX.</summary>
    private static string Clean(string text)
    {
        if (text.All(XmlConvert.IsXmlChar))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(XmlConvert.IsXmlChar(c) ? c.ToString() : $"\\u{(int)c:X4}");
        }

        return sb.ToString();
    }

    private static uint U4(ReadOnlySpan<byte> chunk, int at)
        => at >= 0 && at + 4 <= chunk.Length ? BinaryPrimitives.ReadUInt32LittleEndian(chunk[at..]) : throw new BinaryXmlException($"A read at 0x{at:X} runs past its chunk.");

    private static int U2(ReadOnlySpan<byte> chunk, int at)
        => at >= 0 && at + 2 <= chunk.Length ? BinaryPrimitives.ReadUInt16LittleEndian(chunk[at..]) : throw new BinaryXmlException($"A read at 0x{at:X} runs past its chunk.");

    /// <summary>A common <c>android:</c> attribute's name by its resource id, or null.</summary>
    internal static string? AndroidAttributeName(uint id) => AndroidAttributes.GetValueOrDefault(id);

    /// <summary>The manifest's common <c>android:</c> attributes by resource id, for when the pool's names are gone.</summary>
    private static readonly Dictionary<uint, string> AndroidAttributes = new()
    {
        [0x01010000] = "theme", [0x01010001] = "label", [0x01010002] = "icon", [0x01010003] = "name", [0x01010006] = "permission",
        [0x0101000F] = "debuggable", [0x01010010] = "exported", [0x01010024] = "value", [0x01010025] = "resource",
        [0x0101020C] = "minSdkVersion", [0x0101021B] = "versionCode", [0x0101021C] = "versionName", [0x01010270] = "targetSdkVersion",
        [0x01010271] = "maxSdkVersion", [0x01010280] = "allowBackup",
    };
}
