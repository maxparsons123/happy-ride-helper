using AdaCleanVersion.Engine;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Manages the cancel → session.update → response.create instruction sequencing.
/// Handles auto VAD switching, reprompt grounding, silent suppression,
/// conversation truncation, pacer speech, and VAD re-shielding.
/// </summary>
public sealed class InstructionCoordinator
{
    private sealed record PendingInstruction(string Text, bool IsReprompt, bool IsSilent = false);

    private readonly IRealtimeTransport _transport;
    private readonly Func<CollectionState> _getState;
    private readonly Func<bool> _isMicGated;
    private readonly CancellationToken _ct;

    private PendingInstruction? _pendingInstruction;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    public InstructionCoordinator(
        IRealtimeTransport transport,
        Func<CollectionState> getState,
        Func<bool> isMicGated,
        CancellationToken ct)
    {
        _transport = transport;
        _getState = getState;
        _isMicGated = isMicGated;
        _ct = ct;
    }

    // ─── VAD Configuration ──────────────────────────────────

    /// <summary>
    /// Returns optimal VAD config based on current engine state.
    /// Semantic VAD for complex inputs (addresses, names, clarifications).
    /// Server VAD for quick inputs (passengers, time, confirmations).
    /// </summary>
    public object GetVadConfigForCurrentState()
    {
        var state = _getState();

        var useSemantic = state switch
        {
            CollectionState.CollectingName => true,
            CollectionState.CollectingPickup => true,
            CollectionState.CollectingDestination => true,
            CollectionState.CollectingPassengers => false,
            CollectionState.AwaitingClarification => true,
            CollectionState.VerifyingPickup => false,
            CollectionState.VerifyingDestination => false,
            _ => false
        };

        if (useSemantic)
        {
            return new
            {
                type = "semantic_vad",
                eagerness = "low",
                interrupt_response = true
            };
        }

        return new
        {
            type = "server_vad",
            threshold = 0.5,
            prefix_padding_ms = 300,
            silence_duration_ms = 500
        };
    }

    // ─── Instruction Queuing ────────────────────────────────

    /// <summary>
    /// Queue an instruction from the session layer.
    /// Wired to CleanCallSession.OnAiInstruction.
    /// </summary>
    public void OnSessionInstruction(string instruction, bool isReprompt, bool isSilent)
    {
        Log(isReprompt ? "📋 Queuing REPROMPT instruction" : "📋 Queuing instruction");
        _ = StartSequenceAsync(new PendingInstruction(instruction, isReprompt, isSilent));
    }

    /// <summary>
    /// Consume and apply a pending instruction (session.update only, no response.create).
    /// Used by tool router to pre-apply instruction before sending tool result.
    /// Returns (instruction text, isSilent) or null if nothing was pending.
    /// </summary>
    public async Task<(string Text, bool IsSilent)?> ConsumeAndApplyAsync()
    {
        var pending = Interlocked.Exchange(ref _pendingInstruction, null);
        if (pending == null) return null;

        var vadConfig = GetVadConfigForCurrentState();
        await _transport.SendAsync(new
        {
            type = "session.update",
            session = new
            {
                instructions = pending.Text,
                turn_detection = vadConfig
            }
        }, _ct);

        var isSilent = IsSilentInstruction(pending.Text) || pending.IsSilent;
        Log($"📋 Instruction applied (VAD: {GetVadType(vadConfig)})");
        return (pending.Text, isSilent);
    }

    /// <summary>Apply pending instruction and trigger response.create if not silent.</summary>
    private async Task ApplyPendingAndRespondAsync()
    {
        var result = await ConsumeAndApplyAsync();
        if (result == null) return;

        var (text, isSilent) = result.Value;
        if (isSilent)
        {
            Log("📋 Silent instruction — NO response.create");
            return;
        }

        // Check if this was a reprompt (we need to check original pending)
        // Since we already consumed it, check the text for reprompt markers
        var isReprompt = text.Contains("REPROMPT") || text.Contains("re-ask");
        await SendResponseCreateAsync(text, isReprompt);
    }

