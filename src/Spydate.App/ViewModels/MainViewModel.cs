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
/// The window: what is open, what has been opened lately, the output log, the status bar, and the
/// panels shared by everything in it.
///
/// What it is not is the file. Everything about a particular binary — its tree, its tabs, its
/// xrefs, its patches, where the reader has been in it — lives in a <see cref="FileViewModel"/>,
/// and this holds the one that is <see cref="Active"/>. There is exactly one today; the point of
/// the split is that there need not be (docs/MULTI-FILE.md).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IShell
{
    private static readonly string ProductTitle =
        $"Spydate v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(2) ?? "0.1"} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")})";

    private readonly IFileDialogService _dialogs;
    private readonly WorkspaceService _workspace;

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

    /// <summary>
    /// The file the window is showing, or null when nothing is open. Everything the menus, the tree
    /// and the tabs act on hangs off this.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBinary))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private FileViewModel? _active;

    /// <summary>Timestamped log shown in the Output tool window.</summary>
    public ObservableCollection<string> Output { get; } = new();

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isBusy;

    public bool HasBinary => Active is not null;

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

    public async Task OpenPathAsync(string path)
    {
        SaveAnnotationsIfDirty();
        Active?.Close();
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

            // In place before Begin, because Begin opens the overview tab and starts discovery, and
            // both of those are things the window has to already be showing this file to display.
            var file = new FileViewModel(opened, _dialogs, Debugger, this);
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

    [RelayCommand]
    private void CloseFile()
    {
        SaveAnnotationsIfDirty();
        Active?.Close();
        Active = null;
        _workspace.Close();
        StatusText = "Ready";
        Log("File closed.");
    }

    [RelayCommand]
    private void ClearOutput() => Output.Clear();

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

    /// <summary>
    /// Something else rewrote a binary's project file — an agent driving the MCP server, or a second
    /// copy of Spydate. The watcher fires on a thread-pool thread, and everything the file does about
    /// it is UI-thread only.
    /// </summary>
    private void OnProjectChangedOnDisk(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            if (Active is { } file)
            {
                await file.ReloadProjectAsync(_workspace.ReloadProject).ConfigureAwait(true);
            }
        });
    }
}
