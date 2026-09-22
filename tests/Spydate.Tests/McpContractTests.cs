using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Spydate.Mcp;

namespace Spydate.Tests;

/// <summary>
/// The shape of the tool surface itself, checked by reflection rather than by review.
///
/// Every tool's name, description and parameter descriptions are sent to the agent on connect and
/// stay in its context for the whole session — the manifest is a standing cost paid before any work
/// is done. It is also the only documentation the agent ever gets, so a missing description is not
/// untidiness, it is a tool the model will use wrongly or not at all.
/// </summary>
public class McpContractTests
{
    /// <summary>
    /// Total manifest size. Not a hard limit anyone imposed — a guard against it quietly doubling,
    /// which is what happens when tools accumulate and nobody is counting.
    ///
    /// Raised from 6,000 when the tools learned to read .NET assemblies. The old figure was set when
    /// the surface described one kind of binary and it described it exactly: the manifest stood at
    /// 5,999 characters, so every word about managed code had to be paid for by deleting a word
    /// about native code. That is the wrong trade — the answer to a second kind of file is a second
    /// sentence, not a worse description of the first — and the honest move is to move the number
    /// once, on purpose, rather than to shave real descriptions until they fit a figure nobody
    /// chose for this reason. It bought a mention of C#/IL in two tools and nothing else: Phase 5
    /// deliberately adds no new tool, because a schema only the .NET sessions use is still sent to
    /// every session that never opens one.
    ///
    /// Raised again to 6,600 for mixed mode — debugging a .NET program with the native loop, so a
    /// breakpoint can go into one of its own managed methods or a native DLL it loads. That is a third
    /// thing a binary can be to this server, and by the same argument it earns one sentence in
    /// debug_break — a managed address becomes a managed breakpoint over the native loop — rather than
    /// a shaved description elsewhere. Still no new tool: debug_break already routes by address, and
    /// mixed mode only changes what a managed address there means.
    ///
    /// Raised to 7,300 for debug_config — the first new tool since these were counted, and the reason
    /// the rule bends here rather than breaks. It gives the agent the run configuration itself: reading
    /// and changing the engine, executable, arguments, working directory and break point that a person
    /// otherwise sets in the Debug Program dialog — including switching a .NET target between the
    /// managed and native engines. That is a capability, not a description, and no sentence shaved off
    /// another tool would have bought it; a whole tool is the honest cost, paid once and on purpose.
    ///
    /// Raised to 7,700 for read_file — a raw-bytes reader for a file that is not a PE, so a blob the
    /// analyst points at (a resource, a .inx, an unknown container beside the binary) can be probed as
    /// hex or text instead of being a wall. Everything else here assumes a PE has been parsed into an
    /// image; this is the one tool that reads a file the server cannot open, which is a fourth thing a
    /// file can be to it and, like the engines, earns its own tool rather than a strained overload of
    /// read_data.
    ///
    /// Raised to 8,900 for notes — knowledge about the binary that has no address at all. An
    /// annotation lives at a place; how the strings are encoded, what a subsystem is for, a dead end
    /// not worth chasing again do not, and until now had nowhere to go but chat, which is compacted
    /// away. That is a new kind of thing the project holds — the first since patches — and it earns
    /// note and read_notes, one to write a section and one to read the index and the rest. read_annotation
    /// is the third and the reason the raise is this size: the record an agent already has is elided in
    /// list_annotations and split across view modes in read_function, so it kept re-reading functions it
    /// had already understood. read_annotation is the verb for "what do I already know about this one
    /// thing" that answers it whole, and no sentence shaved off another tool would have bought any of
    /// the three. The descriptions were tightened to what they mean before the number was moved, so this
    /// is the honest cost, paid once and on purpose.
    ///
    /// Nudged to 9,000 when note learned an index — sections read as one document, and alphabetical
    /// order rarely reads coherently, so the writer places them. That is one parameter, not a tool, but
    /// shaving a real description to keep the round number is exactly the trade this comment argues
    /// against; the honest move is to let the figure follow the surface.
    /// </summary>
    private const int MaxManifestChars = 9_000;

    private const int MaxDescriptionChars = 400;

    private static IReadOnlyList<MethodInfo> Tools() => typeof(McpOptions).Assembly
        .GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .ToList();

    [Fact]
    public void ThereAreToolsToFind()
    {
        // WithToolsFromAssembly finds them by reflection, so a rename or a missing attribute would
        // otherwise fail silently as a server that connects and offers nothing.
        Assert.NotEmpty(Tools());
    }

    [Fact]
    public void EveryToolIsNamedTheWayAnAgentExpects()
    {
        foreach (var tool in Tools())
        {
            string? name = tool.GetCustomAttribute<McpServerToolAttribute>()!.Name;
            Assert.False(string.IsNullOrWhiteSpace(name), $"{tool.Name} has no explicit tool name");
            Assert.Matches("^[a-z][a-z0-9_]*$", name!);
        }
    }

    [Fact]
    public void EveryToolAndEveryParameterExplainsItself()
    {
        foreach (var tool in Tools())
        {
            string where = tool.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? tool.Name;

            string? description = tool.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description), $"{where} has no description");
            Assert.True(description!.Length <= MaxDescriptionChars, $"{where}'s description is {description.Length} characters");

            foreach (var parameter in tool.GetParameters())
            {
                if (parameter.ParameterType == typeof(CancellationToken))
                {
                    continue;   // supplied by the host, never shown to the agent
                }

                Assert.True(
                    parameter.GetCustomAttribute<DescriptionAttribute>() is not null,
                    $"{where}({parameter.Name}) has no description, so the agent is guessing what to pass");
            }
        }
    }

    [Fact]
    public void EveryToolAnswersWithText()
    {
        // Answers are read, not parsed. Returning a structured type would make the SDK serialise
        // JSON, which spends a large share of the response repeating field names.
        foreach (var tool in Tools())
        {
            var returns = tool.ReturnType;
            if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
            {
                returns = returns.GetGenericArguments()[0];
            }

            Assert.True(returns == typeof(string), $"{tool.Name} returns {tool.ReturnType.Name}, not a string");
        }
    }

    [Fact]
    public void TheManifestStaysSmallEnoughToCarry()
    {
        int total = 0;
        foreach (var tool in Tools())
        {
            total += (tool.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? string.Empty).Length;
            total += tool.GetCustomAttribute<DescriptionAttribute>()?.Description?.Length ?? 0;
            foreach (var parameter in tool.GetParameters())
            {
                // A CancellationToken is supplied by the server, never asked of the model: it is not
                // in the schema the model is sent, so counting it charged the budget for something
                // nobody pays for.
                if (parameter.ParameterType == typeof(CancellationToken))
                {
                    continue;
                }

                total += parameter.Name?.Length ?? 0;
                total += parameter.GetCustomAttribute<DescriptionAttribute>()?.Description?.Length ?? 0;
            }
        }

        Assert.True(total <= MaxManifestChars, $"the tool manifest is {total} characters, which every session pays for up front");
    }
}
