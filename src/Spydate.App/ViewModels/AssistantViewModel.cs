using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Spydate.Agent;
using Spydate.Agent.Providers;
using Spydate.Agent.Text;
using Spydate.App.Services;
using Spydate.Mcp;
using Spydate.Mcp.Session;

namespace Spydate.App.ViewModels;

/// <summary>
/// One line in the transcript. Kind drives how it is drawn, nothing more.
///
/// The text is observable rather than fixed because an answer arrives in pieces: the line is added
/// as soon as the first token lands and grows from there, instead of appearing whole at the end.
/// While it is arriving it is drawn as the plain text it is so far; once it settles it is parsed as
/// Markdown, which is why <see cref="IsStreaming"/> is observable too.
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
    [NotifyPropertyChangedFor(nameof(FlatText))]
    private string _text;

    /// <summary>An assistant line still being written: drawn as plain text, not yet parsed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlatText))]
    private bool _isStreaming;

    public bool IsYou => Kind == "you";

    public bool IsTool => Kind == "tool";

    public bool IsProblem => Kind == "problem";

    /// <summary>
    /// The plain text this line reads as — what selecting and copying it gives, and what a search
    /// looks through. For a settled answer that is its Markdown flattened by the same walk the view
    /// draws from, so an offset into it is an offset into what is on screen; for everything else it
    /// is the text itself. Cached, because a search reads it once per line per keystroke.
    /// </summary>
    public string FlatText => _flat ??= Kind == "assistant" && !IsStreaming
        ? Spydate.Agent.Text.FlatText.Of(Text)
        : Text;

    private string? _flat;

    partial void OnTextChanged(string value) => _flat = null;

    partial void OnIsStreamingChanged(bool value) => _flat = null;

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

    /// <summary>
    /// The model's own message history for this conversation, without the system message — the thing
    /// the next agent is rebuilt from, so a restored chat is a memory and not just a transcript to
    /// read. Empty for a conversation nothing has been asked in yet.
    /// </summary>
    public IReadOnlyList<ChatMessage> History { get; set; } = [];

    /// <summary>Which provider and model produced <see cref="History"/> — the replay decision needs it.</summary>
    public ProviderKind Provider { get; set; } = ProviderKind.Anthropic;

    public string Model { get; set; } = string.Empty;

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
    private readonly OpenedBinary _binary;
    private readonly DebuggerViewModel _debugger;
    private readonly AssistantProvider _provider;
    private AssistantLine? _answer;
    private AnalysisAgent? _agent;
    private SessionStore? _session;
    private CancellationTokenSource? _turn;

    /// <summary>The conversation restored for this binary to carry into the next agent, or null.</summary>
    private RestoredHistory? _restored;

    /// <summary>
    /// The assistant for one open file: its own conversation about its own binary, driving its own
    /// debugger.
    ///
    /// One per <see cref="FileViewModel"/> rather than one per window, the way the debugger is. Two
    /// binaries disagree completely about what has been said about them, so a shared panel that
    /// reloaded itself on every tab switch both lost the place and, mid-turn, wrote one tab's answer
    /// under another tab's name. Which model to talk to is still a window-wide decision — that is
    /// <see cref="AssistantProvider"/>, shared, and this hears it change through <c>Changed</c>.
    /// </summary>
    public AssistantViewModel(OpenedBinary binary, DebuggerViewModel debugger, AssistantProvider provider)
    {
        _binary = binary;
        _debugger = debugger;
        _provider = provider;
        LogPath = ChatLog.PathFor(binary.Image.Path ?? binary.Image.FileName);

        // A new provider or model knows nothing of the conversation so far, so the agent is dropped
        // and rebuilt from the stored history on the next turn — flattened to text when the provider
        // kind changed, since tool blocks are provider-shaped. What is on screen stays: it is a
        // record, and changing model is no reason to destroy it.
        provider.Changed += (_, _) =>
        {
            ResetAgent();
            UpdateStatus();
        };

        Recall();
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
        LoadInto(newValue, fromEarlierRun: false);
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

        // The model's own history, without the system message it rebuilds each time. Only when an
        // agent exists: Remember runs after ResetAgent in NewSession and StartOver, and a null agent
        // there must not overwrite a stored history with nothing. When it does exist, its provider
        // and model are what the replay decision reads on the way back in.
        if (_agent is { } agent)
        {
            row.History = agent.History.Skip(1).ToList();
            row.Provider = Settings.Provider;
            row.Model = Settings.Model;
        }
    }

    /// <summary>Puts a stored conversation on screen, and holds its history for the next agent built.</summary>
    private void LoadInto(ChatSessionRow row, bool fromEarlierRun)
    {
        Transcript.Clear();

        foreach (var entry in row.Entries)
        {
            Transcript.Add(new AssistantLine(entry.Kind, entry.Text, entry.At));
        }

        _restored = row.History.Count > 0
            ? new RestoredHistory(row.History, row.Provider, fromEarlierRun)
            : null;
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
        LoadInto(blank, fromEarlierRun: false);
        Remember();
        UpdateStatus();
    }

    /// <summary>Which provider, model and key to talk through — shared by every tab's conversation.</summary>
    public AssistantProvider Provider => _provider;

    /// <summary>The model settings, read through the shared provider.</summary>
    private AgentSettings Settings => _provider.Settings;

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
    public bool IsConfigured => _provider.IsConfigured;

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
            Settle();
            var progress = new Progress<AgentStep>(step =>
            {
                switch (step.Kind)
                {
                    case "tool":
                        // A tool call ends the paragraph: whatever it says next is about what the
                        // tool returned, so it belongs in a line of its own.
                        Settle();
                        Add("tool", step.Text);
                        break;

                    case "note":
                        Settle();
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
                        break;

                    case "delta":
                        _answer = Add("assistant", step.Text);
                        _answer.IsStreaming = true;
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

            // Settling the answer flips it from streamed plain text to parsed Markdown: the view is
            // bound to the line, so this is what redraws the finished answer with its headings,
            // tables and code.
            Settle();
            AskCommand.NotifyCanExecuteChanged();
            NewSessionCommand.NotifyCanExecuteChanged();
            UpdateStatus();

            // Before it is written down, so a leaked template is not saved. The recovery scrubs too,
            // but only when it fires - and it cannot fire for a turn that was never streamed, or for
            // one where the markup was not the last thing said.
            Scrub();
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
        _restored = null;      // nothing on screen to refer back to, so no history to carry forward

        if (ActiveSession is { } active)
        {
            active.Entries = [];
            active.History = [];
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
        Settle();
    }

    /// <summary>
    /// The answer stops arriving: it is no longer the streaming line, so it is parsed as Markdown
    /// the next time it is drawn or read. Safe when nothing is streaming.
    /// </summary>
    private void Settle()
    {
        if (_answer is { } line)
        {
            line.IsStreaming = false;
        }

        _answer = null;
    }

    /// <summary>Where this binary's conversation is kept. Fixed for the life of the file.</summary>
    private string LogPath { get; }

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
                .Select(row => new ChatSession
                {
                    Id = row.Id,
                    At = row.At,
                    Entries = row.Entries,
                    History = row.History,
                    Provider = row.Provider,
                    Model = row.Model,
                })
                .ToList(),
            Active = ActiveSession?.Id,
        });
    }

    /// <summary>
    /// Reads back the conversations held about this binary, and puts the last one in front.
    ///
    /// The chosen one's message history is carried into the next agent through <see cref="_restored"/>,
    /// so the model picks up where it left off — it does not re-read what it already read, and a
    /// one-word "yes" the window was closed on still means something. What no longer fits the budget
    /// is dropped oldest-first by <see cref="AnalysisAgent.Trim"/> on the first turn, not here.
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
                History = session.History,
                Provider = session.Provider,
                Model = session.Model,
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
        LoadInto(pick, fromEarlierRun: true);

        // LoadInto has handed the picked conversation's history to the next agent built for this
        // binary, marked as being from an earlier run — so the agent is told, in one line, that any
        // process it was debugging is gone and to re-read before relying on it, and otherwise carries
        // straight on. See AnalysisAgent's restored-history marker. Nothing is added to the transcript
        // to say so: it picks up from the end of what is already on screen, and the names are in the
        // project. A panel that explains itself every time it opens is one more thing to read past.
    }

    private AnalysisAgent Agent()
    {
        if (_agent is not null)
        {
            return _agent;
        }

        if (_binary is not { Analysis: not null } binary)
        {
            throw new InvalidOperationException("This file cannot be analysed — there is nothing to look at.");
        }

        string key = _provider.Key
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

            // And the window's own notes, so a section the assistant records shows in the Notes
            // document, and one a person typed there is in front of the assistant.
            notes: binary.Notes,

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
        // setting both would leave the tools choosing, which they would do wrongly for a mixed-mode
        // assembly.
        //
        // Asked of the panel rather than worked out again from the file. It used to test IsILOnly
        // here, which was the same answer the panel reached and is no longer: a .NET program can be
        // driven by the native loop on purpose, so that breakpoints and patches can go into the
        // native DLLs it loads. Deciding it twice would mean the tools were handed the managed
        // interface for a process the native loop actually owns.
        // The settings object points the store at the debugger for the current engine now, and re-points
        // it whenever the agent changes the engine through debug_config — so the tools always drive the
        // one the run configuration names, without a person opening the Debug Program dialog.
        //
        // This file's own debugger, held from construction rather than asked for: the conversation
        // is about this binary and drives the process this binary starts, whichever tab is forward.
        _session.DebugSettings = new PanelDebugSettings(_debugger, _session);
        var options = McpOptions.Default with { AllowDebug = true };

        // Flattened to text when the provider kind differs from the one that produced the history —
        // tool blocks and their call ids are provider-shaped, and DeepSeek needs a reasoning field
        // that does not survive being saved. See AnalysisAgent.Replayable.
        var restored = _restored is { } r
            ? r with { Messages = AnalysisAgent.Replayable(provider.Kind, r.Provider) ? r.Messages : ChatLog.Flatten(r.Messages) }
            : null;

        _agent = new AnalysisAgent(ChatProviders.Create(provider, key), _session, options, provider, restored);
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
            Settle();
        }
    }

    /// <summary>
    /// A whole new line. The transcript is a bound list now, so adding to it, a line's text growing
    /// and a line's streaming flag flipping each raise their own change and the view follows them —
    /// there are no separate redraw events to keep in step any more.
    /// </summary>
    private AssistantLine Add(string kind, string text)
    {
        var line = new AssistantLine(kind, text);
        Transcript.Add(line);
        return line;
    }

    private void UpdateStatus()
    {
        Status = !IsConfigured
            ? "Not set up yet — Configure to choose a provider and add a key."
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
