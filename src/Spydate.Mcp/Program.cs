using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spydate.Mcp;

/// <summary>
/// Spydate's analysis engine as an MCP server, spoken over stdin and stdout.
///
/// Headless on purpose. The window could have hosted this, but only over HTTP, which would drag the
/// ASP.NET Core runtime into a desktop app that needs nothing but the desktop runtime — and the two
/// share their state through the <c>.spydate</c> project file anyway, which is the thing the window
/// already reads.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout belongs to the protocol. One stray line of logging on it corrupts every frame, and
        // the client reports something that looks nothing like the cause, so nothing else may write
        // there: diagnostics go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "spydate", Version = ThisVersion })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static string ThisVersion =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
