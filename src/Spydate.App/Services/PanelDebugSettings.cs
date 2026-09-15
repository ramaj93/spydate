using System.Runtime.Versioning;
using System.Windows;
using Spydate.App.ViewModels;
using Spydate.Core.Project;
using Spydate.Mcp.Session;

namespace Spydate.App.Services;

/// <summary>
/// The agent's read and write access to the Debug Program settings the window's dialog holds — engine,
/// executable, arguments, working directory and where it breaks. It works on the same
/// <see cref="DebuggerViewModel"/> the dialog does, so a change the agent makes shows in the dialog and
/// is remembered for the binary exactly as a person's would be.
///
/// Switching a .NET target's engine also re-points the session's debug controls: the managed and native
/// engines cannot both drive one process, so the store carries whichever the current engine names, and
/// the debug tools answer through it. This is what lets the agent run a .NET binary managed for locals,
/// then native for mixed-mode breakpoints, without a person opening the dialog between the two.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PanelDebugSettings : IDebugSettings
{
    private readonly DebuggerViewModel _debugger;
    private readonly SessionStore _store;

    public PanelDebugSettings(DebuggerViewModel debugger, SessionStore store)
    {
        _debugger = debugger;
        _store = store;
        OnUi(SyncStore);
    }

    public DebugSettingsSnapshot Read()
    {
        DebugSettingsSnapshot snapshot = null!;
        OnUi(() => snapshot = new DebugSettingsSnapshot
        {
            Engine = _debugger.DebugNatively ? "native" : "managed",
            TargetIsManaged = _debugger.IsManaged,
            Running = _debugger.IsDebugging,
            ExecutableEditable = _debugger.CanEditHost,
            Executable = _debugger.Host,
            Arguments = _debugger.Arguments,
            WorkingDirectory = _debugger.WorkingDirectory,
            BreakAt = LabelOf(_debugger.BreakAt),
            EngineOptions = _debugger.IsManaged ? ["managed", "native"] : ["native"],
            BreakAtOptions = _debugger.BreakAtChoices.Select(c => c.Label).ToList(),
        });

        return snapshot;
    }

    public string? Apply(string? engine, string? executable, string? arguments, string? workingDirectory, string? breakAt)
    {
        string? problem = null;
        OnUi(() =>
        {
            if (_debugger.IsDebugging)
            {
                problem = "a run is in progress; stop it before changing how it starts.";
                return;
            }

            if (engine is { Length: > 0 })
            {
                switch (Normalize(engine))
                {
                    case "managed" or "clr" or "dotnet" or "netclr":
                        if (!_debugger.IsManaged)
                        {
                            problem = "this is not a .NET binary; only the native engine can debug it.";
                            return;
                        }

                        _debugger.DebugNatively = false;
                        break;

                    case "native" or "mixed":
                        _debugger.DebugNatively = true;
                        break;

                    default:
                        problem = $"no such engine \"{engine}\". Use managed or native.";
                        return;
                }
            }

            if (executable is not null)
            {
                if (!_debugger.CanEditHost)
                {
                    problem = "the executable cannot be set for this target — a native EXE is its own program.";
                    return;
                }

                _debugger.Host = executable.Trim();
            }

            if (arguments is not null)
            {
                _debugger.Arguments = arguments;
            }

            if (workingDirectory is not null)
            {
                _debugger.WorkingDirectory = workingDirectory.Trim();
            }

            if (breakAt is { Length: > 0 })
            {
                if (BreakAtFrom(breakAt) is not { } chosen)
                {
                    string options = string.Join(", ", _debugger.BreakAtChoices.Select(c => c.Label));
                    problem = $"no such break point \"{breakAt}\". Use one of: {options}.";
                    return;
                }

                _debugger.BreakAt = chosen;
            }

            // The engine may have moved; the session's debug control follows it.
            SyncStore();
        });

        return problem;
    }

    /// <summary>Points the store at the debugger for the current engine, and clears the other. Both
    /// wrap this one view model, so it is the engine choice, not a second process, that they differ on.</summary>
    private void SyncStore()
    {
        if (_debugger.UsesManagedDebugger)
        {
            _store.ManagedDebug = new PanelManagedDebugControl(_debugger);
            _store.Debug = null;
        }
        else
        {
            _store.Debug = new PanelDebugControl(_debugger);
            _store.ManagedDebug = null;
        }
    }

    private string LabelOf(DebugBreakAt value)
        => _debugger.BreakAtChoices.FirstOrDefault(c => c.Value == value)?.Label ?? value.ToString();

    private DebugBreakAt? BreakAtFrom(string text)
    {
        string wanted = Normalize(text);
        foreach (var choice in _debugger.BreakAtChoices)
        {
            if (Normalize(choice.Label) == wanted || Normalize(choice.Value.ToString()) == wanted)
            {
                return choice.Value;
            }
        }

        return null;
    }

    private static string Normalize(string text)
        => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
