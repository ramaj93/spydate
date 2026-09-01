using System.Collections.Concurrent;
using Spydate.Core.PE;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// The real binaries the suite analyses, parsed and discovered once for the whole run.
///
/// Whole-image discovery of notepad takes a second or two, and a dozen test classes were each doing it
/// again from scratch — most of the suite's time went on repeating the same work rather than on testing
/// anything. Sharing is safe because analysis is built for it: <see cref="X86Disassembler"/> creates a
/// decoder per call, <c>FunctionDiscovery.Discover</c> keeps its whole state in locals, and the caches
/// inside <see cref="BinaryAnalysis"/> are concurrent.
///
/// It is safe for <em>readers</em> only. A test that renames something, writes an annotation, loads PDB
/// symbols, or wants different <see cref="DiscoveryOptions"/> must build its own analysis: the point of
/// those tests is the mutation, and it would be seen by every other test in the run.
/// </summary>
public static class Corpus
{
    public const string NotepadX64 = @"C:\Windows\System32\notepad.exe";
    public const string NotepadX86 = @"C:\Windows\SysWOW64\notepad.exe";

    /// <summary>
    /// The system directory for this process, which is 64-bit, so this is the real System32 rather than
    /// the SysWOW64 a 32-bit process would be redirected to.
    /// </summary>
    public static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <summary>
    /// A big, export-heavy, thoroughly optimised DLL: six tests want one, and they all want this one.
    /// </summary>
    public static string Kernel32X64 => Path.Combine(System32, "kernel32.dll");

    private static readonly ConcurrentDictionary<string, Lazy<PeImage>> Images = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<BinaryAnalysis>> Analyses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the binary is on this machine. Every test that uses one of these is written to pass
    /// vacuously without it, so the suite still runs somewhere that is not this Windows install.
    /// </summary>
    public static bool Has(string path) => File.Exists(path);

    /// <summary>The parsed image, read from disk once.</summary>
    public static PeImage Image(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Images.GetOrAdd(path, p => new Lazy<PeImage>(() => PeImage.Load(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// The image with whole-image discovery already run, under default options. Do not mutate it.
    /// </summary>
    public static BinaryAnalysis Analysed(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Analyses.GetOrAdd(path, p => new Lazy<BinaryAnalysis>(
            () =>
            {
                var analysis = new BinaryAnalysis(Image(p));
                analysis.DiscoverAll();
                return analysis;
            },
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
