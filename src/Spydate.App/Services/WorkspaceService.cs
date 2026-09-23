using System.IO;
using Spydate.Core.Binary;
using Spydate.Core.Jvm;
using Spydate.Core.PE;
using Spydate.Core.Project;
using Spydate.Core.Readings;
using Spydate.Decompiler.Jvm;
using Spydate.Decompiler.Managed;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;

namespace Spydate.App.Services;

/// <summary>Everything loaded for one file: the image (PE or ELF) plus the native and/or managed analysis objects.</summary>
public sealed class OpenedBinary : IDisposable
{
    public OpenedBinary(IBinaryImage image, BinaryAnalysis? analysis, ManagedAssembly? managed, string? managedLoadError, ProjectLoadResult? project, PatchStore? patches = null, BreakpointStore? breakpoints = null, NoteStore? notes = null, IBytecodeReading? bytecode = null, MemberAnnotationStore? members = null)
    {
        MemberAnnotations = members;
        Image = image;
        Analysis = analysis;
        Bytecode = managed is null ? bytecode : new DotNetReading(managed);
        ManagedLoadError = managedLoadError;
        Project = project;
        NativeDecompiler = analysis is null ? null : new NativeDecompiler(analysis);
        Patches = patches ?? new PatchStore();
        Breakpoints = breakpoints ?? new BreakpointStore();
        Notes = notes ?? new NoteStore();
    }

    public IBinaryImage Image { get; }

    /// <summary>A .NET assembly: a PE with a CLR header. No other format has one.</summary>
    public bool IsManaged => Image is PeImage { IsManaged: true };

    /// <summary>
    /// Whether this binary can be run under the debugger. The debugger drives Windows processes, so only a PE
    /// can; an ELF is read, not run, and says so rather than failing somewhere in the launch.
    /// </summary>
    public bool CanDebug => Image.Format == BinaryFormat.Pe;

    /// <summary>The processor, in the format's own words: <c>Amd64</c> for a PE, <c>X86_64</c> for an ELF.</summary>
    public string MachineName => BinaryImage.MachineName(Image);

    /// <summary>The container and its width: <c>PE32+</c>, <c>ELF32</c>.</summary>
    public string ContainerName => BinaryImage.ContainerName(Image);

    /// <summary>Native analysis session; null when the machine type is not x86/x64.</summary>
    public BinaryAnalysis? Analysis { get; }

    public NativeDecompiler? NativeDecompiler { get; }

    /// <summary>
    /// The file's bytecode reading, when it has one — its .NET assembly today. The explorer's namespace tree
    /// and the overview read it through the seam; a second reading slots in beside the native one.
    /// </summary>
    public IBytecodeReading? Bytecode { get; }

    /// <summary>
    /// The .NET assembly behind <see cref="Bytecode"/>, when that is what it is; null for native images, for
    /// another reading, or when loading failed. The .NET-only views — C#/IL documents, the debugger, IL
    /// patches — go through this.
    /// </summary>
    public ManagedAssembly? Managed => (Bytecode as DotNetReading)?.Assembly;

    public string? ManagedLoadError { get; }

    /// <summary>
    /// Every method body with the address its IL sits at, for a managed image.
    ///
    /// What turns a click in the gutter into a breakpoint. A line of the IL listing carries a file
    /// address, and the runtime wants a method token and an offset; this is the only thing that can
    /// get from one to the other, in either direction.
    ///
    /// Lazy, because a native image never asks and a managed one only asks once something is run.
    /// </summary>
    public ManagedBodies? Bodies => _bodies ??= Managed is null ? null : ManagedBodies.Build(Managed);

    private ManagedBodies? _bodies;

    /// <summary>Outcome of looking for this image's <c>.spydate</c> file, when there is an analysis.</summary>
    public ProjectLoadResult? Project { get; }

    /// <summary>Byte changes recorded against this image, written only to a copy.</summary>
    public PatchStore Patches { get; }

    /// <summary>Breakpoints recorded against this image, so the gutter marks survive reopening.</summary>
    public BreakpointStore Breakpoints { get; }

    /// <summary>
    /// What has been learned about the binary as a whole — the keyed sections. Independent of the
    /// analysis, since a note belongs to no address; it exists even for an image that cannot be
    /// disassembled, so the notes an agent recorded about a managed-only assembly still have a home.
    /// </summary>
    public NoteStore Notes { get; }

