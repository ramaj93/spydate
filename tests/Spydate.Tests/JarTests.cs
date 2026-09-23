using System.Text;
using Spydate.Core.Archive;
using Spydate.Core.Binary;
using Spydate.Core.Jvm;
using Spydate.Core.Project;
using Spydate.Core.Readings;
using Spydate.Decompiler.Jvm;
using Spydate.Mcp;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// JAR end to end: the zip directory read in-house, class files parsed and hostile ones refused cleanly, bytecode
/// decoded and listed with names resolved, the JVM reading's packages, nesting and entry point, references and
/// strings, annotations keyed by member through the project file, and the MCP surface an agent reads a JAR with.
/// Everything is built by hand: there is no JDK on a test machine to compile a class.
/// </summary>
public sealed class JarTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spydate-jar-" + Guid.NewGuid().ToString("N")[..8]);

    public JarTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not go away is not a test failure.
        }
    }

    // --- the sample ------------------------------------------------------------

    /// <summary>
    /// <c>com.example.Greeter</c>: a constructor that stores a name, <c>greet</c> that concatenates with a
    /// StringBuilder, <c>main</c> that prints a greeting and runs a lambda, the lambda's body calling
    /// <c>Runtime.exec("calc.exe")</c>, <c>choose</c> with a tableswitch, two constants, a member class and an
    /// anonymous one.
    /// </summary>
    internal static byte[] Greeter()
    {
        var c = new SyntheticClass("com/example/Greeter").SourceFile("Greeter.java");
        ushort sb = c.Class("java/lang/StringBuilder");
        ushort sbInit = c.Method("java/lang/StringBuilder", "<init>", "()V");
        ushort append = c.Method("java/lang/StringBuilder", "append", "(Ljava/lang/String;)Ljava/lang/StringBuilder;");
        ushort toText = c.Method("java/lang/StringBuilder", "toString", "()Ljava/lang/String;");
        ushort hello = c.String("Hello, ");
        ushort world = c.String("world");
        ushort you = c.String("you");
        ushort calc = c.String("calc.exe");
        ushort objectInit = c.Method("java/lang/Object", "<init>", "()V");
        ushort nameField = c.Field("com/example/Greeter", "name", "Ljava/lang/String;");
        ushort greeter = c.Class("com/example/Greeter");
        ushort greeterInit = c.Method("com/example/Greeter", "<init>", "(Ljava/lang/String;)V");
        ushort greet = c.Method("com/example/Greeter", "greet", "(Ljava/lang/String;)Ljava/lang/String;");
        ushort outField = c.Field("java/lang/System", "out", "Ljava/io/PrintStream;");
        ushort println = c.Method("java/io/PrintStream", "println", "(Ljava/lang/String;)V");
        ushort lambda = c.Lambda("run", "()Ljava/lang/Runnable;", "com/example/Greeter", "lambda$main$0", "()V");
        ushort run = c.Method("java/lang/Runnable", "run", "()V", isInterface: true);
        ushort runtime = c.Method("java/lang/Runtime", "getRuntime", "()Ljava/lang/Runtime;");
        ushort exec = c.Method("java/lang/Runtime", "exec", "(Ljava/lang/String;)Ljava/lang/Process;");

        c.AddField(0x0012, "name", "Ljava/lang/String;");
        c.AddField(0x0019, "GREETING", "Ljava/lang/String;", constantValue: c.String("Hello"));
        c.AddField(0x0018, "BIG", "J", constantValue: c.Long(1234567890123L));

        c.AddMethod(0x0001, "<init>", "(Ljava/lang/String;)V",
            [0x2A, 0xB7, .. SyntheticClass.U2(objectInit), 0x2A, 0x2B, 0xB5, .. SyntheticClass.U2(nameField), 0xB1],
            maxStack: 2, maxLocals: 2,
            lines: [(0, 3), (4, 4), (9, 5)],
            locals: [(0, 10, "this", "Lcom/example/Greeter;", 0), (0, 10, "name", "Ljava/lang/String;", 1)]);

        c.AddMethod(0x0001, "greet", "(Ljava/lang/String;)Ljava/lang/String;",
            [
                0xBB, .. SyntheticClass.U2(sb), 0x59, 0xB7, .. SyntheticClass.U2(sbInit),
                0x12, (byte)hello, 0xB6, .. SyntheticClass.U2(append),
                0x2B, 0xB6, .. SyntheticClass.U2(append),
                0xB6, .. SyntheticClass.U2(toText), 0xB0,
            ],
            maxStack: 2, maxLocals: 2,
            lines: [(0, 8)],
            locals: [(0, 23, "this", "Lcom/example/Greeter;", 0), (0, 23, "who", "Ljava/lang/String;", 1)]);

        c.AddMethod(0x0009, "main", "([Ljava/lang/String;)V",
            [
                0xB2, .. SyntheticClass.U2(outField),
                0xBB, .. SyntheticClass.U2(greeter), 0x59, 0x12, (byte)world, 0xB7, .. SyntheticClass.U2(greeterInit),
                0x12, (byte)you, 0xB6, .. SyntheticClass.U2(greet),
                0xB6, .. SyntheticClass.U2(println),
                0xBA, .. SyntheticClass.U2(lambda), 0x00, 0x00,
                0xB9, .. SyntheticClass.U2(run), 0x01, 0x00,
                0xB1,
            ],
            maxStack: 4, maxLocals: 1);

        c.AddMethod(0x100A, "lambda$main$0", "()V",
            [0xB8, .. SyntheticClass.U2(runtime), 0x12, (byte)calc, 0xB6, .. SyntheticClass.U2(exec), 0x57, 0xB1],
            maxStack: 2, maxLocals: 0);

        // iload_1; tableswitch 0..1 -> case 0 returns 1, case 1 returns 2, default returns 0.
        c.AddMethod(0x0001, "choose", "(I)I",
            [
                0x1B, 0xAA, 0x00, 0x00,
                .. SyntheticClass.U4(27), .. SyntheticClass.U4(0), .. SyntheticClass.U4(1),
                .. SyntheticClass.U4(23), .. SyntheticClass.U4(25),
                0x04, 0xAC, 0x05, 0xAC, 0x03, 0xAC,
            ],
            maxStack: 1, maxLocals: 2);

        c.InnerClass("com/example/Greeter$Inner", "com/example/Greeter", "Inner");
        c.InnerClass("com/example/Greeter$1", null, null, 0);
        return c.Build();
    }

    private static byte[] Inner()
        => new SyntheticClass("com/example/Greeter$Inner", access: 0x0020)
            .InnerClass("com/example/Greeter$Inner", "com/example/Greeter", "Inner", 0x0009)
            .AddMethod(0x0000, "<init>", "()V", [0x2A, 0xB7, 0x00, 0x00, 0xB1])
            .Build();

    private static byte[] Anonymous()
        => new SyntheticClass("com/example/Greeter$1", access: 0x0020)
            .Implements("java/lang/Runnable")
            .InnerClass("com/example/Greeter$1", null, null, 0)
            .EnclosingMethod("com/example/Greeter", "main", "([Ljava/lang/String;)V")
            .AddMethod(0x0001, "run", "()V", [0xB1])
            .Build();

    private static byte[] Shape()
        => new SyntheticClass("com/example/shapes/Shape", access: 0x0601)
            .AddMethod(0x0401, "area", "()D", null)
            .Build();

    internal static byte[] SampleJar(params (string Name, byte[] Bytes)[] extra)
        => SyntheticClass.Jar(
            [
                ("META-INF/MANIFEST.MF", SyntheticClass.Manifest("com.example.Greeter")),
                ("com/example/Greeter.class", Greeter()),
                ("com/example/Greeter$Inner.class", Inner()),
                ("com/example/Greeter$1.class", Anonymous()),
                ("com/example/shapes/Shape.class", Shape()),
                ("config/app.properties", Encoding.UTF8.GetBytes("mode=fast\n")),
                .. extra,
            ],
            stored: "config/app.properties");

    private string WriteJar(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static JvmReading Reading(byte[] jar, MemberAnnotationStore? members = null) => new(JarImage.FromBytes(jar, "greeter.jar"), members);

    private static JvmType TypeOf(JvmReading reading, string internalName) => reading.FindType(internalName) ?? throw new InvalidOperationException(internalName);

    private static JvmMember MemberOf(JvmType type, string name) => type.Members.Cast<JvmMember>().First(m => m.Name == name);

    // --- the archive ------------------------------------------------------------

    [Fact]
    public void TheZipDirectoryIsReadInHouseWithMethodsSizesAndContents()
    {
        var archive = ZipArchiveFile.Open(SampleJar());

        Assert.Equal(6, archive.Entries.Count);
        var properties = archive.Find("config/app.properties")!;
        Assert.Equal("stored", properties.MethodName);
        Assert.Equal("mode=fast\n", Encoding.UTF8.GetString(archive.Read(properties, 1024)));

        var greeter = archive.Find("com/example/Greeter.class")!;
        Assert.Equal("deflated", greeter.MethodName);
        Assert.Equal(Greeter(), archive.Read(greeter, 1 << 20));
        Assert.Empty(archive.Warnings);
    }

    [Fact]
    public void AnEntryIsCheckedAgainstItsDirectoryBeforeItIsBelieved()
    {
        byte[] jar = SampleJar();
        var archive = ZipArchiveFile.Open(jar);
        var properties = archive.Find("config/app.properties")!;

        // Larger than the caller will read: refused by its declared size, never inflated.
        Assert.Contains("more than", Assert.Throws<ArchiveException>(() => archive.Read(properties, 4)).Message);

        // One byte of a stored entry changed: its CRC no longer matches.
        int at = Encoding.ASCII.GetString(jar).IndexOf("mode=fast", StringComparison.Ordinal);
        jar[at] = (byte)'M';
        var tampered = ZipArchiveFile.Open(jar);
        Assert.Contains("CRC", Assert.Throws<ArchiveException>(() => tampered.Read(tampered.Find("config/app.properties")!, 1024)).Message);
    }

    [Fact]
    public void ANameThatAppearsTwiceIsReportedAndTheFirstIsTheOneRead()
    {
        byte[] jar = SyntheticClass.Jar([("a.txt", "first"u8.ToArray()), ("a.txt", "second"u8.ToArray())]);
        var archive = ZipArchiveFile.Open(jar);

        Assert.Equal(2, archive.Entries.Count);
        Assert.Contains("a.txt", archive.Duplicates);
        Assert.Contains(archive.Warnings, w => w.Contains("more than once", StringComparison.Ordinal));
        Assert.Equal("first", Encoding.UTF8.GetString(archive.Read(archive.Find("a.txt")!, 100)));
    }

    [Fact]
    public void AZipWithSomethingInFrontIsReadAtItsRealOffsets()
    {
        // A launcher or self-extractor stub before the zip: every offset the zip records is short by its length,
        // yet the end record still points somewhere inside the file — jd-gui.jar ships exactly like this.
        byte[] zip = SampleJar();
        byte[] prefixed = [.. Enumerable.Repeat((byte)0x90, 128_512), .. zip];
        var jar = JarImage.FromBytes(prefixed);

        Assert.Equal(4, jar.Classes.Count);
        Assert.Equal("mode=fast\n", Encoding.UTF8.GetString(jar.Archive.Read(jar.Archive.Find("config/app.properties")!, 100)));
        Assert.Contains(jar.Warnings, w => w.Contains("128,512 bytes precede the zip", StringComparison.Ordinal));
    }

    [Fact]
    public void ALauncherWithAJarAppendedHasBothReadings()
    {
        // A native launcher with the application's JAR appended, as launch4j builds them: the file is a PE, and
        // the program is the archive after it.
        const string notepad = @"C:\Windows\System32\notepad.exe";
        if (!File.Exists(notepad))
        {
            return;
        }

        string path = WriteJar("launcher.exe", [.. File.ReadAllBytes(notepad), .. SampleJar()]);
        var store = new SessionStore();
        store.Set(BinarySession.Open(path, McpOptions.Default));
        var session = store.Current!;

        Assert.NotNull(session.Analysis);
        Assert.IsType<JvmReading>(session.Bytecode);
        Assert.True(session.BytecodeIsTheProgram);
        Assert.Contains("Java launcher", new SessionTools(store, McpOptions.Default).GetOverview(), StringComparison.Ordinal);
        Assert.Contains("return new StringBuilder().append(\"Hello, \").append(who).toString();", new CodeTools(store).ReadFunction("com.example.Greeter::greet"), StringComparison.Ordinal);

        // A member name goes to the member store; an address still goes to the native one.
        var annotations = new AnnotationTools(store, McpOptions.Default);
        Assert.Contains("is now called salute", annotations.Annotate("com.example.Greeter::greet", name: "salute"), StringComparison.Ordinal);
        Assert.Contains("is now launcherStart", annotations.Annotate($"0x{session.Image.EntryPointVa:X}", name: "launcherStart"), StringComparison.Ordinal);
        string listed = annotations.ListAnnotations();
        Assert.Contains("launcherStart", listed, StringComparison.Ordinal);
        Assert.Contains("members of the embedded JAR", listed, StringComparison.Ordinal);
        Assert.Contains("salute", listed, StringComparison.Ordinal);

        // An ordinary binary has no archive in it.
        Assert.Null(JarImage.Embedded(BinaryImage.Load(notepad)));
    }

    [Fact]
    public void NotAZipAndACutShortZipAreRefusedInWords()
    {
        Assert.Throws<ArchiveException>(() => ZipArchiveFile.Open("not a zip at all"u8.ToArray()));

        byte[] jar = SampleJar();
        for (int length = 0; length < jar.Length; length += 97)
        {
            try
            {
                var archive = ZipArchiveFile.Open(jar.AsMemory(0, length));
                foreach (var entry in archive.Entries)
                {
                    try
                    {
                        archive.Read(entry, 1 << 20);
                    }
                    catch (ArchiveException)
                    {
                        // What a cut-short entry should do.
                    }
                }
            }
            catch (ArchiveException)
            {
                // What a cut-short directory should do.
            }
        }
    }

    [Fact]
    public void TheFingerprintIsTheDirectorysAndMovesWithAnyEntry()
    {
        var one = JarImage.FromBytes(SampleJar());
        var again = JarImage.FromBytes(SampleJar());
        var other = JarImage.FromBytes(SampleJar(("extra.txt", "x"u8.ToArray())));

        Assert.Equal(32, one.Fingerprint.Length);
        Assert.Equal(one.Fingerprint, again.Fingerprint);
        Assert.NotEqual(one.Fingerprint, other.Fingerprint);
    }

    [Fact]
    public void AJarOnDiskOpensThroughTheOneDoorAsAnImageWithNoAddresses()
    {
        string path = WriteJar("greeter.jar", SampleJar());
        var image = Assert.IsType<JarImage>(BinaryImage.Load(path));

        Assert.Equal(BinaryFormat.Jar, image.Format);
        Assert.Equal(Architecture.Unknown, image.Architecture);   // what keeps native analysis from starting
        Assert.Empty(image.Sections);
        Assert.Null(image.VaToRva(0x1000));
        Assert.False(image.IsLibrary);   // its manifest names a Main-Class
        Assert.Equal("com/example/Greeter", image.Manifest!.MainClass);
        Assert.Equal(4, image.Classes.Count);
        Assert.Equal("JAR", BinaryImage.ContainerName(image));
    }

    // --- class files ------------------------------------------------------------

    [Fact]
    public void AClassFileIsReadWholeWithItsMembersCodeAndDebugTables()
    {
        var file = ClassFile.Parse(Greeter());

        Assert.Equal("com/example/Greeter", file.Name);
        Assert.Equal("java/lang/Object", file.SuperName);
        Assert.Equal("Java 17", file.JavaVersion);
        Assert.Equal("Greeter.java", file.SourceFile);
        Assert.Equal("com/example", file.PackageName);
        Assert.Equal(["name", "GREETING", "BIG"], file.Fields.Select(f => f.Name));
        Assert.Equal(["<init>", "greet", "main", "lambda$main$0", "choose"], file.Methods.Select(m => m.Name));

        var init = file.Methods[0];
        Assert.True(init.IsConstructor);
        Assert.Equal(3, init.Code!.Lines.Count);
        Assert.Equal(["this", "name"], init.Code.Locals.Select(l => l.Name));
        Assert.Single(file.BootstrapMethods);
        Assert.Equal(2, file.InnerClasses.Count);
        Assert.Empty(file.Warnings);
    }

    [Fact]
    public void AnAttributeThatDoesNotParseCostsItselfNotTheClass()
    {
        // A LineNumberTable claiming more rows than its body holds.
        var c = new SyntheticClass("a/Broken");
        c.AddMethod(0x0009, "f", "()V", [0xB1]);
        c.Unknown("Signature", [0x00]);   // one byte where two are needed
        var file = ClassFile.Parse(c.Build());

        Assert.Equal("a/Broken", file.Name);
        Assert.Contains(file.Warnings, w => w.Contains("Signature", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryTruncationOfAClassIsRefusedAsAClassFormatErrorAndNothingElse()
    {
        byte[] bytes = Greeter();
        for (int length = 0; length < bytes.Length; length++)
        {
            try
            {
                ClassFile.Parse(bytes.AsMemory(0, length));
            }
            catch (ClassFormatException)
            {
                // The only acceptable failure.
            }
        }
    }

    [Fact]
    public void FlippedBytesNeverEscapeAsAnythingButAClassFormatError()
    {
        byte[] original = Greeter();
        var random = new Random(1234);
        for (int round = 0; round < 3000; round++)
        {
            byte[] bytes = (byte[])original.Clone();
            for (int flips = random.Next(1, 4); flips > 0; flips--)
            {
                bytes[random.Next(8, bytes.Length)] = (byte)random.Next(256);
            }

            try
            {
                var file = ClassFile.Parse(bytes);

                // What parsed must also list: the listing reads indices a hostile file made up.
                var reading = Reading(SyntheticClass.Jar([("x.class", bytes)]));
                foreach (var type in reading.Namespaces.SelectMany(n => n.Types))
                {
                    _ = reading.Render(type, null, JvmReading.BytecodeView);
                }

                _ = file.Name;
            }
            catch (ClassFormatException)
            {
            }
        }
    }

    [Fact]
    public void AForgedCountIsRefusedBeforeItSizesAnything()
    {
        // A constant pool of 65535 entries in a 20-byte file.
        byte[] bytes = [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 61, 0xFF, 0xFF, 1, 0, 1, (byte)'A', 0, 0, 0, 0, 0, 0];
        Assert.Contains("cannot fit", Assert.Throws<ClassFormatException>(() => ClassFile.Parse(bytes)).Message);
        Assert.Contains("CAFEBABE", Assert.Throws<ClassFormatException>(() => ClassFile.Parse("PK\x03\x04 not a class"u8.ToArray())).Message);
    }

    // --- bytecode --------------------------------------------------------------

    [Fact]
    public void SwitchesAreDecodedWithTheirPaddingAndAbsoluteTargets()
    {
        var code = ClassFile.Parse(Greeter()).Methods.First(m => m.Name == "choose").Code!.Code;
        var instructions = Bytecode.Decode(code.Span);

        var table = instructions[1];
        Assert.Equal("tableswitch", table.Mnemonic);
        Assert.Equal([(0, 24), (1, 26)], table.Cases!);
        Assert.Equal(28, table.Default);
        Assert.Equal(24, instructions[2].Offset);
        Assert.All(instructions, i => Assert.Null(i.Problem));
    }

    [Fact]
    public void BadCodeIsReportedAsTheLastInstructionNotThrown()
    {
        // wide iinc 300, -2; then an unassigned opcode; then bytes that must not be decoded.
        var instructions = Bytecode.Decode([0xC4, 0x84, 0x01, 0x2C, 0xFF, 0xFE, 0xCB, 0x00, 0x00]);
        Assert.True(instructions[0].IsWide);
        Assert.Equal(300, instructions[0].Operand);
        Assert.Equal(-2, instructions[0].Operand2);
        Assert.Equal("not an opcode", instructions[1].Problem);
        Assert.Equal(2, instructions.Count);

        // A tableswitch whose range claims four billion cases, in eleven bytes.
        var forged = Bytecode.Decode([0xAA, 0, 0, 0, 0, 0, 0, 0, 0x80, 0, 0, 0, 0x7F, 0xFF, 0xFF, 0xFF]);
        Assert.Equal("cut short", Assert.Single(forged).Problem);
    }

    [Fact]
    public void DescriptorsAndSignaturesReadAsJava()
    {
        Assert.Equal("java.lang.String[][]", Descriptors.TypeName("[[Ljava/lang/String;"));
        Assert.Equal("String", Descriptors.TypeName("Ljava/lang/String;", simple: true));
        var parsed = Descriptors.Method("(IJ[Ljava/lang/Object;)V", simple: true)!.Value;
        Assert.Equal(["int", "long", "Object[]"], parsed.Parameters);
        Assert.Equal("void", parsed.Return);
        Assert.Equal(4, Descriptors.ParameterSlots("(IJLjava/lang/String;)V"));
        Assert.Null(Descriptors.Method("(I"));
        Assert.Equal("Lbroken", Descriptors.TypeName("Lbroken"));

        Assert.Equal("java.util.Map<java.lang.String, java.util.List<? extends java.lang.Number>>",
            Descriptors.FieldSignature("Ljava/util/Map<Ljava/lang/String;Ljava/util/List<+Ljava/lang/Number;>;>;"));
        var method = Descriptors.MethodSignature("<T::Ljava/lang/Comparable<TT;>;>(Ljava/util/List<-TT;>;)TT;^Ljava/io/IOException;", simple: true)!.Value;
        Assert.Equal("<T extends Comparable<T>>", method.TypeParameters);
        Assert.Equal(["List<? super T>"], method.Parameters);
        Assert.Equal("T", method.Return);
        Assert.Equal(["IOException"], method.Throws);
        Assert.Equal("Outer<String>.Inner<Integer>", Descriptors.FieldSignature("Lp/Outer<Ljava/lang/String;>.Inner<Ljava/lang/Integer;>;", simple: true));

        // Nested a thousand deep: refused, not recursed into.
        string deep = string.Concat(Enumerable.Repeat("Ljava/util/List<", 1000)) + "Ljava/lang/Object;" + string.Concat(Enumerable.Repeat(">;", 1000));
        Assert.Null(Descriptors.FieldSignature(deep));
    }

    [Fact]
    public void AManifestIsReadWithContinuationsAndSections()
    {
        var manifest = JarManifest.Parse("Manifest-Version: 1.0\r\nMain-Class: com.example.Ma\r\n in\r\nClass-Path: lib/a.jar  lib/b.jar\r\n\r\nName: x/Y.class\r\nSHA-256-Digest: abc\r\n\r\n");

        Assert.Equal("com/example/Main", manifest.MainClass);
        Assert.Equal(["lib/a.jar", "lib/b.jar"], manifest.ClassPath);
        Assert.Equal(1, manifest.EntrySections);
        Assert.Null(manifest["SHA-256-Digest"]);   // a per-entry attribute is not a main one
    }

    // --- the reading -------------------------------------------------------------

    [Fact]
    public void PackagesNestingAndTheEntryPointComeFromTheClassesThemselves()
    {
        var reading = Reading(SampleJar());

        Assert.Equal(BytecodeKind.Jvm, reading.Kind);
        Assert.Equal("Java 17", reading.Platform);
        Assert.Equal("61.0", reading.FormatVersion);
        Assert.Equal(["com.example", "com.example.shapes"], reading.Namespaces.Select(n => n.Name));

        var greeter = Assert.Single(reading.Namespaces[0].Types);
        Assert.Equal("com.example.Greeter", greeter.FullName);
        Assert.Equal(["1", "Inner"], greeter.NestedTypes.Select(t => t.Name));
        Assert.Equal("com.example.Greeter.Inner", greeter.NestedTypes[1].FullName);
        Assert.Equal("com.example.Greeter$1", greeter.NestedTypes[0].FullName);
        Assert.Contains("com/example/Greeter$Inner", greeter.NestedTypes[1].OtherNames);

        Assert.Equal("main(String[]) : void", reading.EntryPoint!.Signature);
        Assert.Equal("interface", reading.Namespaces[1].Types[0].KindName);

        var signatures = greeter.Members.Select(m => m.Signature).ToList();
        Assert.Contains("Greeter(String)", signatures);
        Assert.Contains("greet(String) : String", signatures);
        Assert.Contains("BIG : long", signatures);
    }

    [Fact]
    public void ANestingLoopOnlyACraftedFileHasLeavesBothClassesAtTheTop()
    {
        byte[] a = new SyntheticClass("p/A").InnerClass("p/A", "p/B", "A").Build();
        byte[] b = new SyntheticClass("p/B").InnerClass("p/B", "p/A", "B").Build();
        var reading = Reading(SyntheticClass.Jar([("p/A.class", a), ("p/B.class", b)]));

        Assert.Equal(2, reading.Namespaces.Single().Types.Count);
    }

    [Fact]
    public void TheBytecodeListingNamesWhatTheConstantPoolOnlyNumbers()
    {
        var reading = Reading(SampleJar());
        var greeter = TypeOf(reading, "com/example/Greeter");
        string type = reading.Render(greeter, null, JvmReading.BytecodeView);
        string greet = reading.Render(greeter, MemberOf(greeter, "greet"), JvmReading.BytecodeView);
        string main = reading.Render(greeter, MemberOf(greeter, "main"), JvmReading.BytecodeView);

        Assert.Contains("public class com.example.Greeter", type, StringComparison.Ordinal);
        Assert.Contains("public static final java.lang.String GREETING = \"Hello\";", type, StringComparison.Ordinal);
        Assert.Contains("static final long BIG = 1234567890123L;", type, StringComparison.Ordinal);
        Assert.Contains("public int choose(int arg0)", type, StringComparison.Ordinal);
        Assert.Contains("// nested class com.example.Greeter.Inner", type, StringComparison.Ordinal);

        Assert.Contains("public java.lang.String greet(java.lang.String who)", greet, StringComparison.Ordinal);
        Assert.Contains("ldc             \"Hello, \"", greet, StringComparison.Ordinal);
        Assert.Contains("invokevirtual   java.lang.StringBuilder.append(java.lang.String) : java.lang.StringBuilder", greet, StringComparison.Ordinal);
        Assert.Contains("aload_1  // who", greet, StringComparison.Ordinal);
        Assert.Contains("// line 8", greet, StringComparison.Ordinal);

        Assert.Contains("getstatic       java.lang.System.out : java.io.PrintStream", main, StringComparison.Ordinal);
        Assert.Contains("invokedynamic   run() : java.lang.Runnable  // bootstrap java.lang.invoke.LambdaMetafactory.metafactory", main, StringComparison.Ordinal);
        Assert.Contains("com.example.Greeter.lambda$main$0", main, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => reading.Render(greeter, null, "kotlin"));
    }

    [Fact]
    public void ABrokenClassIsLeftOutAndNamedAndADuplicateIsKeptOnce()
    {
        byte[] duplicate = Greeter();
        var jar = JarImage.FromBytes(SampleJar(("broken/X.class", [0xCA, 0xFE, 0xBA, 0xBE, 0, 0]), ("copy/Greeter.class", duplicate)));
        var reading = new JvmReading(jar);

        Assert.Equal(4, jar.Classes.Count);
        Assert.Contains(jar.Warnings, w => w.Contains("broken/X.class", StringComparison.Ordinal));
        Assert.Contains(jar.Warnings, w => w.Contains("copy/Greeter.class declares com/example/Greeter", StringComparison.Ordinal));
        Assert.Equal("com/example/Greeter.class", reading.FindType("com/example/Greeter")!.Class.Entry.Name);
    }

    [Fact]
    public void ReferencesAndStringsAreReadOutOfEveryMethod()
    {
        var reading = Reading(SampleJar());
        var references = reading.References;
        var greeter = TypeOf(reading, "com/example/Greeter");

        // Who calls greet: main.
        var callers = references.To("com/example/Greeter", "greet");
        Assert.Equal("main", Assert.Single(callers).FromMember.Name);

        // Outside the archive: the lambda body calls Runtime.exec, and the name resolves in any spelling.
        Assert.Equal("lambda$main$0", Assert.Single(references.To("java/lang/Runtime", "exec")).FromMember.Name);
        Assert.Equal(("java/lang/Runtime", "exec"), reading.ReferencedName("java.lang.Runtime::exec"));
        Assert.Equal(("java/lang/Runtime", (string?)null), reading.ReferencedName("java.lang.Runtime"));

        // The lambda's body is reached only through its bootstrap handle.
        var handle = Assert.Single(references.To("com/example/Greeter", "lambda$main$0"));
        Assert.Equal(JvmReferenceKind.Handle, handle.Kind);

        Assert.Contains(references.From(MemberOf(greeter, "<init>")), r => r.Kind == JvmReferenceKind.Write && r.Name == "name");
        Assert.Superset(new HashSet<string> { "Hello, ", "world", "you", "calc.exe", "Hello" }, references.Strings.Select(s => s.Text).ToHashSet());
        Assert.Equal(-1, references.Strings.Single(s => s.Text == "Hello").Offset);   // a constant value, loaded by no code
    }

    // --- annotations keyed by member ---------------------------------------------

    [Fact]
    public void AnAnnotationKeyIsTheInternalNamePlusNameAndDescriptor()
    {
        var reading = Reading(SampleJar());
        var greeter = TypeOf(reading, "com/example/Greeter");

        Assert.Equal("com/example/Greeter", reading.AnnotationKey(greeter, null));
        Assert.Equal("com/example/Greeter.greet(Ljava/lang/String;)Ljava/lang/String;", reading.AnnotationKey(greeter, MemberOf(greeter, "greet")));
        Assert.Equal("com/example/Greeter.name:Ljava/lang/String;", reading.AnnotationKey(greeter, MemberOf(greeter, "name")));
    }

    [Fact]
    public void MemberAnnotationsRoundTripThroughTheProjectFileAndShowInTheListing()
    {
        string path = WriteJar("greeter.jar", SampleJar());
        var image = (JarImage)BinaryImage.Load(path);
        const string key = "com/example/Greeter.greet(Ljava/lang/String;)Ljava/lang/String;";

        var members = new MemberAnnotationStore();
        members.SetName(key, "salute");
        members.SetComment(key, "builds the greeting");
        string saved = SpydateProject.Save(image, new AnnotationStore(), members: members)!;
        Assert.Equal(path + SpydateProject.Extension, saved);
        Assert.Contains("\"member\": \"com/example/Greeter.greet(Ljava/lang/String;)Ljava/lang/String;\"", File.ReadAllText(saved), StringComparison.Ordinal);

        var reloaded = new MemberAnnotationStore();
        var result = SpydateProject.LoadFor(BinaryImage.Load(path), new AnnotationStore(), members: reloaded);
        Assert.True(result.Loaded);
        Assert.Equal(1, result.Applied);
        Assert.Equal("salute", reloaded.Get(key)!.Name);

        var reading = new JvmReading((JarImage)BinaryImage.Load(path), reloaded);
        var greeter = TypeOf(reading, "com/example/Greeter");
        string listing = reading.Render(greeter, MemberOf(greeter, "greet"), JvmReading.BytecodeView);
        Assert.Contains("// renamed: salute", listing, StringComparison.Ordinal);
        Assert.Contains("// builds the greeting", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveMergesMemberKeysExactlyAndKeepsAnotherWritersOnes()
    {
        string path = WriteJar("greeter.jar", SampleJar());
        var image = BinaryImage.Load(path);

        // Two writers, each loaded before the other saved; Java names differ only in case here.
        var window = new MemberAnnotationStore();
        var agent = new MemberAnnotationStore { Source = AnnotationSource.Agent };
        window.SetName("a/B.x()V", "lower");
        agent.SetName("a/B.X()V", "upper");
        SpydateProject.Save(image, new AnnotationStore(), members: window);
        SpydateProject.Save(image, new AnnotationStore(), members: agent);

        var both = new MemberAnnotationStore();
        SpydateProject.LoadFor(image, new AnnotationStore(), members: both);
        Assert.Equal("lower", both.Get("a/B.x()V")!.Name);
        Assert.Equal("upper", both.Get("a/B.X()V")!.Name);
        Assert.Equal(AnnotationSource.Agent, both.Get("a/B.X()V")!.Source);

        // A caller that knows nothing of members — a native save — leaves them in the file.
        SpydateProject.Save(image, new AnnotationStore(), notes: new NoteStore());
        var still = new MemberAnnotationStore();
        SpydateProject.LoadFor(image, new AnnotationStore(), members: still);
        Assert.Equal(2, still.Count);

        // Clearing one removes it from the file.
        both.SetName("a/B.x()V", "");
        SpydateProject.Save(image, new AnnotationStore(), members: both);
        var after = new MemberAnnotationStore();
        SpydateProject.LoadFor(image, new AnnotationStore(), members: after);
        Assert.Null(after.Get("a/B.x()V"));
        Assert.NotNull(after.Get("a/B.X()V"));
    }

    // --- the MCP surface ----------------------------------------------------------

    private SessionStore OpenStore(out string path)
    {
        path = WriteJar("greeter.jar", SampleJar());
        var store = new SessionStore();
        store.Set(BinarySession.Open(path, McpOptions.Default));
        return store;
    }

    [Fact]
    public void TheOverviewSaysWhatTheArchiveHoldsAndHowItRuns()
    {
        string text = new SessionTools(OpenStore(out _), McpOptions.Default).GetOverview();

        Assert.Contains("JAR (zip), 6 files: 4 class files, 2 other files", text, StringComparison.Ordinal);
        Assert.Contains("Main-Class com.example.Greeter", text, StringComparison.Ordinal);
        Assert.Contains("greeter.jar, Java 17, format 61.0", text, StringComparison.Ordinal);
        Assert.Contains("main(String[]) : void", text, StringComparison.Ordinal);
        Assert.Contains("read_file(\"greeter.jar!/\")", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentFindsReadsAndCrossReferencesAJar()
    {
        var store = OpenStore(out _);
        var navigation = new NavigationTools(store);
        var code = new CodeTools(store);

        Assert.Contains("com.example.Greeter::greet(String) : String", navigation.FindSymbol("greet"), StringComparison.Ordinal);
        Assert.Contains("ldc             \"Hello, \"", code.ReadFunction("com.example.Greeter::greet", view: "bytecode"), StringComparison.Ordinal);
        Assert.Contains("\"bytecode\"", code.ReadFunction("com.example.Greeter::greet", view: "asm"), StringComparison.Ordinal);

        string callers = navigation.Xrefs("com.example.Greeter::greet");
        Assert.Contains("com.example.Greeter::main(String[]) : void", callers, StringComparison.Ordinal);

        string exec = navigation.Xrefs("java.lang.Runtime::exec");
        Assert.Contains("lambda$main$0", exec, StringComparison.Ordinal);
        Assert.Contains("outside this archive", exec, StringComparison.Ordinal);

        Assert.Contains("java.lang.Runtime::exec(String) : Process", navigation.Xrefs("com.example.Greeter::lambda$main$0", direction: "from"), StringComparison.Ordinal);
        Assert.Contains("java.lang.Runtime", navigation.ListImports(), StringComparison.Ordinal);
        Assert.Contains("\"calc.exe\"", new StringTools(store).FindStrings("calc"), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFileReadsAndListsTheOpenArchivesEntries()
    {
        var tools = new SessionTools(OpenStore(out _), McpOptions.Default);

        string listing = tools.ReadFile("greeter.jar!/");
        Assert.Contains("config/app.properties", listing, StringComparison.Ordinal);
        Assert.Contains("deflated", listing, StringComparison.Ordinal);

        Assert.Contains("mode=fast", tools.ReadFile("greeter.jar!/config/app.properties", @as: "utf8"), StringComparison.Ordinal);
        Assert.Contains("Did you mean: config/app.properties", tools.ReadFile("greeter.jar!/app.properties"), StringComparison.Ordinal);
        Assert.Contains("is not the open archive", tools.ReadFile("other.jar!/x"), StringComparison.Ordinal);
    }

    [Fact]
    public void NativeToolsSayAJarHasNoMachineCodeRatherThanNothingIsOpen()
    {
        var store = OpenStore(out _);

        Assert.Contains("is a Java archive", new NavigationTools(store).ListFunctions(), StringComparison.Ordinal);
        Assert.Contains("is a Java archive", new CodeTools(store).Disassemble("0x1000"), StringComparison.Ordinal);
        Assert.Contains("is a Java archive", new CodeTools(store).ReadData("0x1000"), StringComparison.Ordinal);
        Assert.DoesNotContain("no binary is open", new NavigationTools(store).ListFunctions(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentsAnnotationOnAMemberIsSavedAndReadBack()
    {
        var store = OpenStore(out string path);
        var annotations = new AnnotationTools(store, McpOptions.Default);

        string done = annotations.Annotate("com.example.Greeter::lambda$main$0", name: "launchCalculator", comment: "runs calc.exe");
        Assert.Contains("is now called launchCalculator", done, StringComparison.Ordinal);
        Assert.Contains("saved to", done, StringComparison.Ordinal);
        Assert.True(File.Exists(path + SpydateProject.Extension));

        Assert.Contains("com/example/Greeter.lambda$main$0()V", annotations.ListAnnotations(), StringComparison.Ordinal);
        Assert.Contains("name      launchCalculator", annotations.ReadAnnotation("com.example.Greeter::lambda$main$0"), StringComparison.Ordinal);
        Assert.Contains("member    lambda$main$0()V  launchCalculator; runs calc.exe", annotations.ReadAnnotation("com.example.Greeter"), StringComparison.Ordinal);
        Assert.Contains("// renamed: launchCalculator (agent)", new CodeTools(store).ReadFunction("com.example.Greeter::lambda$main$0"), StringComparison.Ordinal);

        // A new session on the same file sees it.
        var again = new SessionStore();
        again.Set(BinarySession.Open(path, McpOptions.Default));
        Assert.Contains("launchCalculator", new AnnotationTools(again, McpOptions.Default).ListAnnotations(), StringComparison.Ordinal);
    }
}
