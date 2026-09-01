namespace Spydate.Core.Project;

/// <summary>
/// A between-processes lock for the moment a project file is read, merged and rewritten.
///
/// The merge in <see cref="SpydateProject.SaveTo"/> is read-modify-write, and two writers
/// interleaving inside it would lose whichever change landed between the read and the write. That
/// window is milliseconds, so a lock only has to hold for milliseconds.
///
/// Advisory on purpose: if it cannot be taken, the save goes ahead anyway. A project must never
/// become unwritable because something left a file behind — losing an annotation to a rare
/// interleaving is a smaller harm than refusing to save at all. Staleness needs no timeout, because
/// the lock is a held handle: when a process dies the operating system closes it.
/// </summary>
public static class Advisory
{
    /// <summary>How long to wait for another writer to finish before going ahead regardless.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Takes the lock beside <paramref name="path"/>. Dispose to release. Never throws, and never
    /// returns null: a failure to lock is a failure to exclude, not a failure to proceed.
    /// </summary>
    public static IDisposable Lock(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string lockPath = path + ".lock";
        var deadline = DateTime.UtcNow + Timeout;
        do
        {
            try
            {
                // DeleteOnClose keeps the directory tidy; FileShare.None is what does the excluding.
                return new FileStream(
                    lockPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                Thread.Sleep(Retry);   // somebody else is mid-merge
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException)
            {
                break;   // a directory that will not hold a lock file will not hold one later either
            }
        }
        while (DateTime.UtcNow < deadline);

        return Unlocked.Instance;
    }

    /// <summary>What a caller gets when the lock could not be taken: nothing, disposably.</summary>
    private sealed class Unlocked : IDisposable
    {
        public static readonly Unlocked Instance = new();

        public void Dispose()
        {
        }
    }
}
