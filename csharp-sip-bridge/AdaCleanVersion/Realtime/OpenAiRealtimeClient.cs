using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AdaCleanVersion.Audio;
using AdaCleanVersion.Session;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Bridges OpenAI Realtime API (WebSocket) ↔ SIPSorcery RTPSession via G711RtpPlayout.
/// Supports both PCMU (µ-law) and PCMA (A-law) codecs.
///
/// Mic Gate v4.3 (hybrid buffer-all, flush-tail):
///   Mic gated while AI is speaking. ALL audio buffered during gate.
///   On ungate/barge-in, only trailing speech frames are flushed (max 25 = 500ms).
///   Energy filter prevents silence/noise from causing ghost transcripts.
///   Barge-in flushes tail (not clears) to preserve leading syllables.
///   Audio commit on speech_stopped. Event-driven instruction sequence.
///   Instruction sequence is event-driven (waits for response.canceled).
/// </summary>
public sealed class OpenAiRealtimeClient : IAsyncDisposable
{
    private const string RealtimeUrl = "wss://api.openai.com/v1/realtime";
    private const int ReceiveBufferSize = 16384;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _callId;
    private readonly RTPSession _rtpSession;
    private readonly CleanCallSession _session;
    private readonly ILogger _logger;
    private readonly G711CodecType _codec;
    private readonly CancellationTokenSource _cts = new();

    private ClientWebSocket? _ws;
    private Task? _receiveTask;

    /// <summary>
    /// Jitter-buffered playout engine — all outbound audio is routed through this.
    /// </summary>
    private readonly G711RtpPlayout _playout;

    // ─── Mic Gate v4.3 (hybrid: buffer-all, flush tail only) ──
    // Mic gated while AI speaks. ALL audio buffered during gate.
    // On ungate/barge-in, only the TRAILING speech frames are flushed (not hundreds
    // of silence frames that cause ghost transcripts like "Bye.").
    // This preserves leading speech syllables without flooding OpenAI with noise.

    /// <summary>True = mic is blocked.</summary>
    private volatile bool _micGated;

    /// <summary>Debounce guard for barge-in events (ms tick).</summary>
    private long _lastBargeInTick;

    /// <summary>OpenAI finished sending audio deltas.</summary>
    private volatile bool _responseCompleted;

    private sealed record PendingInstruction(string Text, bool IsReprompt);

    /// <summary>Pending instruction to send after response.canceled arrives.</summary>
    private PendingInstruction? _pendingInstruction;

    /// <summary>
    /// Set when a tool call was processed for the current turn.
    /// Prevents the transcript handler from redundantly processing the same input.
    /// Reset on each new speech_started event.
    /// </summary>
    private volatile bool _toolCalledInResponse;

    // Mic gate buffer: stores ALL gated audio; only trailing speech frames are flushed
    private readonly List<byte[]> _micGateBuffer = new();
    private readonly bool[] _micGateEnergy = new bool[0]; // resized dynamically
    private readonly object _micGateBufferLock = new();
    private readonly byte _g711SilenceByte;

    /// <summary>Max trailing frames to flush (25 frames = 500ms — enough for "four passengers").</summary>
    private const int MicTailMaxFlush = 25;

    /// <summary>Min non-silence bytes in a 160-byte frame to count as speech.</summary>
    private const int SpeechEnergyThreshold = 8;