    /// <summary>Names and comments the user has added; empty when the image cannot be analysed.</summary>
    public AnnotationStore? Annotations => Analysis?.Annotations;

    /// <summary>
    /// Names and comments keyed by member rather than address, for a file whose program has no addresses — a JAR.
    /// Null for every other file.
    /// </summary>
    public MemberAnnotationStore? MemberAnnotations { get; }

    /// <summary>True when there are annotations, patches, breakpoints or notes that have not been written to disk.</summary>
    public bool HasUnsavedAnnotations => Annotations is { IsDirty: true } || Patches.IsDirty || Breakpoints.IsDirty || Notes.IsDirty || MemberAnnotations is { IsDirty: true };

    /// <summary>
    /// Writes the project out, returning where it went (null when there was nothing to write). Notes go
    /// even when there is no analysis to carry annotations — an empty annotation store touches nothing
    /// in the file, so the merge keeps everything else and only the notes are written.
    /// </summary>
    public string? SaveProject() => SpydateProject.Save(Image, Annotations ?? new AnnotationStore(), Patches, Breakpoints, Notes, MemberAnnotations);

    public string DisplayName => Image.FileName;

    public void Dispose()
    {
        Managed?.Dispose();
        (Bytecode as IDisposable)?.Dispose();
    }
}

/// <summary>
/// The files that are open, and which of them the window is showing.
///
/// It used to hold one. Holding several changes almost nothing about what it does — a file is
/// loaded, watched, saved and disposed exactly as before — but it does mean the difference between
/// "the open file" and "the file in front of the reader", which were the same thing and are not any
/// more. <see cref="Current"/> is the second of those, and it is what everything outside a tab
/// (the debugger, the assistant) still asks for.
/// </summary>
public sealed class WorkspaceService : IDisposable
{
    private readonly List<OpenedBinary> _open = new();

    /// <summary>One project-file watcher per open file, so each is told about its own.</summary>
    private readonly Dictionary<OpenedBinary, ProjectFileWatcher> _watchers = new();

    /// <summary>Everything open, oldest first.</summary>
    public IReadOnlyList<OpenedBinary> Open => _open;

    /// <summary>The one the window is showing, or null when nothing is.</summary>
    public OpenedBinary? Current { get; private set; }

    public event EventHandler? CurrentChanged;

    /// <summary>
    /// Raised, off the UI thread, when an open binary's project file was rewritten by something
    /// else — an agent driving the MCP server, or another copy of Spydate. It names which, because
    /// the file it happened to is no longer necessarily the one being looked at.
    /// </summary>
    public event EventHandler<OpenedBinary>? ProjectChangedOnDisk;

