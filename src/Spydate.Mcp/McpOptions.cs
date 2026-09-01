namespace Spydate.Mcp;

/// <summary>
/// How the server was started. Everything here narrows what an agent can do; nothing widens it.
/// </summary>
public sealed record McpOptions
{
    /// <summary>
    /// Refuse every write. The agent can still read everything — the point is a session where you
    /// want its reasoning without its opinions landing in your project.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// When set, binaries may only be opened from inside this directory. Off by default: a local MCP
    /// server already runs with the user's own rights, so this is for confining a session you do not
    /// trust rather than a security boundary that was ever missing.
    /// </summary>
    public string? Root { get; init; }

    /// <summary>
    /// Ceiling on discovery. The default matches the engine's own; lowering it is how you keep
    /// <c>open_binary</c> inside an MCP client's call timeout on a very large image.
    /// </summary>
    public int MaxFunctions { get; init; } = 20_000;

    /// <summary>A binary to open at startup, so the first tool call already has something to read.</summary>
    public string? OpenAtStartup { get; init; }

    public static McpOptions Default { get; } = new();

    /// <summary>
    /// Reads the command line. Unknown arguments are ignored rather than fatal: the server is
    /// launched by a client's configuration, and dying at startup over a stray flag would surface as
    /// an unexplained connection failure.
    /// </summary>
    public static McpOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new McpOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? next = i + 1 < args.Length ? args[i + 1] : null;

            switch (arg)
            {
                case "--read-only":
                    options = options with { ReadOnly = true };
                    break;

                case "--root" when next is not null:
                    options = options with { Root = Path.GetFullPath(next) };
                    i++;
                    break;

                case "--max-functions" when next is not null && int.TryParse(next, out int max) && max > 0:
                    options = options with { MaxFunctions = max };
                    i++;
                    break;

                default:
                    if (!arg.StartsWith('-') && options.OpenAtStartup is null)
                    {
                        options = options with { OpenAtStartup = arg };
                    }

                    break;
            }
        }

        return options;
    }

    /// <summary>
    /// Whether a path may be opened. A rooted server compares full paths, so <c>..</c> cannot climb
    /// out of the subtree it was given.
    /// </summary>
    public bool Allows(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Root is null)
        {
            return true;
        }

        string full = Path.GetFullPath(path);
        return full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(full, Root, StringComparison.OrdinalIgnoreCase);
    }
}
