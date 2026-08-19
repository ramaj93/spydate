using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>Base class for everything shown as a tab in the document area.</summary>
public abstract partial class DocumentViewModel : ObservableObject
{
    protected DocumentViewModel(string key, string title, SymbolRegular icon)
    {
        Key = key;
        Title = title;
        Icon = icon;
    }

    /// <summary>Unique key used to find an already-open document (e.g. <c>disasm:0x140001000</c>).</summary>
    public string Key { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private SymbolRegular _icon;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    public bool CanClose { get; init; } = true;

    /// <summary>The address this document is about, when it has one. Drives the Xrefs panel.</summary>
    public ulong? Address { get; init; }

    /// <summary>Called once when the document becomes visible for the first time; heavy work goes here.</summary>
    public virtual Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool _loaded;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        IsBusy = true;
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _loaded = false;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A generic name/value row for property tables.</summary>
public sealed record PropertyRow(string Name, string Value, string? Note = null);
