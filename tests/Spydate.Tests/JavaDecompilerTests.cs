using Spydate.Core.Jvm;
using Spydate.Core.Project;
using Spydate.Core.Readings;
using Spydate.Decompiler.Jvm;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// The in-house Java view: hand-built methods whose source is known, decompiled and checked for the Java they
/// came from — a loop with <c>&amp;&amp;</c>, a try/catch, a switch on values, constructor chaining, concatenation,
/// <c>new</c>, a lambda — plus names the project gave, and the hostile shapes that must never take the process down.
/// </summary>
public sealed class JavaDecompilerTests
{
    private static JvmReading Reading(params (string Name, byte[] Bytes)[] classes)
        => Reading(null, classes);

    private static JvmReading Reading(MemberAnnotationStore? names, params (string Name, byte[] Bytes)[] classes)
        => new(JarImage.FromBytes(SyntheticClass.Jar(classes)), names);

    private static string Java(JvmReading reading, string type, string? member = null)
    {
        var jvmType = reading.FindType(type)!;
        var target = member is null ? null : jvmType.Members.First(m => m.Name == member);
        return reading.Render(jvmType, target, JvmReading.JavaView);
    }

    /// <summary>
    /// <c>static int count(int[] a, int limit) { int n = 0; for (int i = 0; i &lt; a.length &amp;&amp; a[i] &lt; limit; i++) n++; return n; }</c>
    /// </summary>
    private static byte[] Loops()
    {
        var c = new SyntheticClass("demo/Loops");
        c.AddMethod(0x0009, "count", "([II)I",
            [
                0x03, 0x3D, 0x03, 0x3E,             // n = 0; i = 0
                0x1D, 0x2A, 0xBE, 0xA2, 0x00, 0x13, // 4: if (i >= a.length) goto 26
                0x2A, 0x1D, 0x2E, 0x1B, 0xA2, 0x00, 0x0C, // 10: if (a[i] >= limit) goto 26
                0x84, 0x02, 0x01,                   // 17: n++
                0x84, 0x03, 0x01,                   // 20: i++
                0xA7, 0xFF, 0xED,                   // 23: goto 4
                0x1C, 0xAC,                         // 26: return n
            ],
            maxStack: 2, maxLocals: 4,
            locals: [(0, 28, "a", "[I", 0), (0, 28, "limit", "I", 1), (2, 26, "n", "I", 2), (4, 22, "i", "I", 3)]);
        return c.Build();
    }

    /// <summary><c>static int parse(String s) { try { return Integer.parseInt(s); } catch (NumberFormatException e) { return -1; } }</c></summary>
    private static byte[] Tries()
    {
        var c = new SyntheticClass("demo/Tries");
        ushort parse = c.Method("java/lang/Integer", "parseInt", "(Ljava/lang/String;)I");
        c.AddMethod(0x0009, "parse", "(Ljava/lang/String;)I",
            [0x2A, 0xB8, .. SyntheticClass.U2(parse), 0xAC, 0x4C, 0x02, 0xAC],
            maxStack: 1, maxLocals: 2,
            handlers: [(0, 5, 5, "java/lang/NumberFormatException")],
            locals: [(0, 8, "s", "Ljava/lang/String;", 0), (6, 2, "e", "Ljava/lang/NumberFormatException;", 1)]);
        return c.Build();
    }

    /// <summary><c>static String name(int k) { switch (k) { case 1: return "one"; case 10: return "ten"; default: return "many"; } }</c></summary>
    private static byte[] Switches()
    {
        var c = new SyntheticClass("demo/Switches");
        ushort one = c.String("one");
        ushort ten = c.String("ten");
        ushort many = c.String("many");
        c.AddMethod(0x0009, "name", "(I)Ljava/lang/String;",
            [
                0x1A, 0xAB, 0x00, 0x00,
                .. SyntheticClass.U4(33), .. SyntheticClass.U4(2),
                .. SyntheticClass.U4(1), .. SyntheticClass.U4(27),
                .. SyntheticClass.U4(10), .. SyntheticClass.U4(30),
                0x12, (byte)one, 0xB0, 0x12, (byte)ten, 0xB0, 0x12, (byte)many, 0xB0,
            ],
            maxStack: 1, maxLocals: 1,
            locals: [(0, 37, "k", "I", 0)]);
        return c.Build();
    }

