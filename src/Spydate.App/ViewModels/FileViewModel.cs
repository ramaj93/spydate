using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
using Spydate.App.ViewModels.Documents;
using Spydate.Core.Android;
using Spydate.Core.Archive;
using Spydate.Core.Binary;
using Spydate.Core.Elf;
using Spydate.Core.Jvm;
using Spydate.Core.PE;
using Spydate.Core.Project;
using Spydate.Core.Readings;
using Spydate.Core.Text;
using Spydate.Decompiler.Jvm;
using Spydate.Decompiler.Managed;
using Spydate.Disassembly;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Spydate.App.ViewModels;

/// <summary>
/// What an open file needs from the window around it.
///
/// A file has things to say — it logged a load, it patched an address — but the log and the status
/// bar belong to the window and are shared by everything in it. Passing this rather than the whole
/// <see cref="MainViewModel"/> keeps the direction of the dependency readable: a file may speak to
/// its window, and cannot reach into its siblings.
/// </summary>
public interface IShell
{
    /// <summary>Adds a line to the window's output log.</summary>
    void Log(string message);

    /// <summary>The window's status bar. Latest wins.</summary>
    string StatusText { get; set; }

    /// <summary>How the reader wants a module stepped into to be shown.</summary>
    ForeignModuleView ModuleView { get; }

    /// <summary>
    /// Shows a module stepped into as a read-only file tab of its own, reusing its tab if it
    /// already has one. Null when the window declined to open it.
    /// </summary>
    FileViewModel? ShowModuleTab(OpenedBinary module);

    /// <summary>Opens a file in a new tab of its own, without asking where: a binary taken out of the one in front.</summary>
    Task OpenInNewTabAsync(string path);
}

/// <summary>
/// One open binary, and everything the window shows about it: its explorer tree, its document tabs,
/// its xrefs, warnings and patches, and where the reader has been in it.
///
/// This used to be most of <see cref="MainViewModel"/>, on the assumption that there is one open
/// file. What made the split worth doing is that <c>CloseFile</c> had already written the list: the
/// eleven things it cleared to get back to an empty window are exactly the things that belong to a
/// file rather than to the window, and every one of them is here.
///
/// The debugger and the assistant are among them too: each file has its own, born here and dying in
/// <c>Close</c>. The debugger being per file is what lets two binaries be debugged at once; the
/// assistant being per file is what lets a conversation survive a tab switch and drive the process
/// its own binary starts. Only which model to talk to stays window-wide — see docs/MULTI-FILE.md.
/// </summary>
public sealed partial class FileViewModel : ObservableObject
{
    private readonly IFileDialogService _dialogs;
    private readonly IShell _shell;

    /// <summary>
    /// This file's debugger. The panel binds straight to it, so the Debug tab shows whichever tab
    /// is in front — and a session left running in another one carries on running.
    /// </summary>
    private readonly DebuggerViewModel _debugger;

    public DebuggerViewModel Debugger => _debugger;

    /// <summary>This file's assistant conversation. The panel binds to whichever tab is in front.</summary>
    public AssistantViewModel Assistant { get; }

    /// <summary>A process of this file's is up, so its tab can say so.</summary>
    public bool IsDebugging => _debugger.IsDebugging;

    /// <summary>
    /// A breakpoint came round in this file while the reader was in another tab, and they have not
    /// been to look yet. The tab marks it; going to the tab clears it.
    /// </summary>
    [ObservableProperty]
    private bool _hasUnseenStop;
    private CancellationTokenSource? _analysisCts;

    /// <summary>
    /// Analyses of modules other than this binary, keyed by file path, built on demand the first
    /// time execution stops in one (stepping into an imported DLL). A null value is a remembered
    /// failure — a module that could not be read or is not x86 — so it is not retried on every step.
    /// </summary>
    private readonly Dictionary<string, OpenedBinary?> _modules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This binary's imported function names to the module each comes from — "GetModuleHandleW"
    /// to "KERNEL32.dll" — built once from the import table so a click on an import name in a listing
    /// can find its module without scanning the imports every time the hand cursor asks.
    /// </summary>
    private Dictionary<string, string>? _imports;

    /// <summary>Modules a symbol-server PDB fetch has already been started for, so it runs once each.</summary>
    private readonly HashSet<string> _symbolsWarmed = new(StringComparer.OrdinalIgnoreCase);

