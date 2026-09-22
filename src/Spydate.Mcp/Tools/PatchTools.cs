using System.ComponentModel;
using ModelContextProtocol.Server;
using Spydate.Core.Project;
using Spydate.Decompiler.Managed;
using Spydate.Disassembly;
using Spydate.Mcp.Rendering;
using Spydate.Mcp.Session;

namespace Spydate.Mcp.Tools;

/// <summary>
/// Changing what the code does, as a record rather than as an act.
///
/// A patch here is written to the <c>.spydate</c> project and nowhere else. There is deliberately no
/// tool that writes a patched binary: the server's whole write surface is that one project file, and
/// that is the property which makes it safe to point an agent at a hostile executable. An agent that
/// could assemble bytes and then ask for them to be written out as a runnable file would be a
/// different and much worse thing than one that proposes edits a person applies from the window.
///
/// So the loop is: the agent records patches and says why, the analyst reads them in the Patches
/// tab, and File ▸ Save patched copy is a decision a person makes.
/// </summary>
[McpServerToolType]
public sealed class PatchTools
{
    private const int DefaultLimit = 40;
    private const int MaxLimit = 200;

    private readonly SessionStore _store;
    private readonly McpOptions _options;

    public PatchTools(SessionStore store, McpOptions options)
    {
        _store = store;
        _options = options;
    }

