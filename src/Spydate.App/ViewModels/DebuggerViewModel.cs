using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using System.Reflection.Metadata;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
using Spydate.Core.Project;
using Spydate.Debugger;
using Spydate.Debugger.Managed;
using Spydate.Decompiler.Managed;
using Spydate.Disassembly;

namespace Spydate.App.ViewModels;

/// <summary>One register, as the panel shows it. Changed since the last stop is the useful part:
/// at a breakpoint the question is almost always what the last few instructions did.</summary>
public sealed record RegisterRow(string Name, string Value, bool Changed);

/// <summary>One qword on the stack.</summary>
public sealed record StackRow(string Address, string Value);

/// <summary>
/// One module the debuggee has loaded. <paramref name="IsTarget"/> marks the one the listing is
/// about, which under a host is the whole question — whether the DLL has been loaded yet.
/// </summary>
public sealed record ModuleRow(string Name, string Base, string Path, bool IsTarget);

/// <summary>
/// One thread. <paramref name="IsCurrent"/> is the one that stopped — whose registers are on screen,
/// and the one a step will actually step.
/// </summary>
public sealed record ThreadRow(uint Id, string Start, bool IsCurrent, bool Waiting)
{
    /// <summary>What it is doing, when that decides whether it can be stepped.</summary>
    public string Doing => Waiting ? "waiting" : string.Empty;
}

/// <summary>
/// A patch that is in the running process but not in the project — a hypothesis, tried live before
/// anyone commits to it. Keep writes it into the project; Undo takes it back out of the process.
/// </summary>
public sealed record LivePatchRow(uint Rva, ulong Va, string Was, string Now, string? Comment)
{
    public string Where => $"0x{Va:X}";
}

