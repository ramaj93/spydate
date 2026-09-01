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
    /// </summary>
    private const int MaxManifestChars = 6_000;

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
                total += parameter.Name?.Length ?? 0;
                total += parameter.GetCustomAttribute<DescriptionAttribute>()?.Description?.Length ?? 0;
            }
        }

        Assert.True(total <= MaxManifestChars, $"the tool manifest is {total} characters, which every session pays for up front");
    }
}
