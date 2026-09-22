using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Spydate.Core.PE;
using Spydate.Core.Strings;
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
            // Name the way out. open_binary parses a PE into an image and cannot do anything with a
            // file that is not one, and an agent that hits only this wall concludes non-PEs are
            // unreadable and goes off to reconstruct the bytes from process memory. read_file reads
            // any file's raw bytes and is the answer — the path is already inside --root, since that
            // was checked above, so it is allowed here.
            return $"{path} is not a PE file open_binary can read ({ex.Message}). "
                + "It is still a file: read_file(path) reads its raw bytes as hex or text, which is how "
                + "to look at a non-PE like this — a resource, a .inx, an unknown container.";
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

    [McpServerTool(Name = "read_file")]
    [Description("Read raw bytes of any file on disk - not only a PE - as hex or text, to probe a blob open_binary cannot open (a resource, a .inx, an unknown container). A window of at most 4096 bytes; within --root.")]
    public string ReadFile(
        [Description("Full path to the file.")] string path,
        [Description("Byte offset to start at.")] long offset = 0,
        [Description("Bytes to read, at most 4096.")] int length = 256,
        [Description("\"hex\" (default), \"utf8\" or \"utf16\".")] string @as = "hex")
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "give a path to a file to read";
        }

        if (!_options.Allows(path))
        {
            return $"this server was started with --root {_options.Root} and will not read files outside it";
        }

        if (!File.Exists(path))
        {
            return $"there is no file at {path}";
        }

        try
        {
            long size = new FileInfo(path).Length;
            if (offset < 0)
            {
                offset = 0;
            }

            if (offset > size)
            {
                return $"{Path.GetFileName(path)} is {size} bytes; offset 0x{offset:X} is past its end";
            }

            int want = Math.Clamp(length, 1, 4096);
            int can = (int)Math.Min(want, size - offset);
            byte[] bytes = new byte[can];

            using (var stream = File.OpenRead(path))
            {
                stream.Seek(offset, SeekOrigin.Begin);
                int got = 0;
                while (got < can && stream.Read(bytes, got, can - got) is var n and > 0)
                {
                    got += n;
                }

                if (got < can)
                {
                    bytes = bytes[..got];
                }
            }

            string header = $"{Path.GetFileName(path)}: {size} bytes; showing {bytes.Length} at offset 0x{offset:X}"
                            + ((ulong)offset + (ulong)bytes.Length < (ulong)size ? " (more follows)" : string.Empty) + "\n";

            string body = @as switch
            {
                "utf8" => StringLiterals.Escape(Encoding.UTF8.GetString(bytes)),
                "utf16" => StringLiterals.Escape(Encoding.Unicode.GetString(bytes)),
                _ => HexDump.Render(bytes, (ulong)offset),
            };

            return Budget.Clip(header + body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not read {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    internal const string NothingOpen = "no binary is open - call open_binary(path) first";

    /// <summary>Width of the label column, wide enough for the longest label with a gap after it.</summary>
    private const int LabelWidth = 10;

    private const int MaxSectionsListed = 6;

    private const int MaxReferencesListed = 8;

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

        Managed(sb, session);

        if (image.Warnings.Count > 0)
        {
            Line(sb, "warnings", string.Join("; ", image.Warnings.Take(3)));
        }

        // Costs about twenty tokens and saves an agent that has never seen this server from guessing
        // where to start. Which start, though, depends on which reading of the file is the real one:
        // pointing at the function worklist for an IL-only assembly sends it to sweep up junk.
        if (session.Managed is not null && image.ClrHeader?.IsILOnly == true)
        {
            Line(sb, "next", "find_symbol(query=...) | read_function(target=\"Namespace.Type\") | read_function(target=\"Type::Member\", view=\"il\")");
        }
        else if (session.Analysis is not null)
        {
            Line(sb, "next", "list_functions(named=\"unnamed\", sort=\"refs\") | list_imports() | find_strings(query=...)");
        }

        Notes(sb, session);

        return Budget.Clip(sb.ToString());
    }

    /// <summary>
    /// The other reading of the same file, when there is one.
    ///
    /// A .NET assembly has two descriptions and only one of them is about the program. The native
    /// lines above are true — those really are the sections and that really is the entry point — but
    /// for an IL-only assembly they describe the loader stub, and an agent that reads them as the
    /// program will spend its whole budget naming compiler scaffolding. So the managed facts go in
    /// the same block rather than behind a tool call, and the note says plainly which is which.
    /// </summary>
    private static void Managed(StringBuilder sb, BinarySession session)
    {
        if (session.Image.ClrHeader is not { } clr)
        {
            return;
        }

        if (session.Managed is not { } managed)
        {
            Line(sb, "managed", session.ManagedLoadError is { } why
                ? $"this file has a CLR header, but its metadata could not be read - {why}"
                : "this file has a CLR header, but no metadata was loaded for it");
            return;
        }

        var index = session.ManagedIndex!;
        Line(sb, "assembly", $"{managed.FullName}, {managed.TargetFramework}, metadata {managed.RuntimeVersion}");
        Line(sb, "types", $"{index.Types.Count} in {managed.Namespaces.Count} namespaces, {index.MemberCount} members");

        if (managed.EntryPoint is { } entry)
        {
            Line(sb, "main", entry.Signature);
        }

        if (managed.AssemblyReferences.Count > 0)
        {
            // "needs", not "references": the label column is ten wide and "references" fills it
            // exactly, so the value ran straight into the label with no gap at all.
            Line(sb, "needs", string.Join(", ", managed.AssemblyReferences.Take(MaxReferencesListed))
                                   + (managed.AssemblyReferences.Count > MaxReferencesListed
                                       ? $", +{managed.AssemblyReferences.Count - MaxReferencesListed} more"
                                       : string.Empty));
        }

        if (managed.PInvokes.Count > 0)
        {
            // The native dependencies the PE import table cannot show — a managed image imports only
            // the runtime stub — and where an anti-debug check reaches for the kernel. Named here so an
            // agent does not have to parse the ImplMap table out of the bytes to find them.
            var libraries = managed.PInvokes.Select(p => p.Library).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Line(sb, "native", $"{managed.PInvokes.Count} P/Invoke(s) into {string.Join(", ", libraries.Take(MaxReferencesListed))}"
                                   + (libraries.Count > MaxReferencesListed ? $", +{libraries.Count - MaxReferencesListed} more" : string.Empty)
                                   + " - find_symbol names each and the method that calls it");
        }

        // Which of the two readings to trust, said once, in the terms that decide it. ILOnly is the
        // question exactly: a mixed-mode assembly has real native code and both halves are worth
        // reading, and a ReadyToRun image has native code the runtime prefers over the IL.
        Line(sb, "note", clr.IsILOnly
            ? "IL-only: the disassembly and pseudo-C above describe the CLR loader stub, not the program. "
              + "Read this one with find_symbol and read_function(view=\"csharp\")"
            : "mixed-mode: it carries real native code as well as IL, so both readings are about the program");

        if (clr.ManagedNativeHeader.Size != 0)
        {
            Line(sb, "r2r", "precompiled (ReadyToRun): the runtime may run the native copy rather than the IL shown here");
        }
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

    /// <summary>
    /// What has been learned about the binary as a whole. On open this is the only standing surface an
    /// external agent has — there is no system prompt to carry it — so the section keys are always
    /// listed and the bodies follow up to a small cap, past which read_notes reads the rest.
    /// </summary>
    private static void Notes(StringBuilder sb, BinarySession session)
    {
        var sections = session.Notes.Snapshot();
        if (sections.Count == 0)
        {
            Line(sb, "notes", "none yet (record what you learn with note)");
            return;
        }

        Line(sb, "notes", $"{sections.Count} section{(sections.Count == 1 ? string.Empty : "s")} (read_notes): {string.Join(", ", sections.Select(s => s.Key))}");

        const int cap = 2_500;
        var body = new System.Text.StringBuilder();
        int shown = 0;
        foreach (var (key, note) in sections)
        {
            string block = $"\n## {key}\n{note.Text}\n";
            if (body.Length + block.Length > cap && shown > 0)
            {
                break;
            }

            body.Append(block);
            shown++;
        }

        sb.Append(body);
        if (shown < sections.Count)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n-- {sections.Count - shown} more: read_notes --\n");
        }
    }

    private static string Project(BinarySession session) => session.Project switch
    {
        { Loaded: true } p => $"{p.Applied} annotations{(p.NotesApplied > 0 ? $", {p.NotesApplied} notes" : string.Empty)} from {p.Path}",
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
