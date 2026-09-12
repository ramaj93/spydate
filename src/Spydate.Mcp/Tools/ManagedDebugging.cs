using System.Globalization;
using System.Text;
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

    internal static string Run(IManagedDebugControl debug, string action)
    {
        string what = action.Trim().ToLowerInvariant().Replace('-', '_');

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
    /// Sets a breakpoint on a method, with an optional IL offset written the way a listing writes it.
    ///
    /// The method is resolved through the same names <c>read_function</c> takes, so an agent that has
    /// just read a method can break on it without translating anything.
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

        var found = ManagedTargets.Resolve(session, text);
        if (!found.Found)
        {
            return found.Problem ?? $"'{text}' is not a method in this assembly";
        }

        if (found.Member is not { } member || member.Handle.Kind != System.Reflection.Metadata.HandleKind.MethodDefinition)
        {
            return $"{found.Describe()} is not a method. A breakpoint goes in code, so name one of its methods.";
        }

        uint token = (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(member.Handle);
        string module = System.IO.Path.GetFileName(session.Path);

        if (debug.SetBreakpoint(module, token, offset, on) is { } problem)
        {
            return problem;
        }

        return on
            ? $"breakpoint in {found.Describe()} at IL_{offset:X4}"
            : $"cleared the breakpoint in {found.Describe()} at IL_{offset:X4}";
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
