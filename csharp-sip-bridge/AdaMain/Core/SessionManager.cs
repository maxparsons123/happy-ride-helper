using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AdaMain.Core;

/// <summary>
/// Manages all active call sessions.
/// Thread-safe session creation and cleanup.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ICallSession> _sessions = new();
    private readonly ILogger<SessionManager> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Func<string, string, ICallSession> _sessionFactory;
    
    public int ActiveCount => _sessions.Count;
    
    public event Action<ICallSession>? OnSessionStarted;
    public event Action<ICallSession, string>? OnSessionEnded;
    
    public SessionManager(
        ILogger<SessionManager> logger,
        Func<string, string, ICallSession> sessionFactory)
    {
        _logger = logger;
        _serviceProvider = null!;
        _sessionFactory = sessionFactory;
    }
    
    /// <summary>
    /// Create and start a new session for an incoming call.
    /// </summary>
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
    
    /// <summary>
    /// Get an active session by ID.
    /// </summary>
    public ICallSession? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }
    
    /// <summary>
    /// End and remove a session.
    /// </summary>
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
        
        _logger.LogInformation("Session {SessionId} ended: {Reason} (active: {Count})", 
            session.SessionId, reason, _sessions.Count);
        
        OnSessionEnded?.Invoke(session, reason);
        
        // Fire-and-forget cleanup
        _ = session.DisposeAsync();
    }
    
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing SessionManager with {Count} active sessions", _sessions.Count);
        
        var tasks = _sessions.Values
            .Select(s => s.EndAsync("shutdown"))
            .ToArray();
        
        await Task.WhenAll(tasks);
        
        foreach (var session in _sessions.Values)
            await session.DisposeAsync();
        
        _sessions.Clear();
    }
}
