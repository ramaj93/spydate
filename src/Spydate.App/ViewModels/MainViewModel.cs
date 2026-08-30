using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
using Spydate.App.ViewModels.Documents;
using Spydate.Core.PE;
using Spydate.Core.Text;
using Spydate.Disassembly;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Spydate.App.ViewModels;

/// <summary>One row in the Xrefs panel: a site that refers to the current address.</summary>
public sealed record XrefRow(string From, string Function, string Kind, string Instruction, ulong FromVa, ulong? FunctionEntryVa);

/// <summary>Root view model: file commands, explorer tree, document tabs, output log, status.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly string ProductTitle =
        $"Spydate v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(2) ?? "0.1"} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")})";

    private readonly IFileDialogService _dialogs;
    private readonly WorkspaceService _workspace;
    private CancellationTokenSource? _analysisCts;

    public MainViewModel(IFileDialogService dialogs, WorkspaceService workspace)
    {
        _dialogs = dialogs;
        _workspace = workspace;
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));
        Log("Spydate started. Open a PE file to begin (Ctrl+O).");
    }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    public ObservableCollection<ExplorerNodeViewModel> Explorer { get; } = new();

    public ObservableCollection<DocumentViewModel> Documents { get; } = new();

    /// <summary>Timestamped log shown in the Output tool window.</summary>
    public ObservableCollection<string> Output { get; } = new();

    /// <summary>Parser and analysis warnings for the current file.</summary>
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
    [NotifyPropertyChangedFor(nameof(HasBinary))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private OpenedBinary? _binary;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _analysisText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _gotoAddressText = string.Empty;

    public bool HasBinary => Binary is not null;

    public string WindowTitle => Binary is null ? ProductTitle : $"{ProductTitle} — {Binary.DisplayName}";

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
    }

    /// <summary>
    /// Fills the Xrefs panel with every site that refers to the <paramref name="length"/> bytes at
    /// <paramref name="va"/>. A string literal is referenced by its interior as often as its start.
    /// </summary>
    private void RefreshXrefs(ulong? va, int length)
    {
        Xrefs.Clear();
        if (va is not { } target || Binary?.Analysis is not { } analysis)
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
        if (row is null || Binary?.Analysis is not { } analysis)
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

    partial void OnSelectedNodeChanged(ExplorerNodeViewModel? value)
    {
        if (value?.Target is { } target)
        {
            OpenTarget(target);
        }
    }

    private void Log(string message) => Output.Add($"{DateTime.Now:HH:mm:ss}  {message}");

    // ------------------------------------------------------------------
    // File commands
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        string? path = _dialogs.OpenPeFile();
        if (path is not null)
        {
            await OpenPathAsync(path).ConfigureAwait(true);
        }
    }

    public async Task OpenPathAsync(string path)
    {
        SaveAnnotationsIfDirty();
        _analysisCts?.Cancel();
        IsBusy = true;
        StatusText = $"Loading {Path.GetFileName(path)}…";
        Log($"Loading {path}");
        try
        {
            var opened = await _workspace.OpenAsync(path).ConfigureAwait(true);
            Documents.Clear();
            ActiveDocument = null;
            ClearHistory();
            Binary = opened;
            Explorer.Clear();
            Explorer.Add(ExplorerTreeBuilder.Build(opened));

            var pe = opened.Image;
            StatusText = $"{opened.DisplayName}  ·  {pe.Machine}  ·  {(pe.Is64Bit ? "PE32+" : "PE32")}{(pe.IsManaged ? "  ·  .NET" : string.Empty)}  ·  {pe.Sections.Count} sections";
            Log($"Loaded {opened.DisplayName}: {pe.Machine}, {(pe.Is64Bit ? "PE32+" : "PE32")}, {pe.Length:N0} bytes, " +
                $"{pe.Sections.Count} sections, {pe.Imports.Count + pe.DelayImports.Count} imported modules, " +
                $"{pe.Exports?.Entries.Count ?? 0} exports{(pe.IsManaged ? ", managed" : string.Empty)}.");

            Warnings.Clear();
            foreach (string w in pe.Warnings)
            {
                Warnings.Add(w);
            }

            if (opened.ManagedLoadError is { } managedError)
            {
                Warnings.Add($"Managed decompiler could not load the assembly: {managedError}");
            }

            if (opened.Analysis is null && !pe.IsManaged)
            {
                Warnings.Add($"Machine type {pe.Machine} is not supported by the native disassembler (x86/x64 only).");
            }

            if (opened.Analysis?.Pdb is { } pdb)
            {
                Log(pdb.Loaded
                    ? $"Loaded {pdb.SymbolsAdded:N0} symbols from {pdb.Path}."
                    : $"No symbols: {pdb.Reason}");
            }

            if (opened.Project is { } project)
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

            if (opened.Analysis is { } analysis)
            {
                _ = RunDiscoveryAsync(analysis);
            }
        }
        catch (PeParseException ex)
        {
            StatusText = $"Cannot open: {ex.Message}";
            Log($"ERROR: {ex.Message}");
            MessageBox.Show(ex.Message, "Not a valid PE file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseFile()
    {
        SaveAnnotationsIfDirty();
        _analysisCts?.Cancel();
        Documents.Clear();
        Explorer.Clear();
        Warnings.Clear();
        ActiveDocument = null;
        ClearHistory();
        _workspace.Close();
        Binary = null;
        AnalysisText = string.Empty;
        StatusText = "Ready";
        Log("File closed.");
    }

    // ------------------------------------------------------------------
    // Analysis
    // ------------------------------------------------------------------

    [RelayCommand]
    private void Reanalyze()
    {
        if (Binary?.Analysis is { } analysis)
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
        if (Binary is null)
        {
            return;
        }

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
        ulong va = value >= pe.ImageBase ? value : value < pe.OptionalHeader.SizeOfImage ? pe.RvaToVa((uint)value) : 0;
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
        if (Binary is not { } b)
        {
            return;
        }

        if (b.Analysis is not null && b.Image.EntryPointRva != 0)
        {
            OpenTarget(new DisassemblyTarget(b.Image.EntryPointVa, b.Image.IsDll ? "DllEntryPoint" : "EntryPoint"));
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

    [RelayCommand]
    private void ClearOutput() => Output.Clear();

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
        if (Binary?.Analysis is not { } analysis)
        {
            return CaretTarget.None;
        }

        var code = ActiveDocument as ICaretContext;
        ulong? functionVa = code?.OwningFunctionVa;

        return CaretTargets.Resolve(
            code?.CaretWord,
            code?.CaretAddress,
            ActiveDocument?.Address,
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

    [RelayCommand]
    private async Task RenameSymbolAsync()
    {
        if (Binary?.Analysis is not { } analysis)
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

    [RelayCommand]
    private async Task EditCommentAsync()
    {
        if (Binary?.Analysis is not { } analysis)
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

    /// <summary>
    /// Writes annotations out when the file is being put away. Renames are the user's work, so they are
    /// not thrown away silently - but where they went is logged, since the file may have landed in the
    /// per-user store rather than beside a binary nobody can write to.
    /// </summary>
    public void SaveAnnotationsIfDirty()
    {
        try
        {
            if (_workspace.SaveIfDirty() is { } path)
            {
                Log($"Saved annotations to {path}");
            }
        }
        catch (IOException ex)
        {
            Log($"Could not save the project: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SaveProject()
    {
        if (Binary is not { } binary || binary.Annotations is null)
        {
            return;
        }

        try
        {
            string? path = binary.SaveProject();
            Log(path is null ? "Nothing to save: no names or comments yet." : $"Saved {binary.Annotations.Count} annotation(s) to {path}");
            StatusText = path is null ? "Nothing to save" : "Project saved";
        }
        catch (IOException ex)
        {
            Log($"Could not save the project: {ex.Message}");
            StatusText = "Project not saved";
        }
    }

    /// <summary>
    /// Re-runs the documents whose text mentions names, and retitles the tabs that are named after a
    /// function. A rename can show up anywhere - in the function itself, and at every call site - so
    /// everything that shows code is reloaded rather than guessing which ones changed.
    /// </summary>
    private async Task RefreshAnnotatedDocumentsAsync()
    {
        if (Binary?.Analysis is not { } analysis)
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
            }
        }
    }

    public void OpenTarget(NodeTarget target)
    {
        if (Binary is null)
        {
            return;
        }

        var b = Binary;
        var pe = b.Image;
        DocumentViewModel? doc = target switch
        {
            OverviewTarget => Find("overview") ?? new OverviewDocumentViewModel(b),
            HeadersTarget => Find("headers") ?? new HeadersDocumentViewModel(pe),
            SectionsTarget => Find("sections") ?? new SectionsDocumentViewModel(pe, s => OpenTarget(new HexTarget(s.PointerToRawData))),
            ImportsTarget => Find("imports") ?? new ImportsDocumentViewModel(pe),
            ResourcesTarget => Find("resources") ?? new ResourcesDocumentViewModel(pe, row => OpenTarget(new ResourcePreviewTarget(row.TypeId, row.Id, row.DataRva, row.DataSize, $"{row.Type}: {row.Name}"))),
            ResourcePreviewTarget preview => OpenResource(preview),
            StringsTarget => Find("strings") ?? new StringsDocumentViewModel(pe, b.Analysis, offset => OpenTarget(new HexTarget(offset))),
            ExportsTarget => Find("exports") ?? new ExportsDocumentViewModel(pe, b.Analysis is null ? null : (va, name) => OpenTarget(new DisassemblyTarget(va, name))),
            FunctionsTarget when b.Analysis is { } a => Find("functions") ?? new FunctionsDocumentViewModel(a, OpenFunctionDisassembly, OpenFunctionPseudoC),
            HexTarget h => OpenHex(h.Offset),
            DisassemblyTarget d when b.Analysis is { } a => Find($"disasm:{d.Va:X}") ?? CodeDocumentViewModel.ForFunctionDisassembly(a, a.GetOrDiscoverFunction(d.Va, d.Name), b.NativeDecompiler is null ? null : OpenFunctionPseudoC, b.NativeDecompiler is null ? null : OpenFunctionSplit),
            RangeDisassemblyTarget r when b.Analysis is { } a => Find($"disasm-range:{r.Va:X}") ?? CodeDocumentViewModel.ForRangeDisassembly(a, r.Va, r.Bytes, r.Title),
            ManagedAssemblyTarget when b.Managed is { } m => Find("managed:assembly") ?? ManagedCodeDocumentViewModel.ForAssembly(m),
            ManagedTypeTarget t when b.Managed is { } m => Find($"managed:type:{t.Type.FullName}") ?? ManagedCodeDocumentViewModel.ForType(m, t.Type),
            ManagedMemberTarget mm when b.Managed is { } m => Find($"managed:member:{mm.Type.FullName}::{mm.Member.Handle.GetHashCode():X}") ?? ManagedCodeDocumentViewModel.ForMember(m, mm.Type, mm.Member),
            _ => null,
        };

        if (doc is not null)
        {
            Show(doc);
            Record(doc, target);
        }
    }

    /// <summary>Opens the function the active document is about in both views at once.</summary>
    [RelayCommand]
    private void OpenSideBySide()
    {
        if (Binary?.Analysis is not { } analysis)
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

    /// <summary>Opens the function in both views at once, each following the other.</summary>
    private void OpenFunctionSplit(Function f)
    {
        if (Binary?.Analysis is not { } a || Binary.NativeDecompiler is not { } d)
        {
            return;
        }

        var doc = Find($"split:{f.EntryVa:X}")
                  ?? SplitCodeDocumentViewModel.For(a, d, f, () => a.TryGetFunction(f.EntryVa, out var latest) ? latest : f);
        Show(doc);
        Record(doc, new DisassemblyTarget(f.EntryVa, f.Name));
    }

    private void OpenFunctionDisassembly(Function f)
    {
        if (Binary?.Analysis is { } a)
        {
            var doc = Find($"disasm:{f.EntryVa:X}")
                      ?? CodeDocumentViewModel.ForFunctionDisassembly(a, f, Binary.NativeDecompiler is null ? null : OpenFunctionPseudoC, Binary.NativeDecompiler is null ? null : OpenFunctionSplit);
            Show(doc);
            Record(doc, new DisassemblyTarget(f.EntryVa, f.Name));
        }
    }

    private void OpenFunctionPseudoC(Function f)
    {
        if (Binary?.NativeDecompiler is { } d && Binary.Analysis is { } a)
        {
            var doc = Find($"pseudoc:{f.EntryVa:X}")
                      ?? CodeDocumentViewModel.ForPseudoC(d, f, OpenFunctionDisassembly, () => a.TryGetFunction(f.EntryVa, out var latest) ? latest : f, OpenFunctionSplit);
            Show(doc);
            Record(doc, null, f.EntryVa);
        }
    }

/// <summary>
    /// Opens a resource as text when its type is one Spydate can decode, and as bytes otherwise.
    /// </summary>
    private DocumentViewModel? OpenResource(ResourcePreviewTarget target)
    {
        var pe = Binary!.Image;
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
        var hex = Find("hex") as HexDocumentViewModel ?? new HexDocumentViewModel(Binary!.Image);
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
            else if (entry.PseudoCVa is { } va && Binary?.Analysis is { } analysis)
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
