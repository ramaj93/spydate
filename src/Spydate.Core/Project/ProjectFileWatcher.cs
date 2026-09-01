using Spydate.Core.PE;

namespace Spydate.Core.Project;

/// <summary>
/// Notices when this image's project file is rewritten by something other than the window — an agent
/// driving the MCP server, or a second copy of Spydate.
///
/// Watching is only half of it. The save that produced the change was a merge, so the file on disk is
/// the whole truth about what everyone has decided; the window's job is to catch up with it. That is
/// why the reload replaces rather than adds.
/// </summary>
public sealed class ProjectFileWatcher : IDisposable
{
    /// <summary>
    /// A write arrives as several events — the temp file appears, is renamed over the target, and the
    /// directory is touched — so the reload waits for them to stop rather than running three times.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Lock _gate = new();
    private Timer? _debounce;
    private bool _disposed;

    /// <summary>Raised, off the UI thread, once a change has settled.</summary>
    public event EventHandler? Changed;

    /// <param name="directories">
    /// Where to look. Defaults to both places a project can live — beside the binary, and the
    /// per-user store used when that folder is not writable. Which one is in play can change during
    /// a session, since the first save is what creates it, so both are watched from the start.
    /// </param>
    public ProjectFileWatcher(PeImage image, IEnumerable<string>? directories = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        directories ??= SpydateProject.CandidatePaths(image)
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Cast<string>();

        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(directory, "*" + SpydateProject.Extension)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };

                // A save lands as a rename of the temp file over the target, so Renamed matters as
                // much as Changed. Watching only Changed would miss every write this makes.
                watcher.Changed += OnFileSystemEvent;
                watcher.Created += OnFileSystemEvent;
                watcher.Renamed += OnFileSystemEvent;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A directory that cannot be watched is not a reason to fail to open a file.
            }
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounce?.Dispose();
            _debounce = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, Settle, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _debounce?.Dispose();
            _debounce = null;
        }

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }
}
