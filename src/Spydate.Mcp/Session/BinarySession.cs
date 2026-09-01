using System.Diagnostics;
using Spydate.Core.PE;
using Spydate.Core.Project;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;

namespace Spydate.Mcp.Session;

/// <summary>How much of the image discovery got through, so every answer can say what it is based on.</summary>
public sealed record DiscoveryState(int Functions, bool Complete, TimeSpan Elapsed)
{
    public static DiscoveryState None { get; } = new(0, false, TimeSpan.Zero);

    public string Describe() => Complete
        ? $"{Functions} functions, discovery complete ({Elapsed.TotalSeconds:F1} s)"
        : $"{Functions} functions, discovery capped — results are partial ({Elapsed.TotalSeconds:F1} s)";
}

/// <summary>
/// One open binary and everything derived from it. There is one of these at a time: an agent reads a
/// program, and juggling several would cost every tool an extra parameter to save a case nobody has.
/// </summary>
public sealed class BinarySession : IDisposable
{
    /// <summary>
    /// Taken for the whole of every tool call. The engine's reads are safe concurrently, but
    /// discovery is not re-entrant on one instance, and an annotate-merge-save has to be atomic
    /// against another call reading half of it. An agent's calls are sequential anyway, so holding
    /// this costs nothing and removes a class of question.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<Function>? _functions;
    private int _functionsAt = -1;
    private bool _namesChanged;

    public BinarySession(
        string path,
        PeImage image,
        BinaryAnalysis? analysis,
        ProjectLoadResult? project,
        DiscoveryState discovery)
    {
        Path = path;
        Image = image;
        Analysis = analysis;
        Decompiler = analysis is null ? null : new NativeDecompiler(analysis);
        Project = project;
        Discovery = discovery;

        if (analysis is not null)
        {
            // A rename replaces the Function object behind an address, so the cached order is stale
            // even though the count did not move.
            analysis.Annotations.Changed += (_, _) => _namesChanged = true;
        }
    }

    public string Path { get; }

    public PeImage Image { get; }

    /// <summary>Null when the image is not x86 or x64: there is nothing here that can read it.</summary>
    public BinaryAnalysis? Analysis { get; }

    public NativeDecompiler? Decompiler { get; }

    public ProjectLoadResult? Project { get; }

    public DiscoveryState Discovery { get; private set; }

    /// <summary>
    /// The discovered functions in address order, cached. <see cref="BinaryAnalysis.Functions"/>
    /// allocates and re-sorts the whole set on every access, so a tool that touched it per row would
    /// be quadratic in the size of the image.
    /// </summary>
    public IReadOnlyList<Function> Functions
    {
        get
        {
            if (Analysis is null)
            {
                return Array.Empty<Function>();
            }

            if (_functions is null || _namesChanged || _functionsAt != Analysis.FunctionCount)
            {
                _functionsAt = Analysis.FunctionCount;
                _namesChanged = false;
                _functions = Analysis.Functions;
            }

            return _functions;
        }
    }

    /// <summary>Runs work with exclusive use of the session.</summary>
    public async Task<T> UseAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return work();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens a binary the way the window does — parse, symbols, then the project file *before*
    /// discovery, so functions are found under the names they were already given rather than being
    /// renamed afterwards.
    /// </summary>
    public static BinarySession Open(string path, McpOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        string full = System.IO.Path.GetFullPath(path);
        var image = PeImage.Load(full);
        if (!image.IsX86Family)
        {
            return new BinarySession(full, image, null, null, DiscoveryState.None);
        }

        var analysis = new BinaryAnalysis(image) { ResolveImportSignatures = true };
        analysis.Annotations.Source = AnnotationSource.Agent;
        analysis.LoadPdbSymbols();
        var project = SpydateProject.LoadFor(image, analysis.Annotations);

        var clock = Stopwatch.StartNew();
        var found = analysis.DiscoverAll(options.MaxFunctions, progress: null, cancellationToken);
        clock.Stop();

        var discovery = new DiscoveryState(found.Count, found.Count < options.MaxFunctions, clock.Elapsed);
        return new BinarySession(full, image, analysis, project, discovery);
    }

    public void Dispose() => _gate.Dispose();
}