    [Fact]
    public void ALoopWithAndReadsAsTheLoopItWas()
    {
        string java = Java(Reading(("demo/Loops.class", Loops())), "demo/Loops", "count");

        Assert.Contains("public static int count(int[] a, int limit) {", java, StringComparison.Ordinal);
        Assert.Contains("int n = 0;", java, StringComparison.Ordinal);
        Assert.Contains("while (i < a.length && a[i] < limit) {", java, StringComparison.Ordinal);
        Assert.Contains("n++;", java, StringComparison.Ordinal);
        Assert.Contains("return n;", java, StringComparison.Ordinal);
        Assert.DoesNotContain("goto", java, StringComparison.Ordinal);
    }

    [Fact]
    public void ATryCatchKeepsItsHandlerAndItsVariable()
    {
        string java = Java(Reading(("demo/Tries.class", Tries())), "demo/Tries", "parse");

        Assert.Contains("try {", java, StringComparison.Ordinal);
        Assert.Contains("return Integer.parseInt(s);", java, StringComparison.Ordinal);
        Assert.Contains("} catch (NumberFormatException e) {", java, StringComparison.Ordinal);
        Assert.Contains("return -1;", java, StringComparison.Ordinal);
        Assert.DoesNotContain("goto", java, StringComparison.Ordinal);
    }