    [McpServerTool(Name = "patch")]
    [Description("Record a byte change at an address, given as an instruction to assemble (\"xor eax, eax; ret\") or as raw bytes (\"bytes: 90 90\"). Replaces whole instructions, padding with NOPs. Recorded in the project, and written into a stopped process. Nothing writes a patched binary.")]
    public string Patch(
        [Description("Address, sub_XXXX, or an existing name.")] string target,
        [Description("Instruction(s) to assemble, or bytes: followed by hex.")] string instruction,
        [Description("Why this patch exists. Worth giving: it is what the next reader sees.")] string? comment = null,
        [Description("False to try it live only, recording nothing.")] bool keep = true,
        [Description("Write IL that will not verify anyway. Read the refusal first.")] bool force = false)
    {
        if (_options.ReadOnly)
        {
            return ReadOnlyRefusal;
        }

        if (_store.Current is not { } session)
        {
            return SessionTools.NothingOpen;
        }

        if (session is not { Analysis: not null } && session.Managed is null)
        {
            return $"there is nothing to patch: {session.Pe.Machine} is not a machine this disassembles";
        }

        var resolved = Targets.Resolve(session, target);
        if (!resolved.Found)
        {
            return resolved.Problem!;
        }

        // A hypothesis: into the process, into nothing else. Worth having separately from a recorded
        // patch because most guesses are wrong, and a project full of reverted guesses is a worse
        // record than one holding only what turned out to be true.
        if (!keep)
        {
            if (!_options.AllowDebug || _store.Debug is not { } debug)
            {
                return "trying a patch live changes a running process, so it needs debugging turned on: "
                       + "start the server with --allow-debug, or use the assistant panel in the window.";
            }

            if (debug.TryPatch(resolved.Va, instruction, comment) is { } refused)
            {
                return refused;
            }

            return $"trying 0x{resolved.Va:X} in the running process. Nothing is recorded: "
                   + "revert_patch takes it back out, patch writes it into the project.";
        }

        // Which assembler, decided by where the address lands rather than by what the file claims
        // to be. A mixed-mode assembly has both kinds of code in it, and the CLR header's ILOnly bit
        // is one bit that untrusted input is free to get wrong; whether a method body's IL covers
        // this address is a fact about the bytes.
        var proposal = InIl(session, resolved.Va)
            ? IlPatches.Assemble(session.Managed!, session.Bodies!, session.Pe, resolved.Va, instruction, force)
            : InstructionPatches.Assemble(session.Analysis!, resolved.Va, instruction);

        if (!proposal.Ok)
        {
            return proposal.Problem!;
        }

        var patch = proposal.Patch! with { Comment = comment ?? proposal.Patch!.Comment };

        try
        {
            session.Patches.Source = AnnotationSource.Agent;
            session.Patches.Set(patch.Rva, patch);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        return $"patched 0x{session.Image.RvaToVa(patch.Rva):X}: {patch.OriginalHex} -> {patch.Hex}"
               + $"\n{patch.Comment}"
               + "\nrecorded in the project. Nothing is written to any binary; a person applies it from the window."
               + Precompiled(session)
               + Save(session);
    }

    [McpServerTool(Name = "list_patches")]
    [Description("Every patch recorded or tried live: where, what it replaces, whether it is on, and why.")]
    public string ListPatches(
        [Description("Rows to skip, for paging.")] int offset = 0,
        [Description("Rows to return, at most 200.")] int limit = DefaultLimit)
    {
        if (_store.Current is not { } session)
        {
            return SessionTools.NothingOpen;
        }

        var tried = _store.Debug?.Snapshot().LivePatches ?? [];
        var all = session.Patches.Snapshot();
        if (all.Count == 0 && tried.Count == 0)
        {
            return "no patches";
        }

        int take = Math.Clamp(limit, 1, MaxLimit);
        var page = all.Skip(Math.Max(0, offset)).Take(take).ToList();

        var table = new TextTable(("address", 18), ("was", 24), ("now", 24), ("on", 3), ("why", 60));
        foreach (var patch in page)
        {
            table.Add(
                $"0x{session.Image.RvaToVa(patch.Rva):X}",
                patch.OriginalHex,
                patch.Hex,
                patch.Enabled ? "yes" : "no",
                patch.Comment ?? string.Empty);
        }

        string recorded = all.Count == 0
            ? "no recorded patches"
            : $"{page.Count} of {all.Count} patch(es)\n{table.Render()}";

        if (tried.Count == 0)
        {
            return recorded;
        }

        // Kept apart, because the difference is the whole point: these are in the process and in no
        // file, and they are gone when it stops.
        var live = new TextTable(("address", 18), ("was", 24), ("now", 24), ("why", 60));
        foreach (var (_, va, was, now, why) in tried)
        {
            live.Add($"0x{va:X}", was, now, why ?? string.Empty);
        }

        return $"{recorded}\n\ntried live, not recorded ({tried.Count}):\n{live.Render()}";
    }

    [McpServerTool(Name = "revert_patch")]
    [Description("Remove a patch: the file's bytes go back, in a stopped process too.")]
    public string RevertPatch([Description("Address of the patch, as list_patches shows it.")] string target)
    {
        if (_options.ReadOnly)
        {
            return ReadOnlyRefusal;
        }

        if (_store.Current is not { } session)
        {
            return SessionTools.NothingOpen;
        }

        var resolved = Targets.Resolve(session, target);
        if (!resolved.Found)
        {
            return resolved.Problem!;
        }

        if (session.Image.VaToRva(resolved.Va) is not { } rva)
        {
            return $"0x{resolved.Va:X} is outside the image";
        }

        // Whatever covers the address, not only a patch that starts exactly there — an address read
        // back off a listing may be the middle of a patched run.
        var covering = session.Patches.Covering(rva);
        if (covering is null)
        {
            // Nothing recorded covers it, so this is the other kind: something tried live and never
            // written down. Undoing that is the same word to whoever is asking.
            if (_store.Debug is { } debug && debug.UndoPatch(rva))
            {
                return $"took the live patch at 0x{resolved.Va:X} back out; nothing was recorded";
            }

            return $"no patch covers 0x{resolved.Va:X}";
        }

        session.Patches.Remove(covering.Rva);
        return $"reverted the patch at 0x{session.Image.RvaToVa(covering.Rva):X}" + Save(session);
    }

    /// <summary>
    /// Whether a patched copy of this file would actually run the bytes that were changed.
    ///
    /// A ReadyToRun image carries native code compiled ahead of time beside the IL, and the runtime
    /// prefers it. Patching the IL of such a method is not wrong — the bytes really do change — it
    /// simply may have no effect at all, which is the worst way for a patch to fail: everything
    /// reports success and the program behaves exactly as it did.
    /// </summary>
    private static string Precompiled(BinarySession session)
        => session.Pe.ClrHeader is { ManagedNativeHeader.Size: > 0 }
            ? "\nnote: this assembly is precompiled (ReadyToRun), so the runtime may run its native copy "
              + "rather than the IL you changed - the patch can be correct and still do nothing"
            : string.Empty;

    /// <summary>Whether an address is inside a method's IL, which is what decides how to assemble.</summary>
    private static bool InIl(BinarySession session, ulong va)
        => session.Bodies is { } bodies
           && session.Image.VaToRva(va) is { } rva
           && bodies.At(rva) is not null;

    internal const string ReadOnlyRefusal =
        "this server was started with --read-only, so no patches can be recorded. Everything else still works.";

    private static string Save(BinarySession session)
    {
        try
        {
            string? path = session.Save(session.Image, session.Analysis!.Annotations);
            return path is null ? string.Empty : $"\nsaved to {path}";
        }
        catch (IOException ex)
        {
            return $"\nnot saved: {ex.Message}";
        }
    }
}
