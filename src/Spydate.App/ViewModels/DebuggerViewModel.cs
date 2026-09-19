using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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
/// One breakpoint as the Breakpoints pane lists it: where it is, what names it, and whether it is
/// planted. <see cref="Enabled"/> is two-way — unticking it keeps the breakpoint in the list and the
/// gutter but takes it out of the running process, the way dnSpy disables one without forgetting it.
/// Managed and native breakpoints both appear here; <see cref="Kind"/> says which.
/// </summary>
public sealed partial class BreakpointRow : ObservableObject
{
    private readonly Action<ulong, bool>? _onEnabledChanged;
    private readonly bool _wired;

    public BreakpointRow(ulong address, string location, string kind, bool enabled, Action<ulong, bool>? onEnabledChanged)
    {
        Address = address;
        Location = location;
        Kind = kind;
        _enabled = enabled;                 // set the backing field directly, so building the row does not fire the toggle
        _onEnabledChanged = onEnabledChanged;
        _wired = true;
    }

    public ulong Address { get; }

    public string Where => $"0x{Address:X}";

    public string Location { get; }

    public string Kind { get; }

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        // Only a person ticking the box calls out; the constructor set the field, not the property.
        if (_wired)
        {
            _onEnabledChanged?.Invoke(Address, value);
        }
    }
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
    private readonly OpenedBinary _binary;
    private ManagedDebugSession? _managed;
    private DebugSession? _session;

    /// <summary>Registers as they were at the previous stop, so a change can be pointed at.</summary>
    private IReadOnlyDictionary<string, ulong> _previous = new Dictionary<string, ulong>(StringComparer.Ordinal);

    private readonly IFileDialogService? _dialogs;

    public DebuggerViewModel(OpenedBinary binary, IFileDialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(binary);

        _binary = binary;
        _dialogs = dialogs;

        // Once, here, rather than every time the window changed which file it was showing. A
        // debugger belongs to one file for its whole life now, so there is no moment at which the
        // binary underneath it changes — which is what used to force a session to be stopped on a
        // switch, and is why switching tabs no longer kills what is running.
        RecallTarget();
    }

    /// <summary>Reads back the host and arguments last used for the binary now open.</summary>
    private void RecallTarget()
    {
        var target = _binary.Image.Path is { Length: > 0 } path ? DebugTargets.For(path) : null;

        Host = target?.Host ?? string.Empty;
        Arguments = target?.Arguments ?? string.Empty;
        WorkingDirectory = target?.WorkingDirectory ?? string.Empty;
        CurrentModule = null;
        _arrivedAt = null;
        Modules.Clear();
        Threads.Clear();
        Variables.Clear();
        ManagedThreads.Clear();
        CallStack.Clear();
        _expanded.Clear();
        _statements.Clear();   // a different binary has different methods under the same tokens
        _localNames.Clear();
        RestoreBreakpoints();
        OnPropertyChanged(nameof(NeedsHost));

        // Read back rather than reset. These are remembered per binary exactly as the host and the
        // arguments are, so reopening something picks up the way it was last run — which is the whole
        // point of remembering it.
        //
        // A native file has no CLR to drive, so its engine is Native and there is nothing to choose:
        // the chooser shows it and is off. Only a managed file is a real choice, defaulting to the CLR
        // unless Native was remembered for it. Taken from the file rather than defaulted to the CLR for
        // everything, so a native exe does not come up reading ".NET CLR", which it can never be.
        DebugNatively = !IsManaged || target?.Engine == DebugEngine.Native;
        BreakAt = target?.BreakAt ?? DebugBreakAt.CreateProcess;

        // Which debugger applies follows from the file, so the panel rearranges itself when a
        // different one is opened rather than when something is run.
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(UsesManagedDebugger));
        OnPropertyChanged(nameof(CanChooseDebugger));
        OnPropertyChanged(nameof(ShowsRegisters));
        OnPropertyChanged(nameof(ShowsManagedContext));
        OnPropertyChanged(nameof(IsMixedMode));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanPauseNow));
        OnPropertyChanged(nameof(CanEditHost));
    }

    /// <summary>
    /// Remembers how to run this binary. Written on every change rather than on start, because the
    /// setting most worth keeping is the one somebody typed and then closed the window without using.
    /// </summary>
    private void RememberTarget()
    {
        if (_binary.Image.Path is not { Length: > 0 } path)
        {
            return;
        }

        // Null for whatever the default is, never the default itself. Writing it would give every
        // binary anybody opens an entry in the remembered targets, because IsEmpty would stop being
        // true the moment the panel read its own defaults back.
        DebugTargets.Set(path, new DebugTarget
        {
            Host = Host.Trim() is { Length: > 0 } host ? host : null,
            Arguments = Arguments.Trim() is { Length: > 0 } arguments ? arguments : null,
            WorkingDirectory = WorkingDirectory.Trim() is { Length: > 0 } directory ? directory : null,
            // Only a managed file's Native choice is worth keeping. A native file is Native with no
            // choice in it, so recording that would give every native binary a stored target for a
            // default it cannot depart from — the very thing "null for the default" is meant to avoid.
            Engine = IsManaged && DebugNatively ? DebugEngine.Native : null,
            BreakAt = BreakAt == DebugBreakAt.CreateProcess ? null : BreakAt,
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
    /// Picks the folder the program runs in. A chooser rather than free text, so a path that does not
    /// exist — which a debuggee refuses to start in, with an error that names the process and not the
    /// folder — cannot be typed by mistake. Opens where one is already set, if it is still there.
    /// </summary>
    [RelayCommand]
    private void ChooseWorkingDirectory()
    {
        string? start = WorkingDirectory.Trim() is { Length: > 0 } current ? current : null;
        if (_dialogs?.OpenFolder(start) is { Length: > 0 } chosen)
        {
            WorkingDirectory = chosen;
        }
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

    /// <summary>The call stack of the thread being looked at, innermost first.</summary>
    public ObservableCollection<ManagedFrameRow> CallStack { get; } = new();

    /// <summary>
    /// The frame picked in the Call Stack pane. Picking one moves the locals and the arrow to it,
    /// so a caller's variables can be read, not only the method that stopped.
    /// </summary>
    [ObservableProperty]
    private ManagedFrameRow? _selectedCallFrame;

    private bool _quietCallFrame;

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
    public bool NeedsHost => _binary.Image.IsDll == true && Host.Trim().Length == 0;

    /// <summary>
    /// Whether the open binary is an IL-only assembly.
    ///
    /// A fact about the file, and only that. It used to be the whole decision as well — IL-only meant
    /// the CLR's debugger and nothing else — which was right while there was nothing a native debugger
    /// could usefully do with a .NET process. There is now: a .NET program's own native DLLs are
    /// reachable by name, and stopping in one of those means the native loop has to own the process.
    /// So what the file is and what drives it are two questions, and this answers the first.
    /// </summary>
    public bool IsManaged => _binary.Image.ClrHeader?.IsILOnly == true;

    /// <summary>
    /// Drive this .NET program with the native loop instead of the CLR's interface.
    ///
    /// Only meaningful for an IL-only assembly, and only before it starts: which debugger owns the
    /// process is settled when the session is created, and the two cannot both attach. It is what
    /// makes a breakpoint or a patch in a native DLL the program loads possible at all — the cost
    /// being that there is then no managed frame, no locals and no IL-level stepping, because nothing
    /// is talking to the runtime.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesManagedDebugger))]
    [NotifyPropertyChangedFor(nameof(ShowsRegisters))]
    [NotifyPropertyChangedFor(nameof(ShowsManagedContext))]
    [NotifyPropertyChangedFor(nameof(IsMixedMode))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanPauseNow))]
    private bool _debugNatively;

    /// <summary>
    /// Remembered like the host and the arguments are, because it is the same kind of thing: how to
    /// run this binary, decided once and wanted again next time. Resetting it on every open — which
    /// is what this did when the choice lived on a toolbar checkbox — meant choosing it again for
    /// every session.
    /// </summary>
    partial void OnDebugNativelyChanged(bool value)
    {
        RememberTarget();
        NotifyCommands();
    }

    /// <summary>
    /// Where a run stops of its own accord. <see cref="DebugBreakAt.CreateProcess"/> is the default
    /// because it is what both engines already did: the native loop stops at the loader break, and
    /// the managed one was started with holdAtStart set.
    /// </summary>
    [ObservableProperty]
    private DebugBreakAt _breakAt = DebugBreakAt.CreateProcess;

    partial void OnBreakAtChanged(DebugBreakAt value) => RememberTarget();

    /// <summary>Whether the run can still be configured: not while it is running, since it is settled by then.</summary>
    public bool CanEditRun => !IsDebugging;

    /// <summary>
    /// Whether the executable to launch can be chosen at all.
    ///
    /// Only for a file that is not a program of its own. A native EXE starts itself and <em>is</em> the
    /// executable, so the box is fixed and off. Everything else is a module something else has to load,
    /// and that something is what the box names: a native DLL or driver, and — the case a plain
    /// <see cref="PeImage.IsDll"/> misses — a .NET assembly, whose PE is marked an executable (its
    /// launcher is a separate native apphost) yet which cannot be run on its own. So the test is "is it
    /// a native EXE", written as its negation: managed, or DLL-marked. Off during a run too, like the
    /// rest of the run configuration.
    /// </summary>
    public bool CanEditHost => CanEditRun && (IsManaged || _binary.Image.IsDll == true);

    /// <summary>One row of a chooser: what it means, and what to call it on screen.</summary>
    public sealed record EngineChoice(bool Native, string Label);

    public sealed record BreakAtChoice(DebugBreakAt Value, string Label);

    /// <summary>
    /// The engines, for the run configuration. Only a real choice for an IL-only assembly — a file
    /// with native code of its own has nothing to decide — which is what <see cref="CanChooseDebugger"/>
    /// is for.
    /// </summary>
    public IReadOnlyList<EngineChoice> EngineChoices { get; } =
    [
        new EngineChoice(false, ".NET CLR"),
        new EngineChoice(true, "Native"),
    ];

    /// <summary>
    /// Where to stop, named as dnSpy names them. All four are wired now: the native loop runs on to
    /// an entry point past the loader break, and the managed engine holds, plants the entry-point or
    /// module-cctor breakpoint, and continues to it.
    /// </summary>
    public IReadOnlyList<BreakAtChoice> BreakAtChoices { get; } =
    [
        new BreakAtChoice(DebugBreakAt.None, "Don't break"),
        new BreakAtChoice(DebugBreakAt.CreateProcess, "Create Process"),
        new BreakAtChoice(DebugBreakAt.EntryPoint, "Entry Point"),
        new BreakAtChoice(DebugBreakAt.ModuleCctorOrEntryPoint, "Module cctor or Entry Point"),
    ];

    /// <summary>
    /// Whether debugging this binary means driving the CLR rather than the process.
    ///
    /// The decision, as against <see cref="IsManaged"/>'s fact. Everything that used to ask whether
    /// the file was managed and meant "will the CLR be driving" asks this instead, so that one answer
    /// decides the engine, the panes and where a breakpoint goes.
    /// </summary>
    public bool UsesManagedDebugger => IsManaged && !DebugNatively;

    /// <summary>
    /// Whether the choice is still open. Only for a managed file, and only while nothing is running:
    /// afterwards the session exists and is one kind or the other.
    /// </summary>
    public bool CanChooseDebugger => IsManaged && !IsDebugging;

    /// <summary>Registers and stack words are worth showing only when there is native code.</summary>
    public bool ShowsRegisters => !UsesManagedDebugger;

    /// <summary>
    /// Whether to show a managed call stack beside the rest. True on the CLR's own engine, and also in
    /// mixed mode — a managed program driven by the native loop — where the DAC can walk the managed
    /// stack at a native stop. That is the display half of "both worlds at one stop": native registers
    /// and a managed call stack for the same pause.
    /// </summary>
    public bool ShowsManagedContext => UsesManagedDebugger || IsMixedMode;

    /// <summary>Pausing and running to a cursor are native-only, so far.</summary>
    public bool CanPause => !UsesManagedDebugger;

    /// <summary>The processor flags, spelled out. "ZF 1 CF 0" is read; 0x246 is decoded.</summary>
    [ObservableProperty]
    private string _flags = string.Empty;

    /// <summary>What the debuggee has done, newest last.</summary>
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Breakpoints by static address — the ones in the listing, set before anything runs.</summary>
    public HashSet<ulong> BreakpointAddresses { get; } = new();

    /// <summary>
    /// Breakpoints kept in the listing but taken out of the running process — disabled, not cleared.
    /// A disabled one still draws in the gutter (hollow) and stays in the Breakpoints pane, so it can
    /// be switched back on, but it is not planted and does not stop the program. Not persisted: a
    /// reopened binary brings its breakpoints back enabled, the way a fresh session starts them.
    /// </summary>
    private readonly HashSet<ulong> _disabledBreakpoints = new();

    /// <summary>The disabled breakpoints, for the gutter to draw them apart from the live ones.</summary>
    public IReadOnlySet<ulong> DisabledBreakpointAddresses => _disabledBreakpoints;

    /// <summary>Every breakpoint, for the Breakpoints pane — managed and native alike.</summary>
    public ObservableCollection<BreakpointRow> Breakpoints { get; } = new();

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
    [NotifyPropertyChangedFor(nameof(CanChooseDebugger))]
    [NotifyPropertyChangedFor(nameof(CanEditRun))]
    [NotifyPropertyChangedFor(nameof(CanEditHost))]
    private DebugState _state = DebugState.NotStarted;

    [ObservableProperty]
    private string _status = "Not running.";

    /// <summary>
    /// The module execution is in, plain — <c>ntdll.dll</c>. Null when nothing is stopped, or when
    /// the stop is somewhere no loaded module claims.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string? _currentModule;

    /// <summary>
    /// The Debug panel's header. It names the module when there is one, because stepping leaves the
    /// opened binary routinely — into kernelbase, into ntdll — and nothing on screen said so.
    ///
    /// The header and not the tab below it: the tab strip is a fixed row of short words you aim at,
    /// and one of them growing and shrinking at every step moves the ones beside it.
    /// </summary>
    public string Caption => CurrentModule is { Length: > 0 } module ? $"Debug ({module})" : "Debug";

    public bool IsDebugging => State is DebugState.Running or DebugState.Stopped;

    public bool IsStopped => State == DebugState.Stopped;

    public bool IsRunning => State == DebugState.Running;

    /// <summary>Pausing is native-only so far, so the button says so by being off.</summary>
    public bool CanPauseNow => IsRunning && CanPause;

    /// <summary>Raised when a breakpoint is set or cleared, so listings can redraw their markers.</summary>
    public event EventHandler? BreakpointsChanged;

    /// <summary>Raised when execution stops somewhere, so the window can show where.</summary>
    public event EventHandler<ulong>? StoppedAt;

    /// <summary>
    /// Raised when a breakpoint in the pane is double-clicked, so the window opens the code it is in.
    /// Unlike <see cref="StoppedAt"/> it moves no arrow: it is "show me where this breakpoint is", not
    /// "the program stopped here".
    /// </summary>
    public event EventHandler<ulong>? NavigateRequested;

    /// <summary>
    /// Where the last stop sent the window — the listing address when the stop is inside the opened
    /// image, the run-time address when it is in another module.
    ///
    /// Kept rather than worked out again on demand, because <see cref="ExecutionAddress"/> does not
    /// survive as an answer to "where are we": opening a foreign module rewrites it to that module's
    /// own preferred base so the arrow can be drawn there, and that number means nothing to anything
    /// but the document already showing it. Re-deriving from it opened an empty listing at an address
    /// no module claims.
    /// </summary>
    private ulong? _arrivedAt;

    /// <summary>
    /// Whether the next stop is one somebody asked for — a start, a step, a continue, a run to
    /// cursor — as opposed to a breakpoint coming round on its own.
    ///
    /// It decides whether a stop is allowed to pull the window to this file's tab. Nearly every
    /// stop is asked for and going there is exactly what the reader wants. The one that is not is
    /// a background process hitting a breakpoint while they are reading something else, and
    /// yanking them away from it would be the window deciding it knows better.
    /// </summary>
    private bool _asked;

    /// <summary>True when the stop being reported answers a command issued against this file.</summary>
    public bool StopWasAskedFor { get; private set; }

    /// <summary>Records that what happens next was asked for, so the stop it leads to can say so.</summary>
    private void Expect() => _asked = true;

    /// <summary>Sends the window to a stop, and remembers where, so it can be sent there again.</summary>
    private void Arrive(ulong va)
    {
        _arrivedAt = va;
        StopWasAskedFor = _asked;
        _asked = false;
        StoppedAt?.Invoke(this, va);
    }

    /// <summary>
    /// Opens the line the program is stopped at — what double-clicking <c>rip</c> in the registers
    /// does. The registers pane is where you end up after stepping around, and getting back to the
    /// listing meant finding the tab again; the register that holds the answer may as well be the
    /// way back. It repeats the stop's own navigation exactly, rather than working out a second
    /// answer that could differ from where the arrow actually is.
    /// </summary>
    [RelayCommand]
    private void GoToExecution()
    {
        if (_arrivedAt is { } va)
        {
            StoppedAt?.Invoke(this, va);
        }
    }

    /// <summary>Opens the code a breakpoint sits in — the pane's double-click and its context menu.</summary>
    [RelayCommand]
    private void GoToBreakpoint(BreakpointRow? row)
    {
        if (row is not null)
        {
            NavigateRequested?.Invoke(this, row.Address);
        }
    }

    /// <summary>Clears a breakpoint from the pane, the same as toggling it off in the listing.</summary>
    [RelayCommand]
    private void RemoveBreakpoint(BreakpointRow? row)
    {
        if (row is not null)
        {
            ToggleBreakpoint(row.Address);
        }
    }

    /// <summary>
    /// Enables or disables a breakpoint without forgetting it: a disabled one is taken out of the
    /// running process but kept in the list and the gutter, so it can be switched back on. Works
    /// before a run too — it just marks the set, which the start-up planting then honours.
    /// </summary>
    public void SetBreakpointEnabled(ulong staticVa, bool enabled)
    {
        if (!BreakpointAddresses.Contains(staticVa))
        {
            return;
        }

        bool changed = enabled ? _disabledBreakpoints.Remove(staticVa) : _disabledBreakpoints.Add(staticVa);
        if (!changed)
        {
            return;
        }

        if (enabled)
        {
            PlantBreakpoint(staticVa);
            Add($"enabled the breakpoint at 0x{staticVa:X}");
        }
        else
        {
            UnplantBreakpoint(staticVa);
            Add($"disabled the breakpoint at 0x{staticVa:X}");
        }

        // The row already carries the new state (the tick set it), so the list is not rebuilt here —
        // that would drop the row out from under the click. Only the gutter needs telling.
        BreakpointsVersion++;
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts a breakpoint into the running process, by whichever route its kind takes — a native int3, a
    /// managed breakpoint on the CLR engine, or a managed one planted over the native loop in mixed
    /// mode. A no-op when nothing is running; enabling before a run just leaves it for the start to plant.
    /// </summary>
    private void PlantBreakpoint(ulong staticVa)
    {
        if (UsesManagedDebugger)
        {
            if (_pendingManaged.TryGetValue(staticVa, out var m) && _managed?.SetBreakpoint(m.Module, m.Token, m.Offset) is { } refused)
            {
                Add(refused);
            }
        }
        else if (IsMixedMode && MixedTarget(staticVa) is { } target)
        {
            string? result = _session?.AddManagedBreakpoint(target.Type, (int)target.Token, (int)target.Offset);
            if (result is not null && !result.Contains("nothing is running", StringComparison.Ordinal))
            {
                _seededMixed.Add(staticVa);
            }

            EnsureMixedPump();
        }
        else
        {
            _session?.AddBreakpoint(staticVa);
        }
    }

    /// <summary>Takes a breakpoint back out of the running process, the reverse of <see cref="PlantBreakpoint"/>.</summary>
    private void UnplantBreakpoint(ulong staticVa)
    {
        if (UsesManagedDebugger)
        {
            if (_pendingManaged.TryGetValue(staticVa, out var m))
            {
                _managed?.ClearBreakpoint(m.Module, m.Token, m.Offset);
            }
        }
        else if (IsMixedMode && MixedTarget(staticVa) is { } target)
        {
            _seededMixed.Remove(staticVa);
            _session?.RemoveManagedBreakpoint(target.Type, (int)target.Token, (int)target.Offset);
        }
        else
        {
            _session?.RemoveBreakpoint(staticVa);
        }
    }

    // ------------------------------------------------------------------

    private bool CanStart() => _binary is not null && !IsDebugging && !_starting;

    /// <summary>
    /// Whether a start is in flight. Not the same as running: getting hold of a runtime takes a
    /// moment, and in that moment the state is still "not started", so nothing else says no.
    /// </summary>
    private bool _starting;

    /// <summary>
    /// Turns the panel's break-at choice into what the native loop should do with its loader break.
    ///
    /// It used to be a flag here that swallowed the first stop for "Don't break" and nothing more, so
    /// the two entry-point choices did nothing. The loop now owns all four: the loader break is where
    /// it decides whether to report, let go, or run on to an entry, which is the one place the timing
    /// is not a race.
    /// </summary>
    private static EntryStop NativeEntryStop(DebugBreakAt breakAt) => breakAt switch
    {
        DebugBreakAt.None => EntryStop.DontBreak,
        DebugBreakAt.EntryPoint => EntryStop.ProcessEntry,
        DebugBreakAt.ModuleCctorOrEntryPoint => EntryStop.ModuleEntry,
        _ => EntryStop.LoaderBreak,
    };

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
        Expect();
        if (_binary is not { } binary || binary.Image.Path is not { Length: > 0 } path)
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

        // Asked before anything starts, and already filled in with whatever was used for this binary
        // last time. Closing it is not discarding: every box writes itself through as it is edited,
        // the way it always has, so Close keeps the settings and only declines to run.
        var configure = new Views.RunConfigWindow(this) { Owner = Application.Current?.MainWindow };
        if (configure.ShowDialog() != true)
        {
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

        if (UsesManagedDebugger)
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

        var session = new DebugSession();
        session.Reported += OnReported;
        _session = session;

        // In mixed mode the gutter marks are managed — they cannot be planted as native int3s at their
        // IL's static address, which is not executed code. They are seeded as managed breakpoints once
        // the run is up, by the pump below. In pure native mode a mark is a native address and goes in
        // now, so it is standing before the loader break.
        if (!IsMixedMode)
        {
            foreach (ulong address in BreakpointAddresses)
            {
                if (!_disabledBreakpoints.Contains(address))
                {
                    session.AddBreakpoint(address);
                }
            }
        }

        // Every patch that is switched on goes into the run, so it behaves like the patched copy
        // would without one having to be saved. They are held now and written when the module lands.
        ApplySavedPatches(binary, session);
        binary.Patches.Changed += OnSavedPatchChanged;

        try
        {
            // Under a host, the process is somebody else's program and the addresses on screen belong
            // to a module inside it, so the module has to be named or nothing would translate. The
            // break-at choice and the module's own entry RVA go in with it, so the loader break is
            // taken, let go of, or run past to an entry — decided in the loop rather than out here,
            // where the timing was a race the loop does not have to run.
            session.Start(
                run,
                binary.Image.ImageBase,
                binary.Image.OptionalHeader.SizeOfImage,
                Arguments is { Length: > 0 } arguments ? arguments : null,
                WorkingDirectory is { Length: > 0 } directory ? directory : null,
                host is null ? null : binary.Image.FileName,
                NativeEntryStop(BreakAt),
                binary.Image.EntryPointRva);

            State = DebugState.Running;
            Status = "Running.";
            Add(host is null
                ? $"started {run}"
                : $"started {run}, waiting for {binary.Image.FileName} to load");

            // Mixed mode: start pumping so the managed marks are seeded the moment the CLR is up and
            // planted as their methods JIT, without a native breakpoint of the analyst's own to hang it on.
            if (IsMixedMode)
            {
                EnsureMixedPump();
            }
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
        var session = new ManagedDebugSession();
        session.Reported += OnManagedReported;
        _managed = session;

        // Every patch that is switched on goes into the run, the way it does for a native one — but
        // written into the module's IL as it loads, which for managed code is the only moment it can
        // take: after the JIT has turned that IL into the native code the process actually runs,
        // changing the IL changes nothing. A patch toggled on mid-run therefore waits for a relaunch.
        ApplyManagedPatches(binary, session);

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
            // Held unless the run was asked to go straight through. The runtime does this properly,
            // where the native loop has to be continued out of a stop it takes regardless.
            problem = await Task.Run(
                () => session.Start(run, arguments, directory, holdAtStart: BreakAt != DebugBreakAt.None));
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
        foreach (var (va, pending) in _pendingManaged)
        {
            if (_disabledBreakpoints.Contains(va))
            {
                continue;   // disabled before the run: kept in the list, but not planted
            }

            if (session.SetBreakpoint(pending.Module, pending.Token, pending.Offset) is { } refused)
            {
                Add(refused);
            }
        }

        // Break-at, the managed way: hold at the start (which is why holdAtStart followed the same
        // "anything but Don't break" rule), plant the entry-point or module-cctor breakpoint while
        // held, and continue to it — so the panel comes up stopped where the program first runs its
        // own code, not at the runtime's own initial hold. Both of those methods run once, so an
        // ordinary breakpoint there is a one-shot in all but name.
        if (BreakAt is DebugBreakAt.EntryPoint or DebugBreakAt.ModuleCctorOrEntryPoint
            && PlantManagedEntryBreak(binary, session))
        {
            session.Continue();
            State = DebugState.Running;
            Status = "Running to the entry point.";
            Add("continuing to the entry point");
            SyncManaged();
            NotifyCommands();
            return;
        }

        State = DebugState.Stopped;
        Status = "Held before it ran anything.";
        Add($"started {run} under the .NET debugger, held before it ran anything");
        SyncManaged();
        NotifyCommands();
    }

    /// <summary>
    /// Sets the breakpoint a managed break-at continues to: the module initializer when the choice is
    /// "Module cctor or Entry Point" and the assembly has one — it runs before the entry point —
    /// otherwise the entry point. Returns whether there is something to continue to; a false answer
    /// means hold at the start rather than run on to a stop that will never come.
    /// </summary>
    private bool PlantManagedEntryBreak(OpenedBinary binary, ManagedDebugSession session)
    {
        if (binary.Managed is not { } managed)
        {
            return false;
        }

        var target = BreakAt == DebugBreakAt.ModuleCctorOrEntryPoint
            ? managed.ModuleInitializer ?? managed.EntryPoint
            : managed.EntryPoint;

        if (target is null)
        {
            Add("there is no entry point to break at; holding at the start instead");
            return false;
        }

        string module = binary.Image.FileName;
        uint token = (uint)MetadataTokens.GetToken(target.Handle);
        if (session.SetBreakpoint(module, token, 0) is { } refused)
        {
            Add($"could not set the entry-point breakpoint: {refused}; holding at the start instead");
            return false;
        }

        Add($"break at {target.Name} in {module}; continuing to it");
        return true;
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
        Expect();
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
        Expect();
        if (!IsRunning || _session is not { } session)
        {
            return;
        }

        Add(session.Pause() ? "pausing" : "could not pause it");
    }

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void StepInstruction()
    {
        Expect();
        if (!IsStopped)
        {
            return;
        }

        // Mixed mode: the process is a managed one on the native loop, so a step is an IL step over that
        // loop — the DAC says which IL offset each native range is, and the loop single-steps until it
        // changes — not a bare native instruction. Into descends into a managed callee.
        if (StepMixed(IlStepKind.Into))
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
        Expect();
        if (!IsStopped)
        {
            return;
        }

        if (StepMixed(IlStepKind.Over))
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
    /// A managed step over the native loop, when in mixed mode. Returns true when it handled the step —
    /// so the ordinary native and CLR-engine paths run only when it did not. IL stepping is
    /// <see cref="DebugSession.StepManaged"/>: the DAC's IL-to-native map says which native range each IL
    /// offset occupies, and the loop single-steps until the offset changes, landing on a real IL
    /// boundary rather than mid-statement.
    /// </summary>
    private bool StepMixed(IlStepKind kind)
    {
        if (!IsMixedMode || _session is not { } session || !IsStopped)
        {
            return false;
        }

        Resuming();
        session.StepManaged(kind);
        State = DebugState.Running;
        NotifyCommands();
        return true;
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

    /// <summary>
    /// The loaded module a runtime address falls in — the one with the greatest base at or below it —
    /// as its file path and runtime base, or null. The module's size is not known here, so the caller
    /// confirms the address really lands inside it once it has the module's headers.
    /// </summary>
    public (string Path, ulong Base)? ModuleContaining(ulong runtimeVa)
    {
        if (_session is not { } session)
        {
            return null;
        }

        (string Path, ulong Base)? found = null;
        foreach (var module in session.Modules)
        {
            if (module.Base <= runtimeVa && (found is null || module.Base > found.Value.Base))
            {
                found = (module.Path, module.Base);
            }
        }

        return found;
    }

    /// <summary>The on-disk path of a loaded module by file name, or null when it is not loaded (or
    /// nothing is running). The exact file the running process mapped, which beats guessing at
    /// System32 for a module that could have come from anywhere.</summary>
    public string? ModulePath(string name)
    {
        if (_session is not { } session)
        {
            return null;
        }

        foreach (var module in session.Modules)
        {
            if (string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return module.Path;
            }
        }

        return null;
    }

    /// <summary>Runs until execution reaches an address, without keeping a breakpoint there.</summary>
    public void RunTo(ulong staticVa)
    {
        if (!IsStopped)
        {
            return;
        }

        Expect();
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
        if (UsesManagedDebugger)
        {
            ToggleManagedBreakpoint(staticVa);
            return;
        }

        // Mixed mode: a managed program driven by the native loop. The line is C#, so the mark is a
        // managed breakpoint — resolved to the address the JIT put that IL at and planted as a native
        // int3 there — not a native breakpoint at the IL's static address, which is not code that runs.
        if (IsMixedMode && MixedTarget(staticVa) is { } target)
        {
            ToggleMixedBreakpoint(staticVa, target);
            return;
        }

        if (BreakpointAddresses.Remove(staticVa))
        {
            _disabledBreakpoints.Remove(staticVa);
            _session?.RemoveBreakpoint(staticVa);
            RememberBreakpoint(staticVa, on: false);
            Add($"cleared the breakpoint at 0x{staticVa:X}");
        }
        else
        {
            BreakpointAddresses.Add(staticVa);
            _session?.AddBreakpoint(staticVa);
            RememberBreakpoint(staticVa, on: true);
            Add($"breakpoint at 0x{staticVa:X}");
        }

        BreakpointsVersion++;
        RefreshBreakpoints();
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A managed program driven by the native loop — the case where a native breakpoint and a managed
    /// one both make sense in the same run, and the whole point of mixed mode. True only for an IL-only
    /// assembly the user chose to debug natively; a native file, or one on the CLR's own engine, is not.
    /// </summary>
    public bool IsMixedMode => IsManaged && DebugNatively;

    /// <summary>
    /// The managed method and IL offset a listing address falls in, as the DAC path names them: the
    /// declaring type's full name, the method's metadata token, and the IL offset. Null when the address
    /// is not inside a method body, or the file's metadata cannot name the type — in which case the
    /// caller falls back to a native breakpoint at the address itself.
    /// </summary>
    private (string Type, uint Token, uint Offset)? MixedTarget(ulong staticVa)
    {
        if (_binary is not { } binary || binary.Bodies is not { } bodies)
        {
            return null;
        }

        if (binary.Image.VaToRva(staticVa) is not { } rva || bodies.At(rva) is not { } body)
        {
            return null;
        }

        uint token = (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(body.Method);
        if (binary.Managed?.Locate((int)token)?.Type.FullName is not { Length: > 0 } type)
        {
            return null;
        }

        return (type, token, (uint)body.OffsetOf(rva));
    }

    /// <summary>
    /// Sets or clears a managed breakpoint over the native loop. Marked in the gutter whether or not a
    /// run is going: set before the run it is seeded and planted when the CLR is up and the method has
    /// native code; set during a run it goes to the session at once. The session holds it until it can
    /// plant, so a method not JITted yet is not refused — it is planted on a later call.
    /// </summary>
    private void ToggleMixedBreakpoint(ulong staticVa, (string Type, uint Token, uint Offset) target)
    {
        if (BreakpointAddresses.Remove(staticVa))
        {
            _disabledBreakpoints.Remove(staticVa);
            _seededMixed.Remove(staticVa);
            string? refused = _session?.RemoveManagedBreakpoint(target.Type, (int)target.Token, (int)target.Offset);
            RememberBreakpoint(staticVa, on: false);
            Add(refused ?? $"cleared the managed breakpoint in {target.Type} at IL_{target.Offset:X4}");
        }
        else
        {
            BreakpointAddresses.Add(staticVa);
            RememberBreakpoint(staticVa, on: true);

            if (_session is { } session)
            {
                string? result = session.AddManagedBreakpoint(target.Type, (int)target.Token, (int)target.Offset);
                if (result is null || !result.Contains("nothing is running", StringComparison.Ordinal))
                {
                    _seededMixed.Add(staticVa);
                }

                Add(result ?? $"managed breakpoint in {target.Type} at IL_{target.Offset:X4} — a native int3 where the JIT put it");
                EnsureMixedPump();
            }
            else
            {
                Add($"managed breakpoint in {target.Type} at IL_{target.Offset:X4}; it plants over the native loop when the run starts");
            }
        }

        BreakpointsVersion++;
        RefreshBreakpoints();
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// At a mixed stop, walk the managed stack with the DAC and show it beside the native registers —
    /// and name the stop in managed terms. Only in mixed mode; the CLR's own engine fills these panes
    /// through its own path, and a native program has no managed stack to walk.
    /// </summary>
    /// <summary>
    /// Reads the managed side of a native stop onto the panel — the managed call stack, and the stop
    /// named in managed terms — and returns where the stop sits in the open file as a static address, or
    /// null when it is not in managed code of the opened assembly. That address is the point: the raw
    /// stop is a JITted runtime address with no place in the file image, so the loop reports it as no
    /// address at all; the managed location is what maps back to a static VA the listing and gutter
    /// share, and only off that can the C#/IL view mark the current line and open the method stopped in.
    /// </summary>
    private ulong? RefreshMixedContext()
    {
        if (!IsMixedMode)
        {
            return null;
        }

        CallStack.Clear();
        if (_session is not { } session || session.Managed is not { } overlay)
        {
            return null;
        }

        // The managed frames of the thread that stopped, innermost first. A stop reports a native
        // thread id; the overlay names its threads by OS id, so they line up.
        var thread = overlay.Threads().FirstOrDefault(t => t.OsId == session.CurrentThreadId)
                     ?? overlay.Threads().FirstOrDefault();

        int index = 0;
        bool current = true;
        foreach (var frame in thread?.Frames ?? [])
        {
            bool managed = frame.Method is { Length: > 0 };
            CallStack.Add(new ManagedFrameRow(index++, frame.Method ?? frame.Kind, managed, managed && current));
            if (managed)
            {
                current = false;   // the innermost managed frame is the one stopped in
            }
        }

        // Name the stop in managed terms beside its native address, so a native pause reads as a place
        // in the C#: "Stopped at 0x… — Namespace.Type.Method at IL_XXXX", and map it back to the static
        // address the listing is addressed by so the caller can mark the line and open the method.
        if (overlay.LocationOf(session.CurrentAddress) is { } location)
        {
            Status = $"{Status.TrimEnd('.')}  —  {location.Method} at IL_{location.IlOffset:X4}";
            return location.Module is { Length: > 0 } module
                ? AddressOf(module, (uint)location.MethodToken, (uint)location.IlOffset)
                : null;
        }

        return null;
    }

    /// <summary>Managed marks already handed to the current native session, so the pump does not re-add them.</summary>
    private readonly HashSet<ulong> _seededMixed = new();

    /// <summary>
    /// Polls the session while a mixed run is live: it hands any managed mark to the session once its
    /// overlay exists, and plants held ones as their methods JIT. Polling is inherent to the read-only
    /// DAC — a cold method announces nothing when it compiles, so the plant is caught on the next tick
    /// (see MIXED-MODE.md Phase 5). Off outside a mixed run.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _mixedPump;

    private void EnsureMixedPump()
    {
        if (_mixedPump is not null || Application.Current is null)
        {
            return;
        }

        _mixedPump = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mixedPump.Tick += (_, _) => PumpMixed();
        _mixedPump.Start();
        PumpMixed();
    }

    private void PumpMixed()
    {
        if (_session is not { } session)
        {
            StopMixedPump();
            return;
        }

        foreach (ulong va in BreakpointAddresses.ToList())
        {
            if (_seededMixed.Contains(va) || _disabledBreakpoints.Contains(va) || MixedTarget(va) is not { } target)
            {
                continue;
            }

            string? result = session.AddManagedBreakpoint(target.Type, (int)target.Token, (int)target.Offset);
            if (result is null || !result.Contains("nothing is running", StringComparison.Ordinal))
            {
                _seededMixed.Add(va);
            }
        }

        session.PlantPending();
    }

    private void StopMixedPump()
    {
        _mixedPump?.Stop();
        _mixedPump = null;
        _seededMixed.Clear();
    }

    /// <summary>
    /// Records a breakpoint in the project so it survives reopening — an RVA, whichever debugger it
    /// belongs to, since a managed one's method and offset are re-derived from it on the way back.
    /// Nothing is written to disk here; the store is dirtied and saved with the rest of the project.
    /// </summary>
    private void RememberBreakpoint(ulong staticVa, bool on)
    {
        if (_binary is not { } binary || binary.Image.VaToRva(staticVa) is not { } rva)
        {
            return;
        }

        if (on)
        {
            binary.Breakpoints.Add(rva);
        }
        else
        {
            binary.Breakpoints.Remove(rva);
        }
    }

    /// <summary>
    /// Puts the marks recorded for the binary now open back in the gutter. Called when a binary is
    /// opened, so the breakpoints last set are there again — planted for real when a run next starts,
    /// the same as one set by hand. A managed one's method and IL offset come back off the body map,
    /// exactly as they were worked out when it was set.
    /// </summary>
    private void RestoreBreakpoints()
    {
        BreakpointAddresses.Clear();
        _pendingManaged.Clear();
        _disabledBreakpoints.Clear();   // enabled/disabled is a session matter; a reopened binary starts them on

        if (_binary is { } binary)
        {
            bool managed = binary.Image.ClrHeader?.IsILOnly == true;
            foreach (uint rva in binary.Breakpoints.Snapshot())
            {
                ulong va = binary.Image.RvaToVa(rva);
                BreakpointAddresses.Add(va);

                if (managed && binary.Bodies is { } bodies && bodies.At(rva) is { } body)
                {
                    uint token = (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(body.Method);
                    _pendingManaged[va] = (binary.Image.FileName, token, (uint)body.OffsetOf(rva));
                }
            }
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
            _disabledBreakpoints.Remove(address);
            RememberBreakpoint(address, on: false);
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
        if (_binary is not { } binary || binary.Bodies is not { } bodies)
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
        CurrentModule = State == DebugState.Stopped ? session.StoppedAt?.Module : null;
        if (ExecutionAddress is { } at)
        {
            Arrive(at);
        }

        Modules.Clear();
        foreach (string module in session.Modules)
        {
            Modules.Add(new ModuleRow(module, string.Empty, string.Empty, false));
        }

        // The pane is built from BreakpointAddresses, the one source both engines keep — a managed
        // breakpoint is added to it through the same toggle that hands it to the session — so it names
        // and enables uniformly rather than dumping the managed session's own list as bare text.
        RefreshBreakpoints();

        if (State != DebugState.Stopped)
        {
            // Cleared rather than left, for the same reason the registers are: values belonging to a
            // frame that has run on read as current, and anything reasoning from them is reasoning
            // about somewhere the program no longer is.
            Variables.Clear();
            ManagedThreads.Clear();
            CallStack.Clear();
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
            RefreshCallStack(session);
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
        var opened = new List<VariableRow>();
        foreach (var child in session.Children(row.Path))
        {
            var childRow = new VariableRow(child, row.Depth + 1, OnVariableToggled);
            Variables.Insert(insert++, childRow);
            opened.Add(childRow);
        }

        // Property rows come up showing "…" and are filled in by running their getters, one at a
        // time and off the window's thread — running code in the debuggee is not something to do on
        // the thread painting the panel. Fields and elements are already final and are left alone.
        var properties = opened.Where(r => r.Getter is not null).ToList();
        if (properties.Count > 0)
        {
            _ = EvaluatePropertiesAsync(properties);
        }
    }

    /// <summary>
    /// Fills in each property row by running its getter. Sequential, because every one continues the
    /// debuggee and only one evaluation can be in flight; and abandoned the moment the process moves
    /// on, so a value from a frame that is no longer there is never written into the panel.
    /// </summary>
    private async Task EvaluatePropertiesAsync(IReadOnlyList<VariableRow> rows)
    {
        int stops = _managed?.Stops ?? -1;

        foreach (var row in rows)
        {
            if (_managed is not { } session || !IsStopped || session.Stops != stops || row.Getter is null)
            {
                return;
            }

            var getter = row.Getter;
            var path = row.Path;
            var name = row.Name;
            var type = row.Type;

            var evaluated = await Task.Run(() => session.EvaluateProperty(path, getter, name, type));

            // Back on the window's thread. Only written if nothing has moved underneath in the
            // meantime — a step, a continue, another thread selected — and the row is still shown.
            if (evaluated is not null && _managed == session && IsStopped && session.Stops == stops && Variables.Contains(row))
            {
                row.Resolve(evaluated);
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>
    /// Writes a new value into what a row names.
    ///
    /// Asked for rather than typed into the grid: a value written by accident into a running program
    /// is not something to make easy, and the prompt says in one line what the row will take.
    /// </summary>
    [RelayCommand]
    private void SetVariable(VariableRow? row)
    {
        if (row is null || _managed is not { } session || !IsStopped)
        {
            return;
        }

        if (!row.CanSet)
        {
            Add($"{row.Name} cannot be written into: numbers, characters, bools, enums, null and strings can");
            return;
        }

        if (_dialogs?.AskForText(
                "Set value",
                $"New value for {row.Name}",
                "a number, true or false, an enum member, null, or text in quotes",
                row.Value) is not { } typed)
        {
            return;
        }

        if (session.SetValue(row.Path, typed) is { } problem)
        {
            Add($"could not write {row.Name}: {problem}");
            return;
        }

        Add($"{row.Name} = {typed}");

        // Read back rather than assumed: what the runtime stored is what the pane should show, and a
        // value written into one field can be the same object another row is showing.
        RefreshVariables(session);
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
        if (at is null || _binary is not { } binary || binary.Managed is not { } managed
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

    /// <summary>
    /// Picks a managed thread by its id, for the assistant, which works from ids rather than rows. It
    /// sets the same selection a click on the Threads tab would, so the arrow, the locals and the call
    /// stack follow it in the window as well. Null when it worked, or why it did not.
    /// </summary>
    public string? SelectManagedThread(uint threadId)
    {
        if (_managed is null)
        {
            return "nothing is running";
        }

        var row = ManagedThreads.FirstOrDefault(t => t.Id == threadId);
        if (row is null)
        {
            return $"there is no managed thread {threadId}";
        }

        // Already the one being looked at — nothing to do, and setting it would not fire the change.
        if (row.IsSelected)
        {
            return null;
        }

        // Setting this runs OnSelectedManagedThreadChanged, which calls the session and re-syncs.
        SelectedManagedThread = row;
        return null;
    }

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

    private void RefreshCallStack(ManagedDebugSession session)
    {
        CallStack.Clear();
        foreach (var frame in session.Frames())
        {
            var row = ManagedFrameRow.From(frame);
            CallStack.Add(row);

            if (row.IsCurrent)
            {
                _quietCallFrame = true;
                SelectedCallFrame = row;
                _quietCallFrame = false;
            }
        }
    }

    partial void OnSelectedCallFrameChanged(ManagedFrameRow? value)
    {
        if (_quietCallFrame || value is null || value.IsCurrent || _managed is not { } session)
        {
            return;
        }

        if (!value.IsManaged)
        {
            // A native or runtime frame has no locals to move to; leave the selection where it was.
            _quietCallFrame = true;
            SelectedCallFrame = CallStack.FirstOrDefault(f => f.IsCurrent);
            _quietCallFrame = false;
            return;
        }

        if (session.SelectFrame(value.Index) is { } problem)
        {
            Add(problem);
            return;
        }

        // Re-read the frame's locals and move the arrow, and re-mark the stack — but do not re-read
        // the whole stack, which would fold the just-made selection back to the innermost frame.
        _shownStops = session.Stops;
        RefreshVariables(session);
        Remark(value.Index);
        ExecutionAddress = Located(session.StoppedAt);
        if (ExecutionAddress is { } at)
        {
            Arrive(at);
        }

        NotifyCommands();
    }

    /// <summary>Moves the current-frame marker to the picked frame, without rebuilding the list.</summary>
    private void Remark(int index)
    {
        _quietCallFrame = true;
        for (int i = 0; i < CallStack.Count; i++)
        {
            if (CallStack[i].IsCurrent != (CallStack[i].Index == index))
            {
                CallStack[i] = CallStack[i] with { IsCurrent = CallStack[i].Index == index };
            }
        }

        SelectedCallFrame = CallStack.FirstOrDefault(f => f.Index == index);
        _quietCallFrame = false;
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
                    Status = e.Address is { } at ? $"Stopped at 0x{at:X}." : "Stopped.";
                    CurrentModule = StoppedIn();
                    RefreshModules();
                    RefreshThreads();
                    RefreshRegisters();

                    // In mixed mode the stop is inside JITted code, whose runtime address has no place in
                    // the file image — so e.Address is null and the managed location is the only thing
                    // that maps back to a static VA the listing and gutter share. Prefer it; fall back to
                    // the native address for a native stop (a breakpoint in a module's own code).
                    ulong? shown = RefreshMixedContext() ?? e.Address;
                    ExecutionAddress = shown;
                    if (shown is { } address)
                    {
                        Arrive(address);
                    }
                    else if (_managed is null && _session is { CurrentAddress: > 0 and var runtime })
                    {
                        // A native stop with no listing address is one outside the opened image —
                        // stepped into an imported DLL, the CRT, a system library. There is nothing to
                        // point at in the file on screen, but the window can still open that module's
                        // own code from the run-time address, so hand it over rather than dropping it.
                        Arrive(runtime);
                    }

                    break;

                case "exited":
                    State = DebugState.Exited;
                    ExecutionAddress = null;
                    CurrentModule = null;
                    _arrivedAt = null;

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

    /// <summary>
    /// Which module the program is stopped in.
    ///
    /// The run-time address rather than the listing one, because those are the same number only
    /// inside the opened binary — the whole point of naming the module is the stops that are not.
    /// In mixed mode a stop inside JITted code belongs to no loaded image at all, so the managed
    /// location's own module answers where the address cannot.
    /// </summary>
    private string? StoppedIn()
    {
        if (_session is not { CurrentAddress: > 0 and var runtime } native)
        {
            return _managed?.StoppedAt?.Module;
        }

        return native.ModuleAt(runtime) is { Name.Length: > 0 } module
            ? module.Name
            : native.Managed?.LocationOf(runtime)?.Module;
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
            Breakpoints.Add(new BreakpointRow(
                address,
                BreakpointLocation(address),
                _pendingManaged.ContainsKey(address) ? "managed" : "native",
                !_disabledBreakpoints.Contains(address),
                SetBreakpointEnabled));
        }
    }

    /// <summary>
    /// What a breakpoint sits in, for the pane to read rather than a bare address: a managed one names
    /// its method and IL offset (a native listing is absent for an IL-only file); a native one takes
    /// the name the analysis gives its address. Falls back to the address when nothing can name it.
    /// </summary>
    private string BreakpointLocation(ulong va)
    {
        if (_binary is not { } binary)
        {
            return $"0x{va:X}";
        }

        if (_pendingManaged.TryGetValue(va, out var managed))
        {
            string where = $"IL_{managed.Offset:X4}";
            return binary.Managed?.Locate((int)managed.Token) is (var type, { } member)
                ? $"{type.Name}.{member.Name} ({where})"
                : where;
        }

        return binary.Analysis?.NameFor(va) ?? $"0x{va:X}";
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
    /// Hands every switched-on patch to the managed session, to be written into its method's IL as
    /// the module loads. Nothing is written now: the session holds each one until its module is in,
    /// which is the only point at which a managed patch has any effect. Each is named by the method it
    /// falls in and the offset within it — a managed patch has no address of its own until the runtime
    /// gives the IL one — so a patch that is not inside a method's IL has nowhere managed to go and is
    /// said to be skipped. What went in, and why one did not, arrives on the log as the run starts.
    /// </summary>
    private void ApplyManagedPatches(OpenedBinary binary, ManagedDebugSession session)
    {
        string module = binary.Image.FileName;
        var bodies = binary.Bodies;

        foreach (var patch in binary.Patches.Snapshot().Where(p => p.Enabled))
        {
            if (bodies?.At(patch.Rva) is not { } body)
            {
                Add($"patch at 0x{binary.Image.RvaToVa(patch.Rva):X} not applied: it is not inside a method's IL, "
                    + "so save a patched copy to change bytes that are not managed code");
                continue;
            }

            uint token = (uint)MetadataTokens.GetToken(body.Method);
            uint offset = (uint)body.OffsetOf(patch.Rva);
            session.ApplyOnLoad(new ManagedPatch(module, token, offset, [.. patch.Bytes], [.. patch.Original]));
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

        if (_session is not { } session || _binary is not { } binary)
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

        if (_binary is not { Analysis: { } analysis } binary)
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
        if (row is null || _binary is not { } binary)
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
        StopMixedPump();
        if (_binary is { } open)
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
        CallStack.Clear();
        _shownStops = -1;

        LivePatches.Clear();
        OnPropertyChanged(nameof(HasLivePatches));

        State = DebugState.NotStarted;
        CurrentModule = null;
        _arrivedAt = null;
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
    /// Runs to the end of the current function and stops in whatever called it.
    ///
    /// Three ways to the same place, by who is driving. Mixed mode steps out over the native loop; the
    /// CLR engine asks the runtime, which knows its own frames; and a native stop unwinds one frame
    /// with the platform's own unwinder to find the return address and runs to it. That last is why
    /// this is no longer managed-only: <see cref="DebugSession.StepOutNative"/> reads the function's
    /// unwind info the way the OS does, rather than guessing where the return address sits.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStepOut))]
    private void StepOut()
    {
        Expect();
        if (!IsStopped)
        {
            return;
        }

        // Mixed mode: an IL step-out over the native loop.
        if (StepMixed(IlStepKind.Out))
        {
            return;
        }

        // The CLR engine steps out of the managed frame through the runtime.
        if (_managed is { } session)
        {
            Resuming();
            if (session.StepOut() is { } problem)
            {
                Add(problem);
                return;
            }

            State = DebugState.Running;
            NotifyCommands();
            return;
        }

        // A pure native stop: unwind one frame and run to the caller's return address. Asked before
        // anything moves, so a step out that cannot be worked out — the outermost frame, a function
        // with no unwind information — says so and leaves the program stopped where it was, rather
        // than letting it run on to wherever it would have ended.
        if (_session is { } native)
        {
            if (!native.TryStepOut(out string? problem))
            {
                Add($"cannot step out: {problem}");
                Status = "Cannot step out from here.";
                return;
            }

            Resuming();
            State = DebugState.Running;
            NotifyCommands();
        }
    }

    // Step out works at any stop now: mixed and CLR-engine stops step out through the runtime, and a
    // native stop unwinds one frame. Only that there is a stop to step out of.
    private bool CanStepOut() => IsStopped;

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
        if (_binary is not { } binary || binary.Bodies is not { } bodies)
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
            else
            {
                binary.Breakpoints.Remove(rva);
            }

            Add(refused ?? $"cleared the breakpoint at IL_{offset:X4}");
        }
        else
        {
            BreakpointAddresses.Add(staticVa);

            // Recorded whether or not anything is running. A breakpoint set before the run is the
            // useful kind, and the session plants everything it has been given when its module loads.
            _pendingManaged[staticVa] = (module, token, offset);
            binary.Breakpoints.Add(rva);
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
    /// Sets or clears a breakpoint named by a <c>Type::Method</c> in some assembly — the opened one or
    /// another — resolved as modules load. No gutter marker follows it: the method is in a file that is
    /// not the one on screen, so there is no line here to mark. Returns a line describing the outcome.
    /// </summary>
    internal string SetManagedBreakpointByName(string type, string method, uint ilOffset, bool on)
    {
        if (_managed is not { } session)
        {
            return "nothing is running under the .NET debugger; start it first";
        }

        string result = session.SetBreakpointByName(type, method, ilOffset, on);
        SyncManaged();
        return result;
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
        Expect();
        // One IL offset, descending into a managed call — the native loop's IL step in mixed mode, the
        // CLR engine's own IL step otherwise.
        if (StepMixed(IlStepKind.Into))
        {
            return;
        }

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

    private bool CanStepIl() => IsStopped && (IsManaged || IsMixedMode);

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
        if (at is null || _binary is not { } binary
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

    public void Dispose() => StopSession();
}
