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
        };

        ProviderBox.ItemsSource = Enum.GetValues<ProviderKind>();
        ProviderBox.SelectedItem = settings.Provider;
        ModelBox.Text = settings.Model.Length > 0 ? settings.Model : ProviderSettings.SuggestedModel(settings.Provider);
        EndpointBox.Text = settings.Endpoint ?? string.Empty;

        // The existing key is never shown, not even as dots of the right length: a stored key is
        // reported as present, and replaced only if something is typed here.
        KeyBox.Password = string.Empty;
        UpdateKeyHint();
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
        ModelStatus.Text = string.Empty;
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
            ModelStatus.Text = "Asking...";

            var result = await ModelCatalog.ListAsync(settings, key).ConfigureAwait(true);
            if (!result.Ok)
            {
                // Never a dialog: the list is a convenience, and the box below still works.
                ModelStatus.Text = result.Problem;
                return;
            }

            ModelBox.ItemsSource = result.Models;
            ModelBox.Text = result.Models.Contains(typed, StringComparer.Ordinal) ? typed : result.Models[0];
            ModelStatus.Text = $"{result.Models.Count} models";
        }
        finally
        {
            FetchButton.IsEnabled = true;
        }
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
            MaxToolCalls = Result.MaxToolCalls,
        };

        DialogResult = true;
    }
}
