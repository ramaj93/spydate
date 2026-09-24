using System.IO.Compression;
using System.Xml.Linq;
using Spydate.Core.Android;
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

    /// <summary>The hand-built manifest and DEX file, and a native library for each of two ABIs.</summary>
    private string WriteApk()
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
        Add("lib/x86_64/libnative.so", [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        Add("resources.arsc", [2, 0, 12, 0]);
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
