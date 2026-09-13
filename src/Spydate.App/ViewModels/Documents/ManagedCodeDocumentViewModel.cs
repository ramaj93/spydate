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

/// <summary>
/// Where the open binary's method bodies are, so a listing can carry a file address against its
/// lines.
///
/// Handed to the document rather than looked up by it: the body index belongs to the open binary,
/// and a document that reached for it would be reaching past the thing that owns its lifetime.
/// </summary>
public sealed record ManagedImage(ManagedBodies Bodies, ulong ImageBase, bool Wide);

/// <summary>C# or IL view of a managed assembly, type or member.</summary>
public sealed partial class ManagedCodeDocumentViewModel : DocumentViewModel
{
    private readonly ManagedAssembly _assembly;
    private readonly ManagedType? _type;
    private readonly ManagedMember? _member;
    private CancellationTokenSource? _cts;

    private readonly Func<ManagedImage?>? _image;

    private ManagedCodeDocumentViewModel(
        string key,
        string title,
        SymbolRegular icon,
        ManagedAssembly assembly,
        ManagedType? type,
        ManagedMember? member,
        Func<ManagedImage?>? image = null)
        : base(key, title, icon)
    {
        _assembly = assembly;
        _type = type;
        _member = member;
        _image = image;
    }

    public static ManagedCodeDocumentViewModel ForAssembly(ManagedAssembly assembly)
        => new($"managed:assembly", assembly.Name, SymbolRegular.Library24, assembly, null, null);

    public static ManagedCodeDocumentViewModel ForType(
        ManagedAssembly assembly,
        ManagedType type,
        Func<ManagedImage?>? image = null)
        => new($"managed:type:{assembly.Name}:{type.FullName}", type.Name, SymbolRegular.Class24, assembly, type, null, image);

    public static ManagedCodeDocumentViewModel ForMember(
        ManagedAssembly assembly,
        ManagedType type,
        ManagedMember member,
        Func<ManagedImage?>? image = null)
        => new($"managed:member:{assembly.Name}:{type.FullName}::{member.Handle.GetHashCode():X}", $"{type.Name}.{member.Name}", SymbolRegular.Code24, assembly, type, member, image);

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
        => _image?.Invoke() is { } image && image.Bodies.Of(member.Handle) is { } body
            ? ManagedDecompiler.Addressed(listing, body, image.ImageBase, image.Wide)
            : listing;

    /// <summary>
    /// Decompiled C# with each line's file address in a trailing comment.
    ///
    /// A whole type is fine here, unlike the IL listing: the decompiler says which method each line
    /// came from, so there is nothing to infer from the shape of the text. That is the difference
    /// between reading a listing and being told by the thing that wrote it.
    /// </summary>
    private string Addressed(ManagedSource source)
        => _image?.Invoke() is { } image
            ? ManagedDecompiler.Addressed(source, image.Bodies, image.ImageBase)
            : source.Text;

    private string Produce(ManagedLanguage language, CancellationToken ct)
    {
        var d = _assembly.Decompiler;
        return (language, _member, _type) switch
        {
            (ManagedLanguage.CSharp, { } m, _) => Addressed(d.SourceForMember(m, ct)),
            (ManagedLanguage.CSharp, null, { } t) => Addressed(d.SourceForType(t, ct)),
            (ManagedLanguage.CSharp, null, null) => d.DecompileAssembly(ct),
            (ManagedLanguage.IL, { } m, _) => Addressed(d.DisassembleMember(m, ct), m),
            (ManagedLanguage.IL, null, { } t) => d.DisassembleType(t, ct),
            _ => d.DisassembleModuleHeader(ct),
        };
    }
}
