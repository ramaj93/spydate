using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.Agent;
using Spydate.Agent.Providers;
using Spydate.Agent.Secrets;
using Spydate.App.Services;
using Spydate.Mcp;
using Spydate.Mcp.Session;

namespace Spydate.App.ViewModels;

/// <summary>
/// One line in the transcript. Kind drives how it is coloured, nothing more.
///
/// The text is observable rather than fixed because an answer arrives in pieces: the line is added
/// as soon as the first token lands and grows from there, instead of appearing whole at the end.
/// </summary>
public sealed partial class AssistantLine : ObservableObject
{
    public AssistantLine(string kind, string text)
    {
        Kind = kind;
        _text = text;
    }

    public string Kind { get; }

    [ObservableProperty]
    private string _text;

    public bool IsYou => Kind == "you";

    public bool IsTool => Kind == "tool";

    public bool IsProblem => Kind == "problem";

    public void Append(string more) => Text += more;
}

/// <summary>
/// The assistant panel: a model with the analysis tools, working on the binary that is open.
///
/// It acts on the window's own <c>BinaryAnalysis</c>, not a copy — so a name it gives appears in the
/// documents at once, through the same path a name typed by hand takes. That is the whole reason it
/// is worth having in here rather than driving the MCP server from outside.
/// </summary>
public sealed partial class AssistantViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceService _workspace;
    private readonly ISecretStore _secrets;
    private readonly IFileDialogService _dialogs;
    private Views.ProviderSettingsWindow? _providerDialog;
    private AssistantLine? _answer;
    private AnalysisAgent? _agent;
    private SessionStore? _session;
    private CancellationTokenSource? _turn;

    public AssistantViewModel(WorkspaceService workspace, ISecretStore secrets, IFileDialogService dialogs)
    {
        _workspace = workspace;
        _secrets = secrets;
        _dialogs = dialogs;
        Settings = AgentSettings.Load();

        // A different binary is a different conversation: the old one refers to addresses that mean
        // nothing now, and carrying it over would have the model reason about the wrong program.
        workspace.CurrentChanged += (_, _) => StartOver();
        UpdateStatus();
    }

    public ObservableCollection<AssistantLine> Transcript { get; } = new();

    public AgentSettings Settings { get; private set; }

    [ObservableProperty]
    private string _question = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>The other way round, for anything that should be off while a turn is running.</summary>
    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>True once a provider, a model and a key are all present.</summary>
    public bool IsConfigured => Settings.Model.Length > 0 && !string.IsNullOrEmpty(_secrets.Get(Settings.Provider.ToString()));

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        // Both ways in already check CanExecute, and the command refuses to run concurrently on its
        // own. This is here anyway because the cost of being wrong is not a wasted click: a second
        // turn would append to the same history while the first is mid-flight, and the provider
        // would be sent a conversation with two questions and no answer between them.
        if (IsBusy)
        {
            return;
        }

        string question = Question.Trim();
        if (question.Length == 0)
        {
            return;
        }

        Question = string.Empty;
        Add("you", question);

        try
        {
            IsBusy = true;
            AskCommand.NotifyCanExecuteChanged();

            // A turn can be a long silence — a slow provider, or several tool calls before it says
            // anything. Without this the panel looks broken rather than busy, and the reasonable
            // thing to do about a broken-looking panel is to send the question again.
            Status = "Working…";

            var agent = Agent();
            _turn = new CancellationTokenSource();

            // Progress marshals to the UI thread because it was created here; the steps it reports
            // arrive from wherever the loop happens to be running.
            _answer = null;
            var progress = new Progress<AgentStep>(step =>
            {
                switch (step.Kind)
                {
                    case "tool":
                        // A tool call ends the paragraph: whatever it says next is about what the
                        // tool returned, so it belongs in a line of its own.
                        _answer = null;
                        Add("tool", step.Text);
                        break;

                    case "delta" when _answer is not null:
                        _answer.Append(step.Text);
                        Advanced();
                        break;

                    case "delta":
                        _answer = Add("assistant", step.Text);
                        break;
                }
            });

            string answer = await agent.AskAsync(question, progress, _turn.Token).ConfigureAwait(true);

            // Only when nothing streamed — a provider that does not stream, or a turn that ended on
            // a tool call. Otherwise the text is already on screen and adding it would double it.
            if (_answer is null)
            {
                Add("assistant", answer.Length == 0 ? "(it said nothing)" : answer);
            }
        }
        catch (OperationCanceledException)
        {
            Add("problem", "Stopped.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever a provider SDK throws — a bad key, a wrong model id, no network — belongs in
            // the transcript, where it can be read and acted on, not in a crash dialog.
            Add("problem", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _turn?.Dispose();
            _turn = null;
            AskCommand.NotifyCanExecuteChanged();
            UpdateStatus();
        }
    }

    private bool CanAsk() => !IsBusy;

    [RelayCommand]
    private void Stop() => _turn?.Cancel();

    [RelayCommand]
    private void StartOver()
    {
        _agent?.Dispose();
        _agent = null;
        _session = null;
        Transcript.Clear();
        UpdateStatus();
    }

    /// <summary>
    /// Asks for a provider, a model and a key, and remembers all but the key in plain text.
    ///
    /// There are two ways in — the panel's button and the View menu — with one command behind both,
    /// so this refuses to open a second copy and brings the open one forward instead. Modality is
    /// not enough on its own to rely on: it turns on the owner being set, and two dialogs saving the
    /// same settings is worth ruling out outright rather than by argument.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private void Configure()
    {
        if (_providerDialog is { } already)
        {
            already.Activate();
            return;
        }

        var window = new Views.ProviderSettingsWindow(Settings, _secrets) { Owner = Application.Current?.MainWindow };
        _providerDialog = window;
        ConfigureCommand.NotifyCanExecuteChanged();

        try
        {
            if (window.ShowDialog() != true)
            {
                return;
            }

            Settings = window.Result;
            Settings.Save();
            StartOver();
            Add("tool", $"Using {Settings.Provider} / {Settings.Model}.");
        }
        finally
        {
            _providerDialog = null;
            ConfigureCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>False while the dialog is open, so both ways in show as unavailable.</summary>
    private bool CanConfigure() => _providerDialog is null;

    private AnalysisAgent Agent()
    {
        if (_agent is not null)
        {
            return _agent;
        }

        if (_workspace.Current is not { Analysis: not null } binary)
        {
            throw new InvalidOperationException("Open a binary first — there is nothing to look at.");
        }

        string key = _secrets.Get(Settings.Provider.ToString())
                     ?? throw new InvalidOperationException($"No API key for {Settings.Provider}. Use Configure to add one.");

        if (Settings.Model.Length == 0)
        {
            throw new InvalidOperationException("No model chosen. Use Configure to pick one.");
        }

        // The session wraps what the window already has open rather than analysing the file again:
        // same functions, same annotations, same everything the documents are showing.
        _session = new SessionStore();
        _session.Set(new BinarySession(
            binary.Image.Path ?? binary.Image.FileName,
            binary.Image,
            binary.Analysis,
            binary.Project,
            new DiscoveryState(binary.Analysis!.FunctionCount, true, TimeSpan.Zero)));

        var provider = Settings.ToProviderSettings();
        _agent = new AnalysisAgent(ChatProviders.Create(provider, key), _session, McpOptions.Default, provider);
        return _agent;
    }

    /// <summary>Raised whenever the transcript gains text, so the view can keep the end in sight.</summary>
    public event EventHandler? Advancing;

    private AssistantLine Add(string kind, string text)
    {
        var line = new AssistantLine(kind, text);
        Transcript.Add(line);
        Advanced();
        return line;
    }

    private void Advanced() => Advancing?.Invoke(this, EventArgs.Empty);

    private void UpdateStatus()
    {
        Status = !IsConfigured
            ? "Not set up yet — Configure to choose a provider and add a key."
            : _workspace.Current is null
                ? $"{Settings.Provider} / {Settings.Model} — open a binary to begin."
                : $"{Settings.Provider} / {Settings.Model}";

        OnPropertyChanged(nameof(IsConfigured));
    }

    public void Dispose()
    {
        _turn?.Cancel();
        _turn?.Dispose();
        _agent?.Dispose();
    }
}