    // ─── Auto VAD Config ───────────────────────────────────
    /// <summary>
    /// Returns the optimal VAD configuration based on the current engine state.
    /// Semantic VAD for complex inputs (addresses, names, clarifications).
    /// Server VAD for quick inputs (passengers, time, confirmations).
    /// </summary>
    private dynamic GetVadConfigForCurrentState()
    {
        var state = _session.Engine.State;

        var useSemanticVad = state switch
        {
            Engine.CollectionState.CollectingName => true,
            Engine.CollectionState.CollectingPickup => true,
            Engine.CollectionState.CollectingDestination => true,
            Engine.CollectionState.CollectingPassengers => false, // short answer — server_vad is snappier
            Engine.CollectionState.AwaitingClarification => true,
            Engine.CollectionState.VerifyingPickup => false,    // silence during geocoding
            Engine.CollectionState.VerifyingDestination => false, // silence during geocoding
            _ => false
        };

        if (useSemanticVad)
        {
            return new
            {
                type = "semantic_vad",
                eagerness = "low",        // patient — waits for semantic completion
                interrupt_response = true  // still allow barge-in
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

    public event Action<string>? OnLog;

    /// <summary>Fires with each G.711 audio frame sent to playout (for Simli avatar feeding).</summary>
    public event Action<byte[]>? OnAudioOut;

    /// <summary>Fires when barge-in (speech_started) occurs.</summary>
    public event Action? OnBargeIn;

    /// <summary>Fires when mic is ungated (playout drained, caller can speak).</summary>
    public event Action? OnMicUngated;

    public OpenAiRealtimeClient(
        string apiKey,
        string model,
        string voice,
        string callId,
        RTPSession rtpSession,
        CleanCallSession session,
        ILogger logger,
        G711CodecType codec = G711CodecType.PCMU)
    {
        _apiKey = apiKey;
        _model = model;
        _voice = voice;
        _callId = callId;
        _rtpSession = rtpSession;
        _session = session;
        _logger = logger;
        _codec = codec;
        _g711SilenceByte = G711Codec.SilenceByte(codec);
        _playout = new G711RtpPlayout(rtpSession, codec);
        _playout.OnLog += msg => Log(msg);
        _playout.OnQueueEmpty += OnPlayoutQueueEmpty;
    }

    // ─── Mic Gate Logic (v4.5 — buffer-all, flush tail, playout-driven ungate) ─────

    /// <summary>Called when playout queue drains.</summary>
    private void OnPlayoutQueueEmpty()
    {
        if (!_responseCompleted)
            return; // AI still streaming audio — wait

        if (!_micGated)
            return; // already ungated

        UngateMic();
    }

    /// <summary>Called when response.audio.done arrives.</summary>
    private void OnResponseAudioDone()
    {
        _responseCompleted = true;
        _playout.Flush();
        var queued = _playout.QueuedFrames;
        Log($"🔊 response.audio.done — queued={queued}");

        if (queued == 0)
        {
            UngateMic();
        }
        // else: OnPlayoutQueueEmpty will fire when the queue drains naturally.
        // No aggressive fallback — let playout finish to avoid stuttering.
    }

    private void UngateMic()
    {
        if (!_micGated) return;
        _micGated = false;
        // Normal ungate after playout: DISCARD the buffer (it's all echo, not caller speech).
        // Only barge-in should flush tail frames to OpenAI.
        ClearMicGateBuffer();
        Log("🔓 Mic ungated (audio done + playout drained) — buffer discarded (echo)");
        try { OnMicUngated?.Invoke(); } catch { }
    }

    /// <summary>Gate mic when AI starts responding.</summary>
    private void ArmMicGate()
    {
        _micGated = true;
        _responseCompleted = false;
    }

    // ─── Lifecycle ──────────────────────────────────────────

    public async Task ConnectAsync()
    {
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var url = $"{RealtimeUrl}?model={_model}";
        await _ws.ConnectAsync(new Uri(url), _cts.Token);

        Log("🔌 Connected to OpenAI Realtime");

        await SendSessionConfig();

        // Wire RTP → OpenAI (caller audio in)
        _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;

        // Wire session instructions → OpenAI session.update
        _session.OnAiInstruction += OnSessionAiInstruction;

        // Wire typing sounds control for recalculation bridge
        _session.OnTypingSoundsChanged += enabled =>
        {
            _playout.TypingSoundsEnabled = enabled;
            Log(enabled ? "🔊 Typing sounds enabled (recalculation)" : "🔇 Typing sounds disabled (fare ready)");
        };

        // Wire mic ungate → session no-reply watchdog
        // Watchdog starts AFTER playout drains, not when instruction is sent.
        // This prevents the 12s timer from eating into Ada's speaking time.
        OnMicUngated += () => _session.NotifyMicUngated();

        // Start playout engine
        _playout.Start();

        // Start receive loop
        _receiveTask = Task.Run(ReceiveLoopAsync);

        Log("✅ Bidirectional audio bridge active (mic gate v4.3)");

        // Send greeting as a conversation item (matches AdaSdkModel flow)
        // This happens AFTER session config so the AI knows its role.
        await SendGreetingAsync();
    }

    /// <summary>
    /// Send the greeting as an explicit conversation item, matching AdaSdkModel's approach.
    /// Injects a user message with exact greeting wording, then triggers a response.
    /// </summary>
    private async Task SendGreetingAsync()
    {
        try
        {
            var greetingMessage = _session.BuildGreetingMessage();

            // Inject as a user message (same as AdaSdkModel's AddItem + StartResponse)
            var itemMsg = new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "user",
                    content = new[]
                    {
                        new { type = "input_text", text = greetingMessage }
                    }
                }
            };
            await SendJsonAsync(itemMsg);

            // Trigger the AI to respond to the greeting
            var responseMsg = new
            {
                type = "response.create",
                response = new
                {
                    modalities = new[] { "text", "audio" },
                    instructions = "You must speak exactly one concise greeting question, then stop and wait for caller response. Do not add booking assumptions."
                }
            };
            await SendJsonAsync(responseMsg);

            Log("📢 Greeting sent via conversation item");
        }
        catch (Exception ex)
        {
            Log($"⚠ Greeting send error: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        _rtpSession.OnRtpPacketReceived -= OnRtpPacketReceived;
        _session.OnAiInstruction -= OnSessionAiInstruction;

        _playout.Stop();

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "call ended",
                    CancellationToken.None);
            }
            catch { }
        }

        _ws?.Dispose();
        _cts.Dispose();

        Log("🔌 OpenAI Realtime disconnected");
    }

