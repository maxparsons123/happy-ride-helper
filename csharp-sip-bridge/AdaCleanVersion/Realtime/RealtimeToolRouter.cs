using System.Text.Json;
using AdaCleanVersion.Conversation;
using TaxiBot.Deterministic;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// v8 — Deterministic engine wiring + TurnAnalyzer reconciliation.
/// 
/// Pipeline: caller transcript → TurnAnalyzer classifies intent → override ToolSyncEvent intent → engine.Step() → execute action.
/// Backend results (geocode, dispatch) feed back via engine.Step(BackendResultEvent).
/// Model auto-responds after receiving function_call_output for Ask/Hangup actions.
/// 
/// The TurnAnalyzer acts as a "conversation referee" — it classifies whether the caller's
/// utterance is a direct answer, correction, confirmation, etc. relative to Ada's last question.
/// The engine remains the sole authority for state transitions.
/// </summary>
public sealed class RealtimeToolRouter
{
    private readonly DeterministicBookingEngine _engine;
    private readonly IRealtimeTransport _transport;
    private readonly Func<string, Task<GeocodeResult>> _geocode;
    private readonly Func<BookingSlots, Task<DispatchResult>> _dispatch;
    private readonly TurnAnalyzerRealtime? _turnAnalyzer;
    private readonly CancellationToken _ct;

    private long _lastToolCallTick;
    private volatile bool _toolCalledInResponse;
    private readonly HashSet<string> _processedCallIds = new();
    private volatile bool _frozen; // post-transfer/hangup freeze
    private const long ThrottleMs = 500; // turn-level dedupe window

    // ── Turn context for TurnAnalyzer ──
    private string? _lastAdaQuestion;
    private string? _lastCallerTranscript;
    private readonly object _transcriptLock = new();

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

    /// <summary>Raised after each engine.Step() with the new stage.</summary>
    public event Action<Stage>? OnStageChanged;

    public RealtimeToolRouter(
        DeterministicBookingEngine engine,
        IRealtimeTransport transport,
        Func<string, Task<GeocodeResult>> geocode,
        Func<BookingSlots, Task<DispatchResult>> dispatch,
        CancellationToken ct,
        TurnAnalyzerRealtime? turnAnalyzer = null)
    {
        _engine = engine;
        _transport = transport;
        _geocode = geocode;
        _dispatch = dispatch;
        _ct = ct;
        _turnAnalyzer = turnAnalyzer;

        if (_turnAnalyzer != null)
            _turnAnalyzer.OnLog += Log;
    }

    /// <summary>Reset per-turn state on new speech_started.</summary>
    public void ResetTurn() => _toolCalledInResponse = false;

    /// <summary>
    /// Feed caller transcript for turn analysis context.
    /// Called from OpenAiRealtimeClient when a CallerTranscript event arrives.
    /// </summary>
    public void SetCallerTranscript(string transcript)
    {
        lock (_transcriptLock)
        {
            _lastCallerTranscript = transcript;
        }
    }

    /// <summary>
    /// Call once at session start to get the greeting instruction.
    /// Uses conversation.item.create + response.create (NOT session.update)
    /// so we don't overwrite the system prompt.
    /// </summary>
    public async Task StartAsync()
    {
        var action = _engine.Start();
        if (action is AskAction ask)
        {
            Log($"💬 Greeting: {ask.Text}");
            TrackAdaQuestion(ask.Text);
            OnInstruction?.Invoke(ask.Text);

            // Inject the greeting as a user-role conversation item
            await _transport.SendAsync(
                RealtimeSessionConfig.BuildGreetingItem(ask.Text), _ct);

            // Trigger model to speak the greeting (tool_choice none so it speaks, not calls tools)
            await _transport.SendAsync(
                RealtimeSessionConfig.BuildGreetingResponse(), _ct);
        }
        else
        {
            await ExecuteActionAsync(action, toolCallId: null);
        }
    }

