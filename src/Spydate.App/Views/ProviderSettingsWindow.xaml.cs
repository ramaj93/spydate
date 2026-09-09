using System.Windows;
using System.Windows.Controls;
using Spydate.Agent;
using Spydate.Agent.Providers;
using Spydate.Agent.Secrets;

namespace Spydate.App.Views;

/// <summary>
/// Provider, model and key. The key never travels with the settings: it goes straight into the
/// secret store, and this window only ever holds it long enough to put it there.
/// </summary>
public partial class ProviderSettingsWindow : Window
{
    private readonly ISecretStore _secrets;

    /// <summary>
    /// The context suggestion for whatever model the box last held, in thousands.
    ///
    /// It is what tells a typed number apart from a seeded one. Following the model matters most to
    /// the person who never opens this dialog twice, and overwriting a figure somebody chose by hand
    /// because they then corrected a typo in the model id would be the worse failure of the two.
    /// </summary>
    private int _seededContext;

    public ProviderSettingsWindow(AgentSettings settings, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _secrets = secrets;
        InitializeComponent();

        Result = new AgentSettings
        {
            Provider = settings.Provider,
            Model = settings.Model,
            Endpoint = settings.Endpoint,
            MaxToolCalls = settings.MaxToolCalls,
            Stream = settings.Stream,
            MaxContextTokens = settings.MaxContextTokens,
        };

        StreamBox.IsChecked = settings.Stream;
        ContextBox.Text = (settings.MaxContextTokens / 1000).ToString(System.Globalization.CultureInfo.CurrentCulture);
        ToolCallsBox.Text = settings.MaxToolCalls.ToString(System.Globalization.CultureInfo.CurrentCulture);
        ProviderBox.ItemsSource = Enum.GetValues<ProviderKind>();
        ProviderBox.SelectedItem = settings.Provider;
        ModelBox.Text = settings.Model.Length > 0 ? settings.Model : ProviderSettings.SuggestedModel(settings.Provider);
        EndpointBox.Text = settings.Endpoint ?? string.Empty;

        // The existing key is never shown, not even as dots of the right length: a stored key is
        // reported as present, and replaced only if something is typed here.
        KeyBox.Password = string.Empty;
        UpdateKeyHint();

        // Attached after everything above, so setting the initial model does not read as a change.
        _seededContext = ProviderSettings.SuggestedContextTokens(settings.Provider, ModelBox.Text) / 1000;
        ModelBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnModelTextChanged));
    }

    /// <summary>
    /// Moves the context budget with the model, unless it has been set by hand.
    ///
    /// The number that matters is the model's, and asking every user to look up their own model's
    /// window and convert it to thousands is asking most of them to leave it wrong — which is what
    /// happened: one figure, chosen for no model in particular, quietly bounding every conversation.
    /// </summary>
    private void OnModelTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not ProviderKind kind)
        {
            return;
        }

        int suggested = ProviderSettings.SuggestedContextTokens(kind, ModelBox.Text) / 1000;
        bool untouched = int.TryParse(ContextBox.Text.Trim(), System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.CurrentCulture, out int shown)
                         && shown == _seededContext;

        _seededContext = suggested;
        if (untouched)
        {
            ContextBox.Text = suggested.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    /// <summary>What was chosen. Only meaningful when the dialog was accepted.</summary>
    public AgentSettings Result { get; private set; }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not ProviderKind kind || !IsLoaded)
        {
            return;
        }

        // Switching provider makes the old model id meaningless, so it is replaced with one that
        // works rather than left to fail on the first question. Any list fetched for the previous
        // provider goes too, for the same reason.
        ModelBox.ItemsSource = null;
        ModelBox.Text = ProviderSettings.SuggestedModel(kind);
        EndpointBox.Text = string.Empty;
        Say(string.Empty);
        UpdateKeyHint();
    }

    /// <summary>
    /// Asks the provider what it can run. Typing a model id from memory is a coin toss — providers
    /// rename them, and a wrong one fails at the first question with an error that says nothing
    /// useful — so this turns it into a list. What was typed is kept if it is still on offer.
    /// </summary>
    private async void OnFetchModelsClick(object sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not ProviderKind kind)
        {
            return;
        }

        string typed = ModelBox.Text.Trim();
        var settings = new ProviderSettings
        {
            Kind = kind,
            Model = typed.Length > 0 ? typed : "unused",
            Endpoint = EndpointBox.Text.Trim() is { Length: > 0 } endpoint ? endpoint : null,
        };

        // The key just typed takes precedence over the stored one, so a new key can be checked
        // before it is saved.
        string? key = KeyBox.Password.Length > 0 ? KeyBox.Password : _secrets.Get(kind.ToString());

        try
        {
            FetchButton.IsEnabled = false;
            Say("Asking...");

            var result = await ModelCatalog.ListAsync(settings, key).ConfigureAwait(true);
            if (!result.Ok)
            {
                // Never a dialog: the list is a convenience, and the box below still works.
                Say(result.Problem);
                return;
            }

            ModelBox.ItemsSource = result.Models;
            ModelBox.Text = result.Models.Contains(typed, StringComparer.Ordinal) ? typed : result.Models[0];
            Say($"{result.Models.Count} models");
        }
        finally
        {
            FetchButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Says something on the status line, and takes the line away again when there is nothing to
    /// say. It is one line, so a provider's error is trimmed to fit — and the tooltip carries the
    /// whole of it, because a truncated error is missing the half that says what went wrong.
    /// </summary>
    private void Say(string? text)
    {
        ModelStatus.Text = text ?? string.Empty;
        ModelStatus.ToolTip = text is { Length: > 0 } ? text : null;
        ModelStatus.Visibility = text is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateKeyHint()
    {
        if (ProviderBox.SelectedItem is ProviderKind kind)
        {
            KeyBox.ToolTip = _secrets.Get(kind.ToString()) is null
                ? $"No key stored for {kind}."
                : $"A key for {kind} is already stored. Type here only to replace it.";
        }
    }

    private void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedItem is ProviderKind kind)
        {
            _secrets.Set(kind.ToString(), null);
            KeyBox.Password = string.Empty;
            UpdateKeyHint();
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not ProviderKind kind)
        {
            return;
        }

        if (ModelBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Give a model id — the provider needs to be told which one to use.", "Assistant provider");
            return;
        }

        // Rejected rather than silently rounded: someone who types 1000000 means a million tokens,
        // and quietly reading that as a thousand would give them a fifth of the window they asked
        // for with nothing to say why the assistant kept forgetting things.
        if (!int.TryParse(ContextBox.Text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.CurrentCulture, out int thousands)
            || thousands is < 1 or > 2_000)
        {
            MessageBox.Show(this, "Give the context as a whole number of thousands of tokens, between 1 and 2000.", "Assistant provider");
            return;
        }

        // One is a coherent answer - run one tool, then say something - and the loop needs at least
        // that. The ceiling is there because the number is a bound on how long a single question can
        // run, and a bound nobody would ever reach is not one.
        if (!int.TryParse(ToolCallsBox.Text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.CurrentCulture, out int toolCalls)
            || toolCalls is < 1 or > 200)
        {
            MessageBox.Show(this, "Give the tool calls as a whole number between 1 and 200.", "Assistant provider");
            return;
        }

        if (KeyBox.Password.Length > 0)
        {
            _secrets.Set(kind.ToString(), KeyBox.Password);
        }
        else if (_secrets.Get(kind.ToString()) is null)
        {
            MessageBox.Show(this, $"No key is stored for {kind}. Paste one to use it.", "Assistant provider");
            return;
        }

        Result = new AgentSettings
        {
            Provider = kind,
            Model = ModelBox.Text.Trim(),
            Endpoint = EndpointBox.Text.Trim() is { Length: > 0 } endpoint ? endpoint : null,
            MaxToolCalls = toolCalls,
            Stream = StreamBox.IsChecked == true,
            MaxContextTokens = thousands * 1000,
        };

        DialogResult = true;
    }
}
