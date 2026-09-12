using System.Globalization;
using System.Reflection.Emit;
using Spydate.Core.PE;
using Spydate.Core.Project;
using Spydate.Disassembly;

namespace Spydate.Decompiler.Managed;

/// <summary>
/// Fitting assembled IL over the instructions already there.
///
/// The same rule as the native patcher and for the same reason: whole instructions, exactly as many
/// bytes as were covered, padded with <c>nop</c>. Nothing moves, so no metadata token shifts, no
/// method body changes length, and every offset anybody has written down still means what it did.
/// IL makes the padding exact rather than approximate — <c>nop</c> is one byte, so any shortfall is
/// fillable — which is the one respect in which this is easier than x86.
///
/// It is harder in the respect that matters more. The runtime verifies a method before running it,
/// so a patch that leaves the evaluation stack a different depth is not a program that behaves
/// differently; it is a program that will not start. See <see cref="IlStack"/>.
/// </summary>
public static class IlPatches
{
    /// <summary>
    /// Assembles <paramref name="text"/> and fits it over the IL at <paramref name="va"/>.
    ///
    /// <paramref name="force"/> writes it anyway when the stack depth would not survive. It exists
    /// because the check can be wrong in one direction — a signature it cannot read comes back as
    /// "cannot say" — and refusing outright would make an unreadable reference into a wall. It is
    /// not a way to skip reading the message.
    /// </summary>
    public static PatchProposal Assemble(
        ManagedAssembly assembly,
        ManagedBodies bodies,
        PeImage image,
        ulong va,
        string? text,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(image);

        if (image.VaToRva(va) is not { } rva)
        {
            return PatchProposal.Failed($"0x{va:X} is outside the image");
        }

        if (bodies.At(rva) is not { } body)
        {
            return PatchProposal.Failed(
                $"0x{va:X} is not inside any method's IL. Read the method with read_function(view=\"il\"), "
                + "which prints an address against every instruction.");
        }

        // Asked by walking from the start of the body, never by decoding at the address itself.
        // Decoding there always works: an operand byte is a perfectly good opcode, so 0x28 in the
        // middle of a token reads as a call and a patch placed on it would overwrite the tail of one
        // instruction and leave its head to be decoded against whatever followed. Only the walk
        // knows where instructions actually begin.
        int offset = body.OffsetOf(rva);
        if (!Il.TryCovering(body.Il, offset, out var covering))
        {
            return PatchProposal.Failed($"no instruction covers 0x{va:X}");
        }

        if (covering.Offset != offset)
        {
            return PatchProposal.Failed(
                $"0x{va:X} is inside IL_{covering.Offset:X4} ({covering.Op.Name}) rather than at its start. "
                + $"Patch 0x{image.ImageBase + body.RvaOf(covering.Offset):X} instead.");
        }

        var encoded = IlAssembler.Encode(text, offset);
        if (!encoded.Ok)
        {
            return PatchProposal.Failed(encoded.Problem!);
        }

        // Enough instructions to cover what was assembled, never part of one. An instruction left
        // half-overwritten decodes as whatever its tail spells against the bytes after it.
        int covered = 0;
        int taken = 0;
        foreach (var instruction in Il.Walk(body.Il))
        {
            if (instruction.Offset < offset)
            {
                continue;
            }

            covered += instruction.Length;
            taken++;
            if (covered >= encoded.Bytes.Length)
            {
                break;
            }
        }

        if (covered < encoded.Bytes.Length)
        {
            return PatchProposal.Failed(
                $"{encoded.Bytes.Length} bytes will not fit: only {covered} bytes of whole instructions "
                + $"are left in {Describe(assembly, body)} from IL_{offset:X4}");
        }

        var stack = new IlStack(assembly.Metadata);
        int? was = stack.Delta(body.Il, offset, covered, body.Method);
        int? now = stack.Delta([.. encoded.Bytes], 0, encoded.Bytes.Length, body.Method);

        if (Imbalance(was, now, taken, force) is { } refusal)
        {
            return PatchProposal.Failed(refusal);
        }

        byte[] bytes = new byte[covered];
        Array.Fill(bytes, Il.Nop);
        encoded.Bytes.CopyTo(bytes, 0);

        var present = image.ReadAtVa(va, covered);
        if (present.Length != covered)
        {
            return PatchProposal.Failed($"0x{va:X} is not in any section of the file");
        }

        byte[] original = present.ToArray();
        if (bytes.SequenceEqual(original))
        {
            return PatchProposal.Failed("that would change nothing");
        }

        int padding = covered - encoded.Bytes.Length;
        string what = taken == 1 ? "1 instruction" : $"{taken} instructions";
        string note = padding == 0 ? what : $"{what}, {padding} nop(s) padded";
        string caveat = was is null || now is null ? ", stack effect unchecked" : string.Empty;

        if (RemovesACall(body, offset, covered, encoded.Bytes))
        {
            // Depth is checked; types are not, and this is where the difference bites. Taking out
            // "call uint8[] ReadAllBytes(string)" is depth-neutral — one argument consumed, one
            // result produced — and still will not verify, because what is left on the stack is the
            // string that was going in rather than the array that was coming out. Saying so beats
            // implying a guarantee that only covers half the question.
            caveat += ", removes a call: the depth matches but what is left on the stack is its "
                      + "arguments rather than its result, so check the types";
        }

        return new PatchProposal(
            new Patch
            {
                Rva = body.RvaOf(offset),
                Bytes = bytes,
                Original = original,
                Comment = $"{text?.Trim()} ({note}{caveat}) in {Describe(assembly, body)}",
            },
            null);
    }

