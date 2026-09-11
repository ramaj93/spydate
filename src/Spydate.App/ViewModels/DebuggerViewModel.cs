using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
using Spydate.Core.Project;
using Spydate.Debugger;
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
        OnPropertyChanged(nameof(NeedsHost));
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

    /// <summary>Raised when a breakpoint is set or cleared, so listings can redraw their markers.</summary>
    public event EventHandler? BreakpointsChanged;

    /// <summary>Raised when execution stops somewhere, so the window can show where.</summary>
    public event EventHandler<ulong>? StoppedAt;

    // ------------------------------------------------------------------

    private bool CanStart() => _workspace.Current is not null && !IsDebugging;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (_workspace.Current is not { } binary || binary.Image.Path is not { Length: > 0 } path)
        {
            return;
        }

        // Guarded here as well as by CanExecute, which the button honours and a direct Execute does
        // not — and the assistant calls it directly. A second start does not replace the first: it
        // leaves the running process orphaned and begins another, so the analyst who agreed to run
        // this binary once has two of it, and the panel is showing only one.
        if (IsDebugging)
        {
            Status = "It is already running.";
            Add("already running — stop it before starting it again");
            return;
        }

        // Refused rather than attempted. The register context here is CONTEXT_AMD64; a 32-bit process
        // runs under WOW64 and needs Wow64GetThreadContext, and asking for the 64-bit one gives back
        // a structure that is read as registers and is not. Wrong register values in a debugger are
        // worse than no debugger — they are believed.
        if (!binary.Image.Is64Bit)
        {
            Status = "Only 64-bit binaries can be debugged so far.";
            Add($"{binary.DisplayName} is 32-bit. Reading a WOW64 process's registers needs a different "
                + "call than this uses, and using the wrong one reports values that look real. Not started.");
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
        _session?.Continue();
        State = DebugState.Running;
        Status = "Running.";
        NotifyCommands();
    }

    /// <summary>
    /// Stops it where it is, without ending it. Guarded like the others, because the assistant calls
    /// the command directly and a pause asked of a stopped process would be answered by nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
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
            _session?.RemoveBreakpoint(address);
        }

        BreakpointAddresses.Clear();
        BreakpointsVersion++;
        RefreshBreakpoints();
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------

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
            var row = new ThreadRow(thread.Id, $"0x{thread.StartAddress:X16}", thread.Id == current, _session?.IsWaitingInKernel(thread.Id) ?? false);
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
            if (name == "rflags")
            {
                Flags = DescribeFlags((uint)value);
                continue;
            }

            bool changed = _previous.TryGetValue(name, out ulong was) && was != value;
            Registers.Add(new RegisterRow(name, $"0x{value:X16}", changed));
        }

        _previous = current.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);

        Stack.Clear();
        foreach (var (address, value) in _session is { } stopped ? stopped.StackOf(stopped.SelectedThreadId) : [])
        {
            Stack.Add(new StackRow($"0x{address:X16}", $"0x{value:X16}"));
        }
    }

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
    }

    private static bool DefaultConfirm(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void Dispose() => StopSession();
}
