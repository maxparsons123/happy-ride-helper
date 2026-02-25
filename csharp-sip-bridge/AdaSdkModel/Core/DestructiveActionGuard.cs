// Last updated: 2026-02-25 (v1.0 — Generic destructive action confirmation state machine)
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Core;

/// <summary>
/// Types of destructive actions that require explicit confirmation before execution.
/// Add new entries here as needed — the guard handles them all identically.
/// </summary>
public enum DestructiveActionType
{
    None,
    CancelBooking,
    // Future: RefundPayment, DeleteAccount, StopSubscription, etc.
}

/// <summary>
/// Immutable record of a pending destructive action awaiting user confirmation.
/// </summary>
public sealed record PendingDestructiveAction(
    DestructiveActionType ActionType,
    DateTime RequestedAtUtc);

/// <summary>
/// Generic, reusable confirmation state machine for destructive tool calls.
/// 
/// Flow:
///   1. Ada detects destructive intent → calls tool with confirmed=false
///   2. Tool handler calls <see cref="BeginConfirmation"/> → Ada asks caller to confirm
///   3. Caller confirms → Ada calls tool with confirmed=true
///   4. Tool handler calls <see cref="TryValidate"/> → returns true only if confirmation is pending + not expired
///   5. Tool executes → guard auto-resets
///
/// Safety guarantees:
///   • Random "yes" without a pending action → blocked
///   • Wrong action type confirmed → blocked
///   • Expired confirmation (timeout) → blocked
///   • Status-check immediately before cancel → blocked (via caller-supplied guard)
///   • No keyword scanning, no transcript parsing, no retry counters
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

    /// <summary>Whether a confirmation is currently pending (any type).</summary>
    public bool HasPending => _pending != null;

    /// <summary>The type of the currently pending action (or None).</summary>
    public DestructiveActionType PendingType => _pending?.ActionType ?? DestructiveActionType.None;

    /// <summary>
    /// Begin a confirmation flow. Called when Ada first detects destructive intent
    /// (i.e. tool called with confirmed=false). Ada should then ask the caller to confirm.
    /// </summary>
    public void BeginConfirmation(DestructiveActionType actionType)
    {
        _pending = new PendingDestructiveAction(actionType, DateTime.UtcNow);
        _logger.LogInformation("[{SessionId}] 🔄 Confirmation started for {ActionType}", _sessionId, actionType);
    }

    /// <summary>
    /// Validate that a destructive tool call is allowed right now.
    /// Returns true if confirmation is pending, matches the expected type, and hasn't expired.
    /// On success, the pending state is automatically cleared.
    /// On failure, <paramref name="blockError"/> contains a descriptive error for the AI.
    /// </summary>
    public bool TryValidate(DestructiveActionType expectedType, out string blockError)
    {
        blockError = string.Empty;

        if (_pending == null)
        {
            blockError = "No confirmation in progress. You must ask the caller to confirm first before proceeding.";
            _logger.LogWarning("[{SessionId}] 🛡️ {ActionType} BLOCKED — no pending confirmation", _sessionId, expectedType);
            return false;
        }

        if (_pending.ActionType != expectedType)
        {
            blockError = $"Mismatched action: confirmation was for {_pending.ActionType}, but {expectedType} was attempted. Ask the caller again.";
            _logger.LogWarning("[{SessionId}] 🛡️ {ActionType} BLOCKED — mismatched pending ({PendingType})",
                _sessionId, expectedType, _pending.ActionType);
            return false;
        }

        if (IsExpired())
        {
            blockError = "The confirmation expired. Please ask the caller again.";
            _logger.LogWarning("[{SessionId}] 🛡️ {ActionType} BLOCKED — confirmation expired", _sessionId, expectedType);
            Reset();
            return false;
        }

        // ✅ Valid
        _logger.LogInformation("[{SessionId}] ✅ {ActionType} ALLOWED — confirmation validated", _sessionId, expectedType);
        Reset();
        return true;
    }

    /// <summary>Reset pending state (e.g. on stage change, call end, or after successful validation).</summary>
    public void Reset()
    {
        _pending = null;
    }

    private bool IsExpired()
    {
        return _pending != null &&
               (DateTime.UtcNow - _pending.RequestedAtUtc) > TimeSpan.FromSeconds(_timeoutSeconds);
    }
}