    /// <summary>
    /// Why the replacement will not verify, or null when it will.
    ///
    /// The message says the number, because the number is the fix: an agent told that it is one
    /// value short knows to add a <c>ldc.i4.0</c>, and an agent told only "invalid" knows nothing.
    /// </summary>
    private static string? Imbalance(int? was, int? now, int taken, bool force)
    {
        if (force)
        {
            return null;
        }

        if (was is null || now is null)
        {
            // Not a refusal. Something in the signatures could not be read, which is a fact about
            // the file rather than about the patch, and saying "no" to it would make an unreadable
            // reference into a wall. The patch is marked as unchecked instead.
            return null;
        }

        if (was == now)
        {
            return null;
        }

        int difference = now.Value - was.Value;
        string direction = difference > 0
            ? $"leaves {difference} more value(s) on the stack than"
            : $"leaves {-difference} fewer value(s) on the stack than";

        string fix = difference < 0
            ? " Push what is missing - ldc.i4.0, ldnull - or keep the instruction and change what it does."
            : " Drop what is spare with pop, or replace fewer instructions.";

        return $"that will not verify: the replacement {direction} the {(taken == 1 ? "instruction" : $"{taken} instructions")} "
               + "it covers, so the runtime will refuse the method with an InvalidProgramException rather than run it."
               + fix
               + " Pass force to write it anyway.";
    }

    /// <summary>Whether the replacement takes a call out without putting one back.</summary>
    private static bool RemovesACall(ManagedBody body, int offset, int covered, byte[] replacement)
    {
        bool gone = Il.Walk(body.Il)
            .Where(i => i.Offset >= offset && i.Offset < offset + covered)
            .Any(IsCall);

        return gone && !Il.Walk([.. replacement]).Any(IsCall);
    }

    private static bool IsCall(IlInstruction instruction)
        => instruction.Op.Value == OpCodes.Call.Value
           || instruction.Op.Value == OpCodes.Callvirt.Value
           || instruction.Op.Value == OpCodes.Newobj.Value
           || instruction.Op.Value == OpCodes.Calli.Value;

    private static string Describe(ManagedAssembly assembly, ManagedBody body)
    {
        try
        {
            var method = assembly.Metadata.GetMethodDefinition(body.Method);
            var type = assembly.Metadata.GetTypeDefinition(method.GetDeclaringType());
            string space = assembly.Metadata.GetString(type.Namespace);
            string name = assembly.Metadata.GetString(type.Name);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(space.Length == 0 ? name : $"{space}.{name}")}::{assembly.Metadata.GetString(method.Name)}");
        }
        catch (BadImageFormatException)
        {
            return $"the method at 0x{body.BodyRva:X}";
        }
    }
}
