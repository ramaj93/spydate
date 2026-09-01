using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Spydate.Core.Project;
using Spydate.Mcp.Rendering;
using Spydate.Mcp.Session;

namespace Spydate.Mcp.Tools;

/// <summary>
/// Naming things, which is what the whole loop is for. Understanding a function is only worth
/// anything if the next reader — the next call, the next session, or a person in the window — sees it.
/// </summary>
[McpServerToolType]
public sealed class AnnotationTools
{
    private const int DefaultLimit = 40;
    private const int MaxLimit = 200;

    private readonly SessionStore _store;
    private readonly McpOptions _options;

    public AnnotationTools(SessionStore store, McpOptions options)
    {
        _store = store;
        _options = options;
    }

    [McpServerTool(Name = "annotate")]
    [Description("Name an address, comment on it, or both. Saves immediately, and the window reads the same file. Pass an empty name or comment to clear it and go back to what analysis found.")]
    public string Annotate(
        [Description("Address, sub_XXXX, or an existing name.")] string target,
        [Description("New name. Omit to leave it; pass \"\" to clear it.")] string? name = null,
        [Description("New comment. Omit to leave it; pass \"\" to clear it.")] string? comment = null)
    {
        if (_options.ReadOnly)
        {
            return ReadOnlyRefusal;
        }

        if (_store.Current is not { Analysis: { } analysis } session)
        {
            return SessionTools.NothingOpen;
        }

        if (name is null && comment is null)
        {
            return "give a name, a comment, or both";
        }

        var resolved = Targets.Resolve(session, target);
        if (!resolved.Found)
        {
            return resolved.Problem!;
        }

        if (!InsideImage(session, resolved.Va))
        {
            return $"0x{resolved.Va:X} is outside the image, so it cannot be annotated";
        }

        string was = analysis.NameFor(resolved.Va);
        var sb = new StringBuilder();

        if (name is not null)
        {
            // The stored name is echoed, not the requested one: CleanName turns whitespace into
            // underscores and truncates at 255, and an agent that assumes otherwise will look for a
            // name that does not exist.
            string? applied = analysis.Annotations.SetName(resolved.Va, name);
            sb.Append(applied is null
                ? $"0x{resolved.Va:X} is back to {analysis.NameFor(resolved.Va)}"
                : $"0x{resolved.Va:X} is now {applied}{(was == applied ? string.Empty : $" (was {was})")}");
        }

        if (comment is not null)
        {
            string? applied = analysis.Annotations.SetComment(resolved.Va, comment);
            sb.Append(sb.Length > 0 ? "; " : string.Empty);
            sb.Append(applied is null ? "comment cleared" : $"comment: {applied}");

            // A comment shows against the instruction it is on. The decompiler folds most
            // instructions away, so one set mid-function may only appear in the listing.
            if (analysis.FunctionContaining(resolved.Va) is { EntryVa: var entry } && entry != resolved.Va)
            {
                sb.Append(" (mid-function comments always show in view=\"asm\"; pseudo-C shows them only where the instruction survived)");
            }
        }

        return sb.Append(Persist(session)).ToString();
    }

    [McpServerTool(Name = "annotate_local")]
    [Description("Name one of a function's stack slots, such as arg_0 or local_18. The name belongs to that function only.")]
    public string AnnotateLocal(
        [Description("The function: address, sub_XXXX, or a name.")] string function,
        [Description("The generated slot name, e.g. \"arg_0\" or \"local_18\".")] string slot,
        [Description("New name. Pass \"\" to clear it.")] string? name = null)
    {
        if (_options.ReadOnly)
        {
            return ReadOnlyRefusal;
        }

        if (_store.Current is not { Analysis: { } analysis } session)
        {
            return SessionTools.NothingOpen;
        }

        if (string.IsNullOrWhiteSpace(slot))
        {
            return "give the generated slot name, such as arg_0 or local_18";
        }

        var (resolved, target, _) = Targets.ResolveFunction(session, function);
        if (!resolved.Found || target is null)
        {
            return resolved.Problem ?? $"no function at {function}";
        }

        if (!InsideImage(session, resolved.Va))
        {
            return $"0x{resolved.Va:X} is outside the image, so it cannot be annotated";
        }

        string? applied = analysis.Annotations.SetLocalName(resolved.Va, slot.Trim(), name);
        string where = analysis.NameFor(resolved.Va);

        return (applied is null
            ? $"{slot} in {where} is back to {slot}"
            : $"{slot} in {where} is now {applied}") + Persist(session);
    }

