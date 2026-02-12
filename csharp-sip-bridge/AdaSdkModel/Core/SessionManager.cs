using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Core;

/// <summary>
/// Manages all active call sessions. Thread-safe session creation and cleanup.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ICallSession> _sessions = new();
    private readonly ILogger<SessionManager> _logger;
    private readonly Func<string, string, ICallSession> _sessionFactory;

    public int ActiveCount => _sessions.Count;

    public event Action<ICallSession>? OnSessionStarted;
    public event Action<ICallSession, string>? OnSessionEnded;

    public SessionManager(
        ILogger<SessionManager> logger,
        Func<string, string, ICallSession> sessionFactory)
    {
        _logger = logger;
        _sessionFactory = sessionFactory;
    }

    public async Task<ICallSession> CreateSessionAsync(string callerId, CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var session = _sessionFactory(sessionId, callerId);
        session.OnEnded += HandleSessionEnded;

        if (!_sessions.TryAdd(sessionId, session))
        {
            await session.DisposeAsync();
            throw new InvalidOperationException($"Session {sessionId} already exists");
        }

        _logger.LogInformation("Session {SessionId} created for {CallerId} (active: {Count})",
            sessionId, callerId, _sessions.Count);

        await session.StartAsync(ct);
        OnSessionStarted?.Invoke(session);
        return session;
    }

    public ICallSession? GetSession(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public async Task EndSessionAsync(string sessionId, string reason)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.EndAsync(reason);
            await session.DisposeAsync();
        }
    }

    private void HandleSessionEnded(ICallSession session, string reason)
    {
        _sessions.TryRemove(session.SessionId, out _);
        session.OnEnded -= HandleSessionEnded;
        _logger.LogInformation("Session {SessionId} ended: {Reason} (active: {Count})",
            session.SessionId, reason, _sessions.Count);
        OnSessionEnded?.Invoke(session, reason);
        _ = session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing SessionManager with {Count} active sessions", _sessions.Count);
        var tasks = _sessions.Values.Select(s => s.EndAsync("shutdown")).ToArray();
        await Task.WhenAll(tasks);
        foreach (var session in _sessions.Values)
            await session.DisposeAsync();
        _sessions.Clear();
    }
}