    /// <summary>
    /// Handle response.function_call_arguments.done from OpenAI Realtime.
    /// Parse → TurnAnalyzer classifies → override intent → engine.Step(ToolSyncEvent) → execute action chain → done.
    /// </summary>
    public async Task HandleToolCallAsync(RealtimeEvent evt)
    {
        // 🧊 Post-transfer/hangup freeze — ignore all tool calls
        if (_frozen)
        {
            Log("🧊 Frozen — tool call ignored (post-transfer/hangup)");
            return;
        }

        _toolCalledInResponse = true;

        if (string.IsNullOrEmpty(evt.ToolCallId) || string.IsNullOrEmpty(evt.ToolName))
        {
            Log("⚠ Tool call missing call_id or name — ignoring");
            return;
        }

        // 🔒 Deduplicate by call_id
        if (!_processedCallIds.Add(evt.ToolCallId))
        {
            Log($"⚠ Duplicate tool call ignored (call_id): {evt.ToolCallId}");
            return;
        }

        // ⏱ Turn-level throttle: reject if another tool executed within 500ms
        var nowTick = Environment.TickCount64;
        var lastTick = Interlocked.Exchange(ref _lastToolCallTick, nowTick);
        if (lastTick > 0 && (nowTick - lastTick) < ThrottleMs)
        {
            Log($"⚠ Duplicate tool call ignored (throttle {nowTick - lastTick}ms): {evt.ToolCallId}");
            return;
        }

        Log($"🔧 Tool call: {evt.ToolName}");
        Log($"📥 Tool args: {evt.ToolArgsJson?.Substring(0, Math.Min(evt.ToolArgsJson?.Length ?? 0, 500))}");

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

        // ── TurnAnalyzer reconciliation (if available) ──
        args = await ReconcileTurnAsync(args);

        // ── Convert to ToolSyncEvent ──
        var toolEvent = ToolSyncMapper.FromToolArgs(evt.ToolCallId, args);

        // ── Step the engine ──
        var action = _engine.Step(toolEvent);
        Log($"⚙️ Engine: {_engine.State.Stage} → {action.Kind}");
        OnStageChanged?.Invoke(_engine.State.Stage);

        // ── Execute the action (may chain for geocode/dispatch) ──
        await ExecuteActionAsync(action, evt.ToolCallId);
    }

    /// <summary>
    /// Run the TurnAnalyzer to classify the caller's utterance relative to Ada's question.
    /// If the analysis yields a high-confidence classification that differs from what the AI
    /// sent as "intent", override the intent in the args dictionary.
    /// </summary>
    private async Task<Dictionary<string, object?>> ReconcileTurnAsync(Dictionary<string, object?> args)
    {
        if (_turnAnalyzer == null)
            return args;

        string? callerUtterance;
        string? adaQuestion;
        lock (_transcriptLock)
        {
            callerUtterance = _lastCallerTranscript;
            adaQuestion = _lastAdaQuestion;
        }

        if (string.IsNullOrWhiteSpace(callerUtterance))
        {
            Log("🧠 TurnAnalyzer skipped: no caller transcript available");
            return args;
        }

        var expected = MapStageToExpected(_engine.State.Stage);

        try
        {
            var analysis = await _turnAnalyzer.AnalyzeAsync(
                adaQuestion, expected, callerUtterance, _ct);

            Log($"🧠 Turn: {analysis.Relationship} (conf={analysis.Confidence:F2}, " +
                $"slot={analysis.Slot ?? "null"}, value={analysis.Value ?? "null"})");

            // ── Apply reconciliation overrides ──
            switch (analysis.Relationship)
            {
                case TurnRelationship.ConfirmationYes:
                    // Override intent to "confirm" if engine is in ConfirmDetails
                    if (_engine.State.Stage == Stage.ConfirmDetails)
                    {
                        Log("🧠 Override: intent → confirm (TurnAnalyzer)");
                        args["intent"] = "confirm";
                    }
                    break;

                case TurnRelationship.ConfirmationNo:
                    // Override intent to "decline" if engine is in ConfirmDetails
                    if (_engine.State.Stage == Stage.ConfirmDetails)
                    {
                        Log("🧠 Override: intent → decline (TurnAnalyzer)");
                        args["intent"] = "decline";
                    }
                    break;

                case TurnRelationship.Correction:
                    // If analyzer detected a correction with a specific slot + value,
                    // override the corresponding field and set intent to amend
                    if (!string.IsNullOrWhiteSpace(analysis.Slot) &&
                        !string.IsNullOrWhiteSpace(analysis.Value))
                    {
                        Log($"🧠 Override: {analysis.Slot} → \"{analysis.Value}\" (correction)");
                        args[analysis.Slot] = analysis.Value;
                        // Don't override intent if AI already sent "amend" or a field update
                        if (!args.ContainsKey("intent") || args["intent"]?.ToString() == "update_field")
                            args["intent"] = "amend";
                    }
                    break;

                case TurnRelationship.DirectAnswer:
                    // If the AI's tool args are empty but analyzer found a value,
                    // inject it into the expected slot
                    if (!string.IsNullOrWhiteSpace(analysis.Slot) &&
                        !string.IsNullOrWhiteSpace(analysis.Value))
                    {
                        var currentVal = args.TryGetValue(analysis.Slot, out var v) ? v?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(currentVal))
                        {
                            Log($"🧠 Inject: {analysis.Slot} → \"{analysis.Value}\" (direct answer)");
                            args[analysis.Slot] = analysis.Value;
                        }
                    }
                    break;

                case TurnRelationship.Irrelevant:
                case TurnRelationship.Unclear:
                    // Let the engine handle it with whatever the AI sent
                    Log($"🧠 No override: {analysis.Relationship}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠ TurnAnalyzer error (non-fatal): {ex.Message}");
            // Fall through — engine processes original args
        }

        return args;
    }