    [Fact]
    public void ASwitchIsLabelledByItsValues()
    {
        string java = Java(Reading(("demo/Switches.class", Switches())), "demo/Switches", "name");

        Assert.Contains("switch (k) {", java, StringComparison.Ordinal);
        Assert.Contains("case 1:", java, StringComparison.Ordinal);
        Assert.Contains("case 10:", java, StringComparison.Ordinal);
        Assert.Contains("default:", java, StringComparison.Ordinal);
        Assert.Contains("return \"ten\";", java, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectsCallsConcatenationAndLambdasReadAsJava()
    {
        var reading = Reading(("com/example/Greeter.class", JarTests.Greeter()));
        string java = Java(reading, "com/example/Greeter");

        Assert.Contains("package com.example;", java, StringComparison.Ordinal);
        Assert.Contains("public class Greeter {", java, StringComparison.Ordinal);
        Assert.Contains("public static final String GREETING = \"Hello\";", java, StringComparison.Ordinal);
        Assert.Contains("static final long BIG = 1234567890123L;", java, StringComparison.Ordinal);

        // The constructor stores its argument, and its call to Object() says nothing.
        Assert.Contains("public Greeter(String name) {", java, StringComparison.Ordinal);
        Assert.Contains("this.name = name;", java, StringComparison.Ordinal);
        Assert.DoesNotContain("super()", java, StringComparison.Ordinal);

        Assert.Contains("return new StringBuilder().append(\"Hello, \").append(who).toString();", java, StringComparison.Ordinal);
        Assert.Contains("System.out.println(new Greeter(\"world\").greet(\"you\"));", java, StringComparison.Ordinal);
        Assert.Contains("((Runnable) Greeter::lambda$main$0).run();", java, StringComparison.Ordinal);
        Assert.Contains("Runtime.getRuntime().exec(\"calc.exe\");", java, StringComparison.Ordinal);
        Assert.Contains("case 0:", java, StringComparison.Ordinal);
        Assert.Contains("return 2;", java, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesTheProjectGaveShowWhereverTheMemberIsUsed()
    {
        var names = new MemberAnnotationStore();
        names.SetName("com/example/Greeter.lambda$main$0()V", "launchCalculator");
        names.SetName("com/example/Greeter.greet(Ljava/lang/String;)Ljava/lang/String;", "salute");
        var reading = Reading(names, ("com/example/Greeter.class", JarTests.Greeter()));
        string java = Java(reading, "com/example/Greeter");

        Assert.Contains("Greeter::launchCalculator", java, StringComparison.Ordinal);
        Assert.Contains(".salute(\"you\")", java, StringComparison.Ordinal);
        Assert.Contains("public String salute(String who) {", java, StringComparison.Ordinal);
        Assert.Contains("// renamed: salute", java, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJavaViewIsTheReadingsFirstAndTheBytecodeStaysBehindIt()
    {
        var reading = Reading(("demo/Loops.class", Loops()));
        Assert.Equal([JvmReading.JavaView, JvmReading.BytecodeView], reading.Views);

        var store = new SessionStore();
        store.Set(new BinarySession("loops.jar", reading.Jar, null, null, DiscoveryState.None, bytecode: reading));
        var code = new CodeTools(store);
        Assert.Contains("while (i < a.length && a[i] < limit) {", code.ReadFunction("demo.Loops::count"), StringComparison.Ordinal);
        Assert.Contains("if_icmpge", code.ReadFunction("demo.Loops::count", view: "bytecode"), StringComparison.Ordinal);
    }

    // --- hostile ---------------------------------------------------------------------

    [Fact]
    public void AThreeThousandDeepExpressionIsSpilledNotRecursedInto()
    {
        // 1 + 1 + 1 ... three thousand times, as one expression on the stack.
        var code = new List<byte> { 0x04 };
        for (int i = 0; i < 3000; i++)
        {
            code.AddRange([0x04, 0x60]);
        }

        code.Add(0xAC);
        var c = new SyntheticClass("demo/Deep");
        c.AddMethod(0x0009, "deep", "()I", [.. code], maxStack: 2, maxLocals: 0);
        string java = Java(Reading(("demo/Deep.class", c.Build())), "demo/Deep", "deep");

        Assert.Contains("return ", java, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be decompiled", java, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoThousandNestedLoopsCostTimeNotTheProcess()
    {
        // Two thousand back edges, each to an earlier header: loops nested two thousand deep.
        const int depth = 2000;
        var code = new List<byte>();
        for (int i = 0; i < depth; i++)
        {
            code.Add(0x00);
        }

        for (int j = 0; j < depth; j++)
        {
            int at = code.Count;
            int target = depth - 1 - j;
            code.AddRange([0x1A, 0x9A]);                  // iload_0; ifne back
            code.AddRange(SyntheticClass.U2(target - (at + 1)));
        }

        code.Add(0xB1);
        var c = new SyntheticClass("demo/Nested");
        c.AddMethod(0x0009, "nested", "(I)V", [.. code], maxStack: 1, maxLocals: 1);
        string java = Java(Reading(("demo/Nested.class", c.Build())), "demo/Nested", "nested");

        Assert.Contains("nested(int arg0)", java, StringComparison.Ordinal);
    }

    [Fact]
    public void FlippedBytesNeverEscapeTheJavaView()
    {
        byte[] original = JarTests.Greeter();
        var random = new Random(4321);
        for (int round = 0; round < 1500; round++)
        {
            byte[] bytes = (byte[])original.Clone();
            for (int flips = random.Next(1, 4); flips > 0; flips--)
            {
                bytes[random.Next(8, bytes.Length)] = (byte)random.Next(256);
            }

            JvmReading reading;
            try
            {
                reading = Reading(("x.class", bytes));
            }
            catch (ClassFormatException)
            {
                continue;
            }

            foreach (var type in reading.Namespaces.SelectMany(n => n.Types))
            {
                _ = reading.Render(type, null, JvmReading.JavaView);
            }
        }
    }
}
