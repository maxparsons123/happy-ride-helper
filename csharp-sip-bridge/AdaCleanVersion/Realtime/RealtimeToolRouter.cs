using System.Text.Json;
using AdaCleanVersion.Engine;
using AdaCleanVersion.Session;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Routes sync_booking_data tool calls to CleanCallSession.
/// Handles debouncing, slot locking, pacer logic, transcript injection,
/// and critical pre-tool instruction application.
/// </summary>
public sealed class RealtimeToolRouter
{
    private readonly CleanCallSession _session;
    private readonly IRealtimeTransport _transport;
    private readonly InstructionCoordinator _instructions;
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
        InstructionCoordinator instructions,
        CancellationToken ct)
    {
        _session = session;
        _transport = transport;
        _instructions = instructions;
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
    /// Full pipeline: debounce → slot lock → parse → pacer → session → instruction pre-apply → result → response.
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

        // ── Slot Locking: prevent AI from overwriting slot being geocoded ──
        if (_session.IsSlotLocked(args))
        {
            Log("🔒 Tool update blocked: Slot being geocoded — returning hold status");
            var lockResult = JsonSerializer.Serialize(new
            {
                status = "slot_locked",
                message = "Address is being verified. Please wait."
            });
            await _transport.SendAsync(new
            {
                type = "conversation.item.create",
                item = new { type = "function_call_output", call_id = evt.ToolCallId, output = lockResult }
            }, _ct);
            return;
        }

        // ── Inject raw transcripts for backend context ──
        if (_lastWhisperTranscript != null)
            args["whisper_transcript"] = _lastWhisperTranscript;
        if (_lastAdaTranscript != null)
            args["ada_transcript"] = _lastAdaTranscript;

        // ── Pacer: race geocoder vs 2.5s filler timer ──
        using var pacerCts = new CancellationTokenSource();
        var logicTask = _session.HandleToolCallAsync(evt.ToolName, args, _ct);
        var pacerTask = Task.Delay(2500, pacerCts.Token);

        object result;
        try
        {
            var completed = await Task.WhenAny(logicTask, pacerTask);
            if (completed == pacerTask && !logicTask.IsCompleted)
            {
                Log("⏱️ Pacer triggered — geocoding >2.5s, sending filler");
                await _instructions.SendPacerSpeechAsync("One moment while I check the map...");
            }
            result = await logicTask;
            pacerCts.Cancel();
        }
        catch (Exception ex)
        {
            pacerCts.Cancel();
            Log($"⚠ Tool handler error: {ex.Message}");
            result = new { error = ex.Message };
        }

        // ── VAD re-shield for address readback ──
        var isReadback = _session.Engine.State is
            CollectionState.VerifyingPickup or
            CollectionState.VerifyingDestination;
        if (isReadback)
        {
            Log("🛡️ VAD shielded for readback");
            await _instructions.ShieldVadForReadbackAsync();
        }

        var resultJson = result is string s ? s : JsonSerializer.Serialize(result);
        Log($"✅ Tool result: {(resultJson.Length > 200 ? resultJson[..200] + "..." : resultJson)}");

        // ── CRITICAL SEQUENCING ──
        // Apply pending instruction BEFORE tool result so the AI has correct context
        // when it auto-responds to function_call_output.
        var applied = await _instructions.ConsumeAndApplyAsync();

        // Send tool result
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

        // Trigger response.create based on applied instruction
        if (applied != null)
        {
            var (text, isSilent) = applied.Value;
            if (!isSilent)
            {
                await _transport.SendAsync(new
                {
                    type = "response.create",
                    response = new
                    {
                        modalities = new[] { "text", "audio" },
                        instructions = InstructionCoordinator.BuildStrictInstruction(text)
                    }
                }, _ct);
                Log("📋 Post-tool response.create sent");
            }
            // If silent, don't send response.create — AI stays quiet
        }
        else
        {
            // No instruction queued — safety net
            await _instructions.SendBareResponseCreateAsync();
        }

        // ── VAD shield release after readback ──
        if (isReadback)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                await _instructions.ReleaseVadShieldAsync();
            });
        }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
