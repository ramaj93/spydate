using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
using Spydate.Core.PE;
using Spydate.Core.Project;

namespace Spydate.App.ViewModels;

/// <summary>
/// One entry in the recent-files menu. It carries the command rather than looking it up, because a
/// submenu is rendered in its own popup where binding back to the window is not dependable.
/// </summary>
public sealed record RecentEntry(string Path, string Name, string Folder, System.Windows.Input.ICommand Command);

/// <summary>One row in the Xrefs panel: a site that refers to the current address.</summary>
public sealed record XrefRow(string From, string Function, string Kind, string Instruction, ulong FromVa, ulong? FunctionEntryVa);

/// <summary>
/// The window: which files are open, which one is in front, what has been opened lately, the output
/// log, the status bar, and the panels shared by everything in it.
///
/// What it is not is a file. Everything about a particular binary — its tree, its tabs, its xrefs,
/// its patches, where the reader has been in it — lives in a <see cref="FileViewModel"/>, and the
/// window holds a strip of them with one <see cref="Active"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IShell
{
    private static readonly string ProductTitle =
        $"Spydate v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(2) ?? "0.1"} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")})";

    private readonly IFileDialogService _dialogs;
    private readonly WorkspaceService _workspace;

    /// <summary>
    /// Read once at startup. Nothing in the window writes it yet — the page that does arrives with
    /// the Preferences window — so a value other than the default is one somebody put there by hand.
    /// </summary>
    private Preferences _preferences = PreferenceStore.Load();

    public MainViewModel(IFileDialogService dialogs, WorkspaceService workspace, AssistantViewModel assistant, DebuggerViewModel debugger)
    {
        _dialogs = dialogs;
        workspace.ProjectChangedOnDisk += OnProjectChangedOnDisk;
        _workspace = workspace;
        Assistant = assistant;
        Debugger = debugger;

        // Breakpoints are drawn in the listings, so a toggle redraws them; a stop opens where it
        // stopped, which is the whole reason for stopping there.
        debugger.BreakpointsChanged += (_, _) => Active?.ReloadDocuments();
        debugger.PropertyChanged += (_, e) =>
        {
            // Run to cursor and Test patch live are only offered while something is stopped, so
            // they have to be re-asked when that changes.
            if (e.PropertyName is nameof(DebuggerViewModel.State) or nameof(DebuggerViewModel.IsStopped))
            {
                Active?.NotifyCaretCommands();
                OnPropertyChanged(nameof(IsDebuggingActive));
            }

            // Where the program stopped goes on the window's status bar rather than the Debug
            // panel's own toolbar. It is the one line that changes at every stop and it was only
            // readable with that tab forward — which is exactly when it is least needed, because
            // the panel below already says everything the line does. Latest wins, as a status bar
            // should: a patch made while stopped says so until the next step.
            if (e.PropertyName is nameof(DebuggerViewModel.Status))
            {
                StatusText = Debugger.Status;
            }
        };
        debugger.StoppedAt += (_, va) => Active?.ShowWhereItStopped(va);

        // Double-clicking a breakpoint in the pane opens the code it is in — the same "show me where"
        // as a stop, but moving no arrow, since the program is not there.
        debugger.NavigateRequested += (_, va) => Active?.ShowWhereItStopped(va);
        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFiles));
        RefreshRecent(RecentFiles.Load());
        Log("Spydate started. Open a PE file to begin (Ctrl+O).");
    }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    /// <summary>
    /// The assistant panel. It works on the same analysis the documents do, so a name it gives
    /// appears in them at once rather than after a reload.
    /// </summary>
    public AssistantViewModel Assistant { get; }

    /// <summary>The debugger panel. Nothing it holds runs until somebody asks it to.</summary>
    public DebuggerViewModel Debugger { get; }

    /// <summary>The open files, in the order their tabs appear.</summary>
    public ObservableCollection<FileViewModel> Files { get; } = new();

    /// <summary>
    /// The file in front. Everything the menus, the tree and the tabs act on hangs off this, and
    /// setting it is what makes a tab the current one everywhere else — the debugger and the
    /// assistant both ask the workspace which binary is current rather than being told.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBinary))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private FileViewModel? _active;

    partial void OnActiveChanged(FileViewModel? value) => _workspace.Activate(value?.Binary);

    /// <summary>Timestamped log shown in the Output tool window.</summary>
    public ObservableCollection<string> Output { get; } = new();

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isBusy;

    public bool HasBinary => Active is not null;

    public bool HasFiles => Files.Count > 0;

    /// <summary>Whether a debuggee is up at all, for the tab that owns it to say so.</summary>
    public bool IsDebuggingActive => Debugger.IsDebugging;

    public string WindowTitle => Active is null ? ProductTitle : $"{ProductTitle} — {Active.DisplayName}";

    public void Log(string message) => Output.Add($"{DateTime.Now:HH:mm:ss}  {message}");

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

    /// <summary>Binaries opened lately, newest first. Empty until something has been opened.</summary>
    public ObservableCollection<RecentEntry> Recent { get; } = new();

    public bool HasRecent => Recent.Count > 0;

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // A recent list outlives the files in it. Saying so and taking the entry out is better than
        // a parse error about a file the user did not choose so much as remember.
        if (!File.Exists(path))
        {
            StatusText = $"{Path.GetFileName(path)} is no longer there.";
            Log($"Not found, and removed from recent files: {path}");
            RefreshRecent(RecentFiles.Remove(path));
            return;
        }

        await OpenPathAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ClearRecent()
    {
        RecentFiles.Clear();
        RefreshRecent([]);
    }

    private void RefreshRecent(IReadOnlyList<RecentFile> entries)
    {
        Recent.Clear();
        foreach (var entry in entries)
        {
            Recent.Add(new RecentEntry(entry.Path, entry.Name, entry.Folder, OpenRecentCommand));
        }

        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>
    /// Opens a file, asking where it should go when something is already open.
    ///
    /// Every way in comes through here — the menu, the recent list, a file dropped on the window,
    /// the command line — so they all behave the same way. Opening a file that is already open is
    /// not a question at all: it shows the tab that has it.
    /// </summary>
    public async Task OpenPathAsync(string path)
    {
        if (_workspace.FindByPath(path) is { } already
            && Files.FirstOrDefault(f => ReferenceEquals(f.Binary, already)) is { } openTab)
        {
            Active = openTab;
            StatusText = $"{openTab.DisplayName} is already open.";
            return;
        }

        if (WhereToOpen(path) is not { } destination)
        {
            return;   // cancelled: nothing is opened, and what was open is untouched
        }

        IsBusy = true;
        StatusText = $"Loading {Path.GetFileName(path)}…";
        Log($"Loading {path}");
        try
        {
            var opened = await _workspace.OpenAsync(path).ConfigureAwait(true);

            var pe = opened.Image;
            StatusText = $"{opened.DisplayName}  ·  {pe.Machine}  ·  {(pe.Is64Bit ? "PE32+" : "PE32")}{(pe.IsManaged ? "  ·  .NET" : string.Empty)}  ·  {pe.Sections.Count} sections";
            Log($"Loaded {opened.DisplayName}: {pe.Machine}, {(pe.Is64Bit ? "PE32+" : "PE32")}, {pe.Length:N0} bytes, " +
                $"{pe.Sections.Count} sections, {pe.Imports.Count + pe.DelayImports.Count} imported modules, " +
                $"{pe.Exports?.Entries.Count ?? 0} exports{(pe.IsManaged ? ", managed" : string.Empty)}.");

            // Where the new tab goes, worked out before the old one is closed so that replacing
            // puts it back in the same place in the strip rather than at the end.
            var replacing = destination == OpenDestination.ReplaceCurrent ? Active : null;
            int at = replacing is null ? Files.Count : Files.IndexOf(replacing);

            var file = new FileViewModel(opened, _dialogs, Debugger, this);
            Files.Insert(at, file);

            if (replacing is not null)
            {
                CloseFileTab(replacing);
            }

            // In place before Begin, because Begin opens the overview tab and starts discovery, and
            // both of those are things the window has to already be showing this file to display.
            Active = file;
            file.Begin();

            // Recorded only once it has opened, so a file that turns out not to be a PE does not
            // land in the menu as something worth trying again.
            RefreshRecent(RecentFiles.Add(path));
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

    /// <summary>
    /// Whether the new file takes a tab of its own or the current one's place. Null to do neither.
    ///
    /// Nothing open is not a question. Otherwise it is the reader's, unless they have said in the
    /// preferences that it is not — which today they can only do by editing the file, since the
    /// page that sets it comes with the Preferences window.
    /// </summary>
    private OpenDestination? WhereToOpen(string path)
    {
        if (Active is not { } current)
        {
            return OpenDestination.NewTab;
        }

        return _preferences.OpenDestination switch
        {
            OpenDestination.NewTab => OpenDestination.NewTab,
            OpenDestination.ReplaceCurrent => OpenDestination.ReplaceCurrent,
            _ => _dialogs.AskWhereToOpen(path, current.DisplayName, Debugger.IsDebugging),
        };
    }

    /// <summary>Closes the tab in front.</summary>
    [RelayCommand]
    private void CloseFile() => CloseFileTab(Active);

    /// <summary>Closes one tab, saving its work and moving to a neighbour if it was in front.</summary>
    [RelayCommand]
    private void CloseFileTab(FileViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        SaveIfDirty(file);

        int index = Files.IndexOf(file);
        bool wasActive = ReferenceEquals(Active, file);

        file.Close();
        Files.Remove(file);
        _workspace.Close(file.Binary);

        if (wasActive)
        {
            Active = Files.Count == 0 ? null : Files[Math.Clamp(index - 1, 0, Files.Count - 1)];
        }

        Log($"Closed {file.DisplayName}.");
        if (Files.Count == 0)
        {
            StatusText = "Ready";
        }
    }

    [RelayCommand]
    private void CloseOtherFiles()
    {
        foreach (var file in Files.Where(f => !ReferenceEquals(f, Active)).ToList())
        {
            CloseFileTab(file);
        }
    }

    [RelayCommand]
    private void CloseAllFiles()
    {
        foreach (var file in Files.ToList())
        {
            CloseFileTab(file);
        }
    }

    [RelayCommand]
    private void ClearOutput() => Output.Clear();

    /// <summary>
    /// Writes one file's annotations out when it is being put away. Renames are the user's work, so
    /// they are not thrown away silently - but where they went is logged, since the file may have
    /// landed in the per-user store rather than beside a binary nobody can write to.
    /// </summary>
    private void SaveIfDirty(FileViewModel file)
    {
        try
        {
            if (WorkspaceService.SaveIfDirty(file.Binary) is { } path)
            {
                Log($"Saved annotations to {path}");
            }
        }
        catch (IOException ex)
        {
            Log($"Could not save the project: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes out every open file that has unsaved work. The window closing has to save all of
    /// them, not only the tab that happens to be in front.
    /// </summary>
    public void SaveAnnotationsIfDirty()
    {
        foreach (string path in _workspace.SaveAllIfDirty())
        {
            Log($"Saved annotations to {path}");
        }
    }

    /// <summary>
    /// Something else rewrote a binary's project file — an agent driving the MCP server, or a second
    /// copy of Spydate. The watcher fires on a thread-pool thread, and everything the file does about
    /// it is UI-thread only. It names which file, because that need not be the one being looked at.
    /// </summary>
    private void OnProjectChangedOnDisk(object? sender, Services.OpenedBinary binary)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            if (Files.FirstOrDefault(f => ReferenceEquals(f.Binary, binary)) is { } file)
            {
                await file.ReloadProjectAsync(() => _workspace.ReloadProject(binary)).ConfigureAwait(true);
            }
        });
    }
}
