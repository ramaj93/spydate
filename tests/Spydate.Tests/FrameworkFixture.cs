using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Spydate.Tests;

/// <summary>
/// .NET Framework programs to debug, compiled by the compiler that ships with the runtime itself.
///
/// Built rather than found. The framework's own assemblies are poor examples — mscorlib references
/// nothing and carries no target-framework attribute — and an installed application would make a
/// test about whatever happened to be on the machine. Framework <c>csc</c> is C# 5, so a fixture's
/// source has to be too: no auto-property initialisers, no interpolated strings.
///
/// Cached by a hash of the source, not by name alone. A fixture edited while an old build of it sat
/// in the temp folder under the same name would otherwise go on testing the old program.
/// </summary>
internal static class FrameworkFixture
{
    private static readonly object Gate = new();

    /// <summary>The compiled program, or null when this machine has no .NET Framework compiler.</summary>
    internal static string? Build(string name, string source)
    {
        string compiler = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");

        if (!File.Exists(compiler))
        {
            return null;
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
        string folder = Path.Combine(Path.GetTempPath(), "spydate-framework-fixtures", $"{name}-{hash}");
        string exe = Path.Combine(folder, name + ".exe");

        // Test classes run in parallel, and two of them compiling one fixture into one folder at once
        // is one of them reading a half-written executable.
        lock (Gate)
        {
            if (File.Exists(exe))
            {
                return exe;
            }

            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, name + ".cs");
            File.WriteAllText(file, source);

            using var built = Process.Start(new ProcessStartInfo(compiler)
            {
                ArgumentList = { "-nologo", "-debug:full", "-platform:x64", "-out:" + exe, file },
                WorkingDirectory = folder,
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            built?.WaitForExit(60_000);
            return File.Exists(exe) ? exe : null;
        }
    }
}
