using System.Runtime.Versioning;
using System.Windows;
using Spydate.App.ViewModels;
using Spydate.Debugger;
using Spydate.Mcp.Session;

namespace Spydate.App.Services;

/// <summary>
/// Lets the assistant drive the debugger the window is already showing.
///
/// The same one, deliberately. An agent that started a second process of its own would have its own
/// breakpoints, its own registers and its own idea of where execution is, none of which the analyst
/// watching the panel could see — and the analyst would have approved running the binary once while
/// two copies of it ran. So every call here goes through <see cref="DebuggerViewModel"/>, which
/// means the panel's buttons and the agent's tools are the same actions, and anything the agent does
/// appears in the panel as it happens.
///
/// Everything marshals to the UI thread, because the view model owns collections bound to it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PanelDebugControl : IDebugControl
{
    private readonly DebuggerViewModel _debugger;

    public PanelDebugControl(DebuggerViewModel debugger) => _debugger = debugger;

    public string? Start()
    {
        // The confirmation the view model puts up is the point of routing through it: a person says
        // yes to running this binary, once, whoever asked for it.
        OnUi(() => _debugger.StartCommand.Execute(null));
        return _debugger.IsDebugging ? null : _debugger.Status;
    }

    public void Stop() => OnUi(() => _debugger.StopDebuggingCommand.Execute(null));

    public void Continue() => OnUi(() => _debugger.ContinueCommand.Execute(null));

    public void StepInstruction() => OnUi(() => _debugger.StepInstructionCommand.Execute(null));

    public void StepOver() => OnUi(() => _debugger.StepOverCommand.Execute(null));

    public void RunTo(ulong staticVa) => OnUi(() => _debugger.RunTo(staticVa));

    public bool SetBreakpoint(ulong staticVa, bool on)
    {
        // Toggle is what the panel offers, so this asks for it only when it would change something —
        // otherwise "set it" on an address that already has one would clear it.
        OnUi(() =>
        {
            if (_debugger.BreakpointAddresses.Contains(staticVa) != on)
            {
                _debugger.ToggleBreakpoint(staticVa);
            }
        });

        return _debugger.BreakpointAddresses.Contains(staticVa);
    }

    public DebugSnapshot Snapshot()
    {
        DebugSnapshot snapshot = null!;
        OnUi(() => snapshot = new DebugSnapshot
        {
            State = _debugger.State switch
            {
                DebugState.NotStarted => "not started",
                DebugState.Running => "running",
                DebugState.Stopped => "stopped",
                _ => "exited",
            },
            Status = _debugger.Status,
            Address = _debugger.ExecutionAddress,
            TargetLoaded = _debugger.Modules.Any(m => m.IsTarget),
            Registers = _debugger.Registers.Select(r => (r.Name, Parse(r.Value))).ToList(),
            Flags = _debugger.Flags,
            Stack = _debugger.Stack.Select(s => (Parse(s.Address), Parse(s.Value))).ToList(),
            Modules = _debugger.Modules.Select(m => (m.Name, Parse(m.Base), m.IsTarget)).ToList(),
            Breakpoints = _debugger.BreakpointAddresses.Order().ToList(),
        });

        return snapshot;
    }

    public byte[] ReadMemory(ulong staticVa, int length) => _debugger.ReadMemory(staticVa, length);

    public bool WaitUntilStopped(TimeSpan timeout)
    {
        // Polled rather than awaited on an event, and never on the UI thread: the debug loop reports
        // through the dispatcher, so blocking that thread here would stop the very notification being
        // waited for from ever arriving.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_debugger.State is DebugState.Stopped or DebugState.Exited or DebugState.NotStarted)
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    private static ulong Parse(string hex)
        => Spydate.Core.Text.AddressText.ParseHex(hex) ?? 0;

    private static void OnUi(Action work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            work();
            return;
        }

        dispatcher.Invoke(work);
    }
}
