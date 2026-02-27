using System.Text.Json;
using TaxiBot.Deterministic;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// v7 — Deterministic engine wiring.
/// 
/// Single path: parse tool args → engine.Step(ToolSyncEvent) → execute NextAction → done.
/// Backend results (geocode, dispatch) feed back via engine.Step(BackendResultEvent).
/// Model auto-responds after receiving function_call_output for Ask/Hangup actions.
/// 
/// NO transcript fallback. NO slot locking. NO pacer. NO manual response.create.
/// The engine is the single authority.
/// </summary>
public sealed class RealtimeToolRouter
{
    private readonly DeterministicBookingEngine _engine;
    private readonly IRealtimeTransport _transport;
    private readonly Func<string, Task<GeocodeResult>> _geocode;
    private readonly Func<BookingSlots, Task<DispatchResult>> _dispatch;
    private readonly CancellationToken _ct;

    private long _lastToolCallTick;
    private volatile bool _toolCalledInResponse;

    /// <summary>True if a tool call was processed for the current turn.</summary>
    public bool ToolCalledInResponse => _toolCalledInResponse;

    /// <summary>Current engine state for external inspection.</summary>
    public Stage CurrentStage => _engine.State.Stage;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    /// <summary>Raised when the engine produces an instruction for the model.</summary>
    public event Action<string>? OnInstruction;

    /// <summary>Raised when the engine says to transfer to human.</summary>
    public event Action<string>? OnTransfer;

    /// <summary>Raised when the engine says to hang up.</summary>
    public event Action<string>? OnHangup;

    public RealtimeToolRouter(
        DeterministicBookingEngine engine,
        IRealtimeTransport transport,
        Func<string, Task<GeocodeResult>> geocode,
        Func<BookingSlots, Task<DispatchResult>> dispatch,
        CancellationToken ct)
    {
        _engine = engine;
        _transport = transport;
        _geocode = geocode;
        _dispatch = dispatch;
        _ct = ct;
    }

    /// <summary>Reset per-turn state on new speech_started.</summary>
    public void ResetTurn() => _toolCalledInResponse = false;

    /// <summary>
    /// Call once at session start to get the greeting instruction.
    /// </summary>
    public async Task StartAsync()
    {
        var action = _engine.Start();
        // Greeting has no tool call context — use follow-up path to trigger audio
        if (action is AskAction ask)
        {
            Log($"💬 Ask: {ask.Text}");
            OnInstruction?.Invoke(ask.Text);
            await UpdateInstructionAndRespond(ask.Text);
        }
        else
        {
            await ExecuteActionAsync(action, toolCallId: null);
        }
    }

