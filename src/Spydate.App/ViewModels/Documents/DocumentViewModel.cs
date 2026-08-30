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

    /// <summary>
    /// The address this document is about, when it has one. Drives the Xrefs panel. Documents that
    /// track a selection (the string list) change it as the user moves around.
    /// </summary>
    [ObservableProperty]
    private ulong? _address;

    /// <summary>Bytes covered by <see cref="Address"/>; more than one for a string literal.</summary>
    [ObservableProperty]
    private int _addressLength = 1;

    /// <summary>Called once when the document becomes visible for the first time; heavy work goes here.</summary>
    public virtual Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool _loaded;

    /// <summary>Runs the loader again, for when what the document shows has changed underneath it.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _loaded = false;
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(true);
    }

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

/// <summary>
/// A document showing code, where the caret means something. The naming commands ask these three
/// questions and nothing else, so a single-pane and a split document answer them the same way.
/// </summary>
public interface ICaretContext
{
    /// <summary>Address of the line the caret is on, if it states one.</summary>
    ulong? CaretAddress { get; }

    /// <summary>Identifier under the caret, if it is on one.</summary>
    string? CaretWord { get; }

    /// <summary>
    /// The function this document is about. Distinct from the caret: in a split view the caret moves
    /// around inside one function, and a stack slot belongs to the function, not to the line.
    /// </summary>
    ulong? OwningFunctionVa { get; }
}

/// <summary>A generic name/value row for property tables.</summary>
public sealed record PropertyRow(string Name, string Value, string? Note = null);
