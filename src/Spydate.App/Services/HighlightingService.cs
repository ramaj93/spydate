using System.Reflection;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Spydate.App.Services;

/// <summary>Registers Spydate's embedded XSHD highlighting definitions with AvalonEdit.</summary>
public sealed class HighlightingService
{
    public const string Asm = "Spydate.Asm";
    public const string PseudoC = "Spydate.PseudoC";
    public const string CSharp = "Spydate.CSharp";
    public const string Il = "Spydate.IL";
    public const string Plain = "";

    private static readonly (string Name, string Resource, string[] Extensions)[] Definitions =
    {
        (Asm, "asm.xshd", new[] { ".asm" }),
        (PseudoC, "pseudoc.xshd", new[] { ".pc" }),
        (CSharp, "csharp-dark.xshd", new[] { ".cs" }),
        (Il, "il.xshd", new[] { ".il" }),
    };

    private bool _registered;

    public void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var (name, resource, extensions) in Definitions)
        {
            string resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resource, StringComparison.OrdinalIgnoreCase))
                                  ?? throw new InvalidOperationException($"Embedded highlighting '{resource}' not found.");
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = XmlReader.Create(stream);
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(name, extensions, definition);
        }

        _registered = true;
    }

    public static IHighlightingDefinition? Get(string name)
        => string.IsNullOrEmpty(name) ? null : HighlightingManager.Instance.GetDefinition(name);
}
