using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.App.Services;
using Spydate.Debugger;

namespace Spydate.App.ViewModels;

/// <summary>One register, as the panel shows it. Changed since the last stop is the useful part:
/// at a breakpoint the question is almost always what the last few instructions did.</summary>
public sealed record RegisterRow(string Name, string Value, bool Changed);

/// <summary>One qword on the stack.</summary>
public sealed record StackRow(string Address, string Value);

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

    public DebuggerViewModel(WorkspaceService workspace, Func<string, string, bool>? confirm = null)
    {
        _workspace = workspace;
        _confirm = confirm ?? DefaultConfirm;
        workspace.CurrentChanged += (_, _) => StopSession();
    }

    /// <summary>Registers as of the last stop. Empty while it is running, because they would be a guess.</summary>
    public ObservableCollection<RegisterRow> Registers { get; } = new();

    /// <summary>The top of the stack as of the last stop.</summary>
    public ObservableCollection<StackRow> Stack { get; } = new();

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

        // Asked every time, not once and remembered. The answer is about this binary, and the cost
        // of getting it wrong is running something hostile on the analyst's own machine.
        if (!_confirm(
                "Run this binary?",
                $"{binary.DisplayName} will be started on this machine and will do whatever it does.\n\n"
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
            session.Start(path, binary.Image.ImageBase, binary.Image.OptionalHeader.SizeOfImage);
            State = DebugState.Running;
            Status = "Running.";
            Add($"started {path}");
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
        Add("stopped");
        Status = "Not running.";
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
                case "stopped":
                    State = DebugState.Stopped;
                    ExecutionAddress = e.Address;
                    Status = e.Address is { } at ? $"Stopped at 0x{at:X}." : "Stopped.";
                    RefreshRegisters();
                    if (e.Address is { } address)
                    {
                        StoppedAt?.Invoke(this, address);
                    }

                    break;

                case "exited":
                    State = DebugState.Exited;
                    ExecutionAddress = null;
                    Status = "It exited.";
                    Registers.Clear();
                    break;
            }

            NotifyCommands();
        });
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
