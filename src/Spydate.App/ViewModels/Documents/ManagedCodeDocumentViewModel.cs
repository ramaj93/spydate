using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly Action<SourceReference, ManagedAssembly>? _navigate;
    private IReadOnlyList<SourceReference> _lastReferences = Array.Empty<SourceReference>();
    private IReadOnlyList<SourceDeclaration> _lastDeclarations = Array.Empty<SourceDeclaration>();
    private int? _pendingReveal;

    private ManagedCodeDocumentViewModel(
        string key,
        string title,
        SymbolRegular icon,
        ManagedAssembly assembly,
        ManagedType? type,
        ManagedMember? member,
        Func<ManagedImage?>? image = null,
        Action<SourceReference, ManagedAssembly>? navigate = null)
        : base(key, title, icon)
    {
        _assembly = assembly;
        _type = type;
        _member = member;
        _image = image;
        _navigate = navigate;
    }

    public static ManagedCodeDocumentViewModel ForAssembly(ManagedAssembly assembly)
        => new($"managed:assembly", assembly.Name, SymbolRegular.Library24, assembly, null, null);

    public static ManagedCodeDocumentViewModel ForType(
        ManagedAssembly assembly,
        ManagedType type,
        Func<ManagedImage?>? image = null,
        Action<SourceReference, ManagedAssembly>? navigate = null)
        => new($"managed:type:{assembly.Name}:{type.FullName}", type.Name, SymbolRegular.Class24, assembly, type, null, image, navigate);

    public static ManagedCodeDocumentViewModel ForMember(
        ManagedAssembly assembly,
        ManagedType type,
        ManagedMember member,
        Func<ManagedImage?>? image = null,
        Action<SourceReference, ManagedAssembly>? navigate = null)
        => new($"managed:member:{assembly.Name}:{type.FullName}::{member.Handle.GetHashCode():X}", $"{type.Name}.{member.Name}", SymbolRegular.Code24, assembly, type, member, image, navigate);

    /// <summary>Identifiers in the current C# that name a type or member, for click-to-navigate.</summary>
    [ObservableProperty]
    private IReadOnlyList<SourceReference> _references = Array.Empty<SourceReference>();

    /// <summary>Line to scroll to and mark, 1-based; zero leaves the view where it is.</summary>
    [ObservableProperty]
    private int _revealLine;

    /// <summary>Follows a clicked reference to its definition, through the assembly that owns it.</summary>
    [RelayCommand]
    private void GoToDefinition(SourceReference reference) => _navigate?.Invoke(reference, _assembly);

    /// <summary>
    /// Stops on the line where a member of this type is declared, the way dnSpy opens a member into
    /// its class. The token is a metadata token in this document's assembly. If the C# has not been
    /// produced yet the request is held and applied when it lands; the IL view carries no declarations,
    /// so a reveal switches to C# first.
    /// </summary>
    public void RevealMember(int token)
    {
        _pendingReveal = token;
        if (Language != ManagedLanguage.CSharp)
        {
            Language = ManagedLanguage.CSharp;   // reloads; the pending reveal is applied when it lands
            return;
        }

        ApplyPendingReveal();
    }

    private void ApplyPendingReveal()
    {
        if (_pendingReveal is { } token
            && _lastDeclarations.FirstOrDefault(d => d.Token == token) is { Line: > 0 } declaration)
        {
            // Reset first so re-revealing the line the view is already on still scrolls to it: the
            // editor's RevealLine only acts on a change, and zero means "leave it alone".
            RevealLine = 0;
            RevealLine = declaration.Line;
            _pendingReveal = null;
        }
    }

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
                References = _lastReferences;
                ApplyPendingReveal();
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

    /// <summary>
    /// The decompiler references plus the libraries named in DllImport attributes, so a P/Invoke
    /// declaration can be followed to the DLL it actually calls. See NativeImports.
    /// </summary>
    private static IReadOnlyList<SourceReference> WithNativeModules(IReadOnlyList<SourceReference> references, string text)
    {
        var native = NativeImports.In(text);
        if (native.Count == 0)
        {
            return references;
        }

        var all = new List<SourceReference>(native);
        all.AddRange(references);
        return all;
    }

    private string Produce(ManagedLanguage language, CancellationToken ct)
    {
        var d = _assembly.Decompiler;
        _lastReferences = Array.Empty<SourceReference>();
        _lastDeclarations = Array.Empty<SourceDeclaration>();

        // The C# member and type views carry references — the identifiers a click can follow.
        // The other views (IL, whole assembly) do not, so they leave the set empty.
        switch (language, _member, _type)
        {
            case (ManagedLanguage.CSharp, { } m, _):
            {
                var source = d.SourceForMember(m, ct);
                string addressed = Addressed(source);
                _lastReferences = WithNativeModules(source.References, addressed);
                _lastDeclarations = source.Declarations;
                return addressed;
            }

            case (ManagedLanguage.CSharp, null, { } t):
            {
                var source = d.SourceForType(t, ct);
                string addressed = Addressed(source);
                _lastReferences = WithNativeModules(source.References, addressed);
                _lastDeclarations = source.Declarations;
                return addressed;
            }

            case (ManagedLanguage.CSharp, null, null):
                return d.DecompileAssembly(ct);

            case (ManagedLanguage.IL, { } m, _):
                return Addressed(d.DisassembleMember(m, ct), m);

            case (ManagedLanguage.IL, null, { } t):
                return d.DisassembleType(t, ct);

            default:
                return d.DisassembleModuleHeader(ct);
        }
    }
}
