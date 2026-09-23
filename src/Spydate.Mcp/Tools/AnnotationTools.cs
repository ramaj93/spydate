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
        [Description("Address, sub_XXXX, an existing name, or a .NET Type::Method.")] string target,
        [Description("New name. Omit to leave it; pass \"\" to clear it.")] string? name = null,
        [Description("New comment. Omit to leave it; pass \"\" to clear it.")] string? comment = null)
    {
        if (_options.ReadOnly)
        {
            return ReadOnlyRefusal;
        }

        if (_store.Current is not { } open)
        {
            return SessionTools.NothingOpen;
        }

        if (name is null && comment is null)
        {
            return "give a name, a comment, or both";
        }

        if (open.MemberAnnotations is { } members)
        {
            return AnnotateMember(open, members, target, name, comment);
        }

        if (open is not { Analysis: { } analysis } session)
        {
            return $"there is nothing to annotate: {SessionTools.WhyNoNative(open)}";
        }

        var resolved = Targets.Resolve(session, target);
        if (!resolved.Found)
        {
            // Not an address — but a .NET method named the way read_function and debug_break take it,
            // Namespace.Type::Method, has one all the same: the VA where its IL begins, which is where
            // the C# listing and the gutter address it. Resolving it here means an agent annotates a
            // method by the name it already has, instead of hand-computing an RVA for it.
            if (ManagedMethodVa(session, target) is { } managedVa)
            {
                resolved = TargetResult.Of(managedVa);
            }
            else if (session.BytecodeIndex is not null && BytecodeTargets.Resolve(session, target) is { Found: true } named)
            {
                return $"{named.Describe()} has no single address to annotate; name a method as "
                       + "Namespace.Type::Method, or give an address";
            }
            else
            {
                return resolved.Problem!;
            }
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

        if (_store.Current is { MemberAnnotations: not null })
        {
            return "a JAR's local variables keep the names its class files give them; name the method or field itself with annotate";
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
        [Description("Text to look for in names, comments and local names; an address or exact name matches its row.")] string? query = null,
        [Description("Rows to skip, for paging.")] int offset = 0,
        [Description("Rows to return, at most 200.")] int limit = DefaultLimit)
    {
        if (_store.Current is { MemberAnnotations: { } members } jar)
        {
            return ListMembers(jar, members, source, query, offset, limit);
        }

        if (_store.Current is not { Analysis: { } analysis } session)
        {
            return SessionTools.NothingOpen;
        }

        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);
        string? needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        var all = analysis.Annotations.Snapshot();
        var matching = all
            .Where(e => source switch
            {
                "agent" => e.Value.Source == AnnotationSource.Agent,
                "user" => e.Value.Source == AnnotationSource.User,
                _ => true,
            })
            .Where(e => needle is null || Matches(e.Key, e.Value, needle))
            .ToList();

        var table = new TextTable(("address", 18), ("by", 5), ("name", 44), ("comment", 80), ("locals", 30));
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
        string paging = needle is null ? $"offset={offset + returned}" : $"query=\"{needle}\", offset={offset + returned}";
        string? next = offset + returned < matching.Count ? $"list_annotations({paging})" : null;
        string filters = needle is null ? $"source={source}" : $"source={source}, query={needle}";

        return Budget.Clip(table.Render(needle is null ? "nothing has been named yet" : $"nothing matches \"{needle}\"") + '\n'
                           + TextTable.Meta(returned, matching.Count, all.Count, "annotations", next, filters)
                           + Where(session));
    }

    /// <summary>Whether an annotation's text contains the needle, case-insensitively — name, comment, or a slot name.</summary>
    private static bool Matches(ulong va, Annotation annotation, string needle)
    {
        if ($"0x{va:X}".Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (annotation.Name is { } name && name.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (annotation.Comment is { } comment && comment.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return annotation.Locals is { } locals
               && locals.Any(l => l.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                  || l.Value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    [McpServerTool(Name = "read_annotation")]
    [Description("Everything recorded about one function or address, whole: name, comment, local names, the comments inside it with their addresses, and the note sections that mention it. Read before re-reading a function a past session worked on.")]
    public string ReadAnnotation(
        [Description("Address, sub_XXXX, an existing name, or a .NET Type::Method.")] string target)
    {
        if (_store.Current is { MemberAnnotations: { } members } jar)
        {
            return ReadMember(jar, members, target);
        }

        if (_store.Current is not { Analysis: { } analysis } session)
        {
            return SessionTools.NothingOpen;
        }

        if (!Resolve(session, target, out ulong va, out string? problem))
        {
            return problem!;
        }

        var here = analysis.Annotations.Get(va);
        var containing = analysis.FunctionContaining(va);

        // A bare mid-function address with nothing on it is not an empty answer: the function it sits
        // in is what the caller is really asking about, so show that instead of "nothing recorded".
        if ((here is null || here.IsEmpty) && containing is { } fn && fn.EntryVa != va)
        {
            return $"nothing recorded at 0x{va:X} itself; it is inside {fn.Name}:\n\n"
                   + RenderAnnotation(session, fn.EntryVa);
        }

        return RenderAnnotation(session, va);
    }

    [McpServerTool(Name = "note")]
    [Description("Record what you learn about the binary as a whole, which has no single address: how strings are encoded, what a subsystem does, a dead end. You choose the section key (e.g. string-xor, dead-ends). Markdown, up to 4000 chars a section, as many sections as you need. Saved at once; pass \"\" to remove one.")]
    public string Note(
        [Description("Short section key, e.g. \"overview\" or \"string-xor\".")] string key,
        [Description("The section text. Markdown, multi-line. \"\" removes the section.")] string? text = null,
        [Description("Optional position in the read-together document, lower first. Omit text to only reorder.")] int? index = null)
    {
        if (_options.ReadOnly)
        {
            return ReadOnlyRefusal;
        }

        if (_store.Current is not { } session)
        {
            return SessionTools.NothingOpen;
        }

        string canonical = NoteStore.CleanKey(key) ?? key;

        // Reorder only: an index with no text moves the section without rewriting it. Omitting both is
        // nothing to do, and would otherwise fall through to clearing the section, which is a surprise.
        if (text is null)
        {
            if (index is null)
            {
                return "give text to write, \"\" to remove, or an index to reorder";
            }

            if (session.Notes.SetOrder(key, index.Value) is null)
            {
                return $"no section {canonical} to reorder";
            }

            return $"section {canonical} moved to {index.Value}\n\n" + NoteIndex(session) + PersistNotes(session);
        }

        Note? stored;
        try
        {
            stored = session.Notes.Set(key, text, index);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        string head = stored is null
            ? $"section {canonical} removed"
            : $"section {canonical} saved ({stored.Text.Length} chars)";

        return head + "\n\n" + NoteIndex(session) + PersistNotes(session);
    }

    [McpServerTool(Name = "read_notes")]
    [Description("What was learned about the binary as a whole. No key: the section index (always whole) then the sections in full as far as fits. A key: that section whole.")]
    public string ReadNotes(
        [Description("A section key to read whole. Omit for the index and as many sections as fit.")] string? key = null,
        [Description("Sections to skip, to read the rest a few at a time.")] int offset = 0)
    {
        if (_store.Current is not { } session)
        {
            return SessionTools.NothingOpen;
        }

        var sections = session.Notes.Snapshot();
        if (sections.Count == 0)
        {
            return "nothing has been noted yet; record what you learn with note";
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            string? clean = NoteStore.CleanKey(key);
            if (clean is null || session.Notes.Get(clean) is not { } one)
            {
                return $"no section \"{key}\"\n\n" + NoteIndex(session);
            }

            return $"## {clean}\n{one.Text}";
        }

        return NoteBodies(session, Math.Max(0, offset));
    }

    /// <summary>
    /// Resolves a target to a VA the way <see cref="Annotate"/> does — an address, a sub_ name, an
    /// existing name, or a managed <c>Type::Method</c> whose IL start it addresses — and checks it is
    /// inside the image. Returns false with the problem to hand back when it does not resolve.
    /// </summary>
    private static bool Resolve(BinarySession session, string target, out ulong va, out string? problem)
    {
        va = 0;
        problem = null;

        var resolved = Targets.Resolve(session, target);
        if (!resolved.Found)
        {
            if (ManagedMethodVa(session, target) is { } managedVa)
            {
                resolved = TargetResult.Of(managedVa);
            }
            else if (session.BytecodeIndex is not null && BytecodeTargets.Resolve(session, target) is { Found: true } named)
            {
                problem = $"{named.Describe()} has no single address; name a method as Namespace.Type::Method, or give an address";
                return false;
            }
            else
            {
                problem = resolved.Problem!;
                return false;
            }
        }

        if (!InsideImage(session, resolved.Va))
        {
            problem = $"0x{resolved.Va:X} is outside the image";
            return false;
        }

        va = resolved.Va;
        return true;
    }

    /// <summary>
    /// One address's record, whole: its name and full comment, its local names, every comment recorded
    /// inside the function it heads, and the note sections whose text names it. This is what a re-read
    /// would rediscover, without the re-read — and it does not truncate, because trusting the record is
    /// the whole point of reading it.
    /// </summary>
    private static string RenderAnnotation(BinarySession session, ulong va)
    {
        var analysis = session.Analysis!;
        var annotation = analysis.Annotations.Get(va);
        string name = analysis.NameFor(va);
        string by = annotation is { Source: AnnotationSource.Agent } ? "agent" : "user";
        string when = annotation?.Modified is { } m ? $", {m.ToLocalTime():yyyy-MM-dd HH:mm}" : string.Empty;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"0x{va:X}  {name}");
        if (annotation is not null && !annotation.IsEmpty)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  (by {by}{when})");
        }

        sb.Append('\n');

        if (annotation?.Comment is { Length: > 0 } comment)
        {
            sb.Append(CultureInfo.InvariantCulture, $"comment   {comment}\n");
        }

        if (annotation?.Locals is { Count: > 0 } locals)
        {
            sb.Append(CultureInfo.InvariantCulture, $"locals    {string.Join("  ", locals.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}"))}\n");
        }

        // Comments the previous reader left mid-function. These vanish from pseudo-C where the
        // instruction they sit on folds away, so they are exactly the work a re-read would miss.
        if (analysis.FunctionContaining(va) is { } fn && fn.EntryVa == va)
        {
            var inside = analysis.Annotations.Snapshot()
                .Where(e => e.Key > fn.EntryVa && e.Key < fn.EndVa && e.Value.Comment is { Length: > 0 })
                .OrderBy(e => e.Key)
                .ToList();

            for (int i = 0; i < inside.Count; i++)
            {
                var (at, note) = inside[i];
                string label = i == 0 ? "inside" : "      ";
                string src = note.Source == AnnotationSource.Agent ? "agent" : "user";
                sb.Append(CultureInfo.InvariantCulture, $"{label}    0x{at:X}  {note.Comment}  ({src})\n");
            }
        }

        // Note sections that name this function, so a truth recorded about the binary as a whole is
        // linked back to the place it is about.
        if (annotation?.Name is { Length: > 0 } named)
        {
            var mentions = session.Notes.Snapshot()
                .Where(s => s.Value.Text.Contains(named, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Key)
                .ToList();

            if (mentions.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"notes     {string.Join(", ", mentions)}  (read_notes)\n");
            }
        }

        if (annotation is null || annotation.IsEmpty)
        {
            sb.Append("nothing recorded here yet\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The section index — every key, who wrote it, when, and its size. Never cut by the budget.</summary>
    private static string NoteIndex(BinarySession session)
    {
        var sections = session.Notes.Snapshot();
        if (sections.Count == 0)
        {
            return "no sections yet";
        }

        var table = new TextTable(("section", 30), ("by", 5), ("modified", 16), ("chars", 6));
        foreach (var (key, note) in sections)
        {
            table.Add(
                key,
                note.Source == AnnotationSource.Agent ? "agent" : "user",
                note.Modified is { } m ? m.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty,
                note.Text.Length.ToString(CultureInfo.InvariantCulture));
        }

        return table.Render();
    }

    /// <summary>
    /// The index followed by section bodies from <paramref name="offset"/>, as many as the budget
    /// allows. The index is always whole; only the bodies are paged, so "read me everything" is a
    /// couple of calls rather than a silent clip.
    /// </summary>
    private static string NoteBodies(BinarySession session, int offset)
    {
        var sections = session.Notes.Snapshot();
        var sb = new StringBuilder();
        sb.Append(NoteIndex(session)).Append('\n');

        int room = Budget.MaxChars - sb.Length - 120;   // keep space for the continuation notice
        int shown = 0;
        for (int i = offset; i < sections.Count; i++)
        {
            var (key, note) = sections[i];
            string block = $"\n## {key}\n{note.Text}\n";
            if (sb.Length + block.Length > room && shown > 0)
            {
                break;
            }

            sb.Append(block);
            shown++;
        }

        int remaining = sections.Count - offset - shown;
        if (remaining > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"\n-- {remaining} more section{(remaining == 1 ? string.Empty : "s")}: read_notes(offset={offset + shown}) or read_notes(key=...) --");
        }

        return sb.ToString();
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
        => session.Image.VaToRva(va) is { } rva && rva < session.Image.ImageSize;

    /// <summary>
    /// The VA where a <c>Namespace.Type::Method</c>'s IL begins, or null when the text is not a managed
    /// method of this assembly — a native binary, a type or a field, or a name that resolves to nothing.
    /// The same base-plus-RVA the listing addresses a method body by, so a note lands on its first line.
    /// </summary>
    private static ulong? ManagedMethodVa(BinarySession session, string target)
    {
        if (session.Bodies is not { } bodies)
        {
            return null;
        }

        var resolved = BytecodeTargets.Resolve(session, target);
        return resolved.DotNetMember is { Handle: var handle } && bodies.Of(handle) is { } body
            ? session.Image.ImageBase + body.RvaOf(0)
            : null;
    }

    // --- a reading keyed by member (a JAR) ----------------------------------

    /// <summary>
    /// Names or comments a class, method or field, keyed on what the reading says identifies it for good. The
    /// target is taken the way read_function takes it, so the name an agent just read is the name it annotates.
    /// </summary>
    private string AnnotateMember(BinarySession session, MemberAnnotationStore members, string target, string? name, string? comment)
    {
        var found = BytecodeTargets.Resolve(session, target);
        if (!found.Found)
        {
            return found.Problem ?? $"'{target}' is not a type or member in this {session.Bytecode?.Noun ?? "file"}";
        }

        if (session.Bytecode!.AnnotationKey(found.Type, found.Member) is not { } key)
        {
            return $"{found.Describe()} cannot be annotated";
        }

        string what = found.Describe();
        string? was = members.Get(key)?.Name;
        var sb = new StringBuilder();
        if (name is not null)
        {
            string? applied = members.SetName(key, name);
            sb.Append(applied is null
                ? $"{what} has its own name back"
                : $"{what} is now called {applied}{(was is null || was == applied ? string.Empty : $" (was {was})")}");
        }

        if (comment is not null)
        {
            string? applied = members.SetComment(key, comment);
            sb.Append(sb.Length > 0 ? "; " : string.Empty);
            sb.Append(applied is null ? "comment cleared" : $"comment: {applied}");
        }

        return sb.Append(Persist(session)).ToString();
    }

    private static string ListMembers(BinarySession session, MemberAnnotationStore members, string source, string? query, int offset, int limit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);
        string? needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        var all = members.Snapshot();
        var matching = all
            .Where(e => source switch
            {
                "agent" => e.Value.Source == AnnotationSource.Agent,
                "user" => e.Value.Source == AnnotationSource.User,
                _ => true,
            })
            .Where(e => needle is null
                        || e.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || (e.Value.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (e.Value.Comment?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        var table = new TextTable(("member", 70), ("by", 5), ("name", 36), ("comment", 80));
        foreach (var (key, annotation) in matching.Skip(offset).Take(limit))
        {
            table.Add(key, annotation.Source == AnnotationSource.Agent ? "agent" : "user", annotation.Name ?? string.Empty, annotation.Comment ?? string.Empty);
        }

        int returned = Math.Max(0, Math.Min(limit, matching.Count - offset));
        string paging = needle is null ? $"offset={offset + returned}" : $"query=\"{needle}\", offset={offset + returned}";
        string? next = offset + returned < matching.Count ? $"list_annotations({paging})" : null;
        return Budget.Clip(table.Render(needle is null ? "nothing has been named yet" : $"nothing matches \"{needle}\"") + '\n'
                           + TextTable.Meta(returned, matching.Count, all.Count, "annotations", next, $"source={source}")
                           + Where(session));
    }

    private static string ReadMember(BinarySession session, MemberAnnotationStore members, string target)
    {
        var found = BytecodeTargets.Resolve(session, target);
        if (!found.Found)
        {
            return found.Problem ?? $"'{target}' is not a type or member in this {session.Bytecode?.Noun ?? "file"}";
        }

        string? key = session.Bytecode!.AnnotationKey(found.Type, found.Member);
        var sb = new StringBuilder();
        sb.Append(found.Describe());
        var annotation = key is null ? null : members.Get(key);
        if (annotation is null || annotation.IsEmpty)
        {
            sb.Append('\n');
        }
        else
        {
            string by = annotation.Source == AnnotationSource.Agent ? "agent" : "user";
            string when = annotation.Modified is { } m ? $", {m.ToLocalTime():yyyy-MM-dd HH:mm}" : string.Empty;
            sb.Append(CultureInfo.InvariantCulture, $"  (by {by}{when})\n");
            if (annotation.Name is { } name)
            {
                sb.Append(CultureInfo.InvariantCulture, $"name      {name}\n");
            }

            if (annotation.Comment is { } comment)
            {
                sb.Append(CultureInfo.InvariantCulture, $"comment   {comment}\n");
            }
        }

        // What was recorded about the members of a type, when a type is asked about: the work a re-read would miss.
        if (found.Member is null && key is not null)
        {
            var inside = members.Snapshot()
                .Where(e => e.Key.StartsWith(key + ".", StringComparison.Ordinal))
                .ToList();
            foreach (var (memberKey, note) in inside)
            {
                string text = string.Join("; ", new[] { note.Name, note.Comment }.Where(s => s is not null));
                sb.Append(CultureInfo.InvariantCulture, $"member    {memberKey[(key.Length + 1)..]}  {text}\n");
            }
        }

        if (annotation?.Name is { Length: > 0 } named)
        {
            var mentions = session.Notes.Snapshot()
                .Where(s => s.Value.Text.Contains(named, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Key)
                .ToList();
            if (mentions.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"notes     {string.Join(", ", mentions)}  (read_notes)\n");
            }
        }

        if (annotation is null || annotation.IsEmpty)
        {
            sb.Append("nothing recorded here yet\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

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
            string? path = session.Save(session.Image, session.Analysis?.Annotations ?? new AnnotationStore());
            return path is null ? string.Empty : $"\nsaved to {path}";
        }
        catch (IOException ex)
        {
            return $"\nNOT SAVED: {ex.Message}. The name is set for this session but will not outlive it.";
        }
    }

    /// <summary>
    /// Writes a note through, tolerating a session with no native analysis — a purely managed assembly
    /// has no annotation store, but its notes are still worth keeping. An empty store touches nothing
    /// in the file's annotations, so passing one is safe and the merge preserves everything else.
    /// </summary>
    private string PersistNotes(BinarySession session)
    {
        try
        {
            var annotations = session.Analysis?.Annotations ?? new Spydate.Core.Project.AnnotationStore();
            string? path = session.Save(session.Image, annotations);
            return path is null ? string.Empty : $"\nsaved to {path}";
        }
        catch (IOException ex)
        {
            return $"\nNOT SAVED: {ex.Message}. The note is set for this session but will not outlive it.";
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
