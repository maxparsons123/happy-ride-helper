using System.Text.Json;
using AdaCleanVersion.Session;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Simplified tool router (v6 — boring architecture).
/// 
/// Single path: parse → session.HandleToolCallAsync → send result → done.
/// Model auto-responds after receiving function_call_output.
/// 
/// REMOVED (v5 → v6):
/// - Slot locking
/// - Pacer timer (2.5s race with geocoder)
/// - Pre-tool instruction application (ConsumeAndApplyAsync)
/// - Post-tool manual response.create
/// - VAD re-shielding for readback
/// - InstructionCoordinator dependency
/// 
/// KEPT:
/// - Debounce (200ms)
/// - Transcript injection (whisper + ada) for session context
/// - Turn tracking (ToolCalledInResponse)
/// </summary>
public sealed class RealtimeToolRouter
{
    private readonly CleanCallSession _session;
    private readonly IRealtimeTransport _transport;
    private readonly CancellationToken _ct;

    private long _lastToolCallTick;
    private volatile bool _toolCalledInResponse;
    private volatile string? _lastWhisperTranscript;
    private volatile string? _lastAdaTranscript;

    /// <summary>True if a tool call was processed for the current turn.</summary>
    public bool ToolCalledInResponse => _toolCalledInResponse;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    public RealtimeToolRouter(
        CleanCallSession session,
        IRealtimeTransport transport,
        CancellationToken ct)
    {
        _session = session;
        _transport = transport;
        _ct = ct;
    }

    /// <summary>Reset per-turn state on new speech_started.</summary>
    public void ResetTurn()
    {
        _toolCalledInResponse = false;
        _lastWhisperTranscript = null;
        _lastAdaTranscript = null;
    }

    /// <summary>Capture raw Whisper transcript for injection into tool args.</summary>
    public void SetWhisperTranscript(string transcript) => _lastWhisperTranscript = transcript;

    /// <summary>Capture Ada's spoken transcript for injection into tool args.</summary>
    public void SetAdaTranscript(string transcript) => _lastAdaTranscript = transcript;

    /// <summary>
    /// Handle response.function_call_arguments.done from OpenAI Realtime.
    /// Simple pipeline: debounce → parse → session → send result → done.
    /// Model auto-responds after receiving the tool result.
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

        // ── Inject raw transcripts for session context ──
        if (_lastWhisperTranscript != null)
            args["whisper_transcript"] = _lastWhisperTranscript;
        if (_lastAdaTranscript != null)
            args["ada_transcript"] = _lastAdaTranscript;

        // ── Call session handler ──
        object result;
        try
        {
            result = await _session.HandleToolCallAsync(evt.ToolName, args, _ct);
        }
        catch (Exception ex)
        {
            Log($"⚠ Tool handler error: {ex.Message}");
            result = new { error = ex.Message };
        }

        var resultJson = result is string s ? s : JsonSerializer.Serialize(result);
        Log($"✅ Tool result: {(resultJson.Length > 200 ? resultJson[..200] + "..." : resultJson)}");

        // ── Send tool result — model auto-responds ──
        await _transport.SendAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = evt.ToolCallId,
                output = resultJson
            }
        }, _ct);

        // No response.create. No instruction pre-application.
        // The model reads the tool result and auto-generates the next response
        // using the session instructions already set via session.update.
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
