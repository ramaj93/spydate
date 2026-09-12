using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
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
        //
        // The native path never yields, so the task is finished by the time Execute returns — but it
        // is waited for anyway rather than relying on that, because which path runs is decided by
        // the file that happens to be open.
        Task? starting = null;
        OnUi(() => starting = _debugger.StartCommand.ExecuteAsync(null));
        if (starting is { IsCompleted: false } && Application.Current?.Dispatcher.CheckAccess() != true)
        {
            starting.GetAwaiter().GetResult();
        }

        return _debugger.IsDebugging ? null : _debugger.Status;
    }

    public void Stop() => OnUi(() => _debugger.StopDebuggingCommand.Execute(null));

    public void Continue() => OnUi(() => _debugger.ContinueCommand.Execute(null));

    public void Pause() => OnUi(() => _debugger.PauseCommand.Execute(null));

    public void StepInstruction() => OnUi(() => _debugger.StepInstructionCommand.Execute(null));

    public void StepOver() => OnUi(() => _debugger.StepOverCommand.Execute(null));

    public void RunTo(ulong staticVa) => OnUi(() => _debugger.RunTo(staticVa));

    public string? TryPatch(ulong va, string instruction, string? comment)
    {
        string? problem = "nothing happened";
        OnUi(() => problem = _debugger.TestPatch(va, instruction, comment));
        return problem;
    }

    public bool UndoPatch(uint rva)
    {
        bool done = false;
        OnUi(() => done = _debugger.UndoPatchAt(rva));
        return done;
    }

    public bool SelectThread(uint threadId)
    {
        bool found = false;
        OnUi(() => found = _debugger.SelectThread(threadId));
        return found;
    }

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
            // "not running" rather than "not started": it is also where a session lands after being
            // stopped, and claiming nothing was ever started would be a statement about history this
            // does not have. Which of the two it is, the status line says.
            State = _debugger.State switch
            {
                DebugState.NotStarted => "not running",
                DebugState.Running => "running",
                DebugState.Stopped => "stopped",
                _ => "exited",
            },
            Status = _debugger.Status,
            Address = _debugger.ExecutionAddress,
            TargetLoaded = _debugger.Modules.Any(m => m.IsTarget),
            Threads = _debugger.Threads.Select(t => (t.Id, Parse(t.Start), t.Waiting)).ToList(),
            CurrentThread = _debugger.Threads.FirstOrDefault(t => t.IsCurrent)?.Id ?? 0,
            SelectedThread = _debugger.SelectedThread?.Id ?? 0,
            Registers = _debugger.Registers.Select(r => (r.Name, Parse(r.Value))).ToList(),
            Flags = _debugger.Flags,
            Stack = _debugger.Stack.Select(s => (Parse(s.Address), Parse(s.Value))).ToList(),
            Modules = _debugger.Modules.Select(m => (m.Name, Parse(m.Base), m.IsTarget)).ToList(),
            Breakpoints = _debugger.BreakpointAddresses.Order().ToList(),
            LivePatches = _debugger.LivePatches.Select(p => (p.Rva, p.Va, p.Was, p.Now, p.Comment)).ToList(),

            // The tail of the panel log. Enough to carry a module load, a breakpoint and an exit;
            // not so much that a long run buries the answer to the question actually asked.
            Recent = _debugger.Log.TakeLast(12).ToList(),
        });

        return snapshot;
    }

    public byte[] ReadMemory(ulong staticVa, int length) => _debugger.ReadMemory(staticVa, length);

    public bool WaitUntilStopped(TimeSpan timeout)
    {
        var dispatcher = Application.Current?.Dispatcher;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_debugger.State is DebugState.Stopped or DebugState.Exited or DebugState.NotStarted)
            {
                return true;
            }

            // On the UI thread the wait has to let the dispatcher run rather than sleep through it.
            // The debug loop reports every stop by posting to that dispatcher, so a plain sleep here
            // blocks the one thing that could ever end the wait, and it times out having watched a
            // state that could not change. This used to carry a comment asserting it never ran on
            // the UI thread, which nothing arranged and nothing checked.
            if (dispatcher is not null && dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(10);
            }
            else
            {
                Thread.Sleep(25);
            }
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
