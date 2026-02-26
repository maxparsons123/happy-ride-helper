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
/// Mic Gate v3.1 — Dual-Latch with Echo Guard:
///   Latch 1: _responseCompleted  — set when OpenAI sends response.audio.done
///   Latch 2: OnQueueEmpty        — set when playout queue fully drains
///   Both latches + echo guard timer must pass before mic ungates.
///   _drainGateTaskId ensures only the latest drain task can ungate ("latest task wins").
///   Barge-in (speech_started) immediately cuts both latches and ungates.
/// </summary>
public sealed class OpenAiRealtimeClient : IAsyncDisposable
{
    private const string RealtimeUrl = "wss://api.openai.com/v1/realtime";
    private const int ReceiveBufferSize = 16384;

    /// <summary>Configurable echo guard delay (ms) after both latches before ungating mic.</summary>
    private const int EchoGuardMs = 200;

    /// <summary>Debounce window — ignore OnQueueEmpty signals within this period (ms).</summary>
    private const int DrainDebounceMs = 200;

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

    // ─── Dual-Latch Mic Gate (v3.1) ─────────────────────────
    // The mic is gated (blocked) while AI audio is playing.
    // It only ungates when BOTH latches are set AND the echo guard timer expires.

    /// <summary>True = mic is blocked. Only ungated by the dual-latch mechanism or barge-in.</summary>
    private volatile bool _micGated;

    /// <summary>Latch 1: OpenAI finished sending audio deltas (response.audio.done received).</summary>
    private volatile bool _responseCompleted;

    /// <summary>Latch 2: Playout queue has fully drained (all audio sent to caller).</summary>
    private volatile bool _playoutDrained;

    /// <summary>
    /// Monotonically increasing ID for drain-gate tasks.
    /// "Latest task wins" — only the task whose ID matches _drainGateTaskId can ungate.
    /// Prevents stale drain tasks from ungating after a new response starts.
    /// </summary>
    private volatile int _drainGateTaskId;

    // Echo-tail buffer: stores audio received while mic is gated
    private readonly List<byte[]> _micGateBuffer = new();
    private readonly object _micGateLock = new();

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

    // ─── Dual-Latch Logic ───────────────────────────────────

    /// <summary>
    /// Called when playout queue drains. Sets latch 2 and checks if both latches are set.
    /// </summary>
    private void OnPlayoutQueueEmpty()
    {
        _playoutDrained = true;
        TryUngateMic();
    }

    /// <summary>
    /// Called when response.audio.done arrives. Sets latch 1 and checks if both latches are set.
    /// </summary>
    private void OnResponseAudioDone()
    {
        _responseCompleted = true;
        _playout.Flush(); // flush any partial frame in the accumulator
        TryUngateMic();
    }

    /// <summary>
    /// Check both latches. If both are set, start the echo guard timer.
    /// Only the "latest task wins" — stale drain tasks are discarded.
    /// </summary>
    private void TryUngateMic()
    {
        if (!_micGated) return;
        if (!_responseCompleted || !_playoutDrained) return;

        // Both latches set — start echo guard timer
        var taskId = Interlocked.Increment(ref _drainGateTaskId);
        _ = RunEchoGuardAsync(taskId);
    }

    /// <summary>
    /// Wait for the echo guard period, then ungate if this task is still the latest.
    /// </summary>
    private async Task RunEchoGuardAsync(int taskId)
    {
        try
        {
            await Task.Delay(EchoGuardMs, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // "Latest task wins" — if a new response started during the guard period,
        // _drainGateTaskId will have been incremented and this task is stale.
        if (Volatile.Read(ref _drainGateTaskId) != taskId) return;

        // Still the latest — ungate
        _micGated = false;
        Log("🔓 Mic ungated (dual-latch + echo guard)");
        FlushMicGateBuffer();
    }

    /// <summary>
    /// Reset both latches when a new AI response starts.
    /// </summary>
    private void ArmMicGate()
    {
        _micGated = true;
        _responseCompleted = false;
        _playoutDrained = false;
        // Increment drain gate task ID to invalidate any pending echo guard timer
        Interlocked.Increment(ref _drainGateTaskId);
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

        Log("✅ Bidirectional audio bridge active (dual-latch mic gate v3.1)");
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
    }

    // ─── RTP → OpenAI (Caller Audio In) ─────────────────────

    private void OnRtpPacketReceived(
        IPEndPoint remoteEndPoint,
        SDPMediaTypesEnum mediaType,
        RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio) return;

        var payload = rtpPacket.Payload;

        // Echo-tail: buffer audio while mic is gated
        if (_micGated)
        {
            lock (_micGateLock)
            {
                if (_micGated) // double-check under lock
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
    /// Flush echo-tail buffer: sends any audio captured while mic was gated.
    /// Preserves initial syllables that would otherwise be clipped.
    /// </summary>
    private void FlushMicGateBuffer()
    {
        List<byte[]> buffered;
        lock (_micGateLock)
        {
            if (_micGateBuffer.Count == 0) return;
            buffered = new List<byte[]>(_micGateBuffer);
            _micGateBuffer.Clear();
        }

        Log($"🎤 Flushing {buffered.Count} echo-tail frames");
        foreach (var chunk in buffered)
            ForwardToOpenAi(chunk);
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
            case "response.audio.delta":
                HandleAudioDelta(doc.RootElement);
                break;

            // ── AI response starting → arm the dual-latch mic gate ──
            case "response.audio.started":
            case "response.created":
                ArmMicGate();
                break;

            // ── Latch 1: OpenAI finished sending audio ──
            case "response.audio.done":
                OnResponseAudioDone();
                break;

            case "conversation.item.input_audio_transcription.completed":
                await HandleCallerTranscript(doc.RootElement);
                break;

            case "response.audio_transcript.done":
                var aiText = doc.RootElement.GetProperty("transcript").GetString();
                Log($"🤖 AI: {aiText}");
                break;

            // ── Barge-in: immediately cut everything and ungate ──
            case "input_audio_buffer.speech_started":
                _responseCompleted = false;
                _playoutDrained = false;
                Interlocked.Increment(ref _drainGateTaskId); // kill pending echo guard
                _micGated = false;
                _playout.Clear();
                lock (_micGateLock) _micGateBuffer.Clear();
                Log("🎤 Barge-in — playout cleared, mic ungated immediately");
                OnBargeIn?.Invoke();
                break;

            case "error":
                var errMsg = doc.RootElement.GetProperty("error")
                    .GetProperty("message").GetString();
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

        // Fire audio out event for avatar feeding
        OnAudioOut?.Invoke(g711);
    }

    private async Task HandleCallerTranscript(JsonElement root)
    {
        var transcript = root.GetProperty("transcript").GetString();
        if (string.IsNullOrWhiteSpace(transcript)) return;

        Log($"👤 Caller: {transcript}");

        try
        {
            await _session.ProcessCallerResponseAsync(transcript, _cts.Token);
        }
        catch (Exception ex)
        {
            Log($"⚠ Error processing transcript: {ex.Message}");
        }
    }

    // ─── Instruction Updates ────────────────────────────────

    private void OnAiInstruction(string instruction)
    {
        Log("📋 Sending instruction update");

        var msg = new
        {
            type = "session.update",
            session = new { instructions = instruction }
        };
        _ = SendJsonAsync(msg);

        var responseMsg = new
        {
            type = "response.create",
            response = new { modalities = new[] { "text", "audio" } }
        };
        _ = SendJsonAsync(responseMsg);
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
        _logger.LogInformation(msg);
        OnLog?.Invoke($"[RT:{_callId}] {msg}");
    }
}
