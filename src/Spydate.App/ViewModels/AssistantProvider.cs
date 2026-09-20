using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.Agent;
using Spydate.Agent.Secrets;

namespace Spydate.App.ViewModels;

/// <summary>
/// Which provider, model and key the assistant talks through — the one part of the assistant that
/// belongs to the window rather than to a file.
///
/// Every tab has a conversation of its own, and all of them talk to the same model: choosing a
/// provider is a decision about the window, and a key is entered once. So the settings and the
/// Configure dialog live here, and each tab's <see cref="AssistantViewModel"/> reads through this
/// and hears <see cref="Changed"/> when a save goes through.
/// </summary>
public sealed partial class AssistantProvider : ObservableObject
{
    private readonly ISecretStore _secrets;
    private Views.ProviderSettingsWindow? _dialog;

    public AssistantProvider(ISecretStore secrets)
    {
        _secrets = secrets;
        Settings = AgentSettings.Load();
    }

    public AgentSettings Settings { get; private set; }

    /// <summary>True once a provider, a model and a key are all present.</summary>
    public bool IsConfigured => Settings.Model.Length > 0 && Key is not null;

    /// <summary>The key for the configured provider, or null when there is none.</summary>
    public string? Key => _secrets.Get(Settings.Provider.ToString()) is { Length: > 0 } key ? key : null;

    /// <summary>Raised after new settings are saved, so every conversation can take its next turn on the new model.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Asks for a provider, a model and a key, and remembers all but the key in plain text.
    ///
    /// There are two ways in — the panel's button and the Settings menu — with one command behind
    /// both, so this refuses to open a second copy and brings the open one forward instead.
    /// Modality is not enough on its own to rely on: it turns on the owner being set, and two
    /// dialogs saving the same settings is worth ruling out outright rather than by argument.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private void Configure()
    {
        if (_dialog is { } already)
        {
            already.Activate();
            return;
        }

        var window = new Views.ProviderSettingsWindow(Settings, _secrets) { Owner = Application.Current?.MainWindow };
        _dialog = window;
        ConfigureCommand.NotifyCanExecuteChanged();

        try
        {
            if (window.ShowDialog() != true)
            {
                return;
            }

            Settings = window.Result;
            Settings.Save();
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(IsConfigured));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _dialog = null;
            ConfigureCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>False while the dialog is open, so both ways in show as unavailable.</summary>
    private bool CanConfigure() => _dialog is null;
}
