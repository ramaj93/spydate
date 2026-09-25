using System.IO.Compression;
using System.Xml.Linq;
using Spydate.Core.Android;
using Spydate.Core.Archive;
using Spydate.Core.Binary;
using Spydate.Core.Readings;
using Spydate.Decompiler.Jvm;
using Spydate.Mcp;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// An Android package: a zip of a compiled manifest, DEX code and native libraries, opened as a binary whose program
/// is its bytecode — its classes read through the same JVM reading a JAR's are, as Java and as Dalvik code — and
/// read by an agent the way a JAR is.
/// </summary>
public sealed class ApkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spydate-apk-" + Guid.NewGuid().ToString("N")[..8]);

    public ApkTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The hand-built manifest, DEX file and resource table, and a native library for each of two ABIs — the x86-64 one a real ELF.</summary>
    private string WriteApk(params (string Name, byte[] Bytes)[] extra)
    {
        string path = Path.Combine(_directory, "app.apk");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, byte[] bytes)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }

        Add(ApkImage.ManifestEntry, BinaryXmlTests.Manifest());
        Add("classes.dex", DexTests.GreeterDex());
        Add("lib/arm64-v8a/libnative.so", [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        Add("lib/x86_64/libnative.so", new SyntheticElf { Type = 3, Interpreter = null, Imports = ["puts"], Functions = [("native_hello", 0, 1)] }.Build());
        Add(ApkImage.ResourceTableEntry, ResourceTableTests.Table());
        foreach (var (name, bytes) in extra)
        {
            Add(name, bytes);
        }

        return path;
    }

    [Fact]
    public void TheManifestSaysWhatLaunchesWhatItMayDoAndWhatItDeclares()
    {
        XNamespace android = BinaryXml.AndroidNamespace;
        var document = new XDocument(new XElement("manifest", new XAttribute("package", "com.example.app"),
            new XAttribute(android + "versionCode", "7"), new XAttribute(android + "versionName", "1.2"),
            new XElement("uses-sdk", new XAttribute(android + "minSdkVersion", "24"), new XAttribute(android + "targetSdkVersion", "34")),
            new XElement("uses-permission", new XAttribute(android + "name", "android.permission.INTERNET")),
            new XElement("application", new XAttribute(android + "debuggable", "true"),
                new XElement("activity", new XAttribute(android + "name", ".Main"), new XAttribute(android + "exported", "true"),
                    new XElement("intent-filter",
                        new XElement("action", new XAttribute(android + "name", "android.intent.action.MAIN")),
                        new XElement("category", new XAttribute(android + "name", "android.intent.category.LAUNCHER")))),
                new XElement("service", new XAttribute(android + "name", "com.example.app.Sync")))));

        var manifest = ApkManifest.From(document);

        Assert.Equal(("com.example.app", "7", "1.2", "24", "34"), (manifest.Package, manifest.VersionCode, manifest.VersionName, manifest.MinSdk, manifest.TargetSdk));
        Assert.Equal("com.example.app.Main", manifest.MainActivity);
        Assert.True(manifest.Debuggable);
        Assert.Equal(["android.permission.INTERNET"], manifest.Permissions);
        Assert.Equal([("activity", "com.example.app.Main", true), ("service", "com.example.app.Sync", false)], manifest.Components.Select(c => (c.Kind, c.Name, c.Exported)));
    }

    [Fact]
    public void AnApkOpensWithItsManifestItsClassesAndItsLibraries()
    {
        var apk = Assert.IsType<ApkImage>(BinaryImage.Load(WriteApk()));

        Assert.Equal(BinaryFormat.Apk, apk.Format);
        Assert.Equal("Dalvik", BinaryImage.MachineName(apk));
        Assert.Equal("com.example.app", apk.Manifest!.Package);
        Assert.Null(apk.Manifest.MinSdk); // the test manifest puts it on <manifest>, where Android does not look
        Assert.Single(apk.DexFiles);
        Assert.Equal("com/example/Greeter", Assert.Single(apk.Classes).Class.Name);
        Assert.Equal(["arm64-v8a", "x86_64"], apk.Abis);
        Assert.True(apk.HasResourceTable);
        Assert.True(apk.IsLibrary);
        Assert.Empty(apk.Warnings);
    }

    [Fact]
    public void ItsClassesReadAsJavaAndAsDalvikCode()
    {
        var reading = new JvmReading(ApkImage.Load(WriteApk()));
        var greeter = reading.FindType("com/example/Greeter")!;
        var greet = greeter.Members.Cast<JvmMember>().First(m => m.Name == "greet");

        Assert.Equal(BytecodeKind.Dalvik, reading.Kind);
        Assert.Equal("Android 1.0+", reading.Platform); // no minSdkVersion: what DEX 035 needs
        Assert.Equal("DEX 035", reading.FormatVersion);

        string dalvik = reading.Render(greeter, greet, JvmReading.BytecodeView);
        Assert.Contains("sget-object v0, java.lang.System.out : java.io.PrintStream", dalvik, StringComparison.Ordinal);
        Assert.Contains("const-string v1, \"hello\"", dalvik, StringComparison.Ordinal);
        Assert.Contains("0000-0007 -> 0008  java.lang.RuntimeException", dalvik, StringComparison.Ordinal);

        string java = reading.Render(greeter, null, JvmReading.JavaView);
        Assert.Contains("public class Greeter implements Runnable", java, StringComparison.Ordinal);
        Assert.Contains("public static final int COUNT = 42;", java, StringComparison.Ordinal);
        Assert.Contains("PrintStream out = System.out;", java, StringComparison.Ordinal); // named by the debug table
        Assert.Contains("out.println(\"hello\");", java, StringComparison.Ordinal);
        Assert.Contains("catch (RuntimeException ignored)", java, StringComparison.Ordinal);
        Assert.Contains("public void greet(String name)", java, StringComparison.Ordinal);

        // What the Dalvik code names, as a class's bytecode does.
        Assert.Contains(reading.References.To("java/io/PrintStream", "println"), r => r.FromMember == greet);
        Assert.Contains(reading.References.Strings, s => s.Text == "hello" && s.Member == greet);
    }

    [Fact]
    public void ItsResourceTableNamesWhatTheManifestAndTheResourcesReferTo()
    {
        string path = WriteApk();
        var apk = Assert.IsType<ApkImage>(BinaryImage.Load(path));
        Assert.Equal("string/app_name", apk.ResourceName(ResourceTableTests.AppName));
        Assert.Equal(3, apk.Resources!.Entries.Count);

        using var store = new SessionStore();
        store.Set(BinarySession.Open(path, McpOptions.Default));
        var session = new SessionTools(store, McpOptions.Default);
        Assert.Contains("resources 3 values of 2 resources in 2 types", session.GetOverview(), StringComparison.Ordinal);
        Assert.Contains("android:label=\"@string/app_name\"", session.ReadFile("app.apk!/AndroidManifest.xml"), StringComparison.Ordinal);

        string table = session.ReadFile("app.apk!/resources.arsc");
        Assert.Contains("resource table", table, StringComparison.Ordinal);
        Assert.Contains("0x7F0E0000 style/Theme.Demo = parent @0x01030128", table, StringComparison.Ordinal);
        Assert.Contains("0x0  02 00 0C 00", session.ReadFile("app.apk!/resources.arsc", @as: "hex"), StringComparison.Ordinal);
    }

    [Fact]
    public void ADexFileOnItsOwnOpensAsTheAppsCodeWithoutItsPackage()
    {
        string path = Path.Combine(_directory, "classes.dex");
        File.WriteAllBytes(path, DexTests.GreeterDex());

        Assert.Equal(BinaryFormat.Dex, BinaryImage.Detect(path));
        var dex = Assert.IsType<ApkImage>(BinaryImage.Load(path));
        Assert.False(dex.IsPackage);
        Assert.Null(dex.Archive);
        Assert.Equal("DEX", BinaryImage.ContainerName(dex));
        Assert.Empty(dex.Warnings);

        var reading = new JvmReading(dex);
        Assert.Equal("DEX file", reading.Noun);
        Assert.Contains("public class Greeter implements Runnable", reading.Render(reading.FindType("com/example/Greeter")!, null, JvmReading.JavaView), StringComparison.Ordinal);

        using var store = new SessionStore();
        store.Set(BinarySession.Open(path, McpOptions.Default));
        string overview = new SessionTools(store, McpOptions.Default).GetOverview();
        Assert.Contains("DEX 035, 1 classes", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("manifest", overview, StringComparison.Ordinal);
        Assert.Contains("out.println(\"hello\");", new CodeTools(store).ReadFunction("com.example.Greeter::greet"), StringComparison.Ordinal);

        // A file that says it is DEX and is not one is refused as it opens.
        string broken = Path.Combine(_directory, "broken.dex");
        File.WriteAllBytes(broken, [.. "dex\n035\0"u8, .. new byte[40]]);
        Assert.Throws<Spydate.Core.Dex.DexFormatException>(() => BinaryImage.Load(broken));
    }

    [Fact]
    public async Task ANativeLibraryInsideIsTakenOutAndOpensAsTheElfItIs()
    {
        string path = WriteApk(("../escape.so", [1, 2, 3]));
        var apk = ApkImage.Load(path);
        string cache = Path.Combine(_directory, "cache");

        Assert.True(NestedFile.TrySplit($"{path}!/lib/x86_64/libnative.so", out string archive, out string entry));
        Assert.Equal((path, "lib/x86_64/libnative.so"), (archive, entry));
        Assert.False(NestedFile.TrySplit(path, out _, out _));

        string library = NestedFile.Extract(apk.Archive!, apk.Archive!.Find(entry)!, cache);
        Assert.StartsWith(cache, library, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("lib", "x86_64", "libnative.so"), library, StringComparison.Ordinal);
        Assert.IsType<Spydate.Core.Elf.ElfImage>(BinaryImage.Load(library));
        Assert.Equal(library, NestedFile.Extract(path, entry, cache));

        // A name that would climb out of the archive's folder is refused, not written.
        Assert.Throws<Spydate.Core.Archive.ArchiveException>(() => NestedFile.Extract(apk.Archive, apk.Archive.Find("../escape.so")!, cache));
        Assert.False(File.Exists(Path.Combine(cache, "escape.so")));

        // The agent opens it by the JVM's spelling, and reads it as the ELF it is.
        using var store = new SessionStore();
        var tools = new SessionTools(store, McpOptions.Default);
        string opened = await tools.OpenBinaryAsync($"{path}!/lib/x86_64/libnative.so");
        try
        {
            Assert.Contains("libnative.so", opened, StringComparison.Ordinal);
            Assert.IsType<Spydate.Core.Elf.ElfImage>(store.Current!.Image);
            Assert.Contains("no entry lib/none.so", await tools.OpenBinaryAsync($"{path}!/lib/none.so"), StringComparison.Ordinal);
        }
        finally
        {
            string extracted = store.Current!.Path;
            store.Dispose();
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(extracted)))!, recursive: true);
        }
    }

    [Fact]
    public void AnAgentOpensItReadsItsManifestAndReadsItsCode()
    {
        string path = WriteApk();
        using var store = new SessionStore();
        store.Set(BinarySession.Open(path, McpOptions.Default));
        var session = new SessionTools(store, McpOptions.Default);

        string overview = session.GetOverview();
        Assert.Contains("APK (zip), 5 files: 1 DEX file(s) with 1 classes, 2 native libraries, a resource table", overview, StringComparison.Ordinal);
        Assert.Contains("com.example.app", overview, StringComparison.Ordinal);
        Assert.Contains("arm64-v8a, x86_64: libnative.so", overview, StringComparison.Ordinal);

        string manifest = session.ReadFile("app.apk!/AndroidManifest.xml");
        Assert.Contains("compiled XML", manifest, StringComparison.Ordinal);
        Assert.Contains("package=\"com.example.app\"", manifest, StringComparison.Ordinal);

        string code = new CodeTools(store).ReadFunction("com.example.Greeter::greet");
        Assert.Contains("out.println(\"hello\");", code, StringComparison.Ordinal);
        Assert.Contains("is an Android package", new CodeTools(store).Disassemble("0x1000"), StringComparison.Ordinal);
    }
}
