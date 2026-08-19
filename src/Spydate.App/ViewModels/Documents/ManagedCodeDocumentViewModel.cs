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

    private ManagedCodeDocumentViewModel(string key, string title, SymbolRegular icon, ManagedAssembly assembly, ManagedType? type, ManagedMember? member)
        : base(key, title, icon)
    {
        _assembly = assembly;
        _type = type;
        _member = member;
    }

    public static ManagedCodeDocumentViewModel ForAssembly(ManagedAssembly assembly)
        => new($"managed:assembly", assembly.Name, SymbolRegular.Library24, assembly, null, null);

    public static ManagedCodeDocumentViewModel ForType(ManagedAssembly assembly, ManagedType type)
        => new($"managed:type:{type.FullName}", type.Name, SymbolRegular.Class24, assembly, type, null);

    public static ManagedCodeDocumentViewModel ForMember(ManagedAssembly assembly, ManagedType type, ManagedMember member)
        => new($"managed:member:{type.FullName}::{member.Handle.GetHashCode():X}", $"{type.Name}.{member.Name}", SymbolRegular.Code24, assembly, type, member);

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

    private string Produce(ManagedLanguage language, CancellationToken ct)
    {
        var d = _assembly.Decompiler;
        return (language, _member, _type) switch
        {
            (ManagedLanguage.CSharp, { } m, _) => d.DecompileMember(m, ct),
            (ManagedLanguage.CSharp, null, { } t) => d.DecompileType(t, ct),
            (ManagedLanguage.CSharp, null, null) => d.DecompileAssembly(ct),
            (ManagedLanguage.IL, { } m, _) => d.DisassembleMember(m, ct),
            (ManagedLanguage.IL, null, { } t) => d.DisassembleType(t, ct),
            _ => d.DisassembleModuleHeader(ct),
        };
    }
}
