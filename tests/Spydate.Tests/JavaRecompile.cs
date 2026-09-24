using System.Diagnostics;
using System.Text.RegularExpressions;
using Spydate.Core.Jvm;
using Spydate.Decompiler.Jvm;

namespace Spydate.Tests;

/// <summary>
/// The Java view's correctness harness: every top-level class of a JAR decompiled, written out as a source tree
/// (nested, anonymous and local classes and lambdas are inside their top-level class), and compiled again by javac
/// against the JAR, so a class the decompiled text refers to resolves either way. What javac rejects is counted per
/// file and grouped by message, the error's own names taken out, so the report says which kinds are left.
/// </summary>
internal static partial class JavaRecompile
{
    public sealed record Report(int Files, int FilesWithErrors, int Errors, IReadOnlyList<(int Count, string Message)> Kinds, string Output, string Classes)
    {
        public double Rate => Files == 0 ? 1 : (Files - FilesWithErrors) / (double)Files;

        public override string ToString()
            => $"{Files - FilesWithErrors} of {Files} files compile again ({Rate:P1}); {Errors} errors\n"
               + string.Join('\n', Kinds.Take(20).Select(k => $"  {k.Count,5} x {k.Message}"));
    }

    /// <summary>Decompiles every top-level class of <paramref name="jar"/> into <paramref name="work"/>/src and compiles the tree.</summary>
    public static Report Run(string jdk, string jar, string work, string? classpath = null)
    {
        string src = Path.Combine(work, "src");
        string classes = Path.Combine(work, "out");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(classes);

        var reading = new JvmReading(JarImage.Load(jar));
        var files = new List<string>();
        foreach (var type in reading.Namespaces.SelectMany(n => n.Types).OfType<JvmType>())
        {
            string path = Path.Combine(src, type.File.Name.Replace('/', Path.DirectorySeparatorChar) + ".java");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, reading.Render(type, null, JvmReading.JavaView));
            files.Add(path);
        }

        string list = Path.Combine(work, "files.txt");
        File.WriteAllLines(list, files.Select(f => $"\"{f.Replace('\\', '/')}\""));
        string cp = classpath is null ? jar : $"{jar}{Path.PathSeparator}{classpath}";
        var (_, output) = Execute(Path.Combine(jdk, "javac"), $"-nowarn -encoding UTF-8 -proc:none -Xmaxerrs 100000 -d \"{classes}\" -cp \"{cp}\" @\"{list}\"");

        var errors = ErrorLine().Matches(output).Select(m => (File: m.Groups[1].Value, Message: m.Groups[2].Value.Trim())).ToList();
        var kinds = errors.GroupBy(e => Names().Replace(e.Message, "…")).Select(g => (g.Count(), g.Key)).OrderByDescending(k => k.Item1).ToList();
        int failing = errors.Select(e => Path.GetFullPath(e.File)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new Report(files.Count, failing, errors.Count, kinds, output, classes);
    }

    public static (int Exit, string Output) Execute(string program, string arguments)
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

    [GeneratedRegex(@"^(.+\.java):\d+: error: (.+)$", RegexOptions.Multiline)]
    private static partial Regex ErrorLine();

    /// <summary>Type names in a message (anything with a capital, with its type arguments), so messages of one kind group together.</summary>
    [GeneratedRegex(@"[\w$.]*[A-Z][\w$.<>?,#\[\]]*")]
    private static partial Regex Names();
}
