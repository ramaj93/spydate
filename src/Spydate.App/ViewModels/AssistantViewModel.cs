using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.Agent;
using Spydate.Agent.Providers;
using Spydate.Agent.Secrets;
using Spydate.Agent.Text;
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
    public AssistantLine(string kind, string text, DateTimeOffset? at = null)
    {
        Kind = kind;
        _text = text;
        At = at ?? DateTimeOffset.Now;
    }

    public string Kind { get; }

    public DateTimeOffset At { get; }

    [ObservableProperty]
    private string _text;

    public bool IsYou => Kind == "you";

    public bool IsTool => Kind == "tool";

    public bool IsProblem => Kind == "problem";

    public void Append(string more) => Text += more;
}

/// <summary>
/// One conversation in the picker.
///
/// A class with a settable title rather than the immutable <see cref="ChatSession"/> it is saved as,
/// because the title changes the moment the first question is asked. Replacing a record in the
/// collection would do it too, but replacing the item that is selected clears the selection — the
/// picker would empty itself exactly when the conversation became worth naming.
/// </summary>
public sealed partial class ChatSessionRow : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    /// <summary>What was said, as last stashed. The live one is in the panel's Transcript.</summary>
    public IReadOnlyList<ChatEntry> Entries { get; set; } = [];

    [ObservableProperty]
    private string _title = "New chat";

    /// <summary>
    /// The conversation's name, for anything that falls back to the object's own text rather than
    /// reading the template — an automation client naming the picker's rows, chiefly, which
    /// otherwise announces the name of this class to a screen reader twice over.
    /// </summary>
    public override string ToString() => Title;
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
    private readonly DebuggerViewModel _debugger;
    private Views.ProviderSettingsWindow? _providerDialog;
    private AssistantLine? _answer;
    private AnalysisAgent? _agent;
    private SessionStore? _session;
    private CancellationTokenSource? _turn;

    /// <summary>The end of the conversation restored for this binary, or null when there was none.</summary>
    private string? _earlier;

    public AssistantViewModel(WorkspaceService workspace, ISecretStore secrets, IFileDialogService dialogs, DebuggerViewModel debugger)
    {
        _workspace = workspace;
        _secrets = secrets;
        _dialogs = dialogs;
        _debugger = debugger;
        Settings = AgentSettings.Load();

        // A different binary is a different conversation: the old one refers to addresses that mean
        // nothing now, and carrying it over would have the model reason about the wrong program.
        // What was said about the new one last time is read back in, which is not the same thing —
        // see Recall.
        workspace.CurrentChanged += (_, _) => OnBinaryChanged();
        UpdateStatus();
    }

    public ObservableCollection<AssistantLine> Transcript { get; } = new();

    /// <summary>Every conversation about the open binary, newest last.</summary>
    public ObservableCollection<ChatSessionRow> Sessions { get; } = new();

    /// <summary>
    /// Which conversation is in front. Setting it swaps the transcript and starts a fresh agent:
    /// the other conversation's history belongs to the other conversation, and carrying it across
    /// would be the same mistake as carrying one binary's reasoning onto the next.
    /// </summary>
    [ObservableProperty]
    private ChatSessionRow? _activeSession;

    /// <summary>True while this class is setting ActiveSession itself, so the swap does not re-enter.</summary>
    private bool _switching;

    partial void OnActiveSessionChanged(ChatSessionRow? oldValue, ChatSessionRow? newValue)
    {
        if (_switching || newValue is null)
        {
            return;
        }

        if (oldValue is not null)
        {
            Stash(oldValue);
        }

        ResetAgent();
        LoadInto(newValue);
        Remember();
        UpdateStatus();
    }

    /// <summary>Copies what is on screen back into the row, and renames it from its first question.</summary>
    private void Stash(ChatSessionRow row)
    {
        row.Entries = Transcript
            .Where(line => line.Kind != "note")
            .Select(line => new ChatEntry { Kind = line.Kind, Text = line.Text, At = line.At })
            .ToList();

        row.Title = ChatLog.TitleOf(row.Entries);
    }

    /// <summary>Puts a stored conversation on screen, and hands its end to the next agent built.</summary>
    private void LoadInto(ChatSessionRow row)
    {
        Transcript.Clear();

        foreach (var entry in row.Entries)
        {
            Transcript.Add(new AssistantLine(entry.Kind, entry.Text, entry.At));
        }

        _earlier = ChatLog.Recap(row.Entries);
    }

    private void Select(ChatSessionRow row)
    {
        _switching = true;
        ActiveSession = row;
        _switching = false;
    }

    /// <summary>
    /// Another conversation about the same binary.
    ///
    /// An untouched new chat is reused rather than added to, so that pressing this twice does not
    /// leave a row of identical blanks in the picker.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void NewSession()
    {
        if (ActiveSession is { } active)
        {
            Stash(active);
        }

        var blank = Sessions.FirstOrDefault(row => row.Entries.Count == 0);
        if (blank is null)
        {
            blank = new ChatSessionRow();
            Sessions.Add(blank);
        }

        ResetAgent();
        Select(blank);
        LoadInto(blank);
        Remember();
        UpdateStatus();
    }

    public AgentSettings Settings { get; private set; }

    [ObservableProperty]
    private string _question = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>The other way round, for anything that should be off while a turn is running.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Which provider and model this is. Says the same thing whether or not it is working.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>
    /// Whether it is doing anything, shown beside the box the question is typed into.
    ///
    /// It is a separate line from <see cref="Status"/> because the two answer different questions —
    /// which model am I talking to, and is it listening — and one line cannot hold both. Overwriting
    /// the model with "Working…" took away the answer to the first every time the second changed,
    /// and put the news that a turn had started at the opposite end of the panel from the button
    /// that started it.
    /// </summary>
    [ObservableProperty]
    private string _activity = Idle;

    private const string Idle = "Idle";

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
            NewSessionCommand.NotifyCanExecuteChanged();

            // A turn can be a long silence — a slow provider, or several tool calls before it says
            // anything. Without this the panel looks broken rather than busy, and the reasonable
            // thing to do about a broken-looking panel is to send the question again.
            Activity = "Working…";

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

                    case "note":
                        _answer = null;
                        Add("note", step.Text);
                        break;

                    case "discard":
                        // The markup already streamed onto the screen, so it has to come off again.
                        // Leaving it and appending the retry underneath would show two answers to
                        // one question, the first of which is a template nobody wants to read.
                        Scrub();
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
            Activity = Idle;
            _turn?.Dispose();
            _turn = null;
            _answer = null;
            AskCommand.NotifyCanExecuteChanged();
            NewSessionCommand.NotifyCanExecuteChanged();
            UpdateStatus();

            // Before the turn is drawn and before it is written down, so neither shows a template.
            // The recovery scrubs too, but only when it fires - and it cannot fire for a turn that
            // was never streamed, or for one where the markup was not the last thing said.
            Scrub();
            TurnFinished?.Invoke(this, EventArgs.Empty);
            Remember();
        }
    }

    private bool CanAsk() => !IsBusy;

    /// <summary>
    /// Asks the turn to stop. Not instant — a request already in flight has to come back, and a tool
    /// already running finishes — so the label says "Stopping" rather than pretending it is over.
    /// </summary>
    [RelayCommand]
    private void Stop()
    {
        if (_turn is { } turn)
        {
            Activity = "Stopping…";
            turn.Cancel();
        }
    }

    /// <summary>Empties the conversation in front. The others are left alone.</summary>
    [RelayCommand]
    private void StartOver()
    {
        ResetAgent();
        Transcript.Clear();
        _earlier = null;       // nothing on screen to refer back to, so nothing to resolve against

        if (ActiveSession is { } active)
        {
            active.Entries = [];
            active.Title = "New chat";
        }

        Remember();            // nothing left in this one to remember, so it drops out of the file
        UpdateStatus();
    }

    private void ResetAgent()
    {
        _agent?.Dispose();
        _agent = null;
        _session = null;
        _answer = null;
    }

    private void OnBinaryChanged()
    {
        ResetAgent();
        Transcript.Clear();

        // Cleared before Recall rather than by it: Recall returns early when the new binary has no
        // stored conversation, and the previous binary's would otherwise still be sitting here.
        _earlier = null;
        Recall();
        UpdateStatus();
    }

    /// <summary>Where this binary's conversation is kept, or null when nothing is open.</summary>
    private string? LogPath => _workspace.Current is { } binary
        ? ChatLog.PathFor(binary.Image.Path ?? binary.Image.FileName)
        : null;

    /// <summary>
    /// Writes the conversation out. Called at the end of every turn rather than on exit, because the
    /// run that most needs its notes kept is the one that ends by crashing.
    /// </summary>
    private void Remember()
    {
        if (LogPath is not { } path)
        {
            return;
        }

        if (ActiveSession is { } active)
        {
            Stash(active);
        }

        ChatLog.SaveBook(path, new ChatBook
        {
            Sessions = Sessions
                .Select(row => new ChatSession { Id = row.Id, At = row.At, Entries = row.Entries })
                .ToList(),
            Active = ActiveSession?.Id,
        });
    }

    /// <summary>
    /// Reads back what was said about this binary before.
    ///
    /// The model is not given any of it: a restored conversation is a record to read, not a memory
    /// it can reason from, and the note says so. Quietly reloading it into the history would be the
    /// worse choice — it would double the cost of every turn and let a stale conclusion from weeks
    /// ago steer a fresh one, without anyone being told that is what happened.
    /// </summary>
    private void Recall()
    {
        // Cleared without going through the swap: there is nothing to stash into, and the rows are
        // about to be replaced wholesale.
        _switching = true;
        ActiveSession = null;
        _switching = false;
        Sessions.Clear();

        if (LogPath is not { } path)
        {
            return;
        }

        var book = ChatLog.LoadBook(path);

        foreach (var session in book.Sessions)
        {
            Sessions.Add(new ChatSessionRow
            {
                Id = session.Id,
                At = session.At,
                Entries = session.Entries,
                Title = ChatLog.TitleOf(session.Entries),
            });
        }

        // The one that was in front last time, or the most recent, or a fresh one for a binary
        // nothing has been asked about yet.
        var pick = Sessions.FirstOrDefault(row => row.Id == book.Active) ?? Sessions.LastOrDefault();
        if (pick is null)
        {
            NewSession();
            return;
        }

        Select(pick);
        LoadInto(pick);

        // LoadInto has already handed the end of it to the next agent built for this binary, so that
        // answering the question the last session ended on means something. See AnalysisAgent.Earlier.
        //
        // Nothing is added to say so. There was a line here explaining that the conversation had
        // been restored and how much of it the assistant would be given, which was two sentences of
        // apology for a discontinuity that no longer exists: it picks up from the end of this, and
        // the names are in the project. A panel that explains itself every time it opens is one
        // more thing to read past.
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

            // The agent goes, because the new provider knows nothing of the old conversation. What
            // is on screen stays: it is a record, and changing model is no reason to destroy it.
            ResetAgent();
            UpdateStatus();
            // Which provider and model is on the line above the transcript, so this says only the
            // part that is not: that what follows cannot see what came before it.
            Add("note", "— fresh conversation from here —");
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
            new DiscoveryState(binary.Analysis!.FunctionCount, true, TimeSpan.Zero),
            save: null,

            // The window's own store, so a patch the assistant records appears in the Patches tab
            // rather than in a copy nobody is looking at — the same reason it works on the window's
            // analysis rather than re-reading the file.
            patches: binary.Patches,

            // And the window's own managed assembly. Without it the session's ManagedIndex and Bodies
            // are null, so every managed target — a method to read, a method to break on — resolves as
            // "not in this assembly", which reads exactly like the metadata being unreadable when it is
            // not. ownsManaged is false because the window opened it and its views are still using it;
            // disposing the assistant's session must not dispose it.
            managed: binary.Managed,
            managedLoadError: binary.ManagedLoadError,
            ownsManaged: false));

        var provider = Settings.ToProviderSettings();
        // The window's own debugger, not one of its own. Two processes of the same untrusted binary,
        // with separate breakpoints and only one of them visible, would be worse than no debugger at
        // all — and the analyst approved running it once. Debugging is allowed here for the same
        // reason: a person answers "run this binary?" and is watching the panel while it runs, which
        // is exactly what the stdio server has none of.
        // One or the other, never both: the two debuggers cannot attach to the same process, and
        // which applies is decided by the file rather than by anything asked for here. Setting both
        // would leave the tools choosing, and they would choose wrongly for a mixed-mode assembly.
        if (binary.Image.ClrHeader?.IsILOnly == true)
        {
            _session.ManagedDebug = new PanelManagedDebugControl(_debugger);
        }
        else
        {
            _session.Debug = new PanelDebugControl(_debugger);
        }
        var options = McpOptions.Default with { AllowDebug = true };

        _agent = new AnalysisAgent(ChatProviders.Create(provider, key), _session, options, provider, _earlier);
        return _agent;
    }

    /// <summary>
    /// Takes any leaked tool-call template off the screen, and out of what gets saved.
    ///
    /// Every assistant line, not only the one being written. A template can arrive in the middle of
    /// a turn — the model writes one, then makes a real call, and carries on — and by the time the
    /// turn ends the line holding it is no longer the current one. Removing only the current line
    /// therefore left the markup sitting in the transcript, and <see cref="Remember"/> wrote it to
    /// the log, where it came back on every later open.
    /// </summary>
    private void Scrub()
    {
        bool changed = false;

        for (int i = Transcript.Count - 1; i >= 0; i--)
        {
            var line = Transcript[i];
            if (line.Kind != "assistant")
            {
                continue;
            }

            string clean = ToolCallMarkup.Without(line.Text).TrimEnd();
            if (string.Equals(clean, line.Text, StringComparison.Ordinal))
            {
                continue;
            }

            changed = true;
            if (clean.Length == 0)
            {
                Transcript.RemoveAt(i);
            }
            else
            {
                line.Text = clean;
            }
        }

        if (changed)
        {
            _answer = null;
            Discarding?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised whenever the line being written into grows, so the view can redraw it.</summary>
    public event EventHandler? Advancing;

    /// <summary>Raised when the line being written is thrown away, so the view can un-draw it.</summary>
    public event EventHandler? Discarding;

    /// <summary>Raised when a turn ends, so the view can render the finished answer properly.</summary>
    public event EventHandler? TurnFinished;

    /// <summary>
    /// A whole new line. This does not raise <see cref="Advancing"/>: that means "the last line got
    /// longer", and the collection changing is signal enough on its own. Raising both had the view
    /// draw every added line twice, once for each.
    /// </summary>
    private AssistantLine Add(string kind, string text)
    {
        var line = new AssistantLine(kind, text);
        Transcript.Add(line);
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