    // ─── Session Configuration ──────────────────────────────

    private async Task SendSessionConfig()
    {
        var systemPrompt = _session.GetSystemPrompt();

        // Use native G.711 passthrough — no PCM16 conversion needed
        var audioFormat = _codec == G711CodecType.PCMU ? "g711_ulaw" : "g711_alaw";

        var tools = new object[]
        {
            new
            {
                type = "function",
                name = "sync_booking_data",
                description = "MANDATORY: Persist booking data as collected from the caller. " +
                    "Must be called BEFORE generating any text response when user provides or amends booking details. " +
                    "CRITICAL: Include ALL fields the caller mentioned in their utterance — if they say " +
                    "'from X going to Y with 3 passengers', set pickup, destination, AND passengers in ONE call. " +
                    "NEVER split a compound utterance into multiple calls or ignore mentioned fields. " +
                    "CHANGE DETECTION: If the caller corrects ANY previously provided detail, you MUST call this tool " +
                    "IMMEDIATELY with the corrected value AND explain what changed in the 'interpretation' field.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["caller_name"] = new { type = "string", description = "Caller's name" },
                        ["caller_area"] = new { type = "string", description = "Caller's self-reported area/district (e.g. 'Earlsdon', 'Tile Hill'). Used as location bias for address resolution." },
                        ["pickup"] = new { type = "string", description = "Pickup address VERBATIM from caller's speech. MUST include house/flat numbers exactly as spoken. NEVER strip house numbers." },
                        ["destination"] = new { type = "string", description = "Destination address VERBATIM from caller's speech. MUST include house/flat numbers exactly as spoken. NEVER strip house numbers." },
                        ["passengers"] = new { type = "integer", description = "Number of passengers" },
                        ["pickup_time"] = new { type = "string", description = "Pickup time in YYYY-MM-DD HH:MM format (24h clock) or 'ASAP'. Use REFERENCE_DATETIME to resolve relative times." },
                        ["interpretation"] = new { type = "string", description = "Brief explanation of what you understood from the caller's speech. If this is a CORRECTION, explain what changed." },
                        ["special_instructions"] = new { type = "string", description = "Any special requests or notes the caller wants to add." }
                    }
                }
            }
        };

        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                voice = _voice,
                instructions = systemPrompt,
                input_audio_format = audioFormat,
                output_audio_format = audioFormat,
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.5,
                    prefix_padding_ms = 300,
                    silence_duration_ms = 500
                },
                tools,
                tool_choice = "auto"
            }
        };

        await SendJsonAsync(config);
        Log($"📋 Session configured: {audioFormat} passthrough, VAD + whisper, sync_booking_data tool");
        Log($"🔊 Realtime configured codec: {_codec} (format={audioFormat}, PT={G711Codec.PayloadType(_codec)})");
    }

    // ─── RTP → OpenAI (Caller Audio In) ─────────────────────

    private void OnRtpPacketReceived(
        IPEndPoint remoteEndPoint,
        SDPMediaTypesEnum mediaType,
        RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio) return;

        var payload = rtpPacket.Payload;

        // v4.3: Buffer audio while mic is gated (energy-tagged for selective flush)
        if (_micGated)
        {
            lock (_micGateBufferLock)
            {
                if (_micGated)
                {
                    var copy = new byte[payload.Length];
                    Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
                    _micGateBuffer.Add(copy);
                    return;
                }
            }
        }

        ForwardToOpenAi(payload);
    }

    private void ForwardToOpenAi(byte[] g711Payload)
    {
        try
        {
            // Native G.711 passthrough — no decode needed, OpenAI accepts g711_alaw/g711_ulaw directly
            var b64 = Convert.ToBase64String(g711Payload);
            var msg = new { type = "input_audio_buffer.append", audio = b64 };
            _ = SendJsonAsync(msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to forward RTP to OpenAI");
        }
    }

    /// <summary>
    /// Flush only the TRAILING speech frames from the mic gate buffer.
    /// Scans backwards from end to find the last contiguous speech region,
    /// then sends up to MicTailMaxFlush frames. This prevents hundreds of
    /// silence frames from causing ghost transcripts while preserving
    /// leading syllables of the caller's actual speech.
    /// </summary>
    private void FlushMicGateBuffer()
    {
        byte[][] toFlush;
        lock (_micGateBufferLock)
        {
            int count = _micGateBuffer.Count;
            if (count == 0)
            {
                Log("🎤 Mic buffer flush skipped (empty)");
                return;
            }

            // Tag each frame with speech energy
            int tailStart = Math.Max(0, count - MicTailMaxFlush);
            var selected = new List<byte[]>();

            for (int i = tailStart; i < count; i++)
            {
                var frame = _micGateBuffer[i];
                int nonSilence = 0;
                for (int j = 0; j < frame.Length; j++)
                {
                    if (frame[j] != _g711SilenceByte && ++nonSilence >= SpeechEnergyThreshold)
                        break;
                }
                if (nonSilence >= SpeechEnergyThreshold)
                    selected.Add(frame);
            }

            Log($"🎤 Mic buffer: {count} total, tail region {count - tailStart}, speech frames {selected.Count}");
            toFlush = selected.ToArray();
            _micGateBuffer.Clear();
        }

        if (toFlush.Length == 0)
        {
            Log("🎤 Mic tail flush skipped (no speech energy in tail)");
            return;
        }

        Log($"🎤 Flushing {toFlush.Length} speech frame(s) from tail");
        foreach (var f in toFlush)
            ForwardToOpenAi(f);
    }

    /// <summary>Clear the buffer (only used on session reset).</summary>
    private void ClearMicGateBuffer()
    {
        lock (_micGateBufferLock)
        {
            _micGateBuffer.Clear();
        }
    }

    // ─── OpenAI → RTP (AI Audio Out via Playout) ────────────

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[ReceiveBufferSize];
        var msgBuffer = new MemoryStream();

        try
        {
            while (!_cts.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log("🔌 WebSocket closed by server");
                    break;
                }

                msgBuffer.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(
                    msgBuffer.GetBuffer(), 0, (int)msgBuffer.Length);
                msgBuffer.SetLength(0);

                await HandleServerEvent(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"⚠ Receive loop error: {ex.Message}");
        }
    }

    private async Task HandleServerEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();

        switch (type)
        {
            // ── FAST PATH: Audio deltas — must never be blocked ──
            case "response.audio.delta":
                HandleAudioDelta(doc.RootElement);
                break;

            // ── AI response starting → arm mic gate ──
            case "response.audio.started":
            case "response.created":
                ArmMicGate();
                break;

            // ── AI finished sending audio → ungate mic ──
            case "response.audio.done":
                OnResponseAudioDone();
                break;

            case "conversation.item.input_audio_transcription.completed":
                HandleCallerTranscript(doc.RootElement);
                break;

            // ── Tool calls: sync_booking_data for freeform/burst extraction ──
            case "response.function_call_arguments.done":
                await HandleToolCallAsync(doc.RootElement);
                break;

            case "response.audio_transcript.done":
                var aiText = doc.RootElement.GetProperty("transcript").GetString();
                // Strip [CORRECTION:xxx] tags from the transcript before logging/processing
                // These tags are metadata for the session layer, not spoken content
                var cleanAiText = aiText != null 
                    ? System.Text.RegularExpressions.Regex.Replace(aiText, @"^\[CORRECTION:\w+\]\s*", "").Trim()
                    : aiText;
                Log($"🤖 AI: {cleanAiText}");
                // Feed Ada's transcript to session on background task — don't block receive loop
                // NOTE: Pass ORIGINAL text (with tags) to session so it can detect corrections
                if (!string.IsNullOrWhiteSpace(aiText))
                {
                    var text = aiText; // capture for closure
                    _ = Task.Run(async () => { try { await _session.ProcessAdaTranscriptAsync(text); } catch (Exception ex) { Log($"⚠️ Ada transcript processing error: {ex.Message}"); } });
                }
                break;

            // ── Barge-in: immediately cut everything and ungate (with debounce) ──
            case "input_audio_buffer.speech_started":
                _toolCalledInResponse = false; // Reset for new turn
                var now = Environment.TickCount64;
                var elapsed = now - _lastBargeInTick;
                if (elapsed < 250)
                {
                    Log($"🎤 Barge-in debounced ({elapsed}ms since last — skipped)");
                    break;
                }
                _lastBargeInTick = now;

                // If mic is already ungated, skip re-flush to avoid fragmenting
                // the caller's ongoing speech into tiny clips that Whisper misreads
                if (!_micGated)
                {
                    Log("🎤 Barge-in — mic already ungated, skipping re-flush");
                    break;
                }

                _micGated = false;
                _playout.Clear();
                FlushMicGateBuffer(); // v4.2: flush (not clear) — preserve leading speech
                Log("🎤 Barge-in — playout cleared, mic ungated, buffer flushed");
                try { OnBargeIn?.Invoke(); } catch { }
                break;

            // ── Speech ended → commit audio buffer ──
            // With tools enabled, we let the AI auto-respond so it can call sync_booking_data.
            // The tool call handler processes freeform input and drives state via the engine.
            // If the AI doesn't call a tool, the transcript handler serves as fallback.
            case "input_audio_buffer.speech_stopped":
                // Do NOT cancel — let the AI process and potentially call sync_booking_data
                break;

            // ── Cancel confirmed: now safe to send pending instruction ──
            case "response.canceled":
                Log("🛑 Response canceled");
                await SendPendingInstructionAsync();
                break;

            case "error":
                var errMsg = doc.RootElement.GetProperty("error")
                    .GetProperty("message").GetString();
                if (errMsg != null && (
                    errMsg.Contains("no active response found") ||
                    errMsg.Contains("buffer too small")))
                    break;
                Log($"⚠ OpenAI error: {errMsg}");
                break;

            case "session.created":
                Log("📡 Session created by server");
                break;

            case "session.updated":
                Log("📋 Session config accepted");
                break;
        }
    }

    private void HandleAudioDelta(JsonElement root)
    {
        var b64 = root.GetProperty("delta").GetString();
        if (string.IsNullOrEmpty(b64)) return;

        // GC-free decode: rent from ArrayPool, copy to exact-size span, return rental
        int maxBytes = (b64.Length / 4 + 1) * 3;
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            if (!Convert.TryFromBase64String(b64, rented, out int written) || written == 0)
                return;

            // Copy to exact-size array for BufferG711 (which takes ownership for framing)
            var g711 = new byte[written];
            Buffer.BlockCopy(rented, 0, g711, 0, written);

            // Buffer through playout engine (handles 160-byte framing + 20ms pacing)
            _playout.BufferG711(g711);

            // Fire audio out event for avatar feeding — non-blocking
            try { OnAudioOut?.Invoke(g711); } catch { }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private Task HandleCallerTranscript(JsonElement root)
    {
        var transcript = root.GetProperty("transcript").GetString();
        if (string.IsNullOrWhiteSpace(transcript)) return Task.CompletedTask;

        Log($"👤 Caller: {transcript}");

        // If a tool call already handled this turn's data, skip transcript processing
        // to avoid redundant slot updates. The tool call is the authoritative source.
        if (_toolCalledInResponse)
        {
            Log("📋 Transcript skipped — sync_booking_data already processed this turn");
            return Task.CompletedTask;
        }

        // ── Hybrid Tool-First Strategy ──
        // The AI may be in the process of calling sync_booking_data right now.
        // Whisper transcripts arrive concurrently with the AI's response processing.
        // If we cancel the response immediately, we kill the tool call before it fires.
        // Instead, wait briefly on a background task for the tool call to arrive.
        // If it does, skip transcript processing. If not, fall back to deterministic path.
        var capturedTranscript = transcript; // capture for closure
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait up to 1.5s for a tool call to arrive (AI typically calls within ~800ms)
                for (int i = 0; i < 15; i++)
                {
                    await Task.Delay(100, _cts.Token);
                    if (_toolCalledInResponse)
                    {
                        Log("📋 Transcript skipped (deferred) — sync_booking_data processed this turn");
                        return;
                    }
                }

                // No tool call arrived — fall back to deterministic transcript processing.
                Log("📋 No tool call received — falling back to transcript processing");
                await SendJsonAsync(new { type = "response.cancel" });
                await _session.ProcessCallerResponseAsync(capturedTranscript, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"⚠ Error processing transcript: {ex.Message}");
            }
        });

        return Task.CompletedTask;
    }

    // ─── Tool Call Handling (sync_booking_data) ─────────────

    /// <summary>
    /// Debounce guard for rapid-fire tool calls (ms tick).
    /// </summary>
    private long _lastToolCallTick;

    /// <summary>
    /// Handle response.function_call_arguments.done from OpenAI Realtime.
    /// Parses the tool call, routes to CleanCallSession, sends result back, triggers follow-up response.
    /// </summary>
    private async Task HandleToolCallAsync(JsonElement root)
    {
        _toolCalledInResponse = true; // Prevent transcript handler from redundant processing

        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastToolCallTick) < 200) return;
        Volatile.Write(ref _lastToolCallTick, now);

        var callId = root.TryGetProperty("call_id", out var c) ? c.GetString() : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : "";
        var argsStr = root.TryGetProperty("arguments", out var a) ? a.GetString() : "{}";

        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name))
        {
            Log("⚠ Tool call missing call_id or name — ignoring");
            return;
        }

        Log($"🔧 Tool call: {name}");

        Dictionary<string, object?> args;
        try
        {
            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsStr ?? "{}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex)
        {
            Log($"⚠ Tool args parse error: {ex.Message}");
            args = new();
        }

        // Route to session for processing
        // HandleToolCallAsync may fire OnAiInstruction synchronously, which sets _pendingInstruction.
        object result;
        try
        {
            result = await _session.HandleToolCallAsync(name, args, _cts.Token);
        }
        catch (Exception ex)
        {
            Log($"⚠ Tool handler error: {ex.Message}");
            result = new { error = ex.Message };
        }

        var resultJson = result is string s ? s : JsonSerializer.Serialize(result);
        Log($"✅ Tool result: {(resultJson.Length > 200 ? resultJson[..200] + "..." : resultJson)}");

        // ── CRITICAL SEQUENCING ──
        // The engine may have queued an instruction (e.g., VerifyingPickup readback).
        // We MUST apply it BEFORE sending the tool result, because OpenAI auto-generates
        // a response after receiving function_call_output. If the instruction isn't applied
        // yet, the AI will freestyle with stale context (e.g., "Where would you like to go?"
        // instead of doing the readback).
        
        // Step 1: Consume any pending instruction and send session.update FIRST
        var pending = Interlocked.Exchange(ref _pendingInstruction, null);
        bool isSilent = false;
        if (pending != null)
        {
            var vadConfig = GetVadConfigForCurrentState();
            await SendJsonAsync(new
            {
                type = "session.update",
                session = new
                {
                    instructions = pending.Text,
                    turn_detection = vadConfig
                }
            });
            isSilent = IsSilentInstruction(pending.Text);
            Log($"📋 Pre-tool-result instruction applied (VAD: {vadConfig.type})");
        }

        // Step 2: Send the tool result back to OpenAI
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = resultJson
            }
        });

        // Step 3: Explicitly trigger response.create (unless silent state)
        // We must send this ourselves because we consumed the pending instruction above,
        // so StartInstructionSequenceAsync won't fire response.create.
        if (pending != null && !isSilent)
        {
            await SendJsonAsync(new
            {
                type = "response.create",
                response = new
                {
                    modalities = new[] { "text", "audio" },
                    instructions = BuildStrictResponseInstruction(pending.Text)
                }
            });
            Log($"📋 Post-tool-result response.create sent");
        }
        else if (pending == null)
        {
            // No instruction was queued — this shouldn't normally happen with sync_booking_data,
            // but send response.create as a safety net so the AI doesn't hang silently.
            await SendJsonAsync(new { type = "response.create" });
        }
        // If isSilent, don't send response.create — the AI should stay quiet.
    }

    // ─── Instruction Updates (Event-Driven v4.1) ────────────

    /// <summary>
    /// Determines if the instruction is a "silent" state where Ada should not speak.
    /// For these states, we update session instructions but do NOT trigger response.create.
    /// </summary>
    private static bool IsSilentInstruction(string instruction)
    {
        return instruction.Contains("ABSOLUTE SILENCE") ||
               instruction.Contains("Do NOT speak at all");
    }

    private void OnSessionAiInstruction(string instruction, bool isReprompt)
    {
        Log(isReprompt ? "📋 Queuing REPROMPT instruction update" : "📋 Queuing instruction update");
        _ = StartInstructionSequenceAsync(new PendingInstruction(instruction, isReprompt));
    }

    /// <summary>
    /// Sends cancel and stores the pending instruction.
    /// The actual session.update + response.create happens when response.canceled arrives.
    /// If no response is active, the fallback timer sends it after 150ms.
    /// </summary>
    private async Task StartInstructionSequenceAsync(PendingInstruction pending)
    {
        try
        {
            Interlocked.Exchange(ref _pendingInstruction, pending);

            // Cancel any in-progress response — response.canceled event will trigger the rest
            await SendJsonAsync(new { type = "response.cancel" });

            if (pending.IsReprompt)
            {
                // HARDENED: For reprompts, also clear the input audio buffer to prevent
                // the AI from using stale audio context to generate a rogue response
                await SendJsonAsync(new { type = "input_audio_buffer.clear" });
                Log("🔒 Reprompt: cleared input audio buffer");
            }

            // Fallback: if no response was active, response.canceled won't fire.
            // Wait briefly, then check if _pendingInstruction is still set.
            _ = FallbackInstructionSendAsync();
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
    /// Increased from 150ms to avoid racing with OpenAI's auto-response start.
    /// </summary>
    private async Task FallbackInstructionSendAsync()
    {
        try
        {
            await Task.Delay(300, _cts.Token);
            await SendPendingInstructionAsync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Send the pending instruction (session.update + response.create).
    /// Called either by response.canceled event or by fallback timer.
    /// Thread-safe: only the first caller sends; subsequent calls are no-ops.
    /// Includes Auto VAD Switching based on current engine state.
    /// </summary>
    private async Task SendPendingInstructionAsync()
    {
        var pending = Interlocked.Exchange(ref _pendingInstruction, null);
        if (pending == null) return; // already sent or no pending

        var instruction = pending.Text;

        try
        {
            // ── Auto VAD Switching ──
            var vadConfig = GetVadConfigForCurrentState();

            await SendJsonAsync(new
            {
                type = "session.update",
                session = new
                {
                    instructions = instruction,
                    turn_detection = vadConfig
                }
            });

            // ── Silent State Suppression ──
            // For Extracting/Geocoding, do NOT send response.create.
            // This prevents Ada from speaking "your taxi is on its way" etc.
            if (IsSilentInstruction(instruction))
            {
                Log($"📋 Silent instruction update sent (VAD: {vadConfig.type}) — NO response.create");
                return;
            }

            var isReprompt = pending.IsReprompt;

            // ── Reprompt Grounding ──
            // Inject an explicit system-level conversation item to break the model
            // out of any hallucinated booking confirmation context before re-asking.
            if (isReprompt)
            {
                await SendJsonAsync(new
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
                });
            }

            await SendJsonAsync(new
            {
                type = "response.create",
                response = new
                {
                    modalities = new[] { "text", "audio" },
                    instructions = isReprompt
                        ? BuildRepromptResponseInstruction(instruction)
                        : BuildStrictResponseInstruction(instruction)
                }
            });

            Log(isReprompt
                ? $"🔒 REPROMPT instruction sent (VAD: {vadConfig.type})"
                : $"📋 Instruction update sent (VAD: {vadConfig.type})");
        }
        catch (Exception ex)
        {
            Log($"⚠ Instruction send error: {ex.Message}");
        }
    }

    private static string BuildStrictResponseInstruction(string instruction)
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
            - ⛔ Do NOT greet the caller. Do NOT say "Welcome to Ada Taxi". The call is already in progress.

            {instruction}
            """;
    }

    /// <summary>
    /// Ultra-strict instruction wrapper for reprompts after validation failure.
    /// The AI MUST only re-ask the question — nothing else.
    /// </summary>
    private static string BuildRepromptResponseInstruction(string instruction)
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

    // ─── WebSocket Send ─────────────────────────────────────

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private async Task SendJsonAsync(object payload)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // G.711 codec logic moved to shared G711Codec class

    private void Log(string msg)
    {
        // Fire-and-forget to avoid blocking audio/WebSocket threads on logger I/O
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _logger.LogInformation(msg); } catch { }
        });
        OnLog?.Invoke($"[RT:{_callId}] {msg}");
    }
}
