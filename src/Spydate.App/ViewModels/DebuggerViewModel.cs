using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
using Spydate.Core.Project;
using Spydate.Debugger;

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

    /// <summary>Registers as of the last stop. Empty while it is running, because they would be a guess.</summary>
    public ObservableCollection<RegisterRow> Registers { get; } = new();

    /// <summary>The top of the stack as of the last stop.</summary>
    public ObservableCollection<StackRow> Stack { get; } = new();

    /// <summary>Everything the process has loaded, so it is visible whether the target is among it.</summary>
    public ObservableCollection<ModuleRow> Modules { get; } = new();

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

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void Continue()
    {
        Registers.Clear();
        _session?.Continue();
        State = DebugState.Running;
        Status = "Running.";
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void StepInstruction()
    {
        Registers.Clear();
        _session?.StepInstruction();
        State = DebugState.Running;
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(IsStopped))]
    private void StepOver()
    {
        Registers.Clear();
        _session?.StepOver();
        State = DebugState.Running;
        NotifyCommands();
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

        Registers.Clear();
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
                    break;

                case "stopped":
                    State = DebugState.Stopped;
                    ExecutionAddress = e.Address;
                    Status = e.Address is { } at ? $"Stopped at 0x{at:X}." : "Stopped.";
                    RefreshModules();
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
        var current = _session?.Registers() ?? [];

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
        foreach (var (address, value) in _session?.Stack() ?? [])
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

    private void StopSession()
    {
        ExecutionAddress = null;
        if (_session is { } session)
        {
            session.Reported -= OnReported;
            session.Dispose();
            _session = null;
        }

        State = DebugState.NotStarted;
        Registers.Clear();
        Stack.Clear();
        Modules.Clear();
        Flags = string.Empty;
        NotifyCommands();
    }

    private void Add(string message) => Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");

    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopDebuggingCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        StepInstructionCommand.NotifyCanExecuteChanged();
        StepOverCommand.NotifyCanExecuteChanged();
    }

    private static bool DefaultConfirm(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void Dispose() => StopSession();
}
