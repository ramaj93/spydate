using CommunityToolkit.Mvvm.ComponentModel;
using Spydate.App.Services;
using Spydate.Decompiler.Managed;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

public enum ManagedLanguage
{
    CSharp,
    IL,
}

/// <summary>C# or IL view of a managed assembly, type or member.</summary>
public sealed partial class ManagedCodeDocumentViewModel : DocumentViewModel
{
    private readonly ManagedAssembly _assembly;
    private readonly ManagedType? _type;
    private readonly ManagedMember? _member;
    private CancellationTokenSource? _cts;

    private readonly Func<ManagedMember, (ManagedBody Body, ulong ImageBase, bool Wide)?>? _locate;

    private ManagedCodeDocumentViewModel(
        string key,
        string title,
        SymbolRegular icon,
        ManagedAssembly assembly,
        ManagedType? type,
        ManagedMember? member,
        Func<ManagedMember, (ManagedBody Body, ulong ImageBase, bool Wide)?>? locate = null)
        : base(key, title, icon)
    {
        _assembly = assembly;
        _type = type;
        _member = member;
        _locate = locate;
    }

    public static ManagedCodeDocumentViewModel ForAssembly(ManagedAssembly assembly)
        => new($"managed:assembly", assembly.Name, SymbolRegular.Library24, assembly, null, null);

    public static ManagedCodeDocumentViewModel ForType(ManagedAssembly assembly, ManagedType type)
        => new($"managed:type:{type.FullName}", type.Name, SymbolRegular.Class24, assembly, type, null);

    public static ManagedCodeDocumentViewModel ForMember(
        ManagedAssembly assembly,
        ManagedType type,
        ManagedMember member,
        Func<ManagedMember, (ManagedBody Body, ulong ImageBase, bool Wide)?>? locate = null)
        => new($"managed:member:{type.FullName}::{member.Handle.GetHashCode():X}", $"{type.Name}.{member.Name}", SymbolRegular.Code24, assembly, type, member, locate);

    public IReadOnlyList<ManagedLanguage> Languages { get; } = new[] { ManagedLanguage.CSharp, ManagedLanguage.IL };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Highlighting))]
    private ManagedLanguage _language = ManagedLanguage.CSharp;

    [ObservableProperty]
    private string _text = string.Empty;

    public string Highlighting => Language == ManagedLanguage.CSharp ? HighlightingService.CSharp : HighlightingService.Il;

    public string Subtitle => _member?.Signature ?? _type?.FullName ?? _assembly.FullName;

    partial void OnLanguageChanged(ManagedLanguage value) => _ = ReloadAsync();

    public override Task LoadAsync(CancellationToken cancellationToken) => ReloadAsync();

    private async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusMessage = null;
        try
        {
            var lang = Language;
            string text = await Task.Run(() => Produce(lang, cts.Token), cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
            {
                Text = text;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Text = $"// Decompilation failed:\n// {ex.GetType().Name}: {ex.Message}";
            StatusMessage = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// The IL listing with a file address against every instruction, when there is a body to locate.
    ///
    /// Only for a single member. A whole type's listing restarts its offsets at every method, so
    /// deciding which body a line belongs to would mean parsing ILSpy's own output for
    /// <c>.method</c> headers — a guess about a format that is not a contract, in the one place
    /// where being one method out puts a breakpoint in someone else's code.
    /// </summary>
    private string Addressed(string listing, ManagedMember member)
        => _locate?.Invoke(member) is { } found
            ? ManagedDecompiler.Addressed(listing, found.Body, found.ImageBase, found.Wide)
            : listing;

    private string Produce(ManagedLanguage language, CancellationToken ct)
    {
        var d = _assembly.Decompiler;
        return (language, _member, _type) switch
        {
            (ManagedLanguage.CSharp, { } m, _) => d.DecompileMember(m, ct),
            (ManagedLanguage.CSharp, null, { } t) => d.DecompileType(t, ct),
            (ManagedLanguage.CSharp, null, null) => d.DecompileAssembly(ct),
            (ManagedLanguage.IL, { } m, _) => Addressed(d.DisassembleMember(m, ct), m),
            (ManagedLanguage.IL, null, { } t) => d.DisassembleType(t, ct),
            _ => d.DisassembleModuleHeader(ct),
        };
    }
}