    /// <summary>
    /// Map the current engine Stage to the ExpectedResponse for the TurnAnalyzer.
    /// </summary>
    private static ExpectedResponse MapStageToExpected(Stage stage) => stage switch
    {
        Stage.CollectPickup => ExpectedResponse.Pickup,
        Stage.CollectDropoff => ExpectedResponse.Destination,
        Stage.CollectPassengers => ExpectedResponse.Passengers,
        Stage.CollectTime => ExpectedResponse.PickupTime,
        Stage.ConfirmDetails => ExpectedResponse.ConfirmationYesNo,
        _ => ExpectedResponse.None
    };

    /// <summary>Track Ada's last question for TurnAnalyzer context.</summary>
    private void TrackAdaQuestion(string question)
    {
        lock (_transcriptLock)
        {
            _lastAdaQuestion = question;
        }
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
                TrackAdaQuestion(ask.Text);
                OnInstruction?.Invoke(ask.Text);
                await SendToolResultAsync(toolCallId, new { status = "ok", instruction = ask.Text, stage = _engine.State.Stage.ToString() });
                // Trigger model to speak the instruction as audio
                await TriggerAudioResponse(ask.Text);
                break;

            case HangupAction hangup:
                Log($"📴 Hangup: {hangup.Text}");
                _frozen = true; // 🧊 Freeze — no more tool routing
                OnInstruction?.Invoke(hangup.Text);
                await SendToolResultAsync(toolCallId, new { status = "hangup", instruction = hangup.Text });
                // Trigger model to speak the hangup message
                await TriggerAudioResponse(hangup.Text);
                OnHangup?.Invoke(hangup.Text);
                break;

            case TransferAction transfer:
                Log($"🔀 Transfer: {transfer.Why}");
                _frozen = true; // 🧊 Freeze — no more tool routing
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
            OnStageChanged?.Invoke(_engine.State.Stage);

            // Execute the follow-up action (Ask for next field, or escalate, etc.)
            await ExecuteFollowUpAsync(nextAction);
        }
        catch (Exception ex)
        {
            Log($"⚠ Geocode error: {ex.Message}");
            var failEvent = new BackendResultEvent(type, Ok: false, Error: ex.Message);
            var nextAction = _engine.Step(failEvent);
            OnStageChanged?.Invoke(_engine.State.Stage);
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

            Log($"📦 Dispatch result: ok={result.Ok}, bookingId={result.BookingId ?? "null"}, error={result.Error ?? "none"}");
            var nextAction = _engine.Step(backendEvent);
            Log($"⚙️ Post-dispatch: {_engine.State.Stage} → {nextAction.Kind}");
            OnStageChanged?.Invoke(_engine.State.Stage);
            await ExecuteFollowUpAsync(nextAction);
        }
        catch (Exception ex)
        {
            Log($"⚠ Dispatch error: {ex.Message}");
            var failEvent = new BackendResultEvent(BackendResultType.Dispatch, Ok: false, Error: ex.Message);
            var nextAction = _engine.Step(failEvent);
            OnStageChanged?.Invoke(_engine.State.Stage);
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
                TrackAdaQuestion(ask.Text);
                OnInstruction?.Invoke(ask.Text);
                // Update session instructions and trigger model to speak
                await UpdateInstructionAndRespond(ask.Text);
                break;

            case HangupAction hangup:
                Log($"📴 Follow-up hangup: {hangup.Text}");
                _frozen = true; // 🧊 Freeze
                OnInstruction?.Invoke(hangup.Text);
                await UpdateInstructionAndRespond(hangup.Text);
                OnHangup?.Invoke(hangup.Text);
                break;

            case TransferAction transfer:
                Log($"🔀 Follow-up transfer: {transfer.Why}");
                _frozen = true; // 🧊 Freeze
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

    /// <summary>
    /// Trigger the model to speak an instruction as audio after a tool result.
    /// Uses response.create with tool_choice "none" so the model produces audio, not another tool call.
    /// </summary>
    private async Task TriggerAudioResponse(string instruction)
    {
        await _transport.SendAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "audio", "text" },
                instructions = $"[INSTRUCTION] {instruction}",
                tool_choice = "none"
            }
        }, _ct);
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}

// ── Simple result types for backend callbacks ──

public sealed record GeocodeResult(bool Ok, string? NormalizedAddress = null, string? Error = null);
public sealed record DispatchResult(bool Ok, string? BookingId = null, string? Error = null);
