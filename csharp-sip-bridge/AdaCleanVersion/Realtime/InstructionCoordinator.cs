namespace AdaCleanVersion.Realtime;

/// <summary>
/// Simplified instruction coordinator (v6 — boring architecture).
/// 
/// Single responsibility: relay session instructions to the Realtime API.
/// - Normal instructions: session.update + response.create
/// - Reprompts: session.update + buffer handling + response.create
/// - Silent instructions: session.update only
/// 
/// REMOVED (v5 → v6):
/// - Dynamic VAD switching (single VAD set at session start)
/// - Cancel → update → response.create race choreography
/// - Fallback timer (300ms race guard)
/// - Pacer speech
/// - VAD re-shielding for readback
/// - Pre-tool instruction application (ConsumeAndApplyAsync)
/// - Silent instruction suppression logic
/// 
/// The model auto-responds after tool results.
/// We only trigger response.create for session-driven instructions
/// (reprompts, state transitions outside tool context).
/// </summary>
public sealed class InstructionCoordinator
{
    private readonly IRealtimeTransport _transport;
    private readonly Func<bool> _isMicGated;
    private readonly CancellationToken _ct;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    public InstructionCoordinator(
        IRealtimeTransport transport,
        Func<bool> isMicGated,
        CancellationToken ct)
    {
        _transport = transport;
        _isMicGated = isMicGated;
        _ct = ct;
    }

    // ─── Session Instruction Handler ────────────────────────

    /// <summary>
    /// Handle instruction from session layer (wired to CleanCallSession.OnAiInstruction).
    /// 
    /// Flow:
    ///   1. session.update with new instructions (always)
    ///   2. If reprompt → handle buffer + response.create with reprompt wrapper
    ///   3. If normal → response.create with strict wrapper
    ///   4. If silent → no response.create (model stays quiet)
    /// 
    /// No response.cancel. No fallback timer. No race conditions.
    /// </summary>
    public async void OnSessionInstruction(string instruction, bool isReprompt, bool isSilent)
    {
        try
        {
            // Always update session instructions
            await _transport.SendAsync(new
            {
                type = "session.update",
                session = new { instructions = instruction }
            }, _ct);

            if (isSilent)
            {
                Log("📋 Silent instruction updated — no response.create");
                return;
            }

            if (isReprompt)
            {
                Log("📋 REPROMPT instruction");

                // Preserve or clear caller speech based on mic state
                if (_isMicGated())
                {
                    await _transport.SendAsync(
                        new { type = "input_audio_buffer.clear" }, _ct);
                    Log("🔒 Reprompt: cleared input buffer (mic gated)");
                }
                else
                {
                    // Mic open — caller may have spoken. Commit to preserve.
                    await _transport.SendAsync(
                        new { type = "input_audio_buffer.commit" }, _ct);
                    Log("🔒 Reprompt: committed buffer (mic ungated — preserving speech)");
                }

                // Grounding message to break hallucinated confirmation context
                await _transport.SendAsync(new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "message",
                        role = "user",
                        content = new[]
                        {
                            new { type = "input_text", text = "[SYSTEM] The user's last response was INVALID. Re-ask the question." }
                        }
                    }
                }, _ct);

                await _transport.SendAsync(new
                {
                    type = "response.create",
                    response = new
                    {
                        modalities = new[] { "text", "audio" },
                        instructions = BuildRepromptInstruction(instruction)
                    }
                }, _ct);
                Log("🔒 REPROMPT response.create sent");
            }
            else
            {
                // Normal instruction (post-geocode, state transition, etc.)
                await _transport.SendAsync(new
                {
                    type = "response.create",
                    response = new
                    {
                        modalities = new[] { "text", "audio" },
                        instructions = BuildStrictInstruction(instruction)
                    }
                }, _ct);
                Log("📋 Instruction + response.create sent");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"⚠ Instruction error: {ex.Message}");
        }
    }

    // ─── Conversation Truncation ────────────────────────────

    /// <summary>
    /// Truncate/reset AI conversation context for field corrections.
    /// This is the ONE place we use response.cancel (correction = like barge-in).
    /// </summary>
    public async Task TruncateConversationAsync()
    {
        Log("✂️ Truncating conversation for field correction");
        try
        {
            await _transport.SendAsync(new { type = "response.cancel" }, _ct);
            await Task.Delay(50, _ct);
            await _transport.SendAsync(new { type = "input_audio_buffer.clear" }, _ct);

            await _transport.SendAsync(new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "system",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "[SYSTEM] ⚠️ CONTEXT RESET: The caller changed a booking detail. " +
                                   "Focus ONLY on the current [INSTRUCTION]. Acknowledge naturally then follow it."
                        }
                    }
                }
            }, _ct);

            Log("✅ Context reset injected");
        }
        catch (Exception ex)
        {
            Log($"⚠️ Truncation error: {ex.Message}");
        }
    }

    // ─── Static Instruction Builders ────────────────────────

    public static string BuildStrictInstruction(string instruction)
    {
        return $"""
            CRITICAL EXECUTION MODE:
            - Follow the [INSTRUCTION] below exactly.
            - Ask ONLY what the instruction asks for in this turn.
            - Do NOT confirm booking, dispatch taxi, end call, or summarize unless explicitly instructed.
            - Do NOT invent or normalize addresses/numbers.
            - Keep to one concise response, then wait.
            - ⛔ FORBIDDEN: farewell phrases, closing statements, re-greetings.

            {instruction}
            """;
    }

    public static string BuildRepromptInstruction(string instruction)
    {
        return $"""
            ⛔ REPROMPT MODE ⛔
            The user's input was INVALID or missing. Re-ask the question below.
            Do NOT acknowledge, confirm, or add commentary. Just re-ask and wait.

            {instruction}
            """;
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