    /// <summary>Handle response.canceled event — apply pending instruction.</summary>
    public async Task OnResponseCanceledAsync()
    {
        Log("🛑 Response canceled");
        await ApplyPendingAndRespondAsync();
    }

    // ─── Response Sequencing ────────────────────────────────

    private async Task StartSequenceAsync(PendingInstruction pending)
    {
        try
        {
            Interlocked.Exchange(ref _pendingInstruction, pending);

            // For silent instructions, don't cancel — just let fallback apply
            if (!pending.IsSilent)
            {
                await _transport.SendAsync(new { type = "response.cancel" }, _ct);
            }

            if (pending.IsReprompt)
            {
                // Clear or commit input audio depending on mic state
                if (_isMicGated())
                {
                    await _transport.SendAsync(new { type = "input_audio_buffer.clear" }, _ct);
                    Log("🔒 Reprompt: cleared input audio buffer");
                }
                else
                {
                    // Mic is open — caller may have spoken. Commit to preserve their speech.
                    await _transport.SendAsync(new { type = "input_audio_buffer.commit" }, _ct);
                    Log("🔒 Reprompt: committed buffer (mic ungated — preserving caller speech)");
                }
            }

            // Fallback: if no response was active, response.canceled won't fire
            _ = FallbackSendAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"⚠ Instruction sequence error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fallback: if response.canceled doesn't arrive within 300ms
    /// (because no response was active), send the pending instruction anyway.
    /// </summary>
    private async Task FallbackSendAsync()
    {
        try
        {
            await Task.Delay(300, _ct);

            // Race guard: skip if a response started during the wait
            if (_isMicGated())
            {
                Log("⏳ Fallback skipped — response active (mic gated)");
                return;
            }

            await ApplyPendingAndRespondAsync();
        }
        catch (OperationCanceledException) { }
    }

    // ─── Response Create Helpers ────────────────────────────

    /// <summary>Send response.create with appropriate instruction wrapping.</summary>
    public async Task SendResponseCreateAsync(string instruction, bool isReprompt)
    {
        if (isReprompt)
        {
            // Inject grounding message to break hallucinated confirmation context
            await _transport.SendAsync(new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "user",
                    content = new[]
                    {
                        new { type = "input_text", text = "[SYSTEM] The user's last response was INVALID and has been DISCARDED. The booking is NOT confirmed. Do NOT dispatch a taxi. Re-ask the question as instructed." }
                    }
                }
            }, _ct);
        }

        await _transport.SendAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = isReprompt
                    ? BuildRepromptInstruction(instruction)
                    : BuildStrictInstruction(instruction)
            }
        }, _ct);

        Log(isReprompt ? "🔒 REPROMPT sent" : "📋 response.create sent");
    }

    /// <summary>Send bare response.create as safety net (no instruction override).</summary>
    public async Task SendBareResponseCreateAsync()
    {
        await _transport.SendAsync(new { type = "response.create" }, _ct);
    }

    // ─── Conversation Truncation ────────────────────────────

    /// <summary>
    /// Truncate/reset AI conversation context for field corrections.
    /// Injects a context-reset system message so the AI focuses on fresh instruction.
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
                            text = "[SYSTEM] ⚠️ CONTEXT RESET: The caller has changed a booking detail. " +
                                   "Your previous questions and the caller's previous answers about other fields are IRRELEVANT. " +
                                   "Focus ONLY on the current [INSTRUCTION]. Do NOT repeat any previous question. " +
                                   "🔄 PIVOT: Acknowledge the change naturally (e.g., 'No problem, let me update that.') " +
                                   "and then follow the [INSTRUCTION] exactly."
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

    // ─── VAD Re-shielding ───────────────────────────────────

    /// <summary>Tighten VAD during address readback to prevent echo barge-in.</summary>
    public async Task ShieldVadForReadbackAsync()
    {
        Log("🛡️ VAD re-shielded for readback (threshold=0.8)");
        await _transport.SendAsync(new
        {
            type = "session.update",
            session = new
            {
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.8,
                    prefix_padding_ms = 200,
                    silence_duration_ms = 400
                }
            }
        }, _ct);
    }

    /// <summary>Release VAD shield back to state-appropriate config.</summary>
    public async Task ReleaseVadShieldAsync()
    {
        var vadConfig = GetVadConfigForCurrentState();
        await _transport.SendAsync(new
        {
            type = "session.update",
            session = new
            {
                turn_detection = vadConfig
            }
        }, _ct);
        Log("🛡️ VAD shield released");
    }

    // ─── Pacer Speech ───────────────────────────────────────

    /// <summary>Inject filler phrase during slow geocoding to prevent dead air.</summary>
    public async Task SendPacerSpeechAsync(string fillerText)
    {
        try
        {
            await _transport.SendAsync(new
            {
                type = "response.create",
                response = new
                {
                    modalities = new[] { "text", "audio" },
                    instructions = $"[PACING] Say EXACTLY: \"{fillerText}\" — nothing more. Do NOT ask questions. Do NOT end the call."
                }
            }, _ct);
        }
        catch (Exception ex)
        {
            Log($"⚠ Pacer speech error: {ex.Message}");
        }
    }

    // ─── Static Instruction Builders ────────────────────────

    private static bool IsSilentInstruction(string instruction)
    {
        return instruction.Contains("ABSOLUTE SILENCE") ||
               instruction.Contains("Do NOT speak at all");
    }

    public static string BuildStrictInstruction(string instruction)
    {
        return $"""
            CRITICAL EXECUTION MODE:
            - Follow the [INSTRUCTION] below exactly.
            - Ask ONLY what the instruction asks for in this turn.
            - Do NOT confirm booking, dispatch taxi, end call, or summarize unless explicitly instructed.
            - Do NOT invent or normalize addresses/numbers.
            - Keep to one concise response, then wait.
            - ⛔ FORBIDDEN: Do NOT say "have a great day", "safe travels", "your ride is on its way",
              "booking confirmed", "thank you for confirming", "is there anything else", "goodbye",
              or ANY farewell/closing phrase. The call is IN PROGRESS. You are MID-CONVERSATION.
            - ⛔ Do NOT greet the caller. Do NOT say "Welcome to..." or re-introduce yourself. The call is already in progress.

            {instruction}
            """;
    }

    public static string BuildRepromptInstruction(string instruction)
    {
        return $"""
            ⛔⛔⛔ ABSOLUTE OVERRIDE — REPROMPT MODE ⛔⛔⛔
            
            YOUR PREVIOUS RESPONSE WAS DISCARDED. The user's input was INVALID.
            You MUST re-ask the EXACT question specified in the [INSTRUCTION] below.
            
            FORBIDDEN (violation = system failure):
            ❌ Do NOT say "understood", "got it", "thank you", "no problem"
            ❌ Do NOT confirm any booking or dispatch any taxi
            ❌ Do NOT say goodbye, safe travels, or any farewell
            ❌ Do NOT say "your taxi is on its way" or "booking confirmed"
            ❌ Do NOT acknowledge what the user just said
            ❌ Do NOT add any commentary or filler
            ❌ Do NOT end the conversation
            
            REQUIRED (exactly ONE of these):
            ✅ Say ONLY what the [INSTRUCTION] tells you to say
            ✅ Then STOP and WAIT for the user's answer
            
            {instruction}
            """;
    }

    // ─── Helpers ────────────────────────────────────────────

    private static string GetVadType(object vadConfig)
    {
        try { return ((dynamic)vadConfig).type; }
        catch { return "unknown"; }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
