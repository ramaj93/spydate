using System.Diagnostics;
using Spydate.Core.Jvm;
using Spydate.Decompiler.Jvm;

namespace Spydate.Tests;

/// <summary>
/// The Java view against real javac output: <c>Fixtures/Java/fixtures/Shapes.java</c> — joins that are not a
/// branch's post-dominator, exits from nested loops, try/finally, synchronized, fall-through switches, conditional
/// expressions, booleans and chars kept as ints — and <c>Sugar.java</c> — lambdas, for-each, string and enum
/// switches, switch expressions, try-with-resources, assert, enums, records, annotations, inner, anonymous and
/// local classes — and <c>Legacy.java</c>, compiled for Java 8, where inner classes reach private members through
/// <c>access$000</c> methods and enum switches go through switch maps — compiled with and without debug
/// information, decompiled, compiled again from the decompiled text, and run: the copy must print exactly what the
/// original prints.
///
/// That is the one check that says the output means what the bytecode means, not just that it looks like Java.
/// It needs a JDK: the tests look for one on JAVA_HOME, on PATH and in Android Studio's bundled runtime, and are
/// skipped on a machine without one.
/// </summary>
public sealed class JavacRoundTripTests
{
    private static readonly Lazy<Compiled?> Built = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record Compiled(string Jdk, string Root, string WithDebug, string WithoutDebug, IReadOnlyDictionary<string, string> Expected);