/// <summary>
/// The debugger panel: starting the open binary, stopping it, and reading it while it is stopped.
///
/// It holds a <see cref="DebugSession"/> rather than being one. The session reports from its own
/// thread — Windows requires the debug loop to stay on the thread that started it — so everything
/// here marshals before touching a collection the UI is bound to.
///
/// Starting is always explicit and always asks first. Every other part of Spydate reads the file;
/// this runs it, and the binaries people open here are frequently ones they do not trust.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class DebuggerViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceService _workspace;
    private ManagedDebugSession? _managed;
    private readonly Func<string, string, bool> _confirm;
    private DebugSession? _session;

    /// <summary>Registers as they were at the previous stop, so a change can be pointed at.</summary>
    private IReadOnlyDictionary<string, ulong> _previous = new Dictionary<string, ulong>(StringComparer.Ordinal);

    private readonly IFileDialogService? _dialogs;

    public DebuggerViewModel(WorkspaceService workspace, IFileDialogService? dialogs = null, Func<string, string, bool>? confirm = null)
    {
        _workspace = workspace;
        _dialogs = dialogs;
        _confirm = confirm ?? DefaultConfirm;
        workspace.CurrentChanged += (_, _) =>
        {
            StopSession();
            RecallTarget();
        };
    }

    /// <summary>Reads back the host and arguments last used for the binary now open.</summary>
    private void RecallTarget()
    {
        var target = _workspace.Current?.Image.Path is { Length: > 0 } path ? DebugTargets.For(path) : null;

        Host = target?.Host ?? string.Empty;
        Arguments = target?.Arguments ?? string.Empty;
        WorkingDirectory = target?.WorkingDirectory ?? string.Empty;
        Modules.Clear();
        Threads.Clear();
        Variables.Clear();
        ManagedThreads.Clear();
        _expanded.Clear();
        _statements.Clear();   // a different binary has different methods under the same tokens
        _localNames.Clear();
        OnPropertyChanged(nameof(NeedsHost));

        // Which debugger applies is a fact about the file, so the panel rearranges itself when a
        // different one is opened rather than when something is run.
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(ShowsRegisters));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanPauseNow));
    }

    /// <summary>
    /// Remembers how to run this binary. Written on every change rather than on start, because the
    /// setting most worth keeping is the one somebody typed and then closed the window without using.
    /// </summary>
    private void RememberTarget()
    {
        if (_workspace.Current?.Image.Path is not { Length: > 0 } path)
        {
            return;
        }

        DebugTargets.Set(path, new DebugTarget
        {
            Host = Host.Trim() is { Length: > 0 } host ? host : null,
            Arguments = Arguments.Trim() is { Length: > 0 } arguments ? arguments : null,
            WorkingDirectory = WorkingDirectory.Trim() is { Length: > 0 } directory ? directory : null,
        });
    }

    partial void OnHostChanged(string value)
    {
        RememberTarget();
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnArgumentsChanged(string value) => RememberTarget();

    partial void OnWorkingDirectoryChanged(string value) => RememberTarget();

    /// <summary>
    /// Picks the program that loads this DLL. Its folder becomes the working directory unless one has
    /// already been set — a host usually wants to run where it lives, and finding out that it did not
    /// costs a debugging session.
    /// </summary>
    [RelayCommand]
    private void ChooseHost()
    {
        if (_dialogs?.OpenPeFile() is not { Length: > 0 } chosen)
        {
            return;
        }

        Host = chosen;
        if (WorkingDirectory.Trim().Length == 0 && Path.GetDirectoryName(chosen) is { Length: > 0 } folder)
        {
            WorkingDirectory = folder;
        }
    }

    [RelayCommand]
    private void ClearHost()
    {
        Host = string.Empty;
        WorkingDirectory = string.Empty;
    }

    /// <summary>
    /// Opens the run configuration. Modal, and owned by the window, so it cannot be lost behind it.
    /// </summary>
    [RelayCommand]
    private void ConfigureRun()
    {
        var window = new Views.RunConfigWindow(this) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
    }

    /// <summary>Registers as of the last stop. Empty while it is running, because they would be a guess.</summary>
    public ObservableCollection<RegisterRow> Registers { get; } = new();

    /// <summary>
    /// The arguments and locals of the frame being looked at, as a tree flattened into rows.
    ///
    /// What replaces registers and stack words when the debuggee is .NET. They are not an addition
    /// to those — they are the same question answered by something that knows the answer. A native
    /// stop can say <c>rcx = 0x1F2A40</c>; this can say the path being opened, and open the object
    /// holding it.
    /// </summary>
    public ObservableCollection<VariableRow> Variables { get; } = new();

    /// <summary>
    /// Which rows were open, by path, so a step leaves them open.
    ///
    /// Without it every F10 folded the tree back to its roots, and a reader watching one field of one
    /// object had to reopen it after every statement — which is the thing they were stepping to see.
    /// </summary>
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>The decompiler's names for each method's locals, by token, for the binary that is open.</summary>
    private readonly Dictionary<uint, IReadOnlyDictionary<int, string>> _localNames = new();

    /// <summary>The session's stop count when the tree and threads were last read, so a log line does not reread them.</summary>
    private int _shownStops = -1;

    /// <summary>
    /// Every thread of a stopped .NET process, the one being looked at marked.
    ///
    /// The native panel has always had this; the managed one lost it when its registers pane was
    /// swapped for Locals, and with it any way to see where the other threads were or to look at one.
    /// </summary>
    public ObservableCollection<ManagedThreadRow> ManagedThreads { get; } = new();

    /// <summary>
    /// The thread picked in the Threads pane. Picking one moves the values, the arrow and stepping to
    /// it; the process stays stopped.
    /// </summary>
    [ObservableProperty]
    private ManagedThreadRow? _selectedManagedThread;

    /// <summary>True while the selection is being set from the session rather than by a person.</summary>
    private bool _quietManagedThread;

    /// <summary>The top of the stack as of the last stop.</summary>
    public ObservableCollection<StackRow> Stack { get; } = new();

    /// <summary>Everything the process has loaded, so it is visible whether the target is among it.</summary>
    public ObservableCollection<ModuleRow> Modules { get; } = new();

    /// <summary>
    /// Every thread alive, with the one that stopped marked.
    ///
    /// Worth having even though a step still follows whichever thread reported the event: in a
    /// program with several, a breakpoint hit in one says nothing about where the others are, and
    /// "why did it not stop where I expected" is very often another thread having got there first.
    /// </summary>
    public ObservableCollection<ThreadRow> Threads { get; } = new();

    /// <summary>
    /// The program to start, when the binary cannot start itself. Empty for an EXE, which is its own
    /// host; required for a DLL, which is a module and not a program.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsHost))]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    /// <summary>Whether the open binary cannot be started without a host being chosen first.</summary>
    public bool NeedsHost => _workspace.Current?.Image.IsDll == true && Host.Trim().Length == 0;

    /// <summary>
    /// Whether debugging this binary means driving the CLR rather than the process.
    ///
    /// Decided by the file, not by a setting: an IL-only assembly has no native code of its own to
    /// stop in, and the two debuggers cannot both attach. A mixed-mode assembly stays native, where
    /// its real machine instructions are.
    /// </summary>
    public bool IsManaged => _workspace.Current?.Image.ClrHeader?.IsILOnly == true;

    /// <summary>Registers and stack words are worth showing only when there is native code.</summary>
    public bool ShowsRegisters => !IsManaged;

    /// <summary>Pausing and running to a cursor are native-only, so far.</summary>
    public bool CanPause => !IsManaged;

    /// <summary>The processor flags, spelled out. "ZF 1 CF 0" is read; 0x246 is decoded.</summary>
    [ObservableProperty]
    private string _flags = string.Empty;

    /// <summary>What the debuggee has done, newest last.</summary>
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Breakpoints by static address — the ones in the listing, set before anything runs.</summary>
    public HashSet<ulong> BreakpointAddresses { get; } = new();

    public ObservableCollection<string> Breakpoints { get; } = new();

    /// <summary>
    /// Bumped whenever the set changes. The margin binds a set, which raises nothing when it gains
    /// a member, so this is what actually tells it to redraw.
    /// </summary>
    [ObservableProperty]
    private int _breakpointsVersion;

    /// <summary>Where execution is stopped, as the listing states it. Null when nothing is stopped.</summary>
    [ObservableProperty]
    private ulong? _executionAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDebugging))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    private DebugState _state = DebugState.NotStarted;

    [ObservableProperty]
    private string _status = "Not running.";

    public bool IsDebugging => State is DebugState.Running or DebugState.Stopped;

    public bool IsStopped => State == DebugState.Stopped;

    public bool IsRunning => State == DebugState.Running;

    /// <summary>Pausing is native-only so far, so the button says so by being off.</summary>
    public bool CanPauseNow => IsRunning && CanPause;

    /// <summary>Raised when a breakpoint is set or cleared, so listings can redraw their markers.</summary>
    public event EventHandler? BreakpointsChanged;

    /// <summary>Raised when execution stops somewhere, so the window can show where.</summary>
    public event EventHandler<ulong>? StoppedAt;

    // ------------------------------------------------------------------

    private bool CanStart() => _workspace.Current is not null && !IsDebugging && !_starting;

    /// <summary>
    /// Whether a start is in flight. Not the same as running: getting hold of a runtime takes a
    /// moment, and in that moment the state is still "not started", so nothing else says no.
    /// </summary>
    private bool _starting;

    /// <summary>
    /// Starts the open binary under a debugger.
    ///
    /// Asynchronous for the managed half of it, and that is not a refinement. Getting hold of a
    /// runtime is a wait — for CoreCLR to publish itself, or for the first callback to arrive — and
    /// it is bounded by a timeout rather than by anything the debuggee is obliged to do. Run on the
    /// window's thread, that wait is the window: the menus stop opening, the panel does not repaint,
    /// and a launch that is never going to work holds the whole application for half a minute before
    /// saying so. Which is what a .NET Framework program did here, every time.
    ///
    /// The native path has no such wait and finishes inside this method without ever yielding.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_workspace.Current is not { } binary || binary.Image.Path is not { Length: > 0 } path)
        {
            return;
        }

        // Guarded here as well as by CanExecute, which the button honours and a direct Execute does
        // not — and the assistant calls it directly. A second start does not replace the first: it
        // leaves the running process orphaned and begins another, so the analyst who agreed to run
        // this binary once has two of it, and the panel is showing only one.
        if (IsDebugging || _starting)
        {
            Status = _starting ? "It is already starting." : "It is already running.";
            Add("already running — stop it before starting it again");
            return;
        }

        // A DLL is not a program: CreateProcess refuses it, and the refusal used to arrive after the
        // confirmation, so it asked whether to run something that could not be run. Something else
        // has to load it, and that is what the host is for.
        string? host = Host is { Length: > 0 } chosen ? chosen.Trim() : null;
        if (binary.Image.IsDll && host is null)
        {
            Status = "A DLL needs a host program to load it.";
            Add($"{binary.DisplayName} is a DLL, so it cannot be started on its own. "
                + "Choose the program that loads it, and its breakpoints go in when it is loaded.");
            return;
        }

        if (host is not null && !File.Exists(host))
        {
            Status = "That host program is not there.";
            Add($"the host {host} does not exist");
            return;
        }

        string run = host ?? path;

        if (IsManaged)
        {
            // The host, not the assembly. A .NET assembly with an entry point is still a DLL — the
            // .exe beside it is a native launcher with no metadata of its own — so the thing the
            // analyst opened is almost never the thing that can be started. This used to hand the
            // DLL to CreateProcess, which took it, produced a process with no runtime in it, and
            // reported after thirty seconds that the target did not look like .NET. The breakpoints
            // are unaffected: they name the module they are in and are planted when it loads, which
            // is exactly what running under a host does.
            await StartManagedAsync(binary, run, host);
            return;
        }

        // Asked every time, not once and remembered. The answer is about this binary, and the cost
        // of getting it wrong is running something hostile on the analyst's own machine.
        if (!_confirm(
                "Run this binary?",
                (host is null
                    ? $"{binary.DisplayName} will be started on this machine and will do whatever it does.\n\n"
                    : $"{Path.GetFileName(host)} will be started on this machine, so that it loads "
                      + $"{binary.DisplayName}. Both will do whatever they do.\n\n")
                + "Everything else in Spydate only reads the file. Debug it in a virtual machine if you "
                + "do not know what it is.\n\nStart it?"))
        {
            return;
        }

        var session = new DebugSession();
        session.Reported += OnReported;
        _session = session;

        foreach (ulong address in BreakpointAddresses)
        {
            session.AddBreakpoint(address);
        }

        // Every patch that is switched on goes into the run, so it behaves like the patched copy
        // would without one having to be saved. They are held now and written when the module lands.
        ApplySavedPatches(binary, session);
        binary.Patches.Changed += OnSavedPatchChanged;

        try
        {
            // Under a host, the process is somebody else's program and the addresses on screen belong
            // to a module inside it, so the module has to be named or nothing would translate.
            session.Start(
                run,
                binary.Image.ImageBase,
                binary.Image.OptionalHeader.SizeOfImage,
                Arguments is { Length: > 0 } arguments ? arguments : null,
                WorkingDirectory is { Length: > 0 } directory ? directory : null,
                host is null ? null : binary.Image.FileName);

            State = DebugState.Running;
            Status = "Running.";
            Add(host is null
                ? $"started {run}"
                : $"started {run}, waiting for {binary.Image.FileName} to load");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Add($"could not start it: {ex.Message}");
            Status = ex.Message;
            StopSession();
        }

        NotifyCommands();
    }

    /// <summary>
    /// Runs a .NET assembly under the CLR debugging interface.
    ///
    /// Held before it runs anything, always. That is the only moment at which a breakpoint is
    /// certainly in place before the code it is about, and it costs nothing: the panel comes up
    /// stopped and one click on continue lets it go.
    /// </summary>
    private async Task StartManagedAsync(OpenedBinary binary, string run, string? host)
    {
        if (!_confirm(
                "Run this binary?",
                (host is null
                    ? $"{binary.DisplayName} will be started on this machine under the .NET debugger "
                      + "and will do whatever it does.\n\n"
                    : $"{Path.GetFileName(host)} will be started on this machine under the .NET "
                      + $"debugger, so that it loads {binary.DisplayName}. Both will do whatever they "
                      + "do.\n\n")
                + "Everything else in Spydate only reads the file. Debug "
                + "it in a virtual machine if you do not know what it is.\n\nStart it?"))
        {
            return;
        }

        var session = new ManagedDebugSession();
        session.Reported += OnManagedReported;
        _managed = session;

        Status = $"Starting {Path.GetFileName(run)}…";
        Add($"starting {run} under the .NET debugger");

        string? arguments = Arguments is { Length: > 0 } given ? given : null;
        string? directory = WorkingDirectory is { Length: > 0 } where ? where : null;

        string? problem;
        _starting = true;
        NotifyCommands();
        try
        {
            // Off the window's thread and waited for, rather than done on it. The await comes back
            // here, on the thread that owns the bound collections, so everything below is unchanged.
            problem = await Task.Run(
                () => session.Start(run, arguments, directory, holdAtStart: true));
        }
        finally
        {
            _starting = false;
        }

        if (problem is not null)
        {
            Add($"could not start it: {problem}");
            Status = problem;
            StopSession();
            NotifyCommands();
            return;
        }

        // Put in while it is held, which is the point of holding: every one of them is in place
        // before the code it is about has run. A run that planted them afterwards would be racing
        // the program for the ones near the start, which are the ones people set.
        foreach (var (module, token, offset) in _pendingManaged.Values)
        {
            if (session.SetBreakpoint(module, token, offset) is { } refused)
            {
                Add(refused);
            }
        }

        State = DebugState.Stopped;
        Status = "Held before it ran anything.";
        Add($"started {run} under the .NET debugger, held before it ran anything");
        SyncManaged();
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(IsDebugging))]
    private void StopDebugging()
    {
        StopSession();
        Add("stopped; the process was terminated");

        // Says which of the two ways of not running this is. Stopping kills the debuggee — a
        // debugged process does not outlive its session — and that is worth stating rather than
        // leaving it to read the same as never having started one.
        Status = "Stopped; the process was terminated.";
        NotifyCommands();
    }

    // The three below all guard, and none of them used to. CanExecute disables the buttons, but
    // RelayCommand.Execute does not consult it, so anything calling the command directly - the
    // assistant does - walked straight past it and set the state back to Running on a process that
    // had already exited. That erased the exit code and left every later question answered "still
    // running", which is the loop an agent cannot get out of by trying harder.
    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void Continue()
    {
        if (!IsStopped)
        {
            return;
        }

        Resuming();
        _managed?.Continue();
        _session?.Continue();
        State = DebugState.Running;
        Status = "Running.";
        NotifyCommands();
    }

    /// <summary>
    /// Stops it where it is, without ending it. Guarded like the others, because the assistant calls
    /// the command directly and a pause asked of a stopped process would be answered by nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPauseNow))]
    private void Pause()
    {
        if (!IsRunning || _session is not { } session)
        {
            return;
        }

        Add(session.Pause() ? "pausing" : "could not pause it");
    }

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void StepInstruction()
    {
        if (!IsStopped)
        {
            return;
        }

        Resuming();
        if (_managed is not null && StepStatement(into: true) is { } refused)
        {
            Add(refused);
            return;
        }

        _session?.StepInstruction();
        State = DebugState.Running;
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void StepOver()
    {
        if (!IsStopped)
        {
            return;
        }

        Resuming();
        if (_managed is not null && StepStatement(into: false) is { } declined)
        {
            Add(declined);
            return;
        }

        _session?.StepOver();
        State = DebugState.Running;
        NotifyCommands();
    }

    /// <summary>
    /// About to run again: forget where it was.
    ///
    /// The registers were already cleared — they would be a guess while it runs — but the execution
    /// address was not, so the marker stayed sitting on the instruction it had stopped at through
    /// the whole of the next run. Continuing round a loop back to the same breakpoint then looked
    /// exactly like continuing having done nothing at all.
    /// </summary>
    private void Resuming()
    {
        Registers.Clear();
        Stack.Clear();
        ExecutionAddress = null;
    }

    /// <summary>
    /// Reads the debuggee's memory at an address from the listing. Empty when nothing is running,
    /// which is not exceptional — it is the ordinary state.
    /// </summary>
    public byte[] ReadMemory(ulong staticVa, int length)
        => _session is { } session ? session.ReadMemory(session.ToRuntime(staticVa), length) : [];

    /// <summary>Runs until execution reaches an address, without keeping a breakpoint there.</summary>
    public void RunTo(ulong staticVa)
    {
        if (!IsStopped)
        {
            return;
        }

        Resuming();
        _session?.RunTo(staticVa);
        State = DebugState.Running;
        Status = $"Running to 0x{staticVa:X}.";
        NotifyCommands();
    }

    /// <summary>
    /// Sets or clears a breakpoint at a static address. It works before anything is running: the
    /// addresses are kept here and planted when a session starts, which is how anyone actually uses
    /// a debugger — read the listing, mark the interesting place, then run.
    /// </summary>
    public void ToggleBreakpoint(ulong staticVa)
    {
        if (IsManaged)
        {
            ToggleManagedBreakpoint(staticVa);
            return;
        }

        if (BreakpointAddresses.Remove(staticVa))
        {
            _session?.RemoveBreakpoint(staticVa);
            Add($"cleared the breakpoint at 0x{staticVa:X}");
        }
        else
        {
            BreakpointAddresses.Add(staticVa);
            _session?.AddBreakpoint(staticVa);
            Add($"breakpoint at 0x{staticVa:X}");
        }

        BreakpointsVersion++;
        RefreshBreakpoints();
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearBreakpoints()
    {
        foreach (ulong address in BreakpointAddresses.ToList())
        {
            // A managed one is named by its method rather than by the address it is drawn at, and
            // the runtime has to be told about each. Clearing all of them used to empty the listing
            // and leave every one of them firing.
            if (_pendingManaged.TryGetValue(address, out var managed))
            {
                if (_managed?.ClearBreakpoint(managed.Module, managed.Token, managed.Offset) is { } refused)
                {
                    Add(refused);
                    continue;   // kept, marker and all, because it is still in the process
                }

                _pendingManaged.Remove(address);
            }
            else
            {
                _session?.RemoveBreakpoint(address);
            }

            BreakpointAddresses.Remove(address);
        }

        BreakpointsVersion++;
        RefreshBreakpoints();
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Everything the CLR debugger reports, reduced to what the panel shows.
    ///
    /// Coarser than the native handler on purpose. The managed session has no separate event kinds —
    /// it reports a line of text and keeps its own state — so rather than parsing those lines this
    /// reads the state back after every one of them. There are tens of events in a run, not
    /// thousands, and a handler that inferred meaning from wording would be wrong the first time the
    /// wording changed.
    /// </summary>
    private void OnManagedReported(object? sender, DebugEvent e)
    {
        // From the runtime's own callback thread, or a worker, so nothing here touches a bound
        // collection directly.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            Add(e.Text);
            SyncManaged();
        });
    }

    /// <summary>
    /// The listing address a managed stop corresponds to, or null when there is nothing to point at.
    ///
    /// Null is the ordinary answer for a stop in another assembly — the program is somewhere real,
    /// but not anywhere this window has open, and an arrow drawn on the nearest line of the wrong
    /// file is worse than no arrow.
    /// </summary>
    private ulong? Located(ManagedLocation? at)
        => at is null ? null : AddressOf(at.Module, at.MethodToken, at.Offset);

    /// <summary>
    /// Where a method's IL offset sits in the file now open, or null when it is somewhere else.
    ///
    /// The reverse of what the gutter does, and it has to be the same index in both directions: the
    /// addresses the listing printed came from <see cref="OpenedBinary.Bodies"/>, so an answer
    /// computed any other way could point at a line the reader is not looking at. Null for a method
    /// in another assembly, which is not a fault — a marker on the nearest line of the wrong file
    /// would be worse than no marker.
    /// </summary>
    private ulong? AddressOf(string module, uint methodToken, uint ilOffset)
    {
        if (_workspace.Current is not { } binary || binary.Bodies is not { } bodies)
        {
            return null;
        }

        if (!string.Equals(module, binary.Image.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var handle = System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle((int)(methodToken & 0x00FFFFFF));
        return bodies.Of(handle) is { } body ? binary.Image.ImageBase + body.RvaOf((int)ilOffset) : null;
    }

    /// <summary>Reads the managed session's state onto the panel.</summary>
    private void SyncManaged()
    {
        if (_managed is not { } session)
        {
            return;
        }

        State = session.State;
        Status = session.Status is { Length: > 0 } said ? char.ToUpperInvariant(said[0]) + said[1..] : "Running.";

        // Back the other way: a stop is a method token and an IL offset, and the listing is
        // addressed, so the arrow can be put on the line the program is actually at. The same index
        // that gave the listing its addresses answers this, so the two cannot disagree.
        ExecutionAddress = Located(session.StoppedAt);
        if (ExecutionAddress is { } at)
        {
            StoppedAt?.Invoke(this, at);
        }

        Modules.Clear();
        foreach (string module in session.Modules)
        {
            Modules.Add(new ModuleRow(module, string.Empty, string.Empty, false));
        }

        Breakpoints.Clear();
        foreach (var breakpoint in session.Breakpoints)
        {
            Breakpoints.Add(breakpoint.ToString());
        }

        if (State != DebugState.Stopped)
        {
            // Cleared rather than left, for the same reason the registers are: values belonging to a
            // frame that has run on read as current, and anything reasoning from them is reasoning
            // about somewhere the program no longer is.
            Variables.Clear();
            ManagedThreads.Clear();
            _shownStops = -1;
            NotifyCommands();
            return;
        }

        // Once per stop, not once per event. Every line the session logs while stopped arrives here —
        // a breakpoint planted, a module noted — and rereading the tree for each would fold it and
        // throw away the reader's place for nothing.
        if (session.Stops != _shownStops)
        {
            _shownStops = session.Stops;
            RefreshVariables(session);
            RefreshManagedThreads(session);
        }

        NotifyCommands();
    }

    /// <summary>Reads the frame's values afresh, and reopens whatever was open before.</summary>
    private void RefreshVariables(ManagedDebugSession session)
    {
        var names = LocalNames(session.StoppedAt);

        Variables.Clear();
        foreach (var variable in session.Variables())
        {
            // A local's slot gets the name the C# view gives it. Arguments already have theirs, from
            // the metadata, and `this` is `this`.
            bool isLocal = !variable.Path.Argument && variable.Path.Steps.IsEmpty;
            var named = isLocal && names.TryGetValue((int)variable.Path.Slot, out string? name)
                ? variable with { Name = name }
                : variable;

            Variables.Add(new VariableRow(named, 0, OnVariableToggled));
        }

        // Reopening walks forward over the rows it inserts, so an open row inside an open row opens
        // too without anything recursive.
        for (int i = 0; i < Variables.Count; i++)
        {
            var row = Variables[i];
            if (row.Expandable && _expanded.Contains(row.Path.Key))
            {
                row.SetExpandedQuietly(true);
                Expand(row);
            }
        }
    }

    private void OnVariableToggled(VariableRow row)
    {
        if (row.IsExpanded)
        {
            _expanded.Add(row.Path.Key);
            Expand(row);
        }
        else
        {
            _expanded.Remove(row.Path.Key);
            Collapse(row);
        }
    }

    /// <summary>Inserts a row's children after it, read from the process now.</summary>
    private void Expand(VariableRow row)
    {
        if (_managed is not { } session || !IsStopped)
        {
            return;
        }

        int at = Variables.IndexOf(row);
        if (at < 0)
        {
            return;
        }

        int insert = at + 1;
        foreach (var child in session.Children(row.Path))
        {
            Variables.Insert(insert++, new VariableRow(child, row.Depth + 1, OnVariableToggled));
        }
    }

    /// <summary>Takes out every row below this one that is deeper than it.</summary>
    private void Collapse(VariableRow row)
    {
        int at = Variables.IndexOf(row);
        if (at < 0)
        {
            return;
        }

        while (at + 1 < Variables.Count && Variables[at + 1].Depth > row.Depth)
        {
            Variables.RemoveAt(at + 1);
        }
    }

    /// <summary>
    /// What the decompiler called a method's locals, when the stop is in the binary on screen. Empty
    /// for any other module: there is no decompiled text of it beside the pane to agree with.
    /// </summary>
    private IReadOnlyDictionary<int, string> LocalNames(ManagedLocation? at)
    {
        if (at is null || _workspace.Current is not { } binary || binary.Managed is not { } managed
            || !string.Equals(at.Module, binary.Image.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return NoNames;
        }

        if (_localNames.TryGetValue(at.MethodToken, out var known))
        {
            return known;
        }

        IReadOnlyDictionary<int, string> found;
        try
        {
            found = managed.Decompiler.LocalNamesFor(
                System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle((int)(at.MethodToken & 0x00FFFFFF)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A method the decompiler will not take keeps its slot names. Nothing else depends on it.
            found = NoNames;
        }

        _localNames[at.MethodToken] = found;
        return found;
    }

    private static readonly IReadOnlyDictionary<int, string> NoNames = new Dictionary<int, string>();

    private void RefreshManagedThreads(ManagedDebugSession session)
    {
        ManagedThreads.Clear();
        foreach (var thread in session.Threads())
        {
            var row = ManagedThreadRow.From(thread);
            ManagedThreads.Add(row);

            if (row.IsSelected)
            {
                _quietManagedThread = true;
                SelectedManagedThread = row;
                _quietManagedThread = false;
            }
        }
    }

    partial void OnSelectedManagedThreadChanged(ManagedThreadRow? value)
    {
        if (_quietManagedThread || value is null || value.IsSelected || _managed is not { } session)
        {
            return;
        }

        if (session.SelectThread(value.Id) is { } problem)
        {
            Add(problem);
            return;
        }

        // Now rather than when the session's note arrives, so the pane answers the click it was given.
        SyncManaged();
    }

    private void OnReported(object? sender, DebugEvent e)
    {
        // From the debug loop's own thread, so nothing here touches a bound collection directly.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            Add(e.Text);

            switch (e.Kind)
            {
                case "started":
                case "module":
                    RefreshModules();
                    RefreshThreads();
                    break;

                case "stopped":
                    State = DebugState.Stopped;
                    ExecutionAddress = e.Address;
                    Status = e.Address is { } at ? $"Stopped at 0x{at:X}." : "Stopped.";
                    RefreshModules();
                    RefreshThreads();
                    RefreshRegisters();
                    if (e.Address is { } address)
                    {
                        StoppedAt?.Invoke(this, address);
                    }

                    break;

                case "exited":
                    State = DebugState.Exited;
                    ExecutionAddress = null;

                    // The event's own words, which carry the exit code. "It exited." threw away the
                    // one fact worth having when a process dies — whether it finished or crashed,
                    // and how.
                    Status = char.ToUpperInvariant(e.Text[0]) + e.Text[1..] + ".";

                    // All of it, not just the registers. A stack and a module list belonging to a
                    // process that no longer exists are worse than none: they read as current, and
                    // anything that goes on to use them is reasoning about a dead process.
                    Registers.Clear();
                    Stack.Clear();
                    Modules.Clear();
                    Threads.Clear();
                    Flags = string.Empty;
                    break;
            }

            NotifyCommands();
        });
    }

    /// <summary>
    /// The loaded modules, target first.
    ///
    /// Worth showing because under a host it answers the question the whole arrangement turns on:
    /// has the DLL been loaded yet. Until it has, its addresses mean nothing and its breakpoints are
    /// waiting rather than armed, and there is otherwise no way to tell that from nothing happening.
    /// </summary>
    private void RefreshThreads()
    {
        uint current = _session?.CurrentThreadId ?? 0;
        uint chosen = _session?.SelectedThreadId ?? 0;

        Threads.Clear();
        foreach (var thread in _session?.Threads ?? [])
        {
            var row = new ThreadRow(thread.Id, Hex(thread.StartAddress), thread.Id == current, _session?.IsWaitingInKernel(thread.Id) ?? false);
            Threads.Add(row);

            if (thread.Id == chosen)
            {
                _quiet = true;
                SelectedThread = row;
                _quiet = false;
            }
        }
    }

    /// <summary>
    /// Which thread is being looked at. Setting it changes what the registers show and what a step
    /// will step — the two have to be the same thread, or the registers are describing one thread
    /// while the buttons act on another.
    /// </summary>
    [ObservableProperty]
    private ThreadRow? _selectedThread;

    /// <summary>True while the selection is being set from the debuggee rather than by a person.</summary>
    private bool _quiet;

    partial void OnSelectedThreadChanged(ThreadRow? value)
    {
        if (_quiet || value is null || _session is null)
        {
            return;
        }

        _session.SelectedThreadId = value.Id;
        RefreshRegisters();
    }

    /// <summary>
    /// Picks a thread by id, as clicking its row would. Returns false when no such thread exists.
    ///
    /// Through the row rather than straight to the session, so the panel moves with it: the agent
    /// and the analyst share one debugger, and a selection only one of them could see is how the
    /// registers came to describe a thread nobody was looking at.
    /// </summary>
    public bool SelectThread(uint threadId)
    {
        if (Threads.FirstOrDefault(t => t.Id == threadId) is not { } row)
        {
            return false;
        }

        SelectedThread = row;
        return true;
    }

    private void RefreshModules()
    {
        var loaded = _session?.Modules ?? [];
        ulong target = _session?.LoadedBase ?? 0;

        Modules.Clear();
        foreach (var module in loaded.OrderByDescending(m => m.Base == target && target != 0).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            Modules.Add(new ModuleRow(
                module.Name.Length > 0 ? module.Name : "(unnamed)",
                $"0x{module.Base:X16}",
                module.Path,
                module.Base == target && target != 0));
        }
    }

    private void RefreshRegisters()
    {
        // The thread being looked at, which is the stopped one until somebody picks another. Every
        // thread is suspended at a stop, so any of them can be read.
        var current = (_session is { } session ? session.RegistersOf(session.SelectedThreadId) : null) ?? [];

        Registers.Clear();
        foreach (var (name, value) in current)
        {
            if (name is "rflags" or "eflags")
            {
                Flags = DescribeFlags((uint)value);
                continue;
            }

            bool changed = _previous.TryGetValue(name, out ulong was) && was != value;
            Registers.Add(new RegisterRow(name, Hex(value), changed));
        }

        _previous = current.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);

        Stack.Clear();
        foreach (var (address, value) in _session is { } stopped ? stopped.StackOf(stopped.SelectedThreadId) : [])
        {
            Stack.Add(new StackRow(Hex(address), Hex(value)));
        }
    }

    /// <summary>
    /// As wide as the machine is. Sixteen digits for a 32-bit value is eight leading zeroes on every
    /// row, which is the column the eye has to cross to reach the number that matters.
    /// </summary>
    private string Hex(ulong value) => _session?.Is32Bit == true ? $"0x{value:X8}" : $"0x{value:X16}";

    /// <summary>
    /// The flags that get looked at. A conditional jump is about ZF, SF, OF and CF, and reading them
    /// off a hex RFLAGS by hand is exactly the sort of arithmetic a debugger should have done.
    /// </summary>
    private static string DescribeFlags(uint eflags)
    {
        (string Name, int Bit)[] bits =
        [
            ("CF", 0), ("PF", 2), ("AF", 4), ("ZF", 6), ("SF", 7), ("TF", 8), ("IF", 9), ("DF", 10), ("OF", 11),
        ];

        return string.Join("  ", bits.Select(b => $"{b.Name} {(eflags >> b.Bit) & 1}"));
    }

    private void RefreshBreakpoints()
    {
        Breakpoints.Clear();
        foreach (ulong address in BreakpointAddresses.Order())
        {
            Breakpoints.Add($"0x{address:X}");
        }
    }

    // ------------------------------------------------------------------
    // Patches in the running process
    // ------------------------------------------------------------------

    /// <summary>Patches tried live that the project does not yet hold. See <see cref="LivePatchRow"/>.</summary>
    public ObservableCollection<LivePatchRow> LivePatches { get; } = new();

    public bool HasLivePatches => LivePatches.Count > 0;

    private void ApplySavedPatches(OpenedBinary binary, DebugSession session)
    {
        foreach (var patch in binary.Patches.Snapshot().Where(p => p.Enabled))
        {
            if (Unsafe(binary, patch.Rva, patch.Bytes.Count) is { } why)
            {
                Add($"patch at 0x{binary.Image.RvaToVa(patch.Rva):X} not applied live: {why}");
                continue;
            }

            session.SetPatch(new LivePatch(patch.Rva, patch.Bytes, patch.Original));
        }
    }

    /// <summary>
    /// Follows the project's patches into the running process while it is stopped. A save-patched-copy
    /// is a separate thing; this is the same switch, honoured live.
    /// </summary>
    private void OnSavedPatchChanged(object? sender, PatchChange change)
    {
        // The assistant records patches from its own thread, so this can arrive off the UI thread;
        // it touches the log and the registers, which are bound. A human editing the Patches tab
        // fires it on the UI thread already, where the invoke runs inline.
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnSavedPatchChanged(sender, change));
            return;
        }

        if (_session is not { } session || _workspace.Current is not { } binary)
        {
            return;
        }

        bool on = change.After is { Enabled: true };
        string? refused;
        if (on)
        {
            if (Unsafe(binary, change.Rva, change.After!.Bytes.Count) is { } why)
            {
                Add($"patch at 0x{binary.Image.RvaToVa(change.Rva):X} not applied live: {why}");
                return;
            }

            refused = session.SetPatch(new LivePatch(change.Rva, change.After!.Bytes, change.After!.Original));
        }
        else
        {
            refused = session.ClearPatch(change.Rva);
        }

        if (refused is not null)
        {
            Add($"patch at 0x{binary.Image.RvaToVa(change.Rva):X}: {refused}");
            return;
        }

        // Written down, so it has stopped being a hypothesis: the row goes, and the Patches tab has
        // it now. This is what Keep does by hand, and what happens when the assistant records over
        // something it was trying.
        if (on && LivePatches.FirstOrDefault(p => p.Rva == change.Rva) is { } tried)
        {
            LivePatches.Remove(tried);
            OnPropertyChanged(nameof(HasLivePatches));
        }

        RefreshRegisters();
    }

    /// <summary>
    /// Tries an instruction in the running process without recording it — a hypothesis. Returns null
    /// on success, or why it could not be tried. It shows up as a live patch, to Keep or Undo.
    /// </summary>
    public string? TestPatch(ulong va, string instruction, string? comment = null)
    {
        if (_session is not { } session)
        {
            return "nothing is running to try it in";
        }

        if (_workspace.Current is not { Analysis: { } analysis } binary)
        {
            return "open a function first";
        }

        var proposal = InstructionPatches.Assemble(analysis, va, instruction);
        if (!proposal.Ok)
        {
            return proposal.Problem;
        }

        var patch = proposal.Patch!;
        if (Unsafe(binary, patch.Rva, patch.Bytes.Count) is { } why)
        {
            return why;
        }

        if (session.SetPatch(new LivePatch(patch.Rva, patch.Bytes, patch.Original)) is { } refused)
        {
            return refused;
        }

        // Replaces any earlier hypothesis at the same place, the way the store would.
        for (int i = LivePatches.Count - 1; i >= 0; i--)
        {
            if (LivePatches[i].Rva == patch.Rva)
            {
                LivePatches.RemoveAt(i);
            }
        }

        LivePatches.Add(new LivePatchRow(patch.Rva, va, patch.OriginalHex, patch.Hex, comment ?? patch.Comment));
        OnPropertyChanged(nameof(HasLivePatches));
        Add($"trying 0x{va:X} live: {patch.OriginalHex} → {patch.Hex}");
        RefreshRegisters();
        return null;
    }

    /// <summary>Writes a hypothesis into the project, where it becomes an ordinary patch.</summary>
    [RelayCommand]
    private void KeepPatch(LivePatchRow? row)
    {
        if (row is null || _workspace.Current is not { } binary)
        {
            return;
        }

        var original = binary.Image.ReadAtRva(row.Rva, row.Was.Length / 2).ToArray();
        binary.Patches.Set(row.Rva, new Patch
        {
            Rva = row.Rva,
            Bytes = Convert.FromHexString(row.Now),
            Original = original.Length == row.Now.Length / 2 ? original : Convert.FromHexString(row.Was),
            Comment = row.Comment,
            Source = AnnotationSource.User,
            Modified = DateTimeOffset.Now,
        });

        LivePatches.Remove(row);
        OnPropertyChanged(nameof(HasLivePatches));
        Add($"kept the patch at 0x{row.Va:X}; it is in the project now");
    }

    /// <summary>
    /// Takes back whichever hypothesis covers an address, for callers working from an address rather
    /// than a row — the assistant, which sees the same list through its own tools.
    /// </summary>
    public bool UndoPatchAt(uint rva)
    {
        var row = LivePatches.FirstOrDefault(p => rva >= p.Rva && rva < p.Rva + (uint)(p.Was.Length / 2));
        if (row is null)
        {
            return false;
        }

        UndoPatch(row);
        return !LivePatches.Contains(row);
    }

    /// <summary>Takes a hypothesis back out of the running process.</summary>
    [RelayCommand]
    private void UndoPatch(LivePatchRow? row)
    {
        if (row is null || _session is not { } session)
        {
            return;
        }

        if (session.ClearPatch(row.Rva) is { } refused)
        {
            Add($"could not undo 0x{row.Va:X}: {refused}");
            return;
        }

        LivePatches.Remove(row);
        OnPropertyChanged(nameof(HasLivePatches));
        Add($"undid the live patch at 0x{row.Va:X}");
        RefreshRegisters();
    }

    /// <summary>Why a patch cannot go into the running image, or null when it can.</summary>
    private static string? Unsafe(OpenedBinary binary, uint rva, int length)
        => binary.Image.RelocationWithin(rva, length) is { } at
            ? $"it covers an address the loader relocates (0x{binary.Image.RvaToVa(at):X}), so the file's "
              + "bytes are wrong for the running image; save a patched copy instead"
            : null;

    private void StopSession()
    {
        ExecutionAddress = null;
        if (_workspace.Current is { } open)
        {
            open.Patches.Changed -= OnSavedPatchChanged;
        }

        if (_session is { } session)
        {
            session.Reported -= OnReported;
            session.Dispose();
            _session = null;
        }

        if (_managed is { } managed)
        {
            managed.Reported -= OnManagedReported;
            managed.Dispose();
            _managed = null;
        }

        Variables.Clear();
        ManagedThreads.Clear();
        _shownStops = -1;

        LivePatches.Clear();
        OnPropertyChanged(nameof(HasLivePatches));

        State = DebugState.NotStarted;
        Registers.Clear();
        Stack.Clear();
        Modules.Clear();
        Threads.Clear();
        Flags = string.Empty;
        NotifyCommands();
    }

    private void Add(string message) => Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");

    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopDebuggingCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StepInstructionCommand.NotifyCanExecuteChanged();
        StepOverCommand.NotifyCanExecuteChanged();
        StepOutCommand.NotifyCanExecuteChanged();
        StepIlCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPauseNow));
    }

    /// <summary>
    /// Runs to the end of the current method and stops in whatever called it.
    ///
    /// Managed only, and not an oversight on the native side: stepping out of a native function
    /// means knowing where its return address is, which is a question about an unwinding convention
    /// rather than about the program. The runtime already knows.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStepOut))]
    private void StepOut()
    {
        if (_managed is not { } session || !IsStopped)
        {
            return;
        }

        Resuming();
        if (session.StepOut() is { } problem)
        {
            Add(problem);
            return;
        }

        State = DebugState.Running;
        NotifyCommands();
    }

    private bool CanStepOut() => IsStopped && IsManaged;

    /// <summary>
    /// A breakpoint clicked in the IL gutter, turned into the method and offset the runtime wants.
    ///
    /// The address is the one the listing printed, which is where that IL byte sits in the file. The
    /// body index is the only thing that can get from there to a method token and an offset into it,
    /// and it is the same index the listing's addresses were made from — so a line always maps back
    /// to the instruction it was drawn for.
    ///
    /// The address is kept in <see cref="BreakpointAddresses"/> regardless, because that is what
    /// draws the dot: the marker is about the line, and the line is addressed either way.
    /// </summary>
    private void ToggleManagedBreakpoint(ulong staticVa)
    {
        if (_workspace.Current is not { } binary || binary.Bodies is not { } bodies)
        {
            return;
        }

        if (binary.Image.VaToRva(staticVa) is not { } rva || bodies.At(rva) is not { } body)
        {
            Add($"0x{staticVa:X} is not inside any method's IL");
            return;
        }

        uint token = (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(body.Method);
        uint offset = (uint)body.OffsetOf(rva);
        string module = binary.Image.FileName;

        if (BreakpointAddresses.Remove(staticVa))
        {
            _pendingManaged.Remove(staticVa);
            string? refused = _managed?.ClearBreakpoint(module, token, offset);
            if (refused is not null)
            {
                // Put back, because the runtime still has it. The dot is a statement about the
                // process, and one that disappeared while the breakpoint went on firing would be
                // the most confusing thing this panel could do.
                BreakpointAddresses.Add(staticVa);
                _pendingManaged[staticVa] = (module, token, offset);
            }

            Add(refused ?? $"cleared the breakpoint at IL_{offset:X4}");
        }
        else
        {
            BreakpointAddresses.Add(staticVa);

            // Recorded whether or not anything is running. A breakpoint set before the run is the
            // useful kind, and the session plants everything it has been given when its module loads.
            _pendingManaged[staticVa] = (module, token, offset);
            string? problem = _managed?.SetBreakpoint(module, token, offset);
            Add(problem ?? $"breakpoint in {module} at method 0x{token:X8}+IL_{offset:X4}");
        }

        BreakpointsVersion++;
        RefreshBreakpoints();
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Managed breakpoints set before anything was running, so a run can start with them.
    ///
    /// Kept here rather than in the session because the session does not outlive a run and these
    /// do: a breakpoint put on a line is about the program, not about this particular execution of it.
    /// </summary>
    private readonly Dictionary<ulong, (string Module, uint Token, uint Offset)> _pendingManaged = new();

    /// <summary>The CLR debugging session, for anything that needs to read it rather than drive it.</summary>
    internal ManagedDebugSession? ManagedSession => _managed;

    /// <summary>
    /// One IL instruction, for the assistant. Returns the reason rather than only logging it, because
    /// an agent that is told nothing goes round again.
    /// </summary>
    internal string? StepManaged(bool into)
    {
        if (_managed is not { } session)
        {
            return "this is not a .NET process";
        }

        if (!IsStopped)
        {
            return "it is not stopped, so there is nothing to step";
        }

        Resuming();
        if (StepStatement(into) is { } problem)
        {
            Add(problem);
            return problem;
        }

        State = DebugState.Running;
        NotifyCommands();
        return null;
    }

    internal string? StepOutManaged()
    {
        if (_managed is not { } session)
        {
            return "this is not a .NET process";
        }

        if (!IsStopped)
        {
            return "it is not stopped, so there is nothing to step out of";
        }

        Resuming();
        if (session.StepOut() is { } problem)
        {
            Add(problem);
            return problem;
        }

        State = DebugState.Running;
        NotifyCommands();
        return null;
    }

    /// <summary>
    /// Sets or clears a breakpoint in a managed method, so the panel and the agent share one set.
    ///
    /// Sharing means the marker moves either way: a breakpoint the assistant sets appears in the
    /// gutter, and one it clears leaves it. Two sets that were each correct on their own and did not
    /// match each other would be worse than either.
    /// </summary>
    internal string? SetManagedBreakpoint(string module, uint methodToken, uint ilOffset, bool on)
    {
        if (_managed is not { } session)
        {
            return "nothing is running under the .NET debugger; start it first";
        }

        string? problem = on
            ? session.SetBreakpoint(module, methodToken, ilOffset)
            : session.ClearBreakpoint(module, methodToken, ilOffset);

        if (problem is null)
        {
            // The marker follows what the agent did, so the gutter and the assistant are looking at
            // one set of breakpoints rather than two that quietly disagree.
            Mark(module, methodToken, ilOffset, on);
        }

        SyncManaged();
        return problem;
    }

    /// <summary>
    /// One IL instruction, for reading the IL view.
    ///
    /// Kept as a command of its own because the ordinary steps became statement steps, and a
    /// statement is several instructions. Somebody reading the IL listing wants to watch the stack
    /// being built up, which is exactly what a statement step hides.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStepIl))]
    private void StepIl()
    {
        if (_managed is not { } session || !IsStopped)
        {
            return;
        }

        Resuming();
        if (session.Step(into: true) is { } problem)
        {
            Add(problem);
            return;
        }

        State = DebugState.Running;
        NotifyCommands();
    }

    private bool CanStepIl() => IsStopped && IsManaged;

    /// <summary>
    /// Steps one C# statement, into a call or over it.
    ///
    /// A statement rather than an IL instruction, because a statement is what the reader is looking
    /// at. <c>builder.Logging.ClearProviders();</c> is five instructions — a receiver pushed, a
    /// property fetched, a call, a result discarded — and stepping through them one at a time shows
    /// five stops on the same line, which reads as a debugger that is barely moving.
    ///
    /// Where the statement ends is worked out here rather than in the session, which has the IL and
    /// no way to read a signature. When it cannot be worked out — a frame in another assembly, a
    /// method whose signatures will not read — the session falls back to one instruction on its own,
    /// which moves a little rather than not at all.
    /// </summary>
    private string? StepStatement(bool into)
    {
        if (_managed is not { } session)
        {
            return "this is not a .NET process";
        }

        return Statement(session.StoppedAt) is { } range
            ? session.Step(into, range.From, range.To)
            : session.Step(into);
    }

    /// <summary>The IL range of the statement it is stopped in, when the stop is in the open file.</summary>
    private (uint From, uint To)? Statement(ManagedLocation? at)
    {
        if (at is null || _workspace.Current is not { } binary
            || binary.Bodies is not { } bodies || binary.Managed is not { } managed)
        {
            return null;
        }

        // Only for the binary on screen. A stop in a framework assembly is a real stop and the
        // debugger keeps working there — it simply has no IL of that method to measure.
        if (!string.Equals(at.Module, binary.Image.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var handle = System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle((int)(at.MethodToken & 0x00FFFFFF));
        if (bodies.Of(handle) is null)
        {
            return null;
        }

        foreach (var statement in Statements(managed, at.MethodToken, handle))
        {
            if (statement.Covers(at.MethodToken, (int)at.Offset))
            {
                return ((uint)statement.From, (uint)statement.To);
            }
        }

        return null;
    }

    /// <summary>
    /// The statements of one method, decompiled once and kept.
    ///
    /// Decompiling on every stop would be paid for on every step, and a step is something people
    /// press repeatedly. The same method comes up again and again — stepping through it is the whole
    /// activity — so the second press onwards costs a dictionary lookup.
    /// </summary>
    private IReadOnlyList<SourceStatement> Statements(ManagedAssembly managed, uint token, MethodDefinitionHandle handle)
    {
        if (_statements.TryGetValue(token, out var already))
        {
            return already;
        }

        IReadOnlyList<SourceStatement> found;
        try
        {
            found = managed.Decompiler.StatementsFor(handle);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A method the decompiler will not take is not a reason to stop stepping: without
            // ranges the session steps one instruction, which still moves.
            Add($"could not work out the statements of method 0x{token:X8}: {ex.Message}");
            found = System.Array.Empty<SourceStatement>();
        }

        _statements[token] = found;
        return found;
    }

    private readonly Dictionary<uint, IReadOnlyList<SourceStatement>> _statements = new();

    /// <summary>Moves the dot in the listing to match a breakpoint set or cleared from elsewhere.</summary>
    private void Mark(string module, uint methodToken, uint ilOffset, bool on)
    {
        if (AddressOf(module, methodToken, ilOffset) is not { } staticVa)
        {
            return;   // a method in some other assembly, which this listing cannot show
        }

        if (on)
        {
            BreakpointAddresses.Add(staticVa);
            _pendingManaged[staticVa] = (module, methodToken, ilOffset);
        }
        else
        {
            BreakpointAddresses.Remove(staticVa);
            _pendingManaged.Remove(staticVa);
        }

        BreakpointsVersion++;
        RefreshBreakpoints();
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool DefaultConfirm(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void Dispose() => StopSession();
}
