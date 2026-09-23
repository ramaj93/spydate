using System.Diagnostics;
using Spydate.Core.Jvm;
using Spydate.Decompiler.Jvm;

namespace Spydate.Tests;

/// <summary>
/// The Java view against real javac output: <c>Fixtures/Java/fixtures/Shapes.java</c> — joins that are not a
/// branch's post-dominator, exits from nested loops, try/finally, synchronized, fall-through switches, conditional
/// expressions, booleans and chars kept as ints — compiled with and without debug information, decompiled,
/// compiled again from the decompiled text, and run: the copy must print exactly what the original prints.
///
/// That is the one check that says the output means what the bytecode means, not just that it looks like Java.
/// It needs a JDK: the tests look for one on JAVA_HOME, on PATH and in Android Studio's bundled runtime, and are
/// skipped on a machine without one.
/// </summary>
public sealed class JavacRoundTripTests
{
    private static readonly Lazy<Compiled?> Built = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record Compiled(string Jdk, string Root, string WithDebug, string WithoutDebug, string Expected);

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryMethodIsStructuredWithoutAGoto(bool debug)
    {
        var compiled = Require();
        string java = Decompile(debug ? compiled.WithDebug : compiled.WithoutDebug);

        Assert.DoesNotContain("goto", java, StringComparison.Ordinal);
        Assert.DoesNotContain("// warning", java, StringComparison.Ordinal);
        Assert.DoesNotContain("monitorenter", java, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Throwable", java, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDecompiledClassCompilesAndBehavesLikeTheOriginal(bool debug)
    {
        var compiled = Require();
        string java = Decompile(debug ? compiled.WithDebug : compiled.WithoutDebug);

        string work = Path.Combine(compiled.Root, debug ? "again-g" : "again-nog");
        Directory.CreateDirectory(Path.Combine(work, "src", "fixtures"));
        File.WriteAllText(Path.Combine(work, "src", "fixtures", "Shapes.java"), java);
        var (javacExit, javacOutput) = Run(Path.Combine(compiled.Jdk, "javac"), $"-nowarn -encoding UTF-8 -d \"{Path.Combine(work, "out")}\" \"{Path.Combine(work, "src", "fixtures", "Shapes.java")}\"");
        Assert.True(javacExit == 0, $"the decompiled class does not compile:\n{javacOutput}\n{java}");

        var (exit, output) = Run(Path.Combine(compiled.Jdk, "java"), $"-cp \"{Path.Combine(work, "out")}\" fixtures.Shapes");
        Assert.Equal(0, exit);
        Assert.Equal(compiled.Expected, output);
    }

    [SkippableFact]
    public void TheSourcesShapesComeBack()
    {
        string java = Decompile(Require().WithDebug);

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
        Assert.Contains("java.util.Map<Character, Integer> counts = new java.util.HashMap<>();", java, StringComparison.Ordinal);
        Assert.Contains("counts.put(c, old == null ? 1 : old + 1);", java, StringComparison.Ordinal);
        Assert.Contains("out.add(i);", java, StringComparison.Ordinal);
        Assert.Contains("long total = a + b;", java, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void WithoutDebugInformationBooleansAndCharsAreStillThemselves()
    {
        string java = Decompile(Require().WithoutDebug);

        Assert.Contains("boolean var1 = false;", java, StringComparison.Ordinal);
        Assert.Contains("var1 |= var2[var4] < 0;", java, StringComparison.Ordinal);
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

    private static string Decompile(string jar)
    {
        var reading = new JvmReading(JarImage.Load(jar));
        return reading.Render(reading.FindType("fixtures/Shapes")!, null, JvmReading.JavaView);
    }

    private static Compiled? Build()
    {
        if (FindJdk() is not { } jdk)
        {
            return null;
        }

        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Java", "fixtures", "Shapes.java");
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
            var (exit, output) = Run(Path.Combine(jdk, "javac"), $"{flag} -encoding UTF-8 -d \"{classes}\" \"{source}\"");
            if (exit != 0)
            {
                throw new InvalidOperationException($"javac failed on the fixture: {output}");
            }

            var entries = Directory.EnumerateFiles(classes, "*.class", SearchOption.AllDirectories)
                .Select(f => (Path.GetRelativePath(classes, f).Replace('\\', '/'), File.ReadAllBytes(f)));
            string jar = Path.Combine(root, name + ".jar");
            File.WriteAllBytes(jar, SyntheticClass.Jar(entries));
            return classes;
        }

        string withDebug = Jar("-g", "g");
        string withoutDebug = Jar("-g:none", "nog");
        var (runExit, expected) = Run(Path.Combine(jdk, "java"), $"-cp \"{withDebug}\" fixtures.Shapes");
        if (runExit != 0)
        {
            throw new InvalidOperationException($"the fixture does not run: {expected}");
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
