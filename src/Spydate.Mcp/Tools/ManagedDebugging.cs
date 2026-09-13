using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using Spydate.Decompiler.Managed;
using Spydate.Mcp.Rendering;
using Spydate.Mcp.Session;

namespace Spydate.Mcp.Tools;

/// <summary>
/// The managed half of the four debug tools.
///
/// Kept beside them rather than as tools of its own, on the same reasoning as everything else in
/// this surface: a schema is sent on every turn of every conversation, so a parallel
/// <c>managed_debug_*</c> family would be paid for by every session that never opens a .NET file.
/// The verbs are the same verbs — start, break, step, look — and only what they take differs.
///
/// What differs is the whole point, though. A managed breakpoint is a method and an offset into its
/// IL, not an address; a managed stop reports what the frame holds, not what the registers do. So
/// nothing here pretends to be the native answer in different words.
/// </summary>
internal static class ManagedDebugging
{
    /// <summary>How long to let an action land before reporting where it got.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>How long "wait" waits, for the case the settle cannot cover.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    internal static string Run(IManagedDebugControl debug, string action, uint? thread = null)
    {
        string what = action.Trim().ToLowerInvariant().Replace('-', '_');

        // A thread to act on is chosen before acting, because stepping follows the selected thread:
        // "step thread 5" means make 5 the one being looked at, then step it. Only meaningful while
        // stopped, which is also the only time a step is.
        if (thread is { } wanted && what is "step" or "step_over" or "step_out"
            && debug.SelectThread(wanted) is { } refused)
        {
            return refused;
        }

        // Asked before acting, because the answer decides whether acting means anything. Moving a
        // process that is not stopped does nothing and reports success, which is a loop.
        if (what is "continue" or "step" or "step_over" or "step_out")
        {
            var now = debug.Snapshot();
            if (now.State != "stopped")
            {
                return now.State switch
                {
                    "exited" => $"it has exited — {now.Status} Use start to run it again." + Recent(now),
                    "running" => "it is running and has not stopped. Use wait to be there when it does, "
                                 + "or set a breakpoint somewhere it will reach." + Recent(now),
                    _ => "nothing is running. Use start." + Recent(now),
                };
            }
        }

        switch (what)
        {
            case "start":
                // Held before anything managed runs, always. It is the only moment at which a
                // breakpoint is certainly in place before the code it is about, and an agent that
                // wanted the program to just go can say so with one continue.
                if (debug.Start(holdAtStart: true) is { } problem)
                {
                    return $"did not start: {problem}";
                }

                break;

            case "stop":
                debug.Stop();
                return "stopped, and the process is gone";

            case "continue":
                debug.Continue();
                break;

            case "step":
                if (debug.Step(into: true) is { } into)
                {
                    return into;
                }

                break;

            case "step_over":
                if (debug.Step(into: false) is { } over)
                {
                    return over;
                }

                break;

            case "step_out":
                if (debug.StepOut() is { } out_)
                {
                    return out_;
                }

                break;

            case "wait":
                var idle = debug.Snapshot();
                if (idle.State != "running")
                {
                    return idle.State switch
                    {
                        "stopped" => $"it is already stopped — {idle.Status} Nothing further happens "
                                     + "until you continue or step." + Recent(idle),
                        "exited" => $"it has exited — {idle.Status}" + Recent(idle),
                        _ => "nothing is running, so there is nothing to wait for. Use start." + Recent(idle),
                    };
                }

                break;

            // Named rather than silently missing. Both are real gaps and an agent that asked for one
            // needs to know it did not happen, not to read a state that looks like it did.
            case "pause":
                return "pausing a .NET process is not implemented here. Set a breakpoint somewhere it "
                       + "will reach, or stop it.";

            case "run_to":
                return "run_to is not implemented for .NET. Use debug_break on the method, then continue.";

            default:
                return $"no such action \"{action}\". Use start, stop, continue, step, step_over, step_out or wait.";
        }

        var timeout = what == "wait" ? Patience : Settle;
        if (debug.WaitUntilStopped(timeout))
        {
            return Describe(debug.Snapshot());
        }

        var running = debug.Snapshot();
        return $"still running after {timeout.TotalSeconds:0}s and it has not stopped. Use wait to keep "
               + "waiting, or set a breakpoint somewhere it will reach." + Recent(running);
    }

