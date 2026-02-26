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
/// Mic Gate v4.1:
///   Mic gated while AI is speaking.
///   Ungated when response.audio.done AND playout queue drained.
///   Barge-in (speech_started) immediately cuts and ungates.
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

    // ─── Mic Gate v4.1 ─────────────────────────────────────
    // Mic gated while AI speaks. Ungated when response.audio.done AND playout drains.
    // No echo guard timer — just wait for both conditions.

    /// <summary>True = mic is blocked.</summary>
    private volatile bool _micGated;

    /// <summary>OpenAI finished sending audio deltas.</summary>
    private volatile bool _responseCompleted;

    /// <summary>Pending instruction to send after response.canceled arrives.</summary>
    private volatile string? _pendingInstruction;

    // Ring buffer mic tail: keeps only last 10 frames (200ms) while mic is gated
    private const int MicTailMaxFrames = 10;
    private readonly byte[][] _micTail = new byte[MicTailMaxFrames][];
    private int _micTailIndex;
    private int _micTailCount;
    private readonly object _micTailLock = new();

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

        _playout = new G711RtpPlayout(rtpSession, codec);
        _playout.OnLog += msg => Log(msg);
        _playout.OnQueueEmpty += OnPlayoutQueueEmpty;
    }

    // ─── Mic Gate Logic (v4.1) ─────────────────────────────

    /// <summary>Called when playout queue drains.</summary>
    private void OnPlayoutQueueEmpty()
    {
        if (!_micGated || !_responseCompleted) return;
        UngateMic();
    }

    /// <summary>Called when response.audio.done arrives.</summary>
    private void OnResponseAudioDone()
    {
        _responseCompleted = true;
        _playout.Flush();
        // If playout already drained, ungate now
        if (_playout.QueuedFrames == 0)
            UngateMic();
    }

    private void UngateMic()
    {
        if (!_micGated) return;
        _micGated = false;
        Log("🔓 Mic ungated (audio done + playout drained)");
        FlushMicTail();
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
        _session.OnAiInstruction += OnAiInstruction;

        // Start playout engine
        _playout.Start();

        // Start receive loop
        _receiveTask = Task.Run(ReceiveLoopAsync);

        Log("✅ Bidirectional audio bridge active (mic gate v4.1)");

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
        _session.OnAiInstruction -= OnAiInstruction;

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
                tools = Array.Empty<object>()
            }
        };

        await SendJsonAsync(config);
        Log($"📋 Session configured: {audioFormat} passthrough, VAD + whisper, no tools");
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

        // Ring-buffer mic tail: keep only last 200ms while mic is gated
        if (_micGated)
        {
            lock (_micTailLock)
            {
                if (_micGated)
                {
                    var copy = new byte[payload.Length];
                    Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
                    _micTail[_micTailIndex] = copy;
                    _micTailIndex = (_micTailIndex + 1) % MicTailMaxFrames;
                    if (_micTailCount < MicTailMaxFrames) _micTailCount++;
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
    /// Flush the ring-buffer mic tail (max 10 frames / 200ms).
    /// Preserves initial syllables without flooding OpenAI.
    /// </summary>
    private void FlushMicTail()
    {
        byte[][] frames;
        int count;

        lock (_micTailLock)
        {
            if (_micTailCount == 0) return;
            count = _micTailCount;
            frames = new byte[count][];
            int start = (_micTailIndex - count + MicTailMaxFrames) % MicTailMaxFrames;
            for (int i = 0; i < count; i++)
                frames[i] = _micTail[(start + i) % MicTailMaxFrames];
            _micTailCount = 0;
        }

        Log($"🎤 Flushing {count} mic tail frames");
        foreach (var f in frames)
            ForwardToOpenAi(f);
    }

    /// <summary>Clear the ring buffer (used on barge-in).</summary>
    private void ClearMicTail()
    {
        lock (_micTailLock)
        {
            _micTailCount = 0;
            _micTailIndex = 0;
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

            case "response.audio_transcript.done":
                var aiText = doc.RootElement.GetProperty("transcript").GetString();
                Log($"🤖 AI: {aiText}");
                // Feed Ada's transcript to session on background task — don't block receive loop
                if (!string.IsNullOrWhiteSpace(aiText))
                {
                    var text = aiText; // capture for closure
                    _ = Task.Run(async () => { try { await _session.ProcessAdaTranscriptAsync(text); } catch (Exception ex) { Log($"⚠️ Ada transcript processing error: {ex.Message}"); } });
                }
                break;

            // ── Barge-in: immediately cut everything and ungate ──
            case "input_audio_buffer.speech_started":
                _micGated = false;
                _playout.Clear();
                ClearMicTail();
                Log("🎤 Barge-in — playout cleared, mic ungated");
                try { OnBargeIn?.Invoke(); } catch { }
                break;

            // ── Speech ended → proactively cancel auto-response ──
            // With VAD enabled, OpenAI auto-generates a response when speech ends.
            // We MUST cancel it immediately because our deterministic engine drives
            // all responses via [INSTRUCTION] session.update + response.create.
            // If we wait for the transcript, the auto-response has already started speaking.
            case "input_audio_buffer.speech_stopped":
                await SendJsonAsync(new { type = "response.cancel" });
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

        // Native G.711 passthrough — OpenAI already sends g711_alaw/g711_ulaw, no conversion needed
        var g711 = Convert.FromBase64String(b64);

        // Buffer through playout engine (handles 160-byte framing + 20ms pacing)
        _playout.BufferG711(g711);

        // Fire audio out event for avatar feeding — non-blocking
        try { OnAudioOut?.Invoke(g711); } catch { }
    }

    private Task HandleCallerTranscript(JsonElement root)
    {
        var transcript = root.GetProperty("transcript").GetString();
        if (string.IsNullOrWhiteSpace(transcript)) return Task.CompletedTask;

        Log($"👤 Caller: {transcript}");

        // Process transcript on background task to avoid blocking audio delta receive loop.
        // The auto-response was already canceled on speech_stopped (above).
        // ProcessCallerResponseAsync → engine → emits instruction → session.update + response.create.
        _ = Task.Run(async () =>
        {
            try
            {
                await _session.ProcessCallerResponseAsync(transcript, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"⚠ Error processing transcript: {ex.Message}");
            }
        });

        return Task.CompletedTask;
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

    private void OnAiInstruction(string instruction)
    {
        Log("📋 Queuing instruction update");
        _ = StartInstructionSequenceAsync(instruction);
    }

    /// <summary>
    /// Sends cancel and stores the pending instruction.
    /// The actual session.update + response.create happens when response.canceled arrives.
    /// If no response is active, the fallback timer sends it after 150ms.
    /// </summary>
    private async Task StartInstructionSequenceAsync(string instruction)
    {
        try
        {
            _pendingInstruction = instruction;

            // Cancel any in-progress response — response.canceled event will trigger the rest
            await SendJsonAsync(new { type = "response.cancel" });

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
        var instruction = Interlocked.Exchange(ref _pendingInstruction, null);
        if (instruction == null) return; // already sent or no pending

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

            await SendJsonAsync(new
            {
                type = "response.create",
                response = new
                {
                    modalities = new[] { "text", "audio" },
                    instructions = BuildStrictResponseInstruction(instruction)
                }
            });

            Log($"📋 Instruction update sent (VAD: {vadConfig.type})");
        }
        catch (Exception ex)
        {
            Log($"⚠ Instruction send error: {ex.Message}");
        }
    }

    private static string BuildStrictResponseInstruction(string instruction)
    {
        // Extra guard for fare presentation — prevent greeting reset
        var antiGreeting = instruction.Contains("Present the booking summary") || instruction.Contains("PresentingFare")
            ? "\n- ⚠️ You are MID-CALL. Do NOT greet the caller. Do NOT say 'Welcome to Ada Taxi'. The call is already in progress."
            : "";

        return $"""
            CRITICAL EXECUTION MODE:
            - Follow the [INSTRUCTION] below exactly.
            - Ask ONLY what the instruction asks for in this turn.
            - Do NOT confirm booking, dispatch taxi, end call, or summarize unless explicitly instructed.
            - Do NOT invent or normalize addresses/numbers.
            - Keep to one concise response, then wait.{antiGreeting}

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
