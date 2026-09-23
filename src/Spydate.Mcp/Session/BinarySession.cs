using System.Diagnostics;
using Spydate.Core.Binary;
using Spydate.Core.Jvm;
using Spydate.Core.PE;
using Spydate.Core.Project;
using Spydate.Core.Readings;
using Spydate.Decompiler.Jvm;
using Spydate.Decompiler.Managed;
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
    private BytecodeIndex? _index;
    private ManagedReferences? _references;
    private ManagedBodies? _bodies;
    private readonly bool _ownsManaged;

    /// <param name="save">
    /// How annotations reach disk. Injectable so tests can watch a write without one landing in the
    /// per-user store of whoever is running them.
    /// </param>
    /// <param name="ownsManaged">
    /// Whether disposing this session disposes <paramref name="managed"/>. False when the assembly
    /// belongs to something with a longer life — the window's own open binary, which the assistant
    /// wraps rather than re-reads — so that closing the assistant's session does not pull the metadata
    /// out from under the views still showing it.
    /// </param>
    public BinarySession(
        string path,
        IBinaryImage image,
        BinaryAnalysis? analysis,
        ProjectLoadResult? project,
        DiscoveryState discovery,
        Func<IBinaryImage, AnnotationStore, string?>? save = null,
        PatchStore? patches = null,
        NoteStore? notes = null,
        ManagedAssembly? managed = null,
        string? managedLoadError = null,
        bool ownsManaged = true,
        IBytecodeReading? bytecode = null,
        MemberAnnotationStore? members = null)
    {
        // A lambda rather than the method group: SpydateProject.Save takes optional stores now, and a
        // group with defaulted parameters no longer converts on its own.
        Patches = patches ?? new PatchStore();
        Notes = notes ?? new NoteStore();
        MemberAnnotations = members;
        Save = save ?? ((image, annotations) => SpydateProject.Save(image, annotations, Patches, notes: Notes, members: MemberAnnotations));
        Path = path;
        Image = image;
        Analysis = analysis;
        Decompiler = analysis is null ? null : new NativeDecompiler(analysis);
        Project = project;
        Discovery = discovery;
        Bytecode = managed is null ? bytecode : new DotNetReading(managed);
        ManagedLoadError = managedLoadError;
        _ownsManaged = ownsManaged;

        if (analysis is not null)
        {
            // A rename replaces the Function object behind an address, so the cached order is stale
            // even though the count did not move.
            analysis.Annotations.Changed += (_, _) => _namesChanged = true;
        }
    }

    public string Path { get; }

    public IBinaryImage Image { get; }

    /// <summary>The CLR header, when this is a .NET assembly. Only a PE can be one.</summary>
    public ClrHeader? ClrHeader => (Image as PeImage)?.ClrHeader;

    /// <summary>A .NET assembly with no native code of its own: the IL is the program.</summary>
    public bool IsILOnly => ClrHeader?.IsILOnly == true;

    /// <summary>The processor, in the format's own words, for messages.</summary>
    public string MachineName => BinaryImage.MachineName(Image);

    /// <summary>The debugger runs Windows processes; any other format is read, not run.</summary>
    public bool CanDebug => Image.Format == BinaryFormat.Pe;

    /// <summary>Null when the image is not x86 or x64: there is nothing here that can read it.</summary>
    public BinaryAnalysis? Analysis { get; }

    public NativeDecompiler? Decompiler { get; }

    /// <summary>
    /// The file's bytecode reading, when it has one: its .NET assembly today. The tools that browse types
    /// and members — resolving a name, find_symbol, the overview — work through this and never name a format.
    /// </summary>
    public IBytecodeReading? Bytecode { get; }

    /// <summary>
    /// The assembly's metadata, and the C#/IL decompiler over it, when the reading is .NET and ILSpy could
    /// read it. Null for a native binary — and null for a managed one whose metadata could not be read,
    /// which <see cref="ManagedLoadError"/> distinguishes from the first case. What only .NET has — bodies
    /// at addresses, IL patching, P/Invokes, the debugger — is reached through this.
    /// </summary>
    public ManagedAssembly? Managed => (Bytecode as DotNetReading)?.Assembly;

    /// <summary>
    /// Whether the bytecode is the whole program, so the tools that browse should answer from it rather than
    /// from a native side that is only a loader: an IL-only assembly, whose x86 is the CLR stub, or a
    /// reading of a format with no native code of its own.
    /// </summary>
    public bool BytecodeIsTheProgram => Bytecode is not null && (IsILOnly || Bytecode.Kind != BytecodeKind.DotNet);

    /// <summary>Why <see cref="Managed"/> is null on a file that carries a CLR header.</summary>
    public string? ManagedLoadError { get; }

    /// <summary>
    /// Every type in the reading, nested ones included, flattened and indexed by name.
    ///
    /// Built once and held, because it is what every managed target resolves through:
    /// <see cref="IBytecodeReading.Namespaces"/> is a tree, and walking it per lookup would make
    /// resolving a name cost the size of the assembly.
    /// </summary>
    public BytecodeIndex? BytecodeIndex => _index ??= Bytecode is null ? null : new BytecodeIndex(Bytecode);

    /// <summary>
    /// The name index for a resolved reference, built once and kept, so reading several framework
    /// methods does not re-index the framework each time. The opened assembly's own index is
    /// <see cref="BytecodeIndex"/>; this is for the assemblies it references.
    /// </summary>
    public BytecodeIndex IndexFor(ManagedAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (_referenceIndexes)
        {
            if (!_referenceIndexes.TryGetValue(assembly, out var index))
            {
                _referenceIndexes[assembly] = index = new BytecodeIndex(new DotNetReading(assembly));
            }

            return index;
        }
    }

    private readonly Dictionary<ManagedAssembly, BytecodeIndex> _referenceIndexes = new();

    /// <summary>
    /// The body map for a resolved reference, built once and kept — the same as <see cref="Bodies"/>
    /// is for the opened assembly, so an address in a referenced module can be turned into the method
    /// it falls in.
    /// </summary>
    public ManagedBodies BodiesFor(ManagedAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (_referenceBodies)
        {
            if (!_referenceBodies.TryGetValue(assembly, out var bodies))
            {
                _referenceBodies[assembly] = bodies = ManagedBodies.Build(assembly);
            }

            return bodies;
        }
    }

    private readonly Dictionary<ManagedAssembly, ManagedBodies> _referenceBodies = new();

    /// <summary>
    /// Who refers to what, read out of the IL. Null for a native binary.
    ///
    /// Lazy, and worth being lazy about: it walks every method body in the assembly, which is the
    /// most expensive thing here and is wasted on a session that only ever reads one type.
    /// </summary>
    public ManagedReferences? References => _references ??= Managed is null ? null : ManagedReferences.Build(Managed);

    /// <summary>
    /// Every method body with the address its IL actually sits at. Null for a native binary.
    ///
    /// Kept apart from <see cref="References"/> and much cheaper: this reads a header per method
    /// rather than walking every instruction, and reading one method as IL should not pay for a scan
    /// of the whole assembly.
    /// </summary>
    public ManagedBodies? Bodies => _bodies ??= Managed is null ? null : ManagedBodies.Build(Managed);

    public ProjectLoadResult? Project { get; }

    public DiscoveryState Discovery { get; }

    /// <summary>Byte changes recorded against this image. Never written to any binary from here.</summary>
    public PatchStore Patches { get; }

    /// <summary>What has been learned about the binary as a whole — keyed sections, saved in the project.</summary>
    public NoteStore Notes { get; }

    /// <summary>
    /// Names and comments keyed by member rather than address, for a file whose program has no addresses — a JAR.
    /// Null for every other file, whose annotations live in <see cref="BinaryAnalysis.Annotations"/>.
    /// </summary>
    public MemberAnnotationStore? MemberAnnotations { get; }

    /// <summary>Writes the annotations out, returning where they went.</summary>
    public Func<IBinaryImage, AnnotationStore, string?> Save { get; }

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
        var image = BinaryImage.Load(full);

        // Before the native analysis, and independent of it. A .NET assembly built AnyCPU says
        // I386 in its machine field and so passes the x86 test below, but the bytes in its .text
        // are IL: the two readings of the same file are unrelated, and either can be the useful
        // one — a mixed-mode assembly has both, a NativeAOT publish has only the native side.
        var (managed, managedError) = LoadManaged(image, full);

        // A JAR is nothing but bytecode: no native analysis, its annotations keyed by member. Its classes are
        // parsed here, while the open is still the thing being waited for, rather than by the first tool.
        if (image is JarImage jar)
        {
            var members = new MemberAnnotationStore { Source = AnnotationSource.Agent };
            var jarNotes = new NoteStore { Source = AnnotationSource.Agent };
            var jarProject = SpydateProject.LoadFor(image, new AnnotationStore(), notes: jarNotes, members: members);
            var reading = new JvmReading(jar, members);
            return new BinarySession(full, image, null, jarProject, DiscoveryState.None, notes: jarNotes, bytecode: reading, members: members);
        }

        if (image.Architecture is not (Architecture.X86 or Architecture.X64))
        {
            return new BinarySession(full, image, null, null, DiscoveryState.None, managed: managed, managedLoadError: managedError);
        }

        var analysis = new BinaryAnalysis(image) { ResolveImportSignatures = true };
        analysis.Annotations.Source = AnnotationSource.Agent;
        analysis.LoadPdbSymbols();
        var notes = new NoteStore { Source = AnnotationSource.Agent };
        var project = SpydateProject.LoadFor(image, analysis.Annotations, notes: notes);

        var clock = Stopwatch.StartNew();
        var found = analysis.DiscoverAll(options.MaxFunctions, progress: null, cancellationToken);
        clock.Stop();

        var discovery = new DiscoveryState(found.Count, found.Count < options.MaxFunctions, clock.Elapsed);
        return new BinarySession(full, image, analysis, project, discovery, notes: notes, managed: managed, managedLoadError: managedError);
    }

    /// <summary>
    /// The assembly behind a CLR header, or why it could not be read. Never throws: a file that
    /// claims to be managed and is not must leave every other tool working, since a binary whose
    /// metadata is deliberately broken is a thing an analyst opens on purpose.
    /// </summary>
    private static (ManagedAssembly? Assembly, string? Error) LoadManaged(IBinaryImage image, string path)
    {
        if (image is not PeImage { IsManaged: true })
        {
            return (null, null);
        }

        try
        {
            var assembly = ManagedAssembly.Load(path);
            _ = assembly.Namespaces;   // forces the metadata read, so a failure lands here and not mid-answer
            return (assembly, null);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsManaged)
        {
            Managed?.Dispose();
            (Bytecode as IDisposable)?.Dispose();
        }

        _gate.Dispose();
    }
}