    public FileViewModel(OpenedBinary binary, IFileDialogService dialogs, IShell shell, AssistantProvider assistantProvider)
    {
        Binary = binary;
        _dialogs = dialogs;
        _shell = shell;

        // Its own, born with it and dying with it. One per window was the same thing while a window
        // showed one file; now it is what lets two binaries be under a debugger at once, each with
        // its own breakpoints, its own stack and its own process.
        _debugger = new DebuggerViewModel(binary, dialogs);

        // The assistant is per file for the same reason: two binaries disagree completely about what
        // has been said about them, and a shared panel reloaded itself on every tab switch — losing
        // the place, and mid-turn writing one tab's answer under another's name. It drives this
        // file's debugger, and talks through the window-wide provider, which it hears change.
        Assistant = new AssistantViewModel(binary, _debugger, assistantProvider);
        _debugger.BreakpointsChanged += (_, _) => ReloadDocuments();
        _debugger.StoppedAt += (_, va) => ShowWhereItStopped(va);
        _debugger.NavigateRequested += (_, va) => ShowWhereItStopped(va);
        _debugger.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DebuggerViewModel.State) or nameof(DebuggerViewModel.IsStopped))
            {
                NotifyCaretCommands();
                OnPropertyChanged(nameof(IsDebugging));
            }
        };
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));

        // The tab shows a dot when there is work that is not on disk, so it has to hear about the
        // work. Both stores, because either can be written without the other — a rename through the
        // assistant, a patch from the menu.
        if (Binary.Annotations is { } annotations)
        {
            annotations.Changed += (_, _) => NotifyDirty();
        }

        Binary.Patches.Changed += (_, _) => NotifyDirty();

        // A JAR's names are keyed by member, in their own store; a rename there — from the menu, the assistant
        // or a project reload — shows in the tree without rebuilding it.
        if (Binary.MemberAnnotations is { } members)
        {
            members.Changed += (_, _) =>
            {
                NotifyDirty();
                if (ExplorerTreeBuilder.NamesFor(Binary) is { } names)
                {
                    Application.Current?.Dispatcher.Invoke(() => ExplorerTreeBuilder.Relabel(Explorer, names));
                }
            };
        }
    }

    /// <summary>The image, its analyses, its patches and its breakpoints. A file view model is one of these.</summary>
    public OpenedBinary Binary { get; }

    public string DisplayName => Binary.DisplayName;

    /// <summary>
    /// The whole path, for the tab's tooltip: two files of one name are told apart by where they
    /// are, and a strip of tabs is exactly where that happens.
    /// </summary>
    public string FullPath => Binary.Image.Path ?? Binary.DisplayName;

    /// <summary>Names, comments, patches or breakpoints that are not on disk yet.</summary>
    public bool IsDirty => Binary.HasUnsavedAnnotations;

    /// <summary>Discovery is running, so the tab shows it is still working.</summary>
    [ObservableProperty]
    private bool _isBusy;

    private void NotifyDirty()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => OnPropertyChanged(nameof(IsDirty)));
            return;
        }

        OnPropertyChanged(nameof(IsDirty));
    }

    public ObservableCollection<ExplorerNodeViewModel> Explorer { get; } = new();

    public ObservableCollection<DocumentViewModel> Documents { get; } = new();

    /// <summary>Parser and analysis warnings for this file.</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>References to the address the active document is about.</summary>
    public ObservableCollection<XrefRow> Xrefs { get; } = new();

    [ObservableProperty]
    private string _xrefsCaption = "Xrefs";

    public bool HasDocuments => Documents.Count > 0;

    [ObservableProperty]
    private DocumentViewModel? _activeDocument;

    [ObservableProperty]
    private ExplorerNodeViewModel? _selectedNode;

    [ObservableProperty]
    private string _analysisText = string.Empty;

    [ObservableProperty]
    private string _gotoAddressText = string.Empty;

    private void Log(string message) => _shell.Log(message);

    private string StatusText
    {
        get => _shell.StatusText;
        set => _shell.StatusText = value;
    }

    /// <summary>
    /// The work that turns a loaded binary into a window full of it: the tree, the warnings, the
    /// overview tab, the patches list and function discovery.
    ///
    /// Separate from the constructor because it opens documents and starts a background analysis,
    /// and a constructor that does either is one that cannot be called from a test or a tab strip
    /// without side effects.
    /// </summary>
    public void Begin(bool discover = true)
    {
        Explorer.Clear();
        Explorer.Add(ExplorerTreeBuilder.Build(Binary));

        Warnings.Clear();
        foreach (string w in Binary.Image.Warnings)
        {
            Warnings.Add(w);
        }

        if (Binary.ManagedLoadError is { } managedError)
        {
            Warnings.Add($"Managed decompiler could not load the assembly: {managedError}");
        }

        if (Binary.Analysis is null && !Binary.IsManaged && Binary.Image is not (JarImage or ApkImage))
        {
            Warnings.Add($"Machine type {Binary.MachineName} is not supported by the native disassembler (x86/x64 only).");
        }

        if (Binary.Image is ElfImage elf)
        {
            // An ELF carries its own symbols; there is no PDB to go looking for.
            Log(elf.StaticSymbols.Count > 0
                ? $"Symbols: {elf.StaticSymbols.Count:N0} in .symtab, {elf.DynamicSymbols.Count:N0} in .dynsym, {elf.UnwindRanges.Count:N0} functions in .eh_frame."
                : $"Stripped (no .symtab): {elf.DynamicSymbols.Count:N0} dynamic symbols, {elf.UnwindRanges.Count:N0} functions in .eh_frame.");
        }
        else if (Binary.Image is JarImage jar)
        {
            Log($"{jar.Classes.Count:N0} classes in {Binary.Bytecode?.Namespaces.Count ?? 0} packages; nothing in a JAR has an address, so there are no functions to discover.");
        }
        else if (Binary.Image is ApkImage apk)
        {
            Log($"{apk.Classes.Count:N0} classes in {Binary.Bytecode?.Namespaces.Count ?? 0} packages from {apk.DexFiles.Count} DEX file(s); nothing in Dalvik code has an address, so there are no functions to discover.");
            foreach (string warning in apk.Warnings.Take(20))
            {
                Warnings.Add(warning);
            }
        }
        else if (Binary.Analysis?.Pdb is { } pdb)
        {
            Log(pdb.Loaded
                ? $"Loaded {pdb.SymbolsAdded:N0} symbols from {pdb.Path}."
                : $"No symbols: {pdb.Reason}");
        }

        if (Binary.Project is { } project)
        {
            if (project.Loaded)
            {
                Log($"Loaded {project.Applied} annotation(s) from {project.Path}.");
            }
            else if (project.Path is not null)
            {
                Warnings.Add(project.Reason ?? "The project file was not loaded.");
                Log($"Project not loaded: {project.Reason}");
            }
        }

        if (Warnings.Count > 0)
        {
            Log($"{Warnings.Count} warning(s) — see the Warnings tab.");
        }

        OpenTarget(new OverviewTarget());

        // The assistant and any MCP client write into this same store, so the tab follows what
        // they do rather than only what the menus do.
        Binary.Patches.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(RefreshPatches);
        RefreshPatches();

        // Not for a module stepped into. Walking the whole of ntdll to fill a tree nobody asked for
        // costs seconds and hundreds of megabytes, and the functions that are actually wanted —
        // the ones execution reaches — are discovered one at a time as it reaches them.
        if (discover && Binary.Analysis is { } analysis)
        {
            _ = RunDiscoveryAsync(analysis);
        }
        else if (Binary.Image is JarImage)
        {
            AnalysisText = "Java · bytecode";
        }
        else if (Binary.Image is ApkImage)
        {
            AnalysisText = "Android · Dalvik bytecode";
        }
        else
        {
            AnalysisText = "functions found as they are reached";
        }
    }

    /// <summary>
    /// Opens the function an address is in and puts the execution arrow on it. What a stop in a
    /// module shown as its own tab needs: the module is not the thing being debugged, so its own
    /// debugger has no session — but the arrow is drawn from an address, not from a session.
    /// </summary>
    public void ShowStoppedFunction(Function function, ulong va)
    {
        ArgumentNullException.ThrowIfNull(function);

        OpenTarget(new DisassemblyTarget(function.EntryVa, NameOf(function.EntryVa)));
        _debugger.ExecutionAddress = va;
        ScrollTo(va);
    }

    /// <summary>
    /// Stops anything still running for this file. Called when its tab goes away.
    ///
    /// The debugger first, and not optionally: a debugged process does not outlive its session, and
    /// a tab closed while something was running must not leave an untrusted binary executing with
    /// nothing left in the window attached to it.
    /// </summary>
    public void Close()
    {
        _debugger.Dispose();
        Assistant.Dispose();
        _analysisCts?.Cancel();
        Documents.Clear();
        _modules.Clear();
        _imports = null;
        _symbolsWarmed.Clear();
        Explorer.Clear();
        Warnings.Clear();
        ActiveDocument = null;
        ClearHistory();
        AnalysisText = string.Empty;
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnActiveDocumentPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnActiveDocumentPropertyChanged;
            _ = newValue.EnsureLoadedAsync();
        }

        RefreshXrefs(newValue?.Address, newValue?.AddressLength ?? 1);
    }

    /// <summary>Documents that track a selection move their address, so the panel follows.</summary>
    private void OnActiveDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is DocumentViewModel doc && e.PropertyName is nameof(DocumentViewModel.Address) or nameof(DocumentViewModel.AddressLength))
        {
            RefreshXrefs(doc.Address, doc.AddressLength);
        }

        // Whatever moved, the caret may now be on something different, and the menu items that act on
        // it have to say so. A Rename that is enabled over a blank line and then does nothing is
        // worse than one that is greyed out: the first teaches that the command is unreliable.
        NotifyCaretCommands();
    }

    /// <summary>Redraws every open listing, for a change that only alters how they are marked.</summary>
    public void ReloadDocuments()
    {
        foreach (var document in Documents)
        {
            _ = document.ReloadAsync();
        }
    }

    private string NameOf(ulong va) => Binary.Analysis is { } a ? a.NameFor(va) : $"0x{va:X}";

    /// <summary>
    /// Shows where execution stopped, in the function it stopped inside.
    ///
    /// The function containing the address, not the address itself. Opening a document straight at
    /// a stop address asks the engine for a function starting there, and it makes one — so stepping
    /// through <c>gstring_resize</c> spawned <c>loc_180005F45</c>, then <c>loc_180005F46</c>, each a
    /// fabricated overlapping copy of the same code, each in its own tab. The marker did move; it
    /// moved into a new document every time, so the tab being watched never changed and the whole
    /// thing looked frozen. It also minted a junk function into the analysis on every single step.
    ///
    /// Stopping somewhere no function has been found is still worth showing, and that case opens a
    /// listing at the address as before — it is the one place where making one is the right answer.
    /// </summary>
    public void ShowWhereItStopped(ulong va)
    {
        // A .NET stop is inside a method body, and those bytes are IL. Handing them to the
        // disassembler produces a page of plausible-looking x86 — `add dh, [edx+1]`, `call far` —
        // invented from bytes that are nothing of the sort, and that is what the window used to do
        // on every step: leave the C# the reader was stepping through and open a fabricated native
        // listing named after the address. The method the reader is in is the right answer, and it
        // is usually the tab they are already looking at.
        if (Managed(va) is { } member)
        {
            OpenTarget(member);
            return;
        }

        if (Binary is { Managed: not null })
        {
            // Somewhere in a .NET file with no method of its own — compiler-generated code whose
            // declaring type is not listed, or a stop the body index does not cover. Staying put
            // beats opening a listing of something that is not machine code.
            return;
        }

        if (Binary.Analysis is { } analysis && analysis.FunctionContaining(va) is { } function)
        {
            // Already reading this function where the arrow can follow it — the disassembly, the
            // decompiled C, or the two side by side? Then leave the reader on it: the ExecutionAddress
            // binding moves the arrow on its own, and reopening the disassembly is what made stepping
            // in a decompiled or side-by-side tab snap back to assembly on every press. The graph is
            // an ICaretContext too but carries no arrow, so it is deliberately not one of these.
            if (ActiveDocument is CodeDocumentViewModel or SplitCodeDocumentViewModel
                && (ActiveDocument as ICaretContext)?.OwningFunctionVa == function.EntryVa)
            {
                ScrollTo(va);
                return;
            }

            OpenTarget(new DisassemblyTarget(function.EntryVa, NameOf(function.EntryVa)));
            ScrollTo(va);
            return;
        }

        // Not in this binary: execution stepped into another module — an imported DLL, the CRT,
        // a system library. Show that module's own code rather than disassembling its runtime bytes as
        // if they were the file on screen (which used to fabricate a listing at the wrong base).
        // Nothing more to do when it works; fall through to the raw listing only when it cannot.
        if (ShowForeignModule(va))
        {
            return;
        }

        // A raw listing is only meaningful for an address in this binary. A foreign address we
        // could not resolve to its module — a system DLL with nothing readable on disk — is left as it
        // is rather than disassembled here into nonsense at the wrong base.
        if (Binary.Image.VaToRva(va) is not null)
        {
            OpenTarget(new DisassemblyTarget(va, NameOf(va)));
        }
    }

    /// <summary>
    /// Shows the function a runtime address falls in when it is in a module other than this
    /// binary — the point of "step into" reaching an imported DLL. Analyses that module's file on
    /// demand, translates the runtime address into it, opens the function's disassembly, and puts the
    /// execution arrow on it. False when the address is in no known module, the module cannot be read,
    /// or the translated address lands nowhere in it — the caller then falls back to a raw listing.
    /// </summary>
    private bool ShowForeignModule(ulong runtimeVa)
    {
        if (_debugger.ModuleContaining(runtimeVa) is not { } module)
        {
            return false;
        }

        // This binary is not foreign to itself. An address it can show — in a discovered
        // function, or a stub, or a byte in a gap between its sections — belongs to the file on screen
        // and its own fallback listing, never to a second copy of it opened as a "module". Caught two
        // ways: an address the image maps, and an address the module map still attributes to it (its
        // load region runs past its last mapped section).
        var opened = Binary.Image;
        if (opened.VaToRva(runtimeVa) is not null
            || string.Equals(System.IO.Path.GetFileName(module.Path), opened.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ModuleAnalysis(module.Path) is not { Analysis: { } analysis, Image: { } image } moduleBinary)
        {
            return false;
        }

        // Runtime address → this module's own listing address (its file base plus the run-time bias),
        // confirmed to land somewhere real in the module. A wrong guess at which module it was in — an
        // address in a gap, or past this module — fails the map and is left to the fallback.
        ulong listingVa = runtimeVa - module.Base + image.ImageBase;
        if (image.VaToRva(listingVa) is null)
        {
            return false;
        }

        // Step-into lands on the callee's first instruction, so the stop address is the function's
        // entry; a known function that already covers it is reused. Discovery is per-function and lazy,
        // so this does not walk the whole DLL.
        var function = analysis.FunctionContaining(listingVa)
                       ?? analysis.GetOrDiscoverFunction(listingVa);
        string moduleName = System.IO.Path.GetFileName(module.Path);

        // A tab of its own, when that is what the reader has asked for. It keeps this file's own
        // documents to this file, at the cost of a tab appearing while stepping — which is why it
        // is off unless chosen.
        if (_shell.ModuleView == ForeignModuleView.OwnTab
            && _shell.ShowModuleTab(moduleBinary) is { } tab)
        {
            tab.ShowStoppedFunction(function, listingVa);
            return true;
        }

        // Already reading this foreign function where the arrow can follow it — its disassembly or its
        // decompiled C? Leave the reader on it: only the arrow moves. Without this a step inside a
        // foreign module reopened the disassembly on every press, snapping a decompiled foreign tab
        // back to assembly — the same trap the opened-binary path fixed above. Otherwise open it in the
        // view the reader is already in (decompiled carries forward), matching a fresh foreign stop to
        // where the reader was looking rather than always dropping to disassembly.
        bool alreadyHere = ActiveDocument is CodeDocumentViewModel { OwningFunctionVa: { } owning }
            && owning == function.EntryVa
            && ModuleNameOf(ActiveDocument.Key) is { } activeModule
            && string.Equals(activeModule, moduleName, StringComparison.OrdinalIgnoreCase);

        if (!alreadyHere)
        {
            if (PreferredCodeView() == CodeView.Decompiled && moduleBinary.NativeDecompiler is not null)
            {
                ShowModulePseudoC(moduleBinary, moduleName, function);
            }
            else
            {
                ShowModuleDisassembly(moduleBinary, moduleName, function);
            }
        }

        // The arrow lives on a single shared address. Set it into this module's listing space; this
        // binary's own documents draw no arrow for it, since it is not in their range.
        _debugger.ExecutionAddress = listingVa;
        ScrollTo(listingVa);
        return true;
    }

    /// <summary>
    /// Scrolls the document in front of the reader to an address.
    ///
    /// A listing opens on its function's entry, which is the top; stopping some way into a long
    /// function would otherwise leave the header on screen and the arrow below the fold. Only the
    /// active document, and only when it has a line for the address — a document showing something
    /// else is not moved.
    /// </summary>
    private void ScrollTo(ulong va)
    {
        switch (ActiveDocument)
        {
            case CodeDocumentViewModel code: code.RevealAddress(va); break;
            case SplitCodeDocumentViewModel split: split.RevealAddress(va); break;
            default: break;
        }
    }

    /// <summary>
    /// The on-demand analysis of a module that is not this binary, cached by path. Null — and a
    /// remembered null — when it cannot be read or is not x86, so a step that keeps landing in it does
    /// not keep trying.
    /// </summary>
    private OpenedBinary? ModuleAnalysis(string path)
    {
        if (_modules.TryGetValue(path, out var cached))
        {
            return cached;
        }

        OpenedBinary? opened = null;
        try
        {
            var pe = PeImage.Load(path);
            var analysis = pe.IsX86Family ? new BinaryAnalysis(pe) : null;
            analysis?.LoadPdbSymbols();
            opened = new OpenedBinary(pe, analysis, managed: null, managedLoadError: null, project: null);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"Could not read module {System.IO.Path.GetFileName(path)}: {ex.Message}");
        }

        _modules[path] = opened;
        if (opened is not null)
        {
            WarmModuleSymbols(path, opened);
        }

        return opened;
    }

    /// <summary>
    /// Fetches a foreign module's PDB from the symbol server in the background and folds its function
    /// names into that module's analysis, so a stop deep inside it reads as a name rather than
    /// sub_XXXX. Best-effort and once per module: no PDB, no network, or a build mismatch just leaves
    /// the export names already there. Any open document for the module is reloaded when names arrive.
    /// </summary>
    private void WarmModuleSymbols(string path, OpenedBinary module)
    {
        // A module's PDB is named by its PE CodeView record, so only a PE module has one to fetch.
        if (module.Analysis is not { Image: PeImage image } analysis || !_symbolsWarmed.Add(path))
        {
            return;
        }

        var codeView = image.Debug.Select(d => d.CodeView).FirstOrDefault(c => c is not null);
        if (codeView is null)
        {
            return;
        }

        var symbols = analysis.Symbols;
        string moduleName = System.IO.Path.GetFileName(path);
        _ = Task.Run(() =>
        {
            // Network and PDB parse off the UI thread; the symbol table and documents are touched only
            // back on it, where nothing else is reading them at the same time.
            string? pdbPath = Spydate.Debugger.CoreClrSymbols.PdbFor(path);
            var pdb = pdbPath is null ? null : Spydate.Core.Pdb.PdbFile.TryLoad(pdbPath, out _);
            if (pdb is null || pdb.Guid != codeView.Guid)
            {
                return;
            }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                int before = symbols.Count;
                Spydate.Core.Pdb.PdbSymbols.Apply(image, pdb, symbols);
                if (symbols.Count <= before)
                {
                    return;
                }

                Log($"Loaded {symbols.Count - before} symbols for {moduleName} from its PDB.");
                foreach (var doc in Documents.OfType<CodeDocumentViewModel>()
                             .Where(d => d.Key.StartsWith($"module:{moduleName}:", StringComparison.Ordinal))
                             .ToList())
                {
                    _ = doc.ReloadAsync();
                }
            });
        });
    }

    /// <summary>The member whose IL covers an address, when this file is a .NET assembly.</summary>
    private ManagedMemberTarget? Managed(ulong va)
    {
        if (Binary is not { Managed: { } managed, Bodies: { } bodies } binary
            || binary.Image.VaToRva(va) is not { } rva
            || bodies.At(rva) is not { } body)
        {
            return null;
        }

        foreach (var type in managed.Namespaces.SelectMany(n => n.Types))
        {
            foreach (var member in type.Members)
            {
                if (member.Handle == body.Method)
                {
                    return new ManagedMemberTarget(type, member);
                }
            }
        }

        return null;
    }

    /// <summary>Runs until the caret, without leaving a breakpoint behind.</summary>
    [RelayCommand(CanExecute = nameof(CanRunToCursor))]
    private void RunToCursor()
    {
        if (PatchTarget is { } va)
        {
            _debugger.RunTo(va);
        }
    }

    private bool CanRunToCursor() => _debugger.IsStopped && CanPatchHere();

    /// <summary>Sets or clears a breakpoint at a given address, for the margin, which knows the line
    /// that was clicked and does not need the caret moved to it first.</summary>
    [RelayCommand]
    private void ToggleBreakpointAt(ulong va) => _debugger.ToggleBreakpoint(va);

    /// <summary>Sets or clears a breakpoint where the caret is. Works before anything is running.</summary>
    [RelayCommand(CanExecute = nameof(CanPatchHere))]
    private void ToggleBreakpoint()
    {
        if (PatchTarget is { } va)
        {
            _debugger.ToggleBreakpoint(va);
            StatusText = $"Breakpoint at 0x{va:X}.";
        }
    }

    /// <summary>
    /// Re-asks every command that acts on the caret whether it applies. Public because the window's
    /// run controls change what some of them mean: Run to cursor and Test patch live are only
    /// offered while something is stopped.
    /// </summary>
    public void NotifyCaretCommands()
    {
        RenameSymbolCommand.NotifyCanExecuteChanged();
        EditCommentCommand.NotifyCanExecuteChanged();
        NopOutCommand.NotifyCanExecuteChanged();
        InvertBranchCommand.NotifyCanExecuteChanged();
        PatchCommand.NotifyCanExecuteChanged();
        ToggleBreakpointCommand.NotifyCanExecuteChanged();
        RunToCursorCommand.NotifyCanExecuteChanged();
        TestPatchLiveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Fills the Xrefs panel with every site that refers to the <paramref name="length"/> bytes at
    /// <paramref name="va"/>. A string literal is referenced by its interior as often as its start.
    /// </summary>
    private void RefreshXrefs(ulong? va, int length)
    {
        Xrefs.Clear();
        if (va is not { } target || Binary.Analysis is not { } analysis)
        {
            XrefsCaption = "Xrefs";
            return;
        }

        var hits = new List<(Xref Xref, Function? From)>();
        for (ulong address = target; address < target + (ulong)Math.Max(1, length); address++)
        {
            hits.AddRange(analysis.XrefsTo(address));
        }

        foreach (var (xref, from) in hits.OrderBy(h => h.Xref.FromVa))
        {
            string instruction = analysis.DisassembleRange(xref.FromVa, 16, maxInstructions: 1) is [{ } ins]
                ? ins.Text
                : string.Empty;
            Xrefs.Add(new XrefRow(
                $"0x{xref.FromVa:X}",
                from?.Name ?? analysis.NameFor(xref.FromVa),
                xref.Kind.ToString().ToLowerInvariant(),
                instruction,
                xref.FromVa,
                from?.EntryVa));
        }

        XrefsCaption = Xrefs.Count == 0 ? "Xrefs" : $"Xrefs ({Xrefs.Count})";
    }

    /// <summary>Opens the referring code: the containing function when known, otherwise a raw listing.</summary>
    [RelayCommand]
    private void GoToXref(XrefRow? row)
    {
        if (row is null || Binary.Analysis is not { } analysis)
        {
            return;
        }

        if (row.FunctionEntryVa is { } entry)
        {
            OpenTarget(new DisassemblyTarget(entry, analysis.NameFor(entry)));
        }
        else
        {
            OpenTarget(new RangeDisassemblyTarget(row.FromVa, 128, $"0x{row.FromVa:X}"));
        }
    }

    /// <summary>Opens the Annotations tab: every name, comment and slot name in one list.</summary>
    [RelayCommand]
    private void OpenAnnotations() => OpenTarget(new AnnotationsTarget());

    /// <summary>Opens the Notes tab: what has been learned about the binary as a whole, in editable sections.</summary>
    [RelayCommand]
    private void OpenNotes() => OpenTarget(new NotesTarget());

    /// <summary>
    /// Opens the code an annotation sits on, for the list's double-click. The function that contains
    /// the address rather than the address itself: asking for a function starting mid-function makes
    /// one, and a comment is very often on a line inside a function rather than at its entry.
    /// </summary>
    private void GoToAnnotation(ulong va)
    {
        if (Managed(va) is { } member)
        {
            OpenTarget(member);
            return;
        }

        if (Binary.Analysis is not { } analysis)
        {
            return;
        }

        if (analysis.FunctionContaining(va) is { } function)
        {
            OpenTarget(new DisassemblyTarget(function.EntryVa, analysis.NameFor(function.EntryVa)));
        }
        else
        {
            OpenTarget(new RangeDisassemblyTarget(va, 128, $"0x{va:X}"));
        }
    }

    partial void OnSelectedNodeChanged(ExplorerNodeViewModel? value)
    {
        if (value?.Target is { } target)
        {
            OpenTarget(target);
        }
    }

    // ------------------------------------------------------------------
    // Patching
    //
    // Patches are recorded against the open image and written only to a copy. Nothing here ever
    // touches the file that was opened — see PatchWriter for why that is a rule rather than a habit.
    // ------------------------------------------------------------------

    /// <summary>Every recorded patch for this binary, in address order.</summary>
    public ObservableCollection<Patch> Patches { get; } = new();

    public bool HasPatches => Patches.Count > 0;

    public string PatchesCaption => Patches.Count == 0 ? "Patches" : $"Patches ({Patches.Count})";

    /// <summary>
    /// Where a patch would land: the line the caret is on, and nothing else.
    ///
    /// No falling back to the document's own address. In a listing that fallback means every blank
    /// line, header and comment offers to patch the function's first instruction, which is both
    /// enabled when it should not be and wrong when it is used. A document with no caret at all is
    /// a different case, and that one does fall back.
    /// </summary>
    private ulong? PatchTarget => ActiveDocument is ICaretContext caret
        ? caret.CaretAddress
        : ActiveDocument?.Address;

    /// <summary>True when there is an instruction here to change.</summary>
    private bool CanPatchHere()
        => Binary.Analysis is { } analysis
           && PatchTarget is { } va
           && analysis.Image.VaToOffset(va) is not null
           && analysis.DisassembleRange(va, 16, 1).Count > 0;

    [RelayCommand(CanExecute = nameof(CanPatchHere))]
    private void NopOut() => Propose(analysis => InstructionPatches.NopOut(analysis, PatchTarget!.Value));

    [RelayCommand(CanExecute = nameof(CanPatchHere))]
    private void InvertBranch() => Propose(analysis => InstructionPatches.InvertBranch(analysis, PatchTarget!.Value));

    /// <summary>
    /// Type an instruction, the way Ghidra's patch dialog works. What is typed is assembled at this
    /// address and fitted over whole instructions — see <see cref="InstructionPatches.Assemble"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPatchHere))]
    private void Patch()
    {
        if (Binary.Analysis is not { } analysis || PatchTarget is not { } va)
        {
            return;
        }

        string? typed = _dialogs.AskForPatch(Prompt(
            analysis,
            va,
            "Patch instruction",
            $"Instruction at 0x{va:X}",
            "Several instructions may be separated by semicolons. Shorter is padded with nop; longer takes in the instructions it needs."));

        if (string.IsNullOrWhiteSpace(typed))
        {
            return;
        }

        Propose(_ => InstructionPatches.Assemble(analysis, va, typed));
    }

    /// <summary>
    /// Ties the patch dialog to one address: what is there now, how to read each box as the other,
    /// and how far a replacement of a given size would reach. The dialog does not know what a binary
    /// is, which keeps the assembling where the rest of it lives.
    /// </summary>
    private static PatchPrompt Prompt(BinaryAnalysis analysis, ulong va, string title, string subtitle, string hint)
    {
        byte[] original = analysis.DisassembleRange(va, 16, 1) is [{ } instruction]
            ? instruction.Bytes.ToArray()
            : [];

        return new PatchPrompt(
            title,
            subtitle,
            hint,
            original,
            text => X86Assembler.Encode(text, analysis.Image.Is64Bit, va),
            bytes => InstructionPatches.ReadBack(analysis, va, bytes),
            count => InstructionPatches.Fit(analysis, va, count).Describe(count));
    }

    private bool CanTestPatchLive() => _debugger.IsStopped && CanPatchHere();

    /// <summary>
    /// Assembles an instruction into the running process without recording it — a hypothesis. It goes
    /// into the process there and then, to be watched and then kept or undone. Only while stopped,
    /// and only where the debugger is already running the thing being read.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestPatchLive))]
    private void TestPatchLive()
    {
        if (Binary.Analysis is not { } analysis || PatchTarget is not { } va)
        {
            return;
        }

        string? typed = _dialogs.AskForPatch(Prompt(
            analysis,
            va,
            "Try a patch live",
            $"Instruction at 0x{va:X}, into the running process",
            "It is written into the process now, not the project — Keep it or Undo it from the Live patches tab."));

        if (string.IsNullOrWhiteSpace(typed))
        {
            return;
        }

        if (_debugger.TestPatch(va, typed) is { } problem)
        {
            StatusText = problem;
            Log($"Not tried live: {problem}");
        }
        else
        {
            StatusText = $"Trying 0x{va:X} in the running process. Keep or undo it from the Live patches tab.";
        }
    }

    private void Propose(Func<BinaryAnalysis, PatchProposal> build)
    {
        if (Binary.Analysis is not { } analysis || PatchTarget is null)
        {
            StatusText = "Open a function first — a patch needs an address.";
            return;
        }

        var proposal = build(analysis);
        if (!proposal.Ok)
        {
            StatusText = proposal.Problem ?? "That cannot be patched.";
            Log($"Not patched: {proposal.Problem}");
            return;
        }

        var patch = proposal.Patch!;
        try
        {
            Binary.Patches.Set(patch.Rva, patch);
        }
        catch (ArgumentException ex)
        {
            StatusText = ex.Message;
            Log($"Not patched: {ex.Message}");
            return;
        }

        RefreshPatches();
        Log($"Patched 0x{Binary.Image.RvaToVa(patch.Rva):X}: {patch.OriginalHex} → {patch.Hex} ({patch.Comment})");
        StatusText = $"Patched 0x{Binary.Image.RvaToVa(patch.Rva):X}. Nothing is written until you save a patched copy.";
    }

    [RelayCommand]
    private void RevertPatch(Patch? patch)
    {
        if (patch is null)
        {
            return;
        }

        Binary.Patches.Remove(patch.Rva);
        RefreshPatches();
        Log($"Reverted the patch at 0x{Binary.Image.RvaToVa(patch.Rva):X}.");
    }

    [RelayCommand]
    private void TogglePatch(Patch? patch)
    {
        if (patch is null)
        {
            return;
        }

        Binary.Patches.SetEnabled(patch.Rva, !patch.Enabled);
        RefreshPatches();
    }

    /// <summary>
    /// Writes a patched copy. Always Save As, never over the original — the binary under analysis is
    /// the only record of what it did before, and it is not this program's to overwrite.
    /// </summary>
    [RelayCommand]
    private void SavePatched()
    {
        if (Binary.Patches.EnabledCount == 0)
        {
            StatusText = "No patches are switched on, so a copy would be identical.";
            return;
        }

        string suggested = Path.GetFileNameWithoutExtension(Binary.Image.FileName)
                           + ".patched"
                           + Path.GetExtension(Binary.Image.FileName);

        string filter = Binary.Image.Format == BinaryFormat.Pe
            ? "PE files (*.exe;*.dll;*.sys)|*.exe;*.dll;*.sys|All files (*.*)|*.*"
            : "All files (*.*)|*.*";
        if (_dialogs.SaveFile("Save patched copy", filter, suggested) is not { } path)
        {
            return;
        }

        try
        {
            var result = PatchWriter.Write(Binary.Image, Binary.Patches, path);
            if (!result.Ok)
            {
                foreach (string problem in result.Problems)
                {
                    Warnings.Add(problem);
                    Log($"Patch not applied: {problem}");
                }

                StatusText = $"Nothing was written: {result.Problems.Count} patch(es) did not fit the file.";
                MessageBox.Show(
                    string.Join(Environment.NewLine, result.Problems),
                    "Patched copy not written",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            StatusText = $"Wrote {result.Applied} patch(es) to {Path.GetFileName(path)}.";
            Log($"Wrote a patched copy to {path}: {result.Applied} applied, {result.Skipped} switched off.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = $"Could not write the patched copy: {ex.Message}";
            Log($"ERROR: {ex.Message}");
        }
    }

    private void RefreshPatches()
    {
        Patches.Clear();
        foreach (var patch in Binary.Patches.Snapshot())
        {
            Patches.Add(patch);
        }

        OnPropertyChanged(nameof(HasPatches));
        OnPropertyChanged(nameof(PatchesCaption));

        // The listings mark patched lines, so they are now out of date. Only what is open is redrawn;
        // anything opened later is built from the store as it stands.
        ReloadDocuments();
    }

    // ------------------------------------------------------------------
    // Analysis
    // ------------------------------------------------------------------

    [RelayCommand]
    private void Reanalyze()
    {
        if (Binary.Analysis is { } analysis)
        {
            Log("Re-running function discovery…");
            _ = RunDiscoveryAsync(analysis);
        }
    }

    private async Task RunDiscoveryAsync(BinaryAnalysis analysis)
    {
        _analysisCts?.Cancel();
        var cts = _analysisCts = new CancellationTokenSource();
        var progress = new Progress<AnalysisProgress>(p => AnalysisText = $"{p.FunctionsFound} functions  ·  {p.Message}");
        AnalysisText = "Discovering functions…";
        IsBusy = true;
        var started = DateTime.UtcNow;
        try
        {
            await Task.Run(() => analysis.DiscoverAll(maxFunctions: 50_000, progress, cts.Token), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var elapsed = DateTime.UtcNow - started;
            AnalysisText = $"{analysis.FunctionCount} functions";
            Log($"Discovered {analysis.FunctionCount} functions in {elapsed.TotalMilliseconds:N0} ms.");
            RefreshFunctionNodes(analysis);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AnalysisText = "Analysis failed";
            Log($"ERROR during discovery: {ex.Message}");
            Warnings.Add($"Function discovery failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshFunctionNodes(BinaryAnalysis analysis)
    {
        var root = Explorer.FirstOrDefault();
        var functionsNode = root?.Children.FirstOrDefault(n => n.Target is FunctionsTarget);
        if (functionsNode is null || root is null)
        {
            return;
        }

        int index = root.Children.IndexOf(functionsNode);
        var replacement = new ExplorerNodeViewModel("Functions", Wpf.Ui.Controls.SymbolRegular.BranchFork24, new FunctionsTarget(), analysis.FunctionCount.ToString(CultureInfo.InvariantCulture))
        {
            IsExpanded = functionsNode.IsExpanded,
        };
        replacement.SetChildren(ExplorerTreeBuilder.FunctionNodes(analysis));
        root.Children[index] = replacement;

        foreach (var doc in Documents.OfType<FunctionsDocumentViewModel>())
        {
            doc.Refresh();
        }

        foreach (var doc in Documents.OfType<StringsDocumentViewModel>())
        {
            doc.RefreshReferences();
        }

        RefreshXrefs(ActiveDocument?.Address, ActiveDocument?.AddressLength ?? 1);
    }

    // ------------------------------------------------------------------
    // Navigation
    // ------------------------------------------------------------------

    /// <summary>Go to a VA / RVA / file offset / symbol: opens disassembly for code, hex for data.</summary>
    [RelayCommand]
    private void GoToAddress()
    {
        string text = GotoAddressText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
        {
            var sym = Binary.Analysis?.Symbols.GetByName(GotoAddressText.Trim());
            if (sym is null)
            {
                StatusText = $"'{GotoAddressText}' is not an address or a known symbol.";
                return;
            }

            value = sym.Va;
        }

        var pe = Binary.Image;
        ulong va = value >= pe.ImageBase ? value : value < pe.ImageSize ? pe.RvaToVa((uint)value) : 0;
        if (va != 0 && Binary.Analysis is { } analysis && analysis.Source.IsExecutable(va))
        {
            OpenTarget(new DisassemblyTarget(va, analysis.NameFor(va)));
            return;
        }

        long offset = va != 0 && pe.VaToOffset(va) is { } o ? o : (long)Math.Min(value, (ulong)pe.Length);
        OpenTarget(new HexTarget(offset));
    }

    [RelayCommand]
    private void OpenEntryPoint()
    {
        if (Binary.Analysis is not null && Binary.Image.EntryPointRva != 0)
        {
            OpenTarget(new DisassemblyTarget(Binary.Image.EntryPointVa, Core.Symbols.SymbolTable.EntryPointName(Binary.Image)));
        }
        else if (Binary.Bytecode is JvmReading { MainType: { } mainType, EntryPoint: { } main })
        {
            // A JAR starts where its manifest's Main-Class does: its main method.
            OpenTarget(new ReadingTarget(mainType, main));
        }
        else
        {
            StatusText = "This file has no native entry point to disassemble.";
        }
    }

    // ------------------------------------------------------------------
    // Documents
    // ------------------------------------------------------------------

    [RelayCommand]
    private void CloseDocument(DocumentViewModel? doc)
    {
        if (doc is null || !doc.CanClose)
        {
            return;
        }

        int index = Documents.IndexOf(doc);
        Documents.Remove(doc);
        if (ReferenceEquals(ActiveDocument, doc) || ActiveDocument is null)
        {
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index - 1, 0, Documents.Count - 1)];
        }
    }

    [RelayCommand]
    private void CloseActiveDocument() => CloseDocument(ActiveDocument);

    [RelayCommand]
    private void CloseAllDocuments()
    {
        Documents.Clear();
        ActiveDocument = null;
    }

    // ------------------------------------------------------------------
    // Naming and comments
    // ------------------------------------------------------------------

    /// <summary>
    /// What a naming command should act on. The caret decides: a stack slot if it is on one, then the
    /// name under it (so a callee can be renamed from the code that calls it), then the address of the
    /// line, then whatever the document as a whole is about.
    /// </summary>
    private CaretTarget CurrentTarget()
    {
        if (Binary.Analysis is not { } analysis)
        {
            return CaretTarget.None;
        }

        var code = ActiveDocument as ICaretContext;
        ulong? functionVa = code?.OwningFunctionVa;

        // A document with a caret answers for the caret alone. Passing its own address as a fallback
        // would make every line of a listing resolve to the function's first instruction, so Rename
        // and Comment would be offered everywhere and would act on the wrong place when taken up.
        return CaretTargets.Resolve(
            code?.CaretWord,
            code?.CaretAddress,
            code is null ? ActiveDocument?.Address : null,
            slotForName: word => functionVa is { } fn ? SlotNamed(analysis, fn, word) : null,
            addressForSymbol: word => analysis.Symbols.GetByName(word)?.Va);
    }

    /// <summary>The slot a name belongs to, when the user has already renamed one to it.</summary>
    private static string? SlotNamed(BinaryAnalysis analysis, ulong functionVa, string word)
    {
        foreach (var (slot, chosen) in analysis.Annotations.LocalNamesFor(functionVa))
        {
            if (chosen == word)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// Only where there is something a name would belong to: an address, or a stack slot. Offering
    /// Rename over a blank line and then doing nothing teaches that the command is unreliable, which
    /// is a worse outcome than it being greyed out.
    /// </summary>
    private bool CanRenameSymbol()
        => (Binary.Analysis is not null && CurrentTarget().Kind is CaretTargetKind.Address or CaretTargetKind.StackSlot)
           || ReadingCaretTarget() is not null;

    /// <summary>A comment belongs to an address, which a stack slot still sits on — or, in a JAR, to a member.</summary>
    private bool CanEditComment() => (Binary.Analysis is not null && CurrentTarget().Kind != CaretTargetKind.None) || ReadingCaretTarget() is not null;

    /// <summary>
    /// What a naming command acts on in a listing of a reading annotated by member (a JAR): the member of the
    /// listed type whose name is under the caret — its own name, or the one it has been given — or else what the
    /// listing is of. Null when the active document is not such a listing.
    /// </summary>
    private (IBytecodeType Type, IBytecodeMember? Member, string Key)? ReadingCaretTarget()
    {
        if (Binary is not { MemberAnnotations: not null, Bytecode: { } reading }
            || ActiveDocument is not CodeDocumentViewModel code
            || !_readingTargets.TryGetValue(code.Key, out var target))
        {
            return null;
        }

        var type = target.Type;
        var member = target.Member;
        var names = ExplorerTreeBuilder.NamesFor(Binary);
        if (code.CaretWord is { Length: > 0 } word)
        {
            var named = type.Members.Where(m => m.Name == word || names?.Invoke(type, m) == word).ToList();
            if (named.Count > 0)
            {
                member = member is not null && named.Contains(member) ? member : named[0];
            }
            else if (word == type.Name || names?.Invoke(type, null) == word)
            {
                member = null;
            }
        }

        return reading.AnnotationKey(type, member) is { } key ? (type, member, key) : null;
    }

    /// <summary>Renames a JAR's class, method or field, keyed by member; the listing and the tree follow.</summary>
    private async Task RenameMemberAsync(MemberAnnotationStore members, (IBytecodeType Type, IBytecodeMember? Member, string Key) target)
    {
        string what = target.Member is { } m ? $"{target.Type.FullName}::{m.Signature}" : target.Type.FullName;
        string original = target.Member?.Name ?? target.Type.Name;
        string? entered = _dialogs.AskForText(
            "Rename",
            $"Name for {what}",
            $"Leave it empty to go back to {original}.",
            members.Get(target.Key)?.Name ?? original);
        if (entered is null)
        {
            return;
        }

        string? applied = members.SetName(target.Key, entered);
        Log(applied is null ? $"{what} has its own name back." : $"{what} is now called {applied}.");
        await RefreshAnnotatedDocumentsAsync().ConfigureAwait(true);
    }

    private async Task CommentMemberAsync(MemberAnnotationStore members, (IBytecodeType Type, IBytecodeMember? Member, string Key) target)
    {
        string what = target.Member is { } m ? $"{target.Type.FullName}::{m.Signature}" : target.Type.FullName;
        string? entered = _dialogs.AskForText(
            "Comment",
            $"Comment for {what}",
            "Leave it empty to remove the comment.",
            members.Get(target.Key)?.Comment);
        if (entered is null)
        {
            return;
        }

        string? applied = members.SetComment(target.Key, entered);
        Log(applied is null ? $"Removed the comment on {what}." : $"Commented {what}.");
        await RefreshAnnotatedDocumentsAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRenameSymbol))]
    private async Task RenameSymbolAsync()
    {
        if (Binary.MemberAnnotations is { } members && ReadingCaretTarget() is { } member)
        {
            await RenameMemberAsync(members, member).ConfigureAwait(true);
            return;
        }

        if (Binary.Analysis is not { } analysis)
        {
            return;
        }

        var target = CurrentTarget();
        if (target.Kind == CaretTargetKind.StackSlot && (ActiveDocument as ICaretContext)?.OwningFunctionVa is { } owner)
        {
            await RenameSlotAsync(analysis, owner, target.Slot!).ConfigureAwait(true);
            return;
        }

        if (target.Kind != CaretTargetKind.Address)
        {
            Log("Nothing to rename here: put the caret on a name or an address first.");
            return;
        }

        ulong va = target.Address;
        string generated = analysis.Symbols.NameOrDefault(va);
        string? entered = _dialogs.AskForText(
            "Rename",
            $"Name for 0x{va:X}",
            $"Leave it empty to go back to {generated}.",
            analysis.Annotations.NameFor(va) ?? analysis.NameFor(va));
        if (entered is null)
        {
            return;
        }

        string? applied = analysis.Annotations.SetName(va, entered);
        Log(applied is null
            ? $"0x{va:X} is {analysis.NameFor(va)} again."
            : $"0x{va:X} is now {applied}.");
        await RefreshAnnotatedDocumentsAsync().ConfigureAwait(true);
    }

    /// <summary>Renames one of a function's stack slots rather than an address.</summary>
    private async Task RenameSlotAsync(BinaryAnalysis analysis, ulong functionVa, string slot)
    {
        string? entered = _dialogs.AskForText(
            "Rename",
            $"Name for {slot} in {analysis.NameFor(functionVa)}",
            $"Leave it empty to go back to {slot}.",
            analysis.Annotations.LocalNameFor(functionVa, slot) ?? slot);
        if (entered is null)
        {
            return;
        }

        string? applied = analysis.Annotations.SetLocalName(functionVa, slot, entered);
        Log(applied is null ? $"{slot} is {slot} again." : $"{slot} is now {applied}.");
        await RefreshAnnotatedDocumentsAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanEditComment))]
    private async Task EditCommentAsync()
    {
        if (Binary.MemberAnnotations is { } members && ReadingCaretTarget() is { } member)
        {
            await CommentMemberAsync(members, member).ConfigureAwait(true);
            return;
        }

        if (Binary.Analysis is not { } analysis)
        {
            return;
        }

        // A comment belongs to an address; when the caret is on a stack slot, the line it sits on is
        // what the note is about.
        var target = CurrentTarget();
        ulong? va = target.Kind == CaretTargetKind.Address
            ? target.Address
            : (ActiveDocument as ICaretContext)?.CaretAddress ?? ActiveDocument?.Address;

        if (va is not { } address)
        {
            Log("Nothing to comment here: put the caret on a line with an address first.");
            return;
        }

        string? entered = _dialogs.AskForText(
            "Comment",
            $"Comment for 0x{address:X}",
            "Leave it empty to remove the comment.",
            analysis.Annotations.CommentFor(address));
        if (entered is null)
        {
            return;
        }

        string? applied = analysis.Annotations.SetComment(address, entered);
        Log(applied is null ? $"Removed the comment at 0x{address:X}." : $"Commented 0x{address:X}.");
        await RefreshAnnotatedDocumentsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void SaveProject()
    {
        int? count = Binary.Annotations?.Count ?? Binary.MemberAnnotations?.Count;
        if (count is null)
        {
            return;
        }

        try
        {
            string? path = Binary.SaveProject();
            Log(path is null ? "Nothing to save: no names or comments yet." : $"Saved {count} annotation(s) to {path}");
            StatusText = path is null ? "Nothing to save" : "Project saved";
            NotifyDirty();
        }
        catch (IOException ex)
        {
            Log($"Could not save the project: {ex.Message}");
            StatusText = "Project not saved";
        }
    }

    /// <summary>
    /// Something else rewrote this binary's project file — an agent driving the MCP server, or a
    /// second copy of Spydate. The file is already the merged result of everyone's changes, so the
    /// window catches up with it rather than arguing.
    ///
    /// Except when there is unsaved work here. Then reloading would throw it away, and the two are
    /// merged the next time this side saves, so it is left alone and only mentioned.
    /// </summary>
    public async Task ReloadProjectAsync(Func<ProjectLoadResult?> reload)
    {
        ArgumentNullException.ThrowIfNull(reload);

        if (Binary is { MemberAnnotations: { } members, Analysis: null })
        {
            if (members.IsDirty)
            {
                Log("The project file changed on disk, but there are unsaved changes here, so it was not reloaded. Saving (Ctrl+S) merges both.");
                return;
            }

            if (reload() is { Loaded: true } reloaded)
            {
                Log($"The project file changed on disk; reloaded {reloaded.Applied} annotations.");
                await RefreshAnnotatedDocumentsAsync().ConfigureAwait(true);
            }

            return;
        }

        if (Binary.Analysis is not { } analysis)
        {
            return;
        }

        if (analysis.Annotations.IsDirty || Binary.MemberAnnotations is { IsDirty: true })
        {
            Log("The project file changed on disk, but there are unsaved changes here, so it was not reloaded. Saving (Ctrl+S) merges both.");
            return;
        }

        int before = analysis.Annotations.Count;
        if (reload() is not { Loaded: true } result)
        {
            return;
        }

        Log($"The project file changed on disk; reloaded {result.Applied} annotations (was {before}).");
        await RefreshAnnotatedDocumentsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Re-runs the documents whose text mentions names, and retitles the tabs that are named after a
    /// function. A rename can show up anywhere - in the function itself, and at every call site - so
    /// everything that shows code is reloaded rather than guessing which ones changed.
    /// </summary>
    private async Task RefreshAnnotatedDocumentsAsync()
    {
        // A JAR's listings show the names given to its members, and are titled by them.
        if (Binary.MemberAnnotations is not null)
        {
            foreach (var code in Documents.OfType<CodeDocumentViewModel>().ToList())
            {
                if (_readingTargets.TryGetValue(code.Key, out var target))
                {
                    code.Title = ReadingTitle(target);
                    await code.ReloadAsync().ConfigureAwait(true);
                }
            }
        }

        if (Binary.Analysis is not { } analysis)
        {
            return;
        }

        foreach (var document in Documents.ToList())
        {
            switch (document)
            {
                case SplitCodeDocumentViewModel split:
                    if (split.OwningFunctionVa is { } splitVa)
                    {
                        split.Title = $"{analysis.NameFor(splitVa)} (split)";
                    }

                    await split.ReloadAsync().ConfigureAwait(true);
                    break;

                case CodeDocumentViewModel code:
                    if (code.Address is { } va)
                    {
                        if (code.Key.StartsWith("disasm:", StringComparison.Ordinal))
                        {
                            code.Title = analysis.NameFor(va);
                        }
                        else if (code.Key.StartsWith("pseudoc:", StringComparison.Ordinal))
                        {
                            code.Title = $"{analysis.NameFor(va)} (C)";
                        }
                    }

                    await code.ReloadAsync().ConfigureAwait(true);
                    break;

                case FunctionsDocumentViewModel functions:
                    functions.Refresh();
                    break;

                case AnnotationsDocumentViewModel annotations:
                    annotations.Refresh();
                    break;

                case NotesDocumentViewModel notes:
                    notes.Refresh();
                    break;
            }
        }
    }

    /// <summary>A reading's document key: one per type or member and per view, so each view is its own tab.</summary>
    private static string ReadingKey(IBytecodeReading reading, ReadingTarget target)
        => $"reading:{target.View ?? reading.Views[0]}:{target.Type.FullName}::{target.Member?.Signature}";

    /// <summary>What each open reading document shows, by key: what a naming command acts on, and what a rename re-renders.</summary>
    private readonly Dictionary<string, ReadingTarget> _readingTargets = new(StringComparer.Ordinal);

    /// <summary>A reading document's tab title, under the names given to the type and member when there are any.</summary>
    private string ReadingTitle(ReadingTarget target)
    {
        var names = ExplorerTreeBuilder.NamesFor(Binary);
        string type = names?.Invoke(target.Type, null) ?? target.Type.Name;
        string title = target.Member is { } member ? $"{type}.{names?.Invoke(target.Type, member) ?? member.Name}" : type;
        return target.View is { } view && Binary.Bytecode is { } reading && view != reading.Views[0] ? $"{title} ({view})" : title;
    }

    /// <summary>
    /// A type or member of a bytecode reading that is not .NET, rendered by the reading itself in one of its views
    /// (its first, unless the target names another). Rendered again on reload, so a rename shows; and with a button
    /// for each of the reading's other views.
    /// </summary>
    private DocumentViewModel OpenReading(IBytecodeReading reading, ReadingTarget target)
    {
        string view = target.View ?? reading.Views[0];
        string key = ReadingKey(reading, target);
        _readingTargets[key] = target;

        // Coloured by the view it was rendered in; a view nothing has a definition for stays plain.
        string highlighting = view switch
        {
            JvmReading.BytecodeView => HighlightingService.JvmBytecode,
            JvmReading.JavaView => HighlightingService.Java,
            _ => HighlightingService.Plain,
        };

        var actions = reading.Views
            .Where(v => v != view)
            .Select(v => new CodeAction(ViewLabel(v), v == JvmReading.BytecodeView ? SymbolRegular.Code24 : SymbolRegular.Braces24, () => OpenTarget(target with { View = v })))
            .ToArray();

        return new CodeDocumentViewModel(
            key,
            ReadingTitle(target),
            view == JvmReading.BytecodeView ? SymbolRegular.Code24 : SymbolRegular.Braces24,
            highlighting,
            cancellationToken =>
            {
                try
                {
                    return new CodeContent(reading.Render(target.Type, target.Member, view, cancellationToken), Array.Empty<string>());
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    return new CodeContent($"// {target.Type.FullName} could not be read as {view}: {ex.Message}", Array.Empty<string>());
                }
            },
            actions);
    }

    private static string ViewLabel(string view) => view switch
    {
        JvmReading.BytecodeView => "Bytecode",
        JvmReading.JavaView => "Java",
        _ => view,
    };

    /// <summary>
    /// Opens a file from inside the archive: a class file as the class it declares, anything else as its contents.
    /// A class that did not parse — or a multi-release copy, which is not in the tree — opens as its bytes.
    /// </summary>
    private void OpenEntry(ArchiveEntry entry)
    {
        if (Binary.Bytecode is JvmReading jvm
            && Binary.Image is JarImage jar
            && jar.Classes.FirstOrDefault(c => ReferenceEquals(c.Entry, entry)) is { } parsed
            && jvm.FindType(parsed.File.Name) is { } type)
        {
            OpenTarget(new ReadingTarget(type, null));
            return;
        }

        // An APK's native library opens as the ELF it is.
        if (Binary.Image is ApkImage apk && apk.NativeLibraries.Any(l => ReferenceEquals(l.Entry, entry)))
        {
            OpenNested(entry.Name);
            return;
        }

        OpenTarget(new ArchiveEntryTarget(entry.Name));
    }

    /// <summary>Takes a file out of the package (<see cref="NestedFile"/>) and opens it in a tab of its own.</summary>
    private void OpenNested(string name)
    {
        if (Binary.Image is not ApkImage { Archive: { } archive } apk || archive.Find(name) is not { } entry)
        {
            return;
        }

        try
        {
            string path = NestedFile.Extract(archive, entry);
            _shell.Log($"Opening {apk.FileName}!/{name}, taken out to {path}");
            _ = _shell.OpenInNewTabAsync(path);
        }
        catch (BinaryParseException ex)
        {
            _shell.StatusText = $"Cannot open {name}: {ex.Message}";
            _shell.Log($"ERROR: {name} could not be taken out of {apk.FileName}: {ex.Message}");
        }
    }

    /// <summary>Opens the code at an address, when there is an analysis to read it with.</summary>
    private void OpenCode(ulong va, string name)
    {
        if (Binary.Analysis is not null)
        {
            OpenTarget(new DisassemblyTarget(va, name));
        }
    }

    public void OpenTarget(NodeTarget target)
    {
        var b = Binary;

        // A field or an event has no body to read on its own, so opening one opens its declaring type
        // and stops on the line it is declared — dnSpy's behaviour, and far more use than a document
        // one line long. Methods and properties keep their own documents, where the addresses, the
        // breakpoint gutter and the IL view live.
        if (target is ManagedMemberTarget { Member.Kind: ManagedMemberKind.Field or ManagedMemberKind.Event } memberTarget
            && (memberTarget.Assembly ?? b.Managed) is { } owner)
        {
            RevealManagedMember(owner, memberTarget);
            return;
        }

        // A new function opens in the view the reader is already in. Navigating from a decompiled tab
        // — clicking a function in the tree, following an xref, or the debugger stepping into another
        // function — used to always land on the disassembly, which meant switching back to C by hand
        // every time. Decompiled carries forward; side by side does not (it is a per-function choice,
        // so from it a new function opens as disassembly). The Disassembly and Decompile toolbar
        // buttons still go straight to the view they name, since they call the openers directly.
        if (target is DisassemblyTarget dt && b.Analysis is { } forView
            && PreferredCodeView() == CodeView.Decompiled && b.NativeDecompiler is not null)
        {
            OpenFunctionPseudoC(forView.GetOrDiscoverFunction(dt.Va, dt.Name));
            return;
        }

        // A library inside the package is a binary of its own: it opens in its own tab, not as a document of this one.
        if (target is NestedBinaryTarget nested)
        {
            OpenNested(nested.Name);
            return;
        }

        DocumentViewModel? doc = target switch
        {
            OverviewTarget => Find("overview") ?? new OverviewDocumentViewModel(b),
            HeadersTarget when b.Image is ElfImage elf => Find("headers") ?? ElfDocuments.Headers(elf),
            HeadersTarget when b.Image is PeImage pe => Find("headers") ?? new HeadersDocumentViewModel(pe),
            SegmentsTarget when b.Image is ElfImage elf => Find("segments") ?? ElfDocuments.Segments(elf, offset => OpenTarget(new HexTarget(offset))),
            DynamicTarget when b.Image is ElfImage elf => Find("dynamic") ?? ElfDocuments.Dynamic(elf),
            SymbolsTarget when b.Image is ElfImage elf => Find("symbols") ?? ElfDocuments.Symbols(elf, OpenCode, offset => OpenTarget(new HexTarget(offset))),
            SectionsTarget when b.Image is ElfImage elf => Find("sections") ?? ElfDocuments.Sections(elf, offset => OpenTarget(new HexTarget(offset))),
            SectionsTarget when b.Image is PeImage pe => Find("sections") ?? new SectionsDocumentViewModel(pe, s => OpenTarget(new HexTarget(s.PointerToRawData))),
            ImportsTarget when b.Image is ElfImage elf => Find("imports") ?? ElfDocuments.Imports(elf, OpenCode),
            ImportsTarget when b.Image is PeImage pe => Find("imports") ?? new ImportsDocumentViewModel(pe, b.Analysis),
            ResourcesTarget when b.Image is ApkImage { Resources: { } table } => Find("resources") ?? JarDocuments.Resources(table),
            ResourcesTarget when b.Image is PeImage pe => Find("resources") ?? new ResourcesDocumentViewModel(pe, row => OpenTarget(new ResourcePreviewTarget(row.TypeId, row.Id, row.DataRva, row.DataSize, $"{row.Type}: {row.Name}"))),
            ResourcePreviewTarget preview => OpenResource(preview),
            StringsTarget when b.Bytecode is JvmReading jvm => Find("strings") ?? JarDocuments.Strings(jvm, (type, member) => OpenTarget(new ReadingTarget(type, member))),
            StringsTarget => Find("strings") ?? new StringsDocumentViewModel(b.Image, b.Analysis, offset => OpenTarget(new HexTarget(offset))),
            EntriesTarget when b.Image is JarImage jar => Find("entries") ?? JarDocuments.Entries(jar, OpenEntry),
            EntriesTarget when b.Image is ApkImage { Archive: { } archive } => Find("entries") ?? JarDocuments.Entries(archive, OpenEntry),
            ArchiveEntryTarget entry when b.Image is ApkImage { Archive: { } archive } apk && archive.Find(entry.Name) is { } found => Find($"entry:{entry.Name}") ?? CodeDocumentViewModel.ForText(
                $"entry:{entry.Name}",
                entry.Name[(entry.Name.LastIndexOf('/') + 1)..],
                SymbolRegular.Document24,
                entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? HighlightingService.Xml : HighlightingService.Plain,
                JarDocuments.Contents(archive, found, apk.ResourceName)),
            ArchiveEntryTarget entry when b.Image is JarImage jar && jar.Archive.Find(entry.Name) is { } found => Find($"entry:{entry.Name}") ?? CodeDocumentViewModel.ForText(
                $"entry:{entry.Name}",
                entry.Name[(entry.Name.LastIndexOf('/') + 1)..],
                SymbolRegular.Document24,
                entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? HighlightingService.Xml : HighlightingService.Plain,
                JarDocuments.Contents(jar, found)),
            AnnotationsTarget when b.Analysis is { } ann => Find("annotations") ?? new AnnotationsDocumentViewModel(ann, GoToAnnotation),
            NotesTarget => Find("notes") ?? new NotesDocumentViewModel(b.Notes, b.SaveProject),
            ExportsTarget when b.Image is ElfImage elf => Find("exports") ?? ElfDocuments.Exports(elf, b.Analysis is null ? null : OpenCode),
            ExportsTarget when b.Image is PeImage pe => Find("exports") ?? new ExportsDocumentViewModel(pe, b.Analysis is null ? null : (va, name) => OpenTarget(new DisassemblyTarget(va, name))),
            FunctionsTarget when b.Analysis is { } a => Find("functions") ?? new FunctionsDocumentViewModel(a, OpenFunctionDisassembly, OpenFunctionPseudoC),
            HexTarget h => OpenHex(h.Offset),
            DisassemblyTarget d when b.Analysis is { } a => Find($"disasm:{d.Va:X}") ?? CodeDocumentViewModel.ForFunctionDisassembly(a, a.GetOrDiscoverFunction(d.Va, d.Name), b.NativeDecompiler is null ? null : OpenFunctionPseudoC, b.NativeDecompiler is null ? null : OpenFunctionSplit, OpenFunctionGraph, b.Patches),
            RangeDisassemblyTarget r when b.Analysis is { } a => Find($"disasm-range:{r.Va:X}") ?? CodeDocumentViewModel.ForRangeDisassembly(a, r.Va, r.Bytes, r.Title, b.Patches),
            ReadingTarget rt when b.Bytecode is { } reading => Find(ReadingKey(reading, rt)) ?? OpenReading(reading, rt),
            ManagedAssemblyTarget when b.Managed is { } m => Find("managed:assembly") ?? ManagedCodeDocumentViewModel.ForAssembly(m),

            // The target may name a resolved reference to decompile through rather than this
            // assembly — a type in another module. Only the opened one carries the debugger's
            // addresses (Locate); a reference has no place in the running image to point at.
            ManagedTypeTarget t when (t.Assembly ?? b.Managed) is { } m => Find($"managed:type:{m.Name}:{t.Type.FullName}") ?? ManagedCodeDocumentViewModel.ForType(m, t.Type, t.Assembly is null ? Locate : null, GoToDefinition),
            ManagedMemberTarget mm when (mm.Assembly ?? b.Managed) is { } m => Find($"managed:member:{m.Name}:{mm.Type.FullName}::{mm.Member.Handle.GetHashCode():X}") ?? ManagedCodeDocumentViewModel.ForMember(m, mm.Type, mm.Member, mm.Assembly is null ? Locate : null, GoToDefinition),
            _ => null,
        };

        if (doc is not null)
        {
            Show(doc);
            Record(doc, target);
        }
    }

    /// <summary>
    /// Opens the type a member belongs to and stops on the line the member is declared, reusing the
    /// type's document if it is already open. Only this assembly carries the debugger's
    /// addresses (Locate); a member in a resolved reference is decompiled through that reference.
    /// </summary>
    private void RevealManagedMember(ManagedAssembly assembly, ManagedMemberTarget target)
    {
        var doc = Find($"managed:type:{assembly.Name}:{target.Type.FullName}") as ManagedCodeDocumentViewModel
                  ?? ManagedCodeDocumentViewModel.ForType(assembly, target.Type, target.Assembly is null ? Locate : null, GoToDefinition);
        Show(doc);
        Record(doc, new ManagedTypeTarget(target.Type, target.Assembly));
        doc.RevealMember(MetadataTokens.GetToken(target.Member.Handle));
    }

    /// <summary>Opens the function the active document is about in both views at once.</summary>
    [RelayCommand]
    private void OpenSideBySide()
    {
        if (Binary.Analysis is not { } analysis)
        {
            return;
        }

        ulong? va = (ActiveDocument as ICaretContext)?.OwningFunctionVa ?? ActiveDocument?.Address;
        if (va is not { } entry)
        {
            StatusText = "Open a function first: side by side shows one function in both views.";
            return;
        }

        OpenFunctionSplit(analysis.GetOrDiscoverFunction(entry));
    }

    /// <summary>Opens the control-flow graph of the function the active document is about.</summary>
    [RelayCommand]
    private void OpenGraph()
    {
        if (Binary.Analysis is not { } analysis)
        {
            return;
        }

        ulong? va = (ActiveDocument as ICaretContext)?.OwningFunctionVa ?? ActiveDocument?.Address;
        if (va is not { } entry)
        {
            StatusText = "Open a function first: the graph shows one function's control flow.";
            return;
        }

        OpenFunctionGraph(analysis.GetOrDiscoverFunction(entry));
    }

    /// <summary>
    /// Where a member's IL actually sits, so the listing can carry an address against every line.
    ///
    /// Given to the document rather than looked up by it: the body index belongs to this binary,
    /// and a document that reached for it would be reaching past the thing that owns its lifetime.
    /// </summary>
    private Documents.ManagedImage? Locate()
        => Binary is { Bodies: { } bodies } open
            ? new Documents.ManagedImage(bodies, open.Image.ImageBase, open.Image.Is64Bit)
            : null;

    /// <summary>
    /// Follows a clicked identifier to its definition. The target may be in the assembly on
    /// screen or in one it references; resolution runs from the viewed assembly so a click inside a
    /// reference's code can go on into a third assembly. A definition in this binary opens as
    /// its own code — with the debugger's addresses — and one elsewhere opens through that assembly.
    /// </summary>
    private void GoToDefinition(SourceReference reference, ManagedAssembly viewed)
    {
        // A P/Invoke's library, not a metadata reference: the code it names is native and lives in
        // another file entirely. See ManagedCodeDocumentViewModel.WithNativeModules.
        if (reference.Token == NativeImports.ModuleToken)
        {
            OpenNativeModule(reference.Assembly);
            return;
        }

        var target = reference.Assembly == viewed.SimpleName
            ? viewed
            : viewed.References.FirstOrDefault(r => r.Name == reference.Assembly) is { } refx
                ? viewed.Resolve(refx)
                : null;

        if (target?.Locate(reference.Token) is not { } located)
        {
            StatusText = $"Could not find {reference.Assembly}!0x{reference.Token:X} to go to.";
            return;
        }

        var external = ReferenceEquals(target, Binary.Managed) ? null : target;
        OpenTarget(located.Member is { } member
            ? new ManagedMemberTarget(located.Type, member, external)
            : new ManagedTypeTarget(located.Type, external));
    }

    /// <summary>
    /// Opens the DLL a P/Invoke names, as a tab of its own.
    ///
    /// A tab rather than a document inside this one, and without asking the preference, because
    /// this is not the debugger wandering into a library while stepping — somebody clicked the
    /// name. Wanting to look at it is the whole of what the click means.
    ///
    /// Where it is, in the order that is right most often: the copy already loaded by the running
    /// program, then beside the assembly that imports it — which is where a project's own native
    /// dependencies sit — then the system directory. The import table is no help here: a managed
    /// assembly has no native imports to resolve, which is why the native path could not be reused.
    /// </summary>
    private void OpenNativeModule(string name)
    {
        string bare = TrimExtension(name);
        string file = bare + ".dll";

        string? path = _debugger.ModulePath(file);

        if (path is null && System.IO.Path.GetDirectoryName(Binary.Image.Path) is { Length: > 0 } beside)
        {
            string candidate = System.IO.Path.Combine(beside, file);
            path = File.Exists(candidate) ? candidate : null;
        }

        if (path is null)
        {
            string system = Binary.Image.Is64Bit
                ? Environment.GetFolderPath(Environment.SpecialFolder.System)
                : Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            string candidate = System.IO.Path.Combine(system, file);
            path = File.Exists(candidate) ? candidate : null;
        }

        if (path is null)
        {
            StatusText = $"Could not find {file} — not loaded, not beside {Binary.DisplayName}, not in the system directory.";
            Log($"{file} was not found to open.");
            return;
        }

        if (ModuleAnalysis(path) is not { } module)
        {
            StatusText = $"Could not read {System.IO.Path.GetFileName(path)}.";
            return;
        }

        if (_shell.ShowModuleTab(module) is null)
        {
            StatusText = $"Could not open {System.IO.Path.GetFileName(path)}.";
        }
    }

    private void OpenFunctionGraph(Function f)
    {
        if (Binary.Analysis is not { } a)
        {
            return;
        }

        var doc = Find($"graph:{f.EntryVa:X}")
                  ?? GraphDocumentViewModel.For(a, f, () => a.TryGetFunction(f.EntryVa, out var latest) ? latest : f, _dialogs);
        Show(doc);
        Record(doc, new DisassemblyTarget(f.EntryVa, f.Name));
    }

    /// <summary>
    /// Double-clicking a block in the graph opens the listing for the function it belongs to. The graph
    /// is for the shape of the function; reading the instructions properly is what the listing is for.
    /// </summary>
    [RelayCommand]
    private void OpenBlock(ulong va)
    {
        if (Binary.Analysis is not { } analysis || ActiveDocument is not GraphDocumentViewModel graph
            || graph.OwningFunctionVa is not { } entry)
        {
            return;
        }

        _ = va;   // the block is already selected, which is what the Xrefs panel follows
        OpenFunctionDisassembly(analysis.GetOrDiscoverFunction(entry));
    }

    /// <summary>Opens the function in both views at once, each following the other.</summary>
    private void OpenFunctionSplit(Function f)
    {
        if (Binary.Analysis is not { } a || Binary.NativeDecompiler is not { } d)
        {
            return;
        }

        var doc = Find($"split:{f.EntryVa:X}")
                  ?? SplitCodeDocumentViewModel.For(a, d, f, () => a.TryGetFunction(f.EntryVa, out var latest) ? latest : f, Binary.Patches);
        Show(doc);
        Record(doc, new DisassemblyTarget(f.EntryVa, f.Name));
    }

    private void OpenFunctionDisassembly(Function f)
    {
        if (Binary.Analysis is { } a)
        {
            var doc = Find($"disasm:{f.EntryVa:X}")
                      ?? CodeDocumentViewModel.ForFunctionDisassembly(a, f, Binary.NativeDecompiler is null ? null : OpenFunctionPseudoC, Binary.NativeDecompiler is null ? null : OpenFunctionSplit, OpenFunctionGraph, Binary.Patches);
            Show(doc);
            Record(doc, new DisassemblyTarget(f.EntryVa, f.Name));
        }
    }

    private void OpenFunctionPseudoC(Function f)
    {
        if (Binary.NativeDecompiler is { } d && Binary.Analysis is { } a)
        {
            var doc = Find($"pseudoc:{f.EntryVa:X}")
                      ?? CodeDocumentViewModel.ForPseudoC(d, f, OpenFunctionDisassembly, () => a.TryGetFunction(f.EntryVa, out var latest) ? latest : f, OpenFunctionSplit);
            Show(doc);
            Record(doc, null, f.EntryVa);
        }
    }

    /// <summary>Which single-function view a newly opened function takes.</summary>
    private enum CodeView { Disassembly, Decompiled }

    /// <summary>
    /// The view the reader is in now, so a newly opened function can match it. Only the decompiled
    /// (pseudo-C) tab carries forward — this binary's <c>pseudoc:</c> or a foreign module's
    /// <c>modulec:</c>; the disassembly, side by side, and everything else default to disassembly —
    /// side by side because it is a per-function choice, not a mode to carry along.
    /// </summary>
    private CodeView PreferredCodeView()
        => ActiveDocument?.Key is { } key
           && (key.StartsWith("pseudoc:", StringComparison.Ordinal) || key.StartsWith("modulec:", StringComparison.Ordinal))
            ? CodeView.Decompiled
            : CodeView.Disassembly;

    /// <summary>
    /// Follows a clicked name in a native listing — the disassembly or the decompiled C — to the
    /// function it names, opening it in the view the reader is already in (see PreferredCodeView).
    /// The editor asks CanExecute for every word it draws the hand cursor over, so this stays a fast
    /// lookup: a generated name carries its own address, a real name is a symbol, and only a function
    /// entry is a link — a label inside a function or a data name is left as plain text.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigateWord))]
    private void NavigateWord(string? word)
    {
        if (NavContextFor(ActiveDocument) is not { } context || context.Owner.Analysis is not { } analysis)
        {
            return;
        }

        (var owner, string? moduleName) = context;

        // A function of the document's own binary opens in place — in this binary or in the
        // foreign module the document is showing; a clicked import name opens the module it comes
        // from, the static cousin of stepping into it.
        if (ResolveFunction(word, analysis) is { } va)
        {
            var function = analysis.GetOrDiscoverFunction(va);
            if (moduleName is not null)
            {
                ShowModuleDisassembly(owner, moduleName, function);
            }
            else
            {
                OpenTarget(new DisassemblyTarget(va, analysis.NameFor(va)));
            }
        }
        else if (ImportUnder(word, analysis) is { } import)
        {
            OpenImportedFunction(analysis, import.Module, import.Function);
        }
    }

    private bool CanNavigateWord(string? word)
    {
        if (NavContextFor(ActiveDocument) is not { } context || context.Owner.Analysis is not { } analysis)
        {
            return false;
        }

        return ResolveFunction(word, analysis) is not null || ImportUnder(word, analysis) is not null;
    }

    /// <summary>
    /// The binary a click in a document resolves against, and its module name when the document shows a
    /// foreign module rather than this binary. So a click in a kernel32 document follows
    /// kernel32's own functions and imports, not this file's.
    /// </summary>
    private (OpenedBinary Owner, string? ModuleName)? NavContextFor(DocumentViewModel? doc)
    {
        if (doc?.Key is { } key && ModuleNameOf(key) is { } moduleName
            && _modules.FirstOrDefault(m => m.Value?.Analysis is not null
                   && string.Equals(System.IO.Path.GetFileName(m.Key), moduleName, StringComparison.OrdinalIgnoreCase)).Value is { } module)
        {
            return (module, moduleName);
        }

        return Binary is { Analysis: not null } binary ? (binary, null) : null;
    }

    /// <summary>The module a document key names, or null when it is not a foreign-module document.</summary>
    private static string? ModuleNameOf(string key)
    {
        foreach (string prefix in ModuleKeyPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                int end = key.IndexOf(':', prefix.Length);
                if (end > prefix.Length)
                {
                    return key[prefix.Length..end];
                }
            }
        }

        return null;
    }

    private static readonly string[] ModuleKeyPrefixes = ["module:", "modulec:"];

    /// <summary>
    /// The (module, function) a clicked word names as an import of <paramref name="analysis"/>, or null.
    /// A listing writes an import as its whole symbol — <c>KERNEL32!GetSystemTimeAsFileTime</c>, one word
    /// since <c>!</c> is part of a name here — so the module is in the word; a bare function name is
    /// looked up in the import table, which is kept only for this binary.
    /// </summary>
    private (string Module, string Function)? ImportUnder(string? word, BinaryAnalysis analysis)
    {
        if (string.IsNullOrEmpty(word))
        {
            return null;
        }

        int bang = word.IndexOf('!');
        if (bang > 0 && bang < word.Length - 1 && analysis.Symbols.GetByName(word) is { Kind: Core.Symbols.SymbolKind.Import })
        {
            return (word[..bang], word[(bang + 1)..]);
        }

        return ReferenceEquals(analysis, Binary.Analysis) && ImportMap(analysis).TryGetValue(word, out string? module)
            ? (TrimExtension(module), word)
            : null;
    }

    /// <summary>The entry of the function a listing word names in <paramref name="analysis"/>, or null.</summary>
    private static ulong? ResolveFunction(string? word, BinaryAnalysis analysis)
    {
        if (string.IsNullOrEmpty(word))
        {
            return null;
        }

        ulong? target = Core.Text.AddressText.FromGeneratedName(word) ?? analysis.Symbols.GetByName(word)?.Va;
        return target is { } va && analysis.IsFunctionStart(va) ? va : null;
    }

    /// <summary>Every named import of this binary, mapped to the module it comes from. Built once.</summary>
    private Dictionary<string, string> ImportMap(BinaryAnalysis analysis)
    {
        if (_imports is not null)
        {
            return _imports;
        }

        // Normal imports then delay-loaded ones, first module to name a function wins — the order the image lists them.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var import in analysis.Image.Imports)
        {
            if (import.Name is { Length: > 0 } name)
            {
                map.TryAdd(name, import.Module);
            }
        }

        _imports = map;
        return _imports;
    }

    /// <summary>
    /// Opens the imported function <paramref name="functionName"/> from <paramref name="moduleName"/> —
    /// resolving which file that module is (the running copy when a run is up, otherwise the on-disk
    /// DLL the import table points at), analysing it on demand, and showing the export's disassembly.
    /// </summary>
    private void OpenImportedFunction(BinaryAnalysis owner, string moduleName, string functionName)
    {
        // An ELF's libraries are Linux files. Looking for libc in System32 would at best find nothing and at
        // worst open a Windows DLL that happens to share the name.
        if (owner.Image.Format != BinaryFormat.Pe)
        {
            StatusText = $"{functionName} comes from {moduleName}, a {owner.Image.Format} library; Spydate opens it only if you open that file yourself.";
            return;
        }

        if (ImportedModuleFile(owner, TrimExtension(moduleName)) is not { } path)
        {
            StatusText = $"Could not find {moduleName} on disk to open {functionName}.";
            return;
        }

        if (ModuleAnalysis(path) is not { Analysis: { } analysis } moduleBinary)
        {
            StatusText = $"Could not read {System.IO.Path.GetFileName(path)}.";
            return;
        }

        if (analysis.Symbols.GetByName(functionName)?.Va is not { } exportVa)
        {
            StatusText = $"{functionName} is not an export of {System.IO.Path.GetFileName(path)}.";
            return;
        }

        ShowModuleDisassembly(moduleBinary, System.IO.Path.GetFileName(path), analysis.GetOrDiscoverFunction(exportVa));
    }

    /// <summary>
    /// Opens a function of a module other than this binary as disassembly, reusing an open
    /// document. The Decompile action and every click inside the document run against the module's own
    /// analysis, so a foreign module reads and navigates like this binary does.
    /// </summary>
    private CodeDocumentViewModel ShowModuleDisassembly(OpenedBinary module, string moduleName, Function function)
    {
        var analysis = module.Analysis!;
        var doc = Find($"module:{moduleName}:{function.EntryVa:X}") as CodeDocumentViewModel
                  ?? CodeDocumentViewModel.ForModuleDisassembly(
                         analysis, function, moduleName,
                         module.NativeDecompiler is null ? null : f => ShowModulePseudoC(module, moduleName, f));
        Show(doc);
        return doc;
    }

    /// <summary>Opens a module function's decompiled C, with a Disassembly action back.</summary>
    private void ShowModulePseudoC(OpenedBinary module, string moduleName, Function function)
    {
        if (module.NativeDecompiler is not { } decompiler || module.Analysis is not { } analysis)
        {
            return;
        }

        var doc = Find($"modulec:{moduleName}:{function.EntryVa:X}") as CodeDocumentViewModel
                  ?? CodeDocumentViewModel.ForModulePseudoC(
                         decompiler, analysis, function, moduleName, f => ShowModuleDisassembly(module, moduleName, f));
        Show(doc);
    }

    private static string TrimExtension(string name)
        => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// The on-disk file of an imported module named <paramref name="bare"/> (no extension). The running
    /// copy when a run is up; then whatever the import table resolved (api-sets, forwarders); then the
    /// system directory for this binary's architecture, where the common system DLLs live.
    /// </summary>
    private string? ImportedModuleFile(BinaryAnalysis owner, string bare)
    {
        if (_debugger.ModulePath(bare + ".dll") is { } loaded)
        {
            return loaded;
        }

        if (owner.Signatures?.Modules.FirstOrDefault(m =>
                string.Equals(TrimExtension(m.Name), bare, StringComparison.OrdinalIgnoreCase))?.Path is { } resolved)
        {
            return resolved;
        }

        string system = owner.Image.Is64Bit
            ? Environment.GetFolderPath(Environment.SpecialFolder.System)        // System32: 64-bit DLLs
            : Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);    // SysWOW64: 32-bit DLLs
        string candidate = System.IO.Path.Combine(system, bare + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Opens a resource as text when its type is one Spydate can decode, and as bytes otherwise.
    /// </summary>
    private DocumentViewModel? OpenResource(ResourcePreviewTarget target)
    {
        if (Binary.Image is not PeImage pe)
        {
            return null;   // resources are a PE's own
        }

        var node = new ResourceNode { Level = 3, Id = target.Id, DataRva = target.DataRva, DataSize = target.DataSize };
        var data = ResourceDecoder.ReadData(pe, node);
        if (data.IsEmpty)
        {
            return null;
        }

        string key = $"resource:{target.DataRva:X}";
        var existing = Find(key);
        if (existing is not null)
        {
            return existing;
        }

        switch ((ResourceType)target.TypeId)
        {
            case ResourceType.Manifest:
                return CodeDocumentViewModel.ForText(key, target.Title, SymbolRegular.Document24, HighlightingService.Xml, ResourceDecoder.ReadManifest(data.Span));

            case ResourceType.Version when ResourceDecoder.ReadVersionInfo(data.Span) is { } version:
                return CodeDocumentViewModel.ForText(key, target.Title, SymbolRegular.Info24, HighlightingService.Plain, FormatVersionInfo(version));

            case ResourceType.String:
                var strings = ResourceDecoder.ReadStringTable(data.Span, target.Id);
                if (strings.Count > 0)
                {
                    return CodeDocumentViewModel.ForText(
                        key,
                        target.Title,
                        SymbolRegular.TextT24,
                        HighlightingService.Plain,
                        string.Join(Environment.NewLine, strings.Select(s => $"{s.Id,6}  {s.Text}")));
                }

                break;
        }

        // Anything else is bytes: icons, dialogs, binary blobs.
        return OpenHex(pe.RvaToOffset(target.DataRva) is { } offset ? offset : 0);
    }

    private static string FormatVersionInfo(VersionInfo version)
    {
        var sb = new StringBuilder();
        sb.Append("File version:     ").AppendLine(version.FileVersion?.ToString() ?? "(none)");
        sb.Append("Product version:  ").AppendLine(version.ProductVersion?.ToString() ?? "(none)");
        sb.Append("File flags:       ").AppendLine($"0x{version.FileFlags:X8}");
        sb.Append("File OS:          ").AppendLine($"0x{version.FileOs:X8}");
        sb.Append("File type:        ").AppendLine($"0x{version.FileType:X8}");

        foreach (var table in version.StringTables)
        {
            sb.AppendLine();
            sb.Append("[").Append(table.LanguageCodePage).AppendLine("]");
            int width = table.Strings.Count == 0 ? 0 : table.Strings.Max(s => s.Key.Length);
            foreach (var (name, value) in table.Strings)
            {
                sb.Append("  ").Append(name.PadRight(width)).Append("  ").AppendLine(value);
            }
        }

        return sb.ToString();
    }

    private DocumentViewModel OpenHex(long offset)
    {
        var hex = Find("hex") as HexDocumentViewModel ?? new HexDocumentViewModel(Binary.Image);
        Show(hex);
        hex.GoToOffset(offset);
        return hex;
    }

    private DocumentViewModel? Find(string key) => Documents.FirstOrDefault(d => d.Key == key);

    private void Show(DocumentViewModel doc)
    {
        if (!Documents.Contains(doc))
        {
            Documents.Add(doc);
        }

        ActiveDocument = doc;
    }

    // ------------------------------------------------------------------
    // Navigation history
    // ------------------------------------------------------------------

    /// <summary>
    /// One place the user has been. The key finds the document while it is open; the target (or the
    /// function address, for pseudo-C, which has no target of its own) is how it is opened again after
    /// the tab has been closed.
    /// </summary>
    private sealed record HistoryEntry(string Key, string Title, NodeTarget? Target, ulong? PseudoCVa);

    private readonly List<HistoryEntry> _history = new();
    private int _historyCursor = -1;

    /// <summary>Set while replaying history, so going back does not itself become history.</summary>
    private bool _navigating;

    public bool CanNavigateBack => _historyCursor > 0;

    public bool CanNavigateForward => _historyCursor >= 0 && _historyCursor < _history.Count - 1;

    /// <summary>Where Back would take you, for the button's tooltip.</summary>
    public string BackTooltip => CanNavigateBack ? $"Back to {_history[_historyCursor - 1].Title} (Alt+Left)" : "Back (Alt+Left)";

    public string ForwardTooltip => CanNavigateForward ? $"Forward to {_history[_historyCursor + 1].Title} (Alt+Right)" : "Forward (Alt+Right)";

    private void Record(DocumentViewModel? document, NodeTarget? target, ulong? pseudoCVa = null)
    {
        if (document is null || _navigating)
        {
            return;
        }

        // Re-showing where you already are is not a move.
        if (_historyCursor >= 0 && _history[_historyCursor].Key == document.Key)
        {
            return;
        }

        // Going somewhere new from the middle of the history drops what was ahead, as a browser does.
        if (_historyCursor < _history.Count - 1)
        {
            _history.RemoveRange(_historyCursor + 1, _history.Count - _historyCursor - 1);
        }

        _history.Add(new HistoryEntry(document.Key, document.Title, target, pseudoCVa));
        _historyCursor = _history.Count - 1;

        const int limit = 200;
        if (_history.Count > limit)
        {
            _history.RemoveRange(0, _history.Count - limit);
            _historyCursor = _history.Count - 1;
        }

        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
        OnPropertyChanged(nameof(BackTooltip));
        OnPropertyChanged(nameof(ForwardTooltip));
        NavigateBackCommand.NotifyCanExecuteChanged();
        NavigateForwardCommand.NotifyCanExecuteChanged();
    }

    private void ClearHistory()
    {
        _history.Clear();
        _historyCursor = -1;
        NotifyHistoryChanged();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateBack))]
    private void NavigateBack() => GoToHistory(_historyCursor - 1);

    [RelayCommand(CanExecute = nameof(CanNavigateForward))]
    private void NavigateForward() => GoToHistory(_historyCursor + 1);

    private void GoToHistory(int index)
    {
        if (index < 0 || index >= _history.Count)
        {
            return;
        }

        var entry = _history[index];
        _navigating = true;
        try
        {
            if (Find(entry.Key) is { } open)
            {
                Show(open);
            }
            else if (entry.PseudoCVa is { } va && Binary.Analysis is { } analysis)
            {
                OpenFunctionPseudoC(analysis.GetOrDiscoverFunction(va));
            }
            else if (entry.Target is { } target)
            {
                OpenTarget(target);
            }
            else
            {
                StatusText = $"{entry.Title} is closed and cannot be reopened.";
                return;
            }

            _historyCursor = index;
            NotifyHistoryChanged();
        }
        finally
        {
            _navigating = false;
        }
    }
}
