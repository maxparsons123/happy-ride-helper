// Ported from AdaSdkModel — Generic destructive action confirmation state machine
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Types of destructive actions that require explicit confirmation.
/// </summary>
public enum DestructiveActionType
{
    None,
    CancelBooking,
    // Future: RefundPayment, DeleteAccount, etc.
}

/// <summary>
/// Immutable record of a pending destructive action awaiting confirmation.
/// </summary>
public sealed record PendingDestructiveAction(
    DestructiveActionType ActionType,
    DateTime RequestedAtUtc);

/// <summary>
/// Generic, reusable confirmation state machine for destructive tool calls.
/// 
/// Flow:
///   1. Ada detects destructive intent → calls tool with confirmed=false
///   2. BeginConfirmation → Ada asks caller to confirm
///   3. Caller confirms → calls tool with confirmed=true
///   4. TryValidate → returns true only if pending + not expired
///   5. Guard auto-resets after successful validation
/// 
/// Safety: Random "yes" without pending = blocked. Wrong type = blocked. Expired = blocked.
/// </summary>
public sealed class DestructiveActionGuard
{
    private readonly ILogger _logger;
    private readonly string _sessionId;
    private readonly int _timeoutSeconds;
    private PendingDestructiveAction? _pending;

    public DestructiveActionGuard(ILogger logger, string sessionId, int confirmationTimeoutSeconds = 30)
    {
        _logger = logger;
        _sessionId = sessionId;
        _timeoutSeconds = confirmationTimeoutSeconds;
    }

    public bool HasPending => _pending != null;
    public DestructiveActionType PendingType => _pending?.ActionType ?? DestructiveActionType.None;

    public void BeginConfirmation(DestructiveActionType actionType)
    {
        _pending = new PendingDestructiveAction(actionType, DateTime.UtcNow);
        _logger.LogInformation("[{SessionId}] 🔄 Confirmation started for {ActionType}", _sessionId, actionType);
    }

    public bool TryValidate(DestructiveActionType expectedType, out string blockError)
    {
        blockError = string.Empty;

        if (_pending == null)
        {
            blockError = "No confirmation in progress. Ask the caller to confirm first.";
            _logger.LogWarning("[{SessionId}] 🛡️ {ActionType} BLOCKED — no pending", _sessionId, expectedType);
            return false;
        }

        if (_pending.ActionType != expectedType)
        {
            blockError = $"Mismatched action: pending={_pending.ActionType}, attempted={expectedType}.";
            _logger.LogWarning("[{SessionId}] 🛡️ {ActionType} BLOCKED — mismatched", _sessionId, expectedType);
            return false;
        }

        if (IsExpired())
        {
            blockError = "Confirmation expired. Please ask again.";
            _logger.LogWarning("[{SessionId}] 🛡️ {ActionType} BLOCKED — expired", _sessionId, expectedType);
            Reset();
            return false;
        }

        _logger.LogInformation("[{SessionId}] ✅ {ActionType} ALLOWED", _sessionId, expectedType);
        Reset();
        return true;
    }

    public void Reset() => _pending = null;

    private bool IsExpired() =>
        _pending != null && (DateTime.UtcNow - _pending.RequestedAtUtc) > TimeSpan.FromSeconds(_timeoutSeconds);
}