    [SkippableTheory]
    [InlineData("Shapes", true)]
    [InlineData("Shapes", false)]
    [InlineData("Sugar", true)]
    [InlineData("Sugar", false)]
    [InlineData("Legacy", true)]
    [InlineData("Legacy", false)]
    public void EveryMethodIsStructuredWithoutAGoto(string name, bool debug)
    {
        var compiled = Require();
        string java = Decompile(debug ? compiled.WithDebug : compiled.WithoutDebug, name);

        Assert.DoesNotContain("goto", java, StringComparison.Ordinal);
        Assert.DoesNotContain("// warning", java, StringComparison.Ordinal);
        Assert.DoesNotContain("monitorenter", java, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Throwable", java, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("Shapes", true)]
    [InlineData("Shapes", false)]
    [InlineData("Sugar", true)]
    [InlineData("Sugar", false)]
    [InlineData("Legacy", true)]
    [InlineData("Legacy", false)]
    public void TheDecompiledClassCompilesAndBehavesLikeTheOriginal(string name, bool debug)
    {
        var compiled = Require();
        string java = Decompile(debug ? compiled.WithDebug : compiled.WithoutDebug, name);

        // Nested, anonymous and local classes are written inside the class: one source file compiles them all again.
        string work = Path.Combine(compiled.Root, $"again-{name}-{(debug ? "g" : "nog")}");
        Directory.CreateDirectory(Path.Combine(work, "src", "fixtures"));
        string source = Path.Combine(work, "src", "fixtures", name + ".java");
        File.WriteAllText(source, java);
        var (javacExit, javacOutput) = Run(Path.Combine(compiled.Jdk, "javac"), $"-nowarn -encoding UTF-8 -d \"{Path.Combine(work, "out")}\" \"{source}\"");
        Assert.True(javacExit == 0, $"the decompiled class does not compile:\n{javacOutput}\n{java}");

        var (exit, output) = Run(Path.Combine(compiled.Jdk, "java"), $"-cp \"{Path.Combine(work, "out")}\" fixtures.{name}");
        Assert.Equal(0, exit);
        Assert.Equal(compiled.Expected[name], output);
    }

    [SkippableFact]
    public void TheSourcesShapesComeBack()
    {
        string java = Decompile(Require().WithDebug, "Shapes");

        Assert.Contains("for (int i = 0; i < values.length; i++) {", java, StringComparison.Ordinal);
        Assert.Contains("do {", java, StringComparison.Ordinal);
        Assert.Contains("} while (n != 0);", java, StringComparison.Ordinal);
        Assert.Contains("return x >= lo && x <= hi;", java, StringComparison.Ordinal);
        Assert.Contains("seen |= v < 0;", java, StringComparison.Ordinal);
        Assert.Contains("return a > b ? (a > c ? a : c) : (b > c ? b : c);", java, StringComparison.Ordinal);
        Assert.Contains("result = strict ? \"less (strict)\" : \"less\";", java, StringComparison.Ordinal);
        Assert.Contains("synchronized (LOCK) {", java, StringComparison.Ordinal);
        Assert.Contains("} finally {", java, StringComparison.Ordinal);
        Assert.Contains("String w = it.next();", java, StringComparison.Ordinal);
        Assert.Contains("Map<Character, Integer> counts = new HashMap<>();", java, StringComparison.Ordinal);
        Assert.Contains("counts.put(c, old == null ? 1 : old + 1);", java, StringComparison.Ordinal);
        Assert.Contains("out.add(i);", java, StringComparison.Ordinal);
        Assert.Contains("long total = a + b;", java, StringComparison.Ordinal);
        Assert.Contains("for (int i = 0; i < texts.length; i++) {", java, StringComparison.Ordinal);
        Assert.Contains("if (skipNegative) {\n                        continue;", java.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("} catch (RuntimeException t) {\n                if (seen == null) {", java.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [SkippableFact]
    public void TheLanguagesShortcutsAndNestedClassesComeBack()
    {
        string java = Decompile(Require().WithDebug, "Sugar");

        Assert.Contains("import java.util.function.Supplier;", java, StringComparison.Ordinal);
        Assert.Contains("private static final int[] PRIMES = new int[] {2, 3, 5, 7, 11};", java, StringComparison.Ordinal);
        Assert.Contains("private final List<String> log = new ArrayList<>();", java, StringComparison.Ordinal);
        Assert.Contains("for (int v : values) {", java, StringComparison.Ordinal);
        Assert.Contains("for (String w : words) {", java, StringComparison.Ordinal);
        Assert.Contains("switch (day) {", java, StringComparison.Ordinal);
        Assert.Contains("case \"mon\":", java, StringComparison.Ordinal);
        Assert.Contains("case RED:", java, StringComparison.Ordinal);
        Assert.Contains("return switch (k) {", java, StringComparison.Ordinal);
        Assert.Contains("case 1, 2 -> 10;", java, StringComparison.Ordinal);
        Assert.Contains("yield t + 1;", java, StringComparison.Ordinal);
        Assert.Contains("in.forEach(x -> out.add(x + offset));", java, StringComparison.Ordinal);
        Assert.Contains("Function<Integer, String> f = String::valueOf;", java, StringComparison.Ordinal);
        Assert.Contains("Supplier<List<String>> make = ArrayList::new;", java, StringComparison.Ordinal);
        Assert.Contains("IntBinaryOperator op = (a, b) -> a * b + offset;", java, StringComparison.Ordinal);
        Assert.Contains("try (BufferedReader r = new BufferedReader(new StringReader(text))) {", java, StringComparison.Ordinal);
        Assert.Contains("while ((line = r.readLine()) != null) {", java, StringComparison.Ordinal);
        Assert.Contains("assert x >= 0 : \"negative\";", java, StringComparison.Ordinal);
        Assert.Contains("Runnable r = new Runnable() {", java, StringComparison.Ordinal);
        Assert.Contains("return captured + 1;", java, StringComparison.Ordinal);
        Assert.Contains("class Local {", java, StringComparison.Ordinal);
        Assert.Contains("RED(\"r\"),", java, StringComparison.Ordinal);
        Assert.Contains("BLUE(\"b\") {", java, StringComparison.Ordinal);
        Assert.Contains("public record Point(int x, int y) {", java, StringComparison.Ordinal);
        Assert.Contains("public Point {", java, StringComparison.Ordinal);
        Assert.Contains("public class Tally {", java, StringComparison.Ordinal);
        Assert.Contains("@Note(value = \"sums\", weight = 3)", java, StringComparison.Ordinal);
        Assert.Contains("int weight() default 1;", java, StringComparison.Ordinal);
        Assert.Contains("Arrays.asList(\"a\", \"b\", \"c\")", java, StringComparison.Ordinal);
        Assert.Contains("private static final Supplier<String> LATER = () -> Sugar.TAIL.get(0);", java, StringComparison.Ordinal);
        Assert.Contains("public static class Holder extends ArrayList<Holder.Item> {", java, StringComparison.Ordinal);
        Assert.Contains("return (c >= lo && c <= hi) != negated;", java, StringComparison.Ordinal);
        Assert.Contains("widen((byte) 3, (short) 4)", java, StringComparison.Ordinal);
        Assert.Contains("pick((String) null, \"x\")", java, StringComparison.Ordinal);
        Assert.Contains("call((Supplier<String>) () -> \"s\")", java, StringComparison.Ordinal);
        Assert.Contains("count(\"none\") + count(\"two\", 1, 2)", java, StringComparison.Ordinal);
        Assert.Contains("IntFunction<String[]> make = String[]::new;", java, StringComparison.Ordinal);
        Assert.DoesNotContain("lambda$", java, StringComparison.Ordinal);
        Assert.DoesNotContain("this$0", java, StringComparison.Ordinal);
        Assert.DoesNotContain("val$", java, StringComparison.Ordinal);
        Assert.DoesNotContain("$VALUES", java, StringComparison.Ordinal);
        Assert.DoesNotContain("$assertionsDisabled", java, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Java8sAccessorsAndSwitchMapsAreWrittenOut()
    {
        string java = Decompile(Require().WithDebug, "Legacy");

        Assert.Contains("return Legacy.this.secret + this.bonus;", java, StringComparison.Ordinal);
        Assert.Contains("Legacy.this.secret = value;", java, StringComparison.Ordinal);
        Assert.Contains("return Legacy.this.twice(Legacy.this.secret);", java, StringComparison.Ordinal);
        Assert.Contains("switch (unit) {", java, StringComparison.Ordinal);
        Assert.Contains("case SECONDS:", java, StringComparison.Ordinal);
        Assert.Contains("Peek other = legacy.new Peek(1);", java, StringComparison.Ordinal);
        Assert.Contains("new Secret(42).value", java, StringComparison.Ordinal);
        Assert.Contains("super(\"derived\");", java, StringComparison.Ordinal);
        Assert.DoesNotContain("access$", java, StringComparison.Ordinal);
        Assert.DoesNotContain("$SwitchMap$", java, StringComparison.Ordinal);
        Assert.DoesNotContain("requireNonNull", java, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void WithoutDebugInformationBooleansAndCharsAreStillThemselves()
    {
        string java = Decompile(Require().WithoutDebug, "Shapes");

        Assert.Contains("boolean var1 = false;", java, StringComparison.Ordinal);
        Assert.Contains("for (int var5 : arg0) {", java, StringComparison.Ordinal);
        Assert.Contains("var1 |= var5 < 0;", java, StringComparison.Ordinal);
        Assert.Contains("== 'a' ||", java, StringComparison.Ordinal);
        Assert.Contains("? 'C' : 'F';", java, StringComparison.Ordinal);
        Assert.Contains("} catch (NumberFormatException var2) {", java, StringComparison.Ordinal);
        Assert.Contains("} catch (NullPointerException var2_2) {", java, StringComparison.Ordinal);
    }

    private static Compiled Require()
    {
        var compiled = Built.Value;
        Skip.If(compiled is null, "no JDK found (JAVA_HOME, PATH, Android Studio's jbr)");
        return compiled!;
    }

    private static string Decompile(string jar, string name)
    {
        var reading = new JvmReading(JarImage.Load(jar));
        return reading.Render(reading.FindType($"fixtures/{name}")!, null, JvmReading.JavaView);
    }

    private static Compiled? Build()
    {
        if (FindJdk() is not { } jdk)
        {
            return null;
        }

        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Java", "fixtures");
        string sources = string.Join(' ', new[] { "Shapes", "Sugar" }.Select(n => $"\"{Path.Combine(fixtures, n + ".java")}\""));
        string root = Path.Combine(Path.GetTempPath(), "spydate-javac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };

        string Jar(string flag, string name)
        {
            string classes = Path.Combine(root, name);
            var (exit, output) = Run(Path.Combine(jdk, "javac"), $"{flag} -encoding UTF-8 -d \"{classes}\" {sources}");
            var (legacyExit, legacyOutput) = Run(Path.Combine(jdk, "javac"), $"{flag} --release 8 -Xlint:-options -encoding UTF-8 -d \"{classes}\" \"{Path.Combine(fixtures, "Legacy.java")}\"");
            if (exit != 0 || legacyExit != 0)
            {
                throw new InvalidOperationException($"javac failed on the fixture: {output}{legacyOutput}");
            }

            var entries = Directory.EnumerateFiles(classes, "*.class", SearchOption.AllDirectories)
                .Select(f => (Path.GetRelativePath(classes, f).Replace('\\', '/'), File.ReadAllBytes(f)));
            string jar = Path.Combine(root, name + ".jar");
            File.WriteAllBytes(jar, SyntheticClass.Jar(entries));
            return classes;
        }

        string withDebug = Jar("-g", "g");
        string withoutDebug = Jar("-g:none", "nog");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in new[] { "Shapes", "Sugar", "Legacy" })
        {
            var (runExit, output) = Run(Path.Combine(jdk, "java"), $"-cp \"{withDebug}\" fixtures.{name}");
            if (runExit != 0)
            {
                throw new InvalidOperationException($"the fixture {name} does not run: {output}");
            }

            expected[name] = output;
        }

        return new Compiled(jdk, root, withDebug + ".jar", withoutDebug + ".jar", expected);
    }

    /// <summary>A JDK's bin directory: one with javac, from JAVA_HOME, PATH or Android Studio's bundled runtime.</summary>
    private static string? FindJdk()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("JAVA_HOME") is { } home ? Path.Combine(home, "bin") : null,
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "jbr", "bin"));
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c, OperatingSystem.IsWindows() ? "javac.exe" : "javac")));
    }

    private static (int Exit, string Output) Run(string program, string arguments)
    {
        var start = new ProcessStartInfo(program, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }
}