    /// <summary>
    /// Handle response.function_call_arguments.done from OpenAI Realtime.
    /// Parse → engine.Step(ToolSyncEvent) → execute action chain → done.
    /// </summary>
    public async Task HandleToolCallAsync(RealtimeEvent evt)
    {
        _toolCalledInResponse = true;

        // ── Debounce rapid-fire tool calls ──
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastToolCallTick) < 200) return;
        Volatile.Write(ref _lastToolCallTick, now);

        if (string.IsNullOrEmpty(evt.ToolCallId) || string.IsNullOrEmpty(evt.ToolName))
        {
            Log("⚠ Tool call missing call_id or name — ignoring");
            return;
        }

        Log($"🔧 Tool call: {evt.ToolName}");

        // ── Parse arguments ──
        Dictionary<string, object?> args;
        try
        {
            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                evt.ToolArgsJson ?? "{}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex)
        {
            Log($"⚠ Tool args parse error: {ex.Message}");
            args = new();
        }

        // ── Convert to ToolSyncEvent ──
        var toolEvent = ToolSyncMapper.FromToolArgs(evt.ToolCallId, args);

        // ── Step the engine ──
        var action = _engine.Step(toolEvent);
        Log($"⚙️ Engine: {_engine.State.Stage} → {action.Kind}");

        // ── Execute the action (may chain for geocode/dispatch) ──
        await ExecuteActionAsync(action, evt.ToolCallId);
    }

    /// <summary>
    /// Execute a NextAction. For backend actions (geocode, dispatch),
    /// this calls the backend, feeds the result back into engine.Step(),
    /// and recurses on the resulting action.
    /// </summary>
    private async Task ExecuteActionAsync(NextAction action, string? toolCallId)
    {
        switch (action)
        {
            case AskAction ask:
                Log($"💬 Ask: {ask.Text}");
                OnInstruction?.Invoke(ask.Text);
                await SendToolResultAsync(toolCallId, new { status = "ok", instruction = ask.Text, stage = _engine.State.Stage.ToString() });
                break;

            case HangupAction hangup:
                Log($"📴 Hangup: {hangup.Text}");
                OnInstruction?.Invoke(hangup.Text);
                await SendToolResultAsync(toolCallId, new { status = "hangup", instruction = hangup.Text });
                OnHangup?.Invoke(hangup.Text);
                break;

            case TransferAction transfer:
                Log($"🔀 Transfer: {transfer.Why}");
                await SendToolResultAsync(toolCallId, new { status = "transfer", reason = transfer.Why });
                OnTransfer?.Invoke(transfer.Why);
                break;

            case GeocodePickupAction geo:
                Log($"📍 Geocoding pickup: {geo.RawAddress}");
                await SendToolResultAsync(toolCallId, new { status = "geocoding", address = geo.RawAddress, stage = _engine.State.Stage.ToString() });
                await ExecuteGeocodeAsync(BackendResultType.GeocodePickup, geo.RawAddress);
                break;

            case GeocodeDropoffAction geo:
                Log($"📍 Geocoding dropoff: {geo.RawAddress}");
                await SendToolResultAsync(toolCallId, new { status = "geocoding", address = geo.RawAddress, stage = _engine.State.Stage.ToString() });
                await ExecuteGeocodeAsync(BackendResultType.GeocodeDropoff, geo.RawAddress);
                break;

            case DispatchAction disp:
                Log($"🚕 Dispatching...");
                await SendToolResultAsync(toolCallId, new { status = "dispatching", stage = _engine.State.Stage.ToString() });
                await ExecuteDispatchAsync(disp.Slots);
                break;

            case NoneAction none:
                Log($"⏭ None: {none.Why}");
                await SendToolResultAsync(toolCallId, new { status = "no_op", reason = none.Why });
                break;

            case SilenceAction silence:
                Log($"🤫 Silence: {silence.Why}");
                // Don't send tool result — let model stay quiet
                break;

            default:
                Log($"⚠ Unknown action type: {action.GetType().Name}");
                await SendToolResultAsync(toolCallId, new { status = "error", reason = "unknown action" });
                break;
        }
    }

    /// <summary>
    /// Call geocode backend, feed result back into engine, execute resulting action.
    /// </summary>
    private async Task ExecuteGeocodeAsync(BackendResultType type, string rawAddress)
    {
        try
        {
            var result = await _geocode(rawAddress);
            var backendEvent = new BackendResultEvent(
                Type: type,
                Ok: result.Ok,
                NormalizedAddress: result.NormalizedAddress,
                Error: result.Error);

            var nextAction = _engine.Step(backendEvent);
            Log($"⚙️ Post-geocode: {_engine.State.Stage} → {nextAction.Kind}");

            // Execute the follow-up action (Ask for next field, or escalate, etc.)
            await ExecuteFollowUpAsync(nextAction);
        }
        catch (Exception ex)
        {
            Log($"⚠ Geocode error: {ex.Message}");
            var failEvent = new BackendResultEvent(type, Ok: false, Error: ex.Message);
            var nextAction = _engine.Step(failEvent);
            await ExecuteFollowUpAsync(nextAction);
        }
    }

    /// <summary>
    /// Call dispatch backend, feed result back into engine, execute resulting action.
    /// </summary>
    private async Task ExecuteDispatchAsync(BookingSlots slots)
    {
        try
        {
            var result = await _dispatch(slots);
            var backendEvent = new BackendResultEvent(
                Type: BackendResultType.Dispatch,
                Ok: result.Ok,
                BookingId: result.BookingId,
                Error: result.Error);

            var nextAction = _engine.Step(backendEvent);
            Log($"⚙️ Post-dispatch: {_engine.State.Stage} → {nextAction.Kind}");
            await ExecuteFollowUpAsync(nextAction);
        }
        catch (Exception ex)
        {
            Log($"⚠ Dispatch error: {ex.Message}");
            var failEvent = new BackendResultEvent(BackendResultType.Dispatch, Ok: false, Error: ex.Message);
            var nextAction = _engine.Step(failEvent);
            await ExecuteFollowUpAsync(nextAction);
        }
    }

    /// <summary>
    /// Execute a follow-up action from a backend result.
    /// These are NOT tool results — they update session instructions + trigger response.create.
    /// </summary>
    private async Task ExecuteFollowUpAsync(NextAction action)
    {
        switch (action)
        {
            case AskAction ask:
                Log($"💬 Follow-up ask: {ask.Text}");
                OnInstruction?.Invoke(ask.Text);
                // Update session instructions and trigger model to speak
                await UpdateInstructionAndRespond(ask.Text);
                break;

            case HangupAction hangup:
                Log($"📴 Follow-up hangup: {hangup.Text}");
                OnInstruction?.Invoke(hangup.Text);
                await UpdateInstructionAndRespond(hangup.Text);
                OnHangup?.Invoke(hangup.Text);
                break;

            case TransferAction transfer:
                Log($"🔀 Follow-up transfer: {transfer.Why}");
                OnTransfer?.Invoke(transfer.Why);
                break;

            case GeocodePickupAction geo:
                // Chain: geocode result led to another geocode (e.g., amend)
                await ExecuteGeocodeAsync(BackendResultType.GeocodePickup, geo.RawAddress);
                break;

            case GeocodeDropoffAction geo:
                await ExecuteGeocodeAsync(BackendResultType.GeocodeDropoff, geo.RawAddress);
                break;

            case DispatchAction disp:
                await ExecuteDispatchAsync(disp.Slots);
                break;

            default:
                Log($"⏭ Follow-up no-op: {action.Kind}");
                break;
        }
    }

    /// <summary>
    /// Send session.update with new instruction + response.create to make model speak.
    /// Used for backend-driven state changes (post-geocode, post-dispatch).
    /// </summary>
    private async Task UpdateInstructionAndRespond(string instruction)
    {
        await _transport.SendAsync(new
        {
            type = "session.update",
            session = new
            {
                instructions = $"[INSTRUCTION] {instruction}"
            }
        }, _ct);

        // Override tool_choice to "none" so the model speaks audio instead of calling tools
        await _transport.SendAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "audio", "text" },
                tool_choice = "none"
            }
        }, _ct);
    }

    /// <summary>
    /// Send tool result back to OpenAI. Model auto-responds.
    /// </summary>
    private async Task SendToolResultAsync(string? toolCallId, object result)
    {
        if (string.IsNullOrEmpty(toolCallId)) return;

        var resultJson = JsonSerializer.Serialize(result);
        Log($"✅ Tool result: {(resultJson.Length > 200 ? resultJson[..200] + "..." : resultJson)}");

        await _transport.SendAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = toolCallId,
                output = resultJson
            }
        }, _ct);
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}

// ── Simple result types for backend callbacks ──

public sealed record GeocodeResult(bool Ok, string? NormalizedAddress = null, string? Error = null);
public sealed record DispatchResult(bool Ok, string? BookingId = null, string? Error = null);