    /// <summary>
    /// Loads a file and adds it to the open set. It does not become <see cref="Current"/> — where a
    /// newly opened file goes is the window's decision, and the reader may be asked about it.
    /// </summary>
    public async Task<OpenedBinary> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var opened = await Task.Run(() => Load(path), cancellationToken).ConfigureAwait(true);
        _open.Add(opened);
        Watch(opened);
        return opened;
    }

    /// <summary>The open file loaded from this path, or null. Opening one twice should find its tab.</summary>
    public OpenedBinary? FindByPath(string path)
        => _open.FirstOrDefault(b => string.Equals(b.Image.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shows one of the open files, or nothing.</summary>
    public void Activate(OpenedBinary? binary)
    {
        if (ReferenceEquals(Current, binary))
        {
            return;
        }

        Current = binary;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Closes one file: stops watching it, lets go of it, and moves off it if it was the one being
    /// shown. Saving anything unsaved is the caller's to do first, while it still knows what it had.
    /// </summary>
    public void Close(OpenedBinary binary)
    {
        ArgumentNullException.ThrowIfNull(binary);

        if (_watchers.Remove(binary, out var watcher))
        {
            watcher.Dispose();
        }

        _open.Remove(binary);
        binary.Dispose();

        if (ReferenceEquals(Current, binary))
        {
            Current = null;
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void CloseAll()
    {
        foreach (var binary in _open.ToList())
        {
            Close(binary);
        }
    }

    /// <summary>
    /// Re-reads a project file over the top of what is in memory. Replaces rather than merges: the
    /// file is already the merged truth, so anything no longer in it was removed on purpose and has
    /// to go from here too. Clearing announces each removal, which is how the names leave the symbol
    /// table as well as the store.
    /// </summary>
    public ProjectLoadResult? ReloadProject(OpenedBinary binary)
    {
        ArgumentNullException.ThrowIfNull(binary);

        if (binary.Analysis is not { } analysis)
        {
            // Still catch up on notes for a managed-only image, whose agent may have written some — and on the
            // member annotations of a JAR, which is all a JAR's annotations are.
            binary.Notes.Clear();
            binary.MemberAnnotations?.Clear();
            var result = SpydateProject.LoadFor(binary.Image, new AnnotationStore(), notes: binary.Notes, members: binary.MemberAnnotations);
            return binary.MemberAnnotations is null ? null : result;
        }

        analysis.Annotations.Clear();
        binary.Patches.Clear();
        binary.Notes.Clear();
        return SpydateProject.LoadFor(binary.Image, analysis.Annotations, binary.Patches, notes: binary.Notes);
    }

    private void Watch(OpenedBinary opened)
    {
        if (opened.Analysis is null && opened.MemberAnnotations is null)
        {
            return;   // nothing to annotate, so nothing to notice
        }

        var watcher = new ProjectFileWatcher(opened.Image);
        watcher.Changed += (_, _) => ProjectChangedOnDisk?.Invoke(this, opened);
        _watchers[opened] = watcher;
    }

    public void Dispose()
    {
        CloseAll();
    }

    /// <summary>Saves one file's annotations if any have changed. Returns where they went.</summary>
    public static string? SaveIfDirty(OpenedBinary? binary)
        => binary is { HasUnsavedAnnotations: true } ? binary.SaveProject() : null;

    /// <summary>
    /// Saves every open file that has unsaved work, returning where each went. Closing the window
    /// has to write all of them, not only the tab that happens to be in front.
    /// </summary>
    public IReadOnlyList<string> SaveAllIfDirty()
    {
        var written = new List<string>();
        foreach (var binary in _open)
        {
            try
            {
                if (SaveIfDirty(binary) is { } path)
                {
                    written.Add(path);
                }
            }
            catch (IOException)
            {
                // One file that cannot be written must not stop the others being saved.
            }
        }

        return written;
    }

    private static OpenedBinary Load(string path)
    {
        var image = BinaryImage.Load(path);

        // A JAR is only bytecode: no native analysis, annotations keyed by member. Its classes are parsed here,
        // off the UI thread, where the rest of opening happens.
        if (image is JarImage jar)
        {
            var members = new MemberAnnotationStore();
            var jarNotes = new NoteStore();
            var jarProject = SpydateProject.LoadFor(image, new AnnotationStore(), notes: jarNotes, members: members);
            var reading = new JvmReading(jar, members);

            // Every reference and string in the archive, which the Strings document asks for on the UI thread:
            // decoded now, in the background, so a large JAR's first look at its strings does not stall the window.
            _ = Task.Run(() => reading.References);
            return new OpenedBinary(image, null, null, null, jarProject, notes: jarNotes, bytecode: reading, members: members);
        }

        BinaryAnalysis? analysis = image.Architecture is Architecture.X86 or Architecture.X64 ? new BinaryAnalysis(image) : null;
        analysis?.LoadPdbSymbols();

        // Before discovery, so a renamed function is discovered under the name the user gave it.
        var patches = new PatchStore();
        var breakpoints = new BreakpointStore();
        var notes = new NoteStore();
        ProjectLoadResult? project;
        if (analysis is not null)
        {
            project = SpydateProject.LoadFor(image, analysis.Annotations, patches, breakpoints, notes);
        }
        else
        {
            // No analysis to carry annotations, but a managed-only assembly can still have notes an
            // agent wrote against it; load them over a throwaway store so they reappear in the window.
            SpydateProject.LoadFor(image, new AnnotationStore(), notes: notes);
            project = null;
        }

        // The .NET reading rides on a PE's CLR header; no other format carries one.
        ManagedAssembly? managed = null;
        string? managedError = null;
        if (image is PeImage { IsManaged: true })
        {
            try
            {
                managed = ManagedAssembly.Load(path);
                _ = managed.Namespaces; // force metadata load off the UI thread
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException)
            {
                managedError = ex.Message;
            }
        }

        return new OpenedBinary(image, analysis, managed, managedError, project, patches, breakpoints, notes);
    }
}
