using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Spydate.Core.PE;
using Spydate.Mcp.Rendering;
using Spydate.Mcp.Session;

namespace Spydate.Mcp.Tools;

/// <summary>Opening a binary, and asking again what is open.</summary>
[McpServerToolType]
public sealed class SessionTools
{
    private readonly SessionStore _store;
    private readonly McpOptions _options;

    public SessionTools(SessionStore store, McpOptions options)
    {
        _store = store;
        _options = options;
    }

    [McpServerTool(Name = "open_binary")]
    [Description("Open a PE file (exe/dll/sys) for analysis and return an orientation summary. Replaces whatever was open. Run this first.")]
    public async Task<string> OpenBinaryAsync(
        [Description("Full path to the file, e.g. C:\\\\Windows\\\\System32\\\\notepad.exe")] string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "give a path to a PE file to open";
        }

        if (!_options.Allows(path))
        {
            return $"this server was started with --root {_options.Root} and will not open files outside it";
        }

        if (!File.Exists(path))
        {
            return $"there is no file at {path}";
        }

        try
        {
            var session = await _store.OpenAsync(() => BinarySession.Open(path, _options, cancellationToken), cancellationToken).ConfigureAwait(false);
            return Overview(session, opened: true);
        }
        catch (PeParseException ex)
        {
            return $"{path} is not a PE file this can read: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not read {path}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_overview")]
    [Description("Re-print the summary of the binary that is currently open: architecture, sections, imports, counts, and what analysis found.")]
    public string GetOverview()
        => _store.Current is { } session ? Overview(session, opened: false) : NothingOpen;

    internal const string NothingOpen = "no binary is open - call open_binary(path) first";

    /// <summary>Width of the label column, wide enough for the longest label with a gap after it.</summary>
    private const int LabelWidth = 10;

    private const int MaxSectionsListed = 6;

    /// <summary>
    /// One screenful that answers what an agent needs before it can ask anything useful. Deliberately
    /// dense: every fact here is one it would otherwise spend a round trip on, and the whole block
    /// costs less than a single function body.
    /// </summary>
    internal static string Overview(BinarySession session, bool opened)
    {
        var image = session.Image;
        var sb = new StringBuilder();

        Line(sb, opened ? "opened" : "open", image.FileName);
        Line(sb, "path", session.Path);
        Line(sb, "format",
            $"{(image.Is64Bit ? "PE32+" : "PE32")} {image.Machine}, {image.Subsystem} subsystem, base 0x{image.ImageBase:X}, {Size(image.Length)}{(image.IsManaged ? ", .NET managed" : string.Empty)}");

        if (image.EntryPointRva != 0)
        {
            Line(sb, "entry", $"0x{image.EntryPointVa:X}  {session.Analysis?.NameFor(image.EntryPointVa) ?? "entry"}");
        }

        string sections = string.Join(
            " | ",
            image.Sections.Take(MaxSectionsListed).Select(s => $"{s.Name} 0x{s.VirtualAddress:X} {Size(s.VirtualExtent)} {Flags(s)}"));
        if (image.Sections.Count > MaxSectionsListed)
        {
            sections += $" | +{image.Sections.Count - MaxSectionsListed} more";
        }

        Line(sb, "sections", sections);

        int imported = image.Imports.Sum(m => m.Functions.Count) + image.DelayImports.Sum(m => m.Functions.Count);
        Line(sb, "imports", $"{image.Imports.Count + image.DelayImports.Count} modules, {imported} functions");
        Line(sb, "exports", image.Exports is { } e ? $"{e.Entries.Count} from {e.Name}" : "none");

        if (session.Analysis is { } analysis)
        {
            Line(sb, "analysis", $"{session.Discovery.Describe()}, {analysis.Xrefs.Count} references");
            Line(sb, "symbols", Pdb(session));
            Line(sb, "project", Project(session));
        }
        else
        {
            Line(sb, "analysis", $"none - {image.Machine} is not a machine this disassembles, so only headers and strings are readable");
        }

        if (image.IsManaged)
        {
            Line(sb, "note", "this is a .NET assembly; what the native decompiler produces for it describes the CLR stub, not the program");
        }

        if (image.Warnings.Count > 0)
        {
            Line(sb, "warnings", string.Join("; ", image.Warnings.Take(3)));
        }

        if (session.Analysis is not null)
        {
            // Costs about twenty tokens and saves an agent that has never seen this server from
            // guessing where to start.
            Line(sb, "next", "list_functions(named=\"unnamed\", sort=\"refs\") | list_imports() | find_strings(query=...)");
        }

        return Budget.Clip(sb.ToString());
    }

    /// <summary>
    /// One labelled line. Output is plain ASCII throughout: a decorative dash or ellipsis reads the
    /// same and costs more tokens than the character is worth.
    /// </summary>
    private static void Line(StringBuilder sb, string label, string value)
        => sb.Append(label.PadRight(LabelWidth)).Append(value).Append('\n');

    private static string Pdb(BinarySession session) => session.Analysis?.Pdb switch
    {
        { Loaded: true } p => $"{p.SymbolsAdded} from {p.Path}",
        { Reason: { Length: > 0 } reason } => $"no PDB ({reason})",
        _ => "no PDB",
    };

    private static string Project(BinarySession session) => session.Project switch
    {
        { Loaded: true } p => $"{p.Applied} annotations from {p.Path}",
        { Reason: { Length: > 0 } reason } => reason,
        _ => "none yet; annotations will be saved when you make one",
    };

    private static string Flags(SectionHeader section)
    {
        var sb = new StringBuilder(3);
        if (section.IsReadable)
        {
            sb.Append('R');
        }

        if (section.IsWritable)
        {
            sb.Append('W');
        }

        if (section.IsExecutable)
        {
            sb.Append('X');
        }

        return sb.Length == 0 ? "-" : sb.ToString();
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} bytes",
    };
}
