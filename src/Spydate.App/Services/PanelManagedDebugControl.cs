using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Spydate.App.ViewModels;
using Spydate.Mcp.Session;

namespace Spydate.App.Services;

/// <summary>
/// Lets the assistant drive the .NET debugger the window is already showing.
///
/// The same one, for the same reason as <see cref="PanelDebugControl"/>: an agent that started a
/// process of its own would have its own breakpoints and its own idea of where execution is, none of
/// which the analyst watching the panel could see — and the analyst approved running the binary
/// once. So every action goes through <see cref="DebuggerViewModel"/>, which means the panel's
/// buttons and the agent's tools are the same actions and each sees what the other did.
///
/// Actions marshal to the UI thread, because the view model owns collections bound to it. Reads do
/// not: they ask the session directly, which is safe because the session guards its own state, and
/// marshalling them would have an agent's question block on whatever the window happened to be doing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PanelManagedDebugControl : IManagedDebugControl
{
    private readonly DebuggerViewModel _debugger;

    public PanelManagedDebugControl(DebuggerViewModel debugger) => _debugger = debugger;

    public string? Start(bool holdAtStart)
    {
        // The confirmation the view model puts up is the point of routing through it: a person says
        // yes to running this binary, once, whoever asked for it. It always holds at the start, so
        // the flag is not passed on — an agent that wants it running says continue.
        OnUi(() => _debugger.StartCommand.Execute(null));
        return _debugger.IsDebugging ? null : _debugger.Status;
    }

    public void Stop() => OnUi(() => _debugger.StopDebuggingCommand.Execute(null));

    public void Continue() => OnUi(() => _debugger.ContinueCommand.Execute(null));

    public string? Step(bool into)
    {
        string? problem = null;
        OnUi(() => problem = _debugger.StepManaged(into));
        return problem;
    }

    public string? StepOut()
    {
        string? problem = null;
        OnUi(() => problem = _debugger.StepOutManaged());
        return problem;
    }

    public string? SetBreakpoint(string module, uint methodToken, uint ilOffset, bool on)
    {
        string? problem = "nothing happened";
        OnUi(() => problem = _debugger.SetManagedBreakpoint(module, methodToken, ilOffset, on));
        return problem;
    }

    public bool WaitUntilStopped(TimeSpan timeout)
        => _debugger.ManagedSession?.WaitUntilStopped(timeout) ?? false;

    public ManagedSnapshot Snapshot()
    {
        if (_debugger.ManagedSession is not { } session)
        {
            return new ManagedSnapshot { State = "not started", Status = "nothing is running" };
        }

        var at = session.StoppedAt;
        return new ManagedSnapshot
        {
            State = session.State switch
            {
                Debugger.DebugState.Running => "running",
                Debugger.DebugState.Stopped => "stopped",
                Debugger.DebugState.Exited => "exited",
                _ => "not started",
            },
            Status = session.Status,
            Where = at is null ? null : $"{at.Module}!0x{at.MethodToken:X8}+IL_{at.Offset:X4}",
            Mapping = at?.Mapping ?? string.Empty,
            Arguments = Slots(session, arguments: true),
            Locals = Slots(session, arguments: false),
            Modules = session.Modules,
            Breakpoints = session.Breakpoints.Select(b => b.ToString()).ToList(),
            Recent = session.Recent,
        };
    }

    private static IReadOnlyList<ManagedSlot> Slots(Debugger.Managed.ManagedDebugSession session, bool arguments)
        => session.Values(arguments).Select(v => new ManagedSlot(v.Index, v.Kind, v.Text)).ToList();

    /// <summary>
    /// Runs something on the UI thread and waits for it, so a caller gets the answer rather than a
    /// promise. Same shape as the native adapter, and load-bearing for the same reason: the agent
    /// calls these from its own thread and the view model's collections are bound.
    /// </summary>
    private static void OnUi(Action work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            work();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            work();
            return;
        }

        dispatcher.Invoke(work, DispatcherPriority.Normal);
    }
}