    /// <summary>
    /// Sets a breakpoint on a method, named either the way <c>read_function</c> names it —
    /// <c>Type::Method</c>, with an optional <c>+IL_7</c> offset — or by a listing address, which the
    /// managed IL view prints against every instruction. An address is turned into the method it falls
    /// in and the offset within it, because a managed breakpoint is a method and an IL offset, never an
    /// address: that is what survives the method being recompiled, and it is all the runtime will take.
    /// </summary>
    internal static string Break(BinarySession session, IManagedDebugControl debug, string target, bool on)
    {
        string text = target.Trim();
        uint offset = 0;

        // "Type::Method+IL_7", which is how the listing writes a place inside a method. Split before
        // resolving, because the resolver knows nothing about offsets.
        int plus = text.LastIndexOf('+');
        if (plus > 0)
        {
            string tail = text[(plus + 1)..].Trim();
            if (tail.StartsWith("IL_", StringComparison.OrdinalIgnoreCase))
            {
                tail = tail[3..];
            }

            if (uint.TryParse(tail, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
            {
                offset = parsed;
                text = text[..plus].Trim();
            }
        }

        uint token;
        string what;
        string module = System.IO.Path.GetFileName(session.Path);

        var found = ManagedTargets.Resolve(session, text);
        if (found.Found)
        {
            if (found.Member is not { } member || member.Handle.Kind != HandleKind.MethodDefinition)
            {
                return $"{found.Describe()} is not a method. A breakpoint goes in code, so name one of its methods.";
            }

            token = (uint)MetadataTokens.GetToken(member.Handle);
            what = $"{found.Describe()} at IL_{offset:X4}";
        }
        else if (ElsewhereByName(found, text) is var (typeName, methodName))
        {
            // Not in the opened assembly, shaped like a method, and not a near-miss of an opened type —
            // so a Type::Method in another assembly, a framework one most often, resolved as the modules
            // load. A typo of an opened name (which the resolver would offer a fix for) is left to that
            // fix rather than turned into a breakpoint that waits for an assembly that never comes.
            return debug.SetBreakpointByName(typeName, methodName, offset, on);
        }
        else if (AsAddress(session, target) is { } located)
        {
            // A listing address rather than a name — the whole target, since an address carries its
            // own offset and never a +IL_ suffix. It may be in the opened module or a referenced one,
            // so the breakpoint goes into whichever module the address resolved in.
            token = located.Token;
            offset = located.Offset;
            module = located.Module;
            what = $"{located.Method} at IL_{offset:X4} (0x{located.Va:X})";
        }
        else if (Targets.Resolve(session, target) is { Found: true } address)
        {
            // A real address, but AsAddress could not turn it into one method. If it is outside the
            // opened module's own span it is in another assembly — where an address cannot name a
            // module on its own, because assemblies share an image base — so it is named by method
            // instead. Inside the opened module it simply is not in any method's IL.
            ulong start = session.Image.ImageBase;
            ulong end = start + session.Image.OptionalHeader.SizeOfImage;
            return address.Va < start || address.Va >= end
                ? $"0x{address.Va:X} is not in the opened module — it is in another assembly, where a "
                  + "breakpoint is set by name (Type::Method, or Type::Method+IL_7) rather than by address: "
                  + "an address does not name a module, since assemblies commonly share an image base."
                : $"0x{address.Va:X} is not inside any method's IL, so there is no managed method to break in there";
        }
        else
        {
            // Neither a name nor an address. The name failure is the more useful to report, since an
            // agent that meant an address rarely mistypes one into a method name.
            return found.Problem
                   ?? $"'{text}' is not a method in this assembly, nor an address inside one's IL";
        }

        if (debug.SetBreakpoint(module, token, offset, on) is { } problem)
        {
            return problem;
        }

        return on ? $"breakpoint in {what}" : $"cleared the breakpoint in {what}";
    }

    /// <summary>
    /// The type and method of a <c>Type::Method</c> that belongs to another assembly, or null when the
    /// text is not that: not shaped like a method, or a name the opened assembly nearly has — a typo it
    /// would rather suggest a fix for than defer forever. The signal is the resolver's own message: a
    /// suggestion ("Did you mean") or a real opened type missing the method ("has no member") both mean
    /// the name was aimed at the opened assembly, so it is not sent off to wait for another one.
    /// </summary>
    private static (string Type, string Method)? ElsewhereByName(ManagedTarget found, string text)
    {
        int mark = text.IndexOf("::", StringComparison.Ordinal);
        if (mark <= 0 || text[(mark + 2)..].Trim() is not { Length: > 0 } method)
        {
            return null;
        }

        if (found.Problem is { } problem
            && (problem.Contains("Did you mean", StringComparison.Ordinal)
                || problem.Contains("has no member", StringComparison.Ordinal)))
        {
            return null;
        }

        return (text[..mark].Trim(), method);
    }

    /// <summary>Where a breakpoint lands, when a target was a listing address inside a method's IL.</summary>
    private readonly record struct AddressBreak(ulong Va, uint Token, uint Offset, string Method, string Module);

    /// <summary>
    /// A listing address turned into the module, method and offset it falls in, or null when it is not
    /// an address or does not land inside any method's IL. The opened assembly is tried first, then
    /// each referenced one against its own image base — an address in a framework or dependency module,
    /// which carries the same <c>ImageBase + RVA</c> the opened one does. The body map is the only
    /// thing that can do this, the same map the IL view uses to print the addresses in the first place.
    /// </summary>
    private static AddressBreak? AsAddress(BinarySession session, string target)
    {
        var resolved = Targets.Resolve(session, target);
        if (!resolved.Found)
        {
            return null;
        }

        ulong va = resolved.Va;

        if (session.Image.VaToRva(va) is { } rva && session.Bodies?.At(rva) is { } body)
        {
            return new AddressBreak(va, (uint)MetadataTokens.GetToken(body.Method), (uint)body.OffsetOf(rva),
                MethodName(session.Managed, body.Method), System.IO.Path.GetFileName(session.Path));
        }

        // A referenced module, by its own image base. This only works when exactly one reference lays
        // claim to the address: managed DLLs share a base far too often — 0x400000 and 0x180000000 are
        // both common defaults — for an address to name a module on its own, so a hit in two of them is
        // no answer at all. When it is ambiguous the caller is told to name it by method instead, which
        // is unambiguous. Requiring a method's IL there, not merely the address range, narrows it.
        AddressBreak? unique = null;
        if (session.Managed is { } managed)
        {
            foreach (var reference in managed.References)
            {
                if (managed.Resolve(reference) is not { } assembly || assembly.ImageBase == 0 || va < assembly.ImageBase)
                {
                    continue;
                }

                uint refRva = (uint)(va - assembly.ImageBase);
                if (session.BodiesFor(assembly).At(refRva) is { } refBody)
                {
                    if (unique is not null)
                    {
                        return null;   // more than one module has code there — ambiguous, so no answer
                    }

                    unique = new AddressBreak(va, (uint)MetadataTokens.GetToken(refBody.Method), (uint)refBody.OffsetOf(refRva),
                        MethodName(assembly, refBody.Method), assembly.ModuleName);
                }
            }
        }

        return unique;
    }

    /// <summary>A method's name as <c>Namespace.Type::Method</c>, for a message about an address.</summary>
    private static string MethodName(ManagedAssembly? assembly, MethodDefinitionHandle handle)
    {
        if (assembly is null)
        {
            return $"method 0x{MetadataTokens.GetToken(handle):X8}";
        }

        try
        {
            var metadata = assembly.Metadata;
            var method = metadata.GetMethodDefinition(handle);
            var type = metadata.GetTypeDefinition(method.GetDeclaringType());
            string space = metadata.GetString(type.Namespace);
            string name = metadata.GetString(type.Name);
            return $"{(space.Length == 0 ? name : $"{space}.{name}")}::{metadata.GetString(method.Name)}";
        }
        catch (BadImageFormatException)
        {
            return $"method 0x{MetadataTokens.GetToken(handle):X8}";
        }
    }

    /// <summary>
    /// What a managed stop is: where, and what the frame is holding.
    ///
    /// The values are the part with no native equivalent and the reason for the whole exercise. A
    /// native stop reports registers and stack words and leaves the reader to work out which of them
    /// is the path being opened; here the runtime knows, so this says.
    /// </summary>
    internal static string Describe(ManagedSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.Append(snapshot.State);
        if (snapshot.Where is { Length: > 0 } at)
        {
            sb.Append(" at ").Append(at);
            if (snapshot.Mapping is { Length: > 0 } mapping && mapping != "exact")
            {
                // Said every time it is not exact. A frame in a prologue or in code the JIT
                // reordered maps approximately, and an offset read as exact when it is not points at
                // the wrong line with no sign that it is wrong.
                sb.Append(" (").Append(mapping).Append(" mapping)");
            }
        }

        sb.Append(" — ").Append(snapshot.Status).Append('\n');

        if (snapshot.Threads.Count > 0)
        {
            sb.Append("\nthreads:\n");
            foreach (string thread in snapshot.Threads)
            {
                sb.Append("  ").Append(thread).Append('\n');
            }
        }

        Slots(sb, "arguments", snapshot.Arguments);
        Slots(sb, "locals", snapshot.Locals);

        if (snapshot.Breakpoints.Count > 0)
        {
            sb.Append('\n').Append("breakpoints:\n");
            foreach (string breakpoint in snapshot.Breakpoints)
            {
                sb.Append("  ").Append(breakpoint).Append('\n');
            }
        }

        if (snapshot.Modules.Count > 0)
        {
            sb.Append('\n').Append(CultureInfo.InvariantCulture, $"{snapshot.Modules.Count} modules loaded\n");
        }

        sb.Append(Recent(snapshot));
        return Budget.Clip(sb.ToString().TrimEnd());
    }

    private static void Slots(StringBuilder sb, string label, IReadOnlyList<ManagedSlot> slots)
    {
        if (slots.Count == 0)
        {
            return;
        }

        sb.Append('\n').Append(label).Append(":\n");
        foreach (var slot in slots)
        {
            sb.Append("  ").Append(slot).Append('\n');
        }
    }

    private static string Recent(ManagedSnapshot snapshot)
        => snapshot.Recent.Count == 0
            ? string.Empty
            : "\n\nwhat it has done:\n" + string.Join("\n", snapshot.Recent.TakeLast(12).Select(line => $"  {line}"));
}
