using System.Text;
using Spydate.Core.Android;
using static Spydate.Tests.BinaryXmlTests;

namespace Spydate.Tests;

/// <summary>
/// An APK's resource table, hand-built: a string in the default configuration and again in <c>fr-rCA-xhdpi-v21</c>
/// (the second with 16-bit offsets), a style whose one entry is found through a sparse table and is a bag, and a
/// type-spec chunk to be passed over — then damaged every way, which must be a table error and nothing else.
/// </summary>
public sealed class ResourceTableTests
{
    /// <summary>The ids the table defines: the manifest test's label, and a style.</summary>
    internal const uint AppName = 0x7F0F001E;
    internal const uint Theme = 0x7F0E0000;

    private const uint None = 0xFFFFFFFF;

    /// <summary>Types 1–15, so that <c>string</c> is 0x0F and <c>style</c> 0x0E as aapt numbers them in a real app.</summary>
    private static readonly string[] TypeNames = ["anim", "animator", "array", "attr", "bool", "color", "dimen", "drawable", "fraction", "id", "integer", "interpolator", "layout", "style", "string"];

    internal static byte[] Table()
    {
        byte[] typePool = StringPool(TypeNames);
        byte[] keyPool = StringPool(["app_name", "Theme.Demo"]);

        var package = new List<byte>();
        package.AddRange(U4(0x7F));
        byte[] name = new byte[256];
        Encoding.Unicode.GetBytes("com.example.app").CopyTo(name, 0);
        package.AddRange(name);
        package.AddRange(U4(288));                             // type strings, from the package chunk's start
        package.AddRange(U4((uint)TypeNames.Length));
        package.AddRange(U4((uint)(288 + typePool.Length)));   // key strings
        package.AddRange(U4(2));
        package.AddRange(U4(0));                               // type id offset
        package.AddRange(typePool);
        package.AddRange(keyPool);

        // A type spec, which the reader has no use for and must step over.
        package.AddRange(Chunk(0x0202, 16, [0x0F, 0, .. U2(0), .. U4(31), .. Enumerable.Repeat((byte)0, 31 * 4)]));

        // string/app_name = "Demo": 31 32-bit offsets, only the last one present.
        var offsets = Enumerable.Range(0, 31).SelectMany(i => U4(i == 30 ? 0 : None)).ToArray();
        package.AddRange(TypeChunk(0x0F, 0, 31, Config(), offsets, SimpleEntry(0, 0x03, 0)));

        // Again in fr-rCA-xhdpi-v21, with 16-bit offsets (padded to four bytes).
        var packed = Enumerable.Range(0, 31).SelectMany(i => U2(i == 30 ? 0 : 0xFFFF)).Concat(new byte[2]).ToArray();
        package.AddRange(TypeChunk(0x0F, 0x02, 31, Config(language: "fr", region: "CA", density: 320, sdk: 21), packed, SimpleEntry(0, 0x03, 1)));

        // style/Theme.Demo: a sparse table of one, and a bag with a parent and one android: item.
        byte[] bag = [.. U2(16), .. U2(0x0001), .. U4(1), .. U4(0x01030128), .. U4(1), .. U4(0x01010000), .. U2(8), 0, 0x1D, .. U4(0xFF0000FF)];
        package.AddRange(TypeChunk(0x0E, 0x01, 1, Config(), [.. U2(0), .. U2(0)], bag));

        byte[] values = StringPool(["Demo", "Démo"]);
        return Chunk(0x0002, 12, [.. U4(1), .. values, .. Chunk(0x0200, 288, [.. package])]);
    }

    private static byte[] SimpleEntry(uint key, byte type, uint data) => [.. U2(8), .. U2(0), .. U4(key), .. U2(8), 0, type, .. U4(data)];

    private static byte[] TypeChunk(byte id, byte flags, int count, byte[] config, byte[] offsets, byte[] entries)
    {
        int headerSize = 20 + config.Length;
        byte[] header = [id, flags, .. U2(0), .. U4((uint)count), .. U4((uint)(headerSize + offsets.Length)), .. config];
        return Chunk(0x0201, (ushort)headerSize, [.. header, .. offsets, .. entries]);
    }

    private static byte[] Config(string? language = null, string? region = null, int density = 0, int sdk = 0)
    {
        byte[] config = new byte[64];
        U4(64).CopyTo(config, 0);
        if (language is not null)
        {
            Encoding.ASCII.GetBytes(language).CopyTo(config, 8);
        }

        if (region is not null)
        {
            Encoding.ASCII.GetBytes(region).CopyTo(config, 10);
        }

        U2(density).CopyTo(config, 14);
        U2(sdk).CopyTo(config, 24);
        return config;
    }

    [Fact]
    public void ATableReadsBackItsResourcesByIdNameAndConfiguration()
    {
        var table = ResourceTable.Parse(Table());

        Assert.Empty(table.Warnings);
        var package = Assert.Single(table.Packages);
        Assert.Equal((0x7F, "com.example.app", 2, 3), (package.Id, package.Name, package.Types, package.Entries));

        var strings = table.Entries.Where(e => e.Id == AppName).ToList();
        Assert.Equal(["Demo", "Démo"], strings.Select(e => table.ValueText(e)));
        Assert.Equal(["", "fr-rCA-xhdpi-v21"], strings.Select(e => e.Config));
        Assert.Equal("string/app_name", table.NameOf(AppName));

        var style = Assert.Single(table.Entries, e => e.Id == Theme);
        Assert.Equal("style/Theme.Demo", style.FullName);
        Assert.Equal("parent @0x01030128; 1 item: android:attr/theme=#FF0000FF", table.ValueText(style));
        Assert.Contains("0x7F0F001E string/app_name [fr-rCA-xhdpi-v21] = Démo", table.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestReferencesAreWrittenByTheNamesTheTableGives()
    {
        var table = ResourceTable.Parse(Table());

        Assert.Contains("android:label=\"@string/app_name\"", BinaryXml.ToText(Manifest(), table.NameOf), StringComparison.Ordinal);
        Assert.Contains("android:label=\"@0x7F0F001E\"", BinaryXml.ToText(Manifest()), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTruncationAndRandomDamageIsATableErrorOrAWarning()
    {
        byte[] whole = Table();
        Assert.True(ResourceTable.IsResourceTable(whole));
        Assert.False(ResourceTable.IsResourceTable(Manifest()));
        Assert.Throws<ResourceTableException>(() => ResourceTable.Parse(Manifest()));

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
                var table = ResourceTable.Parse(bytes);
                _ = table.ToText();
            }
            catch (Spydate.Core.Binary.BinaryParseException)
            {
            }
        }
    }
}
