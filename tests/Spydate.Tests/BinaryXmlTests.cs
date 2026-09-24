using System.Text;
using System.Xml.Linq;
using Spydate.Core.Android;

namespace Spydate.Tests;

/// <summary>
/// Android's compiled XML: a hand-built manifest with typed attributes — a string, an integer, a boolean, a
/// reference, a dimension — an attribute whose name an obfuscator emptied, and hostile input, which must be a
/// <see cref="BinaryXmlException"/> and nothing else.
/// </summary>
public sealed class BinaryXmlTests
{
    private const string Android = BinaryXml.AndroidNamespace;

    /// <summary>A string pool (UTF-16), a resource map, a namespace, <c>&lt;manifest&gt;</c> holding <c>&lt;uses-sdk&gt;</c>.</summary>
    internal static byte[] Manifest()
    {
        string[] strings = ["versionCode", "", "debuggable", "margin", "android", Android, "manifest", "package", "com.example.app", "uses-sdk", "label", "minSdkVersion"];
        uint[] resourceIds = [0x0101021B, 0x0101020C, 0x0101000F];
        const uint None = 0xFFFFFFFF;

        var body = new List<byte>();
        body.AddRange(StringPool(strings));
        body.AddRange(Chunk(0x0180, 8, resourceIds.SelectMany(BitConverter.GetBytes).ToArray()));
        body.AddRange(Chunk(0x0100, 16, [.. Node(), .. U4(4), .. U4(5)]));
        body.AddRange(Element(6, [
            (5, 0, 0x10, 7),         // android:versionCode="7" (named in the pool)
            (5, 1, 0x10, 26),        // an emptied name: the resource map says minSdkVersion
            (5, 2, 0x12, 0xFFFFFFFF), // android:debuggable="true"
            (5, 3, 0x05, (16 << 8) | 1), // android:margin="16dp"
            (5, 10, 0x01, 0x7F0F001E), // android:label="@0x7F0F001E"
            (None, 7, 0x03, 8),      // package="com.example.app"
        ]));
        body.AddRange(Element(9, []));
        body.AddRange(Chunk(0x0103, 16, [.. Node(), .. U4(None), .. U4(9)]));
        body.AddRange(Chunk(0x0103, 16, [.. Node(), .. U4(None), .. U4(6)]));
        body.AddRange(Chunk(0x0101, 16, [.. Node(), .. U4(4), .. U4(5)]));
        return Chunk(0x0003, 8, [.. body]);
    }

    private static byte[] Element(uint name, (uint Ns, uint Name, byte Type, uint Data)[] attributes)
    {
        var ext = new List<byte>();
        ext.AddRange(U4(0xFFFFFFFF));
        ext.AddRange(U4(name));
        ext.AddRange(U2(20));
        ext.AddRange(U2(20));
        ext.AddRange(U2(attributes.Length));
        ext.AddRange(U2(0));
        ext.AddRange(U2(0));
        ext.AddRange(U2(0));
        foreach (var a in attributes)
        {
            ext.AddRange(U4(a.Ns));
            ext.AddRange(U4(a.Name));
            ext.AddRange(U4(a.Type == 0x03 ? a.Data : 0xFFFFFFFF));
            ext.AddRange(U2(8));
            ext.Add(0);
            ext.Add(a.Type);
            ext.AddRange(U4(a.Data));
        }

        return Chunk(0x0102, 16, [.. Node(), .. ext]);
    }

    internal static byte[] StringPool(string[] strings)
    {
        var offsets = new List<byte>();
        var data = new List<byte>();
        foreach (string s in strings)
        {
            offsets.AddRange(U4((uint)data.Count));
            data.AddRange(U2(s.Length));
            data.AddRange(Encoding.Unicode.GetBytes(s));
            data.AddRange(U2(0));
        }

        while (data.Count % 4 != 0)
        {
            data.Add(0);
        }

        var header = new List<byte>();
        header.AddRange(U4((uint)strings.Length));
        header.AddRange(U4(0));
        header.AddRange(U4(0));
        header.AddRange(U4((uint)(28 + offsets.Count)));
        header.AddRange(U4(0));
        return Chunk(0x0001, 28, [.. header, .. offsets, .. data]);
    }

    private static byte[] Node() => [.. U4(1), .. U4(0xFFFFFFFF)];

    internal static byte[] Chunk(ushort type, ushort headerSize, byte[] rest)
        => [.. U2(type), .. U2(headerSize), .. U4((uint)(8 + rest.Length)), .. rest];

    internal static byte[] U2(int v) => BitConverter.GetBytes((ushort)v);

    internal static byte[] U4(uint v) => BitConverter.GetBytes(v);

    [Fact]
    public void AManifestReadsBackWithItsTypedValues()
    {
        var document = BinaryXml.Read(Manifest());
        XNamespace android = Android;
        var manifest = document.Root!;

        Assert.Equal("manifest", manifest.Name.LocalName);
        Assert.Equal("com.example.app", (string?)manifest.Attribute("package"));
        Assert.Equal("7", (string?)manifest.Attribute(android + "versionCode"));
        Assert.Equal("26", (string?)manifest.Attribute(android + "minSdkVersion"));
        Assert.Equal("true", (string?)manifest.Attribute(android + "debuggable"));
        Assert.Equal("16dp", (string?)manifest.Attribute(android + "margin"));
        Assert.Equal("@0x7F0F001E", (string?)manifest.Attribute(android + "label"));
        Assert.Equal("uses-sdk", Assert.Single(manifest.Elements()).Name.LocalName);

        string text = BinaryXml.ToText(Manifest());
        Assert.Contains("xmlns:android=\"http://schemas.android.com/apk/res/android\"", text, StringComparison.Ordinal);
        Assert.Contains("android:versionCode=\"7\"", text, StringComparison.Ordinal);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TextXmlIsNotBinaryXml()
    {
        byte[] text = Encoding.UTF8.GetBytes("<manifest package=\"x\"/>");
        Assert.False(BinaryXml.IsBinaryXml(text));
        Assert.Throws<BinaryXmlException>(() => BinaryXml.Read(text));
    }

    [Fact]
    public void EveryTruncationAndRandomDamageIsABinaryXmlError()
    {
        byte[] whole = Manifest();
        for (int length = 0; length < whole.Length; length++)
        {
            Try(whole.AsSpan(0, length).ToArray());
        }

        var random = new Random(20260924);
        for (int round = 0; round < 3000; round++)
        {
            byte[] bytes = (byte[])whole.Clone();
            for (int k = 0; k < 1 + random.Next(6); k++)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }

            Try(bytes);
        }

        static void Try(byte[] bytes)
        {
            try
            {
                _ = BinaryXml.ToText(bytes);
            }
            catch (BinaryXmlException)
            {
            }
        }
    }
}
