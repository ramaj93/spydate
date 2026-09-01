using System.ComponentModel;
using ModelContextProtocol.Server;
using Spydate.Core.Project;
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
    [Description("Record a byte change at an address, given as an instruction to assemble (\"xor eax, eax; ret\") or as raw bytes (\"bytes: 90 90\"). Replaces whole instructions, padding with NOPs. Recorded in the project only - nothing writes a patched binary.")]
    public string Patch(
        [Description("Address, sub_XXXX, or an existing name.")] string target,
        [Description("Instruction(s) to assemble, or bytes: followed by hex.")] string instruction,
        [Description("Why this patch exists. Worth giving: it is what the next reader sees.")] string? comment = null)
    {
        if (_options.ReadOnly)
        {
            return ReadOnlyRefusal;
        }

        if (_store.Current is not { Analysis: { } analysis } session)
        {
            return SessionTools.NothingOpen;
        }

        var resolved = Targets.Resolve(session, target);
        if (!resolved.Found)
        {
            return resolved.Problem!;
        }

        var proposal = InstructionPatches.Assemble(analysis, resolved.Va, instruction);
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
               + Save(session);
    }

    [McpServerTool(Name = "list_patches")]
    [Description("Every recorded patch: where, what it replaces, whether it is switched on, and why.")]
    public string ListPatches(
        [Description("Rows to skip, for paging.")] int offset = 0,
        [Description("Rows to return, at most 200.")] int limit = DefaultLimit)
    {
        if (_store.Current is not { } session)
        {
            return SessionTools.NothingOpen;
        }

        var all = session.Patches.Snapshot();
        if (all.Count == 0)
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

        return $"{page.Count} of {all.Count} patch(es)\n{table.Render()}";
    }

    [McpServerTool(Name = "revert_patch")]
    [Description("Remove a recorded patch, so those bytes go back to what the file says.")]
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
            return $"no patch covers 0x{resolved.Va:X}";
        }

        session.Patches.Remove(covering.Rva);
        return $"reverted the patch at 0x{session.Image.RvaToVa(covering.Rva):X}" + Save(session);
    }

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
