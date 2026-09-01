using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spydate.Core.PE;
using Spydate.Mcp.Session;

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

        var options = McpOptions.Parse(args);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SessionStore>();
        builder.Services
            .AddMcpServer(server => server.ServerInfo = new() { Name = "spydate", Version = ThisVersion })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();

        // A binary named on the command line is opened before the first tool call, so a client
        // configured for one program does not have to be told about it again.
        if (options.OpenAtStartup is { } path && options.Allows(path) && File.Exists(path))
        {
            try
            {
                host.Services.GetRequiredService<SessionStore>().Set(BinarySession.Open(path, options));
            }
            catch (Exception ex) when (ex is PeParseException or IOException or UnauthorizedAccessException)
            {
                // Reported through stderr by the logger; the server still starts, and open_binary
                // works, so a bad path in a client's configuration is not fatal to the session.
                host.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(Program))
                    .LogWarning("could not open {Path}: {Message}", path, ex.Message);
            }
        }

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static string ThisVersion =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
