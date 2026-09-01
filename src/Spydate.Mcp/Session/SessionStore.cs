namespace Spydate.Mcp.Session;

/// <summary>
/// The binary currently open, if any. A separate gate from the session's own: opening replaces the
/// thing every other tool is holding, so it cannot happen while one of them is mid-answer.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BinarySession? _current;

    /// <summary>The open binary, or null. Tools say so rather than throwing.</summary>
    public BinarySession? Current => _current;

    /// <summary>Replaces whatever was open. The previous session is disposed once nothing is using it.</summary>
    public async Task<BinarySession> OpenAsync(Func<BinarySession> open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var opened = open();
            var previous = _current;
            _current = opened;
            previous?.Dispose();
            return opened;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Used by tests and by the startup path, which have no other caller to race with.</summary>
    public void Set(BinarySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var previous = _current;
        _current = session;
        if (!ReferenceEquals(previous, session))
        {
            previous?.Dispose();
        }
    }

    public void Dispose()
    {
        _current?.Dispose();
        _gate.Dispose();
    }
}