    [McpServerTool(Name = "list_annotations")]
    [Description("Every name and comment recorded for this binary, and who set it. Read this to pick up where a previous session left off, or to review what an agent has done.")]
    public string ListAnnotations(
        [Description("\"agent\", \"user\" or \"all\". Default \"all\".")] string source = "all",
        [Description("Rows to skip, for paging.")] int offset = 0,
        [Description("Rows to return, at most 200.")] int limit = DefaultLimit)
    {
        if (_store.Current is not { Analysis: { } analysis } session)
        {
            return SessionTools.NothingOpen;
        }

        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);

        var all = analysis.Annotations.Snapshot();
        var matching = all
            .Where(e => source switch
            {
                "agent" => e.Value.Source == AnnotationSource.Agent,
                "user" => e.Value.Source == AnnotationSource.User,
                _ => true,
            })
            .ToList();

        var table = new TextTable(("address", 18), ("by", 5), ("name", 44), ("comment", 52), ("locals", 30));
        foreach (var (va, annotation) in matching.Skip(offset).Take(limit))
        {
            table.Add(
                $"0x{va:X}",
                annotation.Source == AnnotationSource.Agent ? "agent" : "user",
                annotation.Name ?? string.Empty,
                annotation.Comment ?? string.Empty,
                annotation.Locals is { Count: > 0 } locals ? string.Join(", ", locals.Select(l => $"{l.Key}={l.Value}")) : string.Empty);
        }

        int returned = Math.Max(0, Math.Min(limit, matching.Count - offset));
        string? next = offset + returned < matching.Count ? $"list_annotations(offset={offset + returned})" : null;

        return Budget.Clip(table.Render("nothing has been named yet") + '\n'
                           + TextTable.Meta(returned, matching.Count, all.Count, "annotations", next, $"source={source}")
                           + Where(session));
    }

    /// <summary>
    /// Whether an address is somewhere this image actually covers.
    ///
    /// <c>VaToRva</c> alone is not enough: it only asks whether the address is at or above the image
    /// base, so anything higher — including an address from a different binary an agent still had in
    /// hand — converts to a plausible-looking RVA and is written to the project file as though it
    /// meant something. Annotating a place that does not exist is worse than refusing to.
    /// </summary>
    private static bool InsideImage(BinarySession session, ulong va)
        => session.Image.VaToRva(va) is { } rva && rva < session.Image.OptionalHeader.SizeOfImage;

    internal const string ReadOnlyRefusal =
        "this server was started with --read-only, so nothing can be renamed or commented. Everything else still works.";

    // ------------------------------------------------------------------

    /// <summary>
    /// Writes through on every change. There is no separate save tool on purpose: one whose only
    /// failure mode is "the agent forgot to call it" would lose work by default, and the file is a
    /// few KB of JSON. The save merges, so a person naming things in the window at the same time
    /// does not lose theirs.
    /// </summary>
    private string Persist(BinarySession session)
    {
        try
        {
            string? path = session.Save(session.Image, session.Analysis!.Annotations);
            return path is null ? string.Empty : $"\nsaved to {path}";
        }
        catch (IOException ex)
        {
            return $"\nNOT SAVED: {ex.Message}. The name is set for this session but will not outlive it.";
        }
    }

    /// <summary>
    /// Where the project lives. Worth saying: a binary in System32 is not writable, so the file goes
    /// to the per-user store instead, and someone looking beside the binary would conclude nothing
    /// had been saved at all.
    /// </summary>
    private static string Where(BinarySession session)
    {
        var candidates = SpydateProject.CandidatePaths(session.Image);
        string? existing = candidates.FirstOrDefault(File.Exists);
        return existing is null ? string.Empty : $"\n-- stored in {existing} --";
    }
}
