using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AdaCleanVersion.Audio;
using AdaCleanVersion.Session;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Bridges OpenAI Realtime API (WebSocket) ↔ SIPSorcery RTPSession via MuLawRtpPlayout.
///
/// Audio flow:
///   Caller → RTP (G.711 µ-law) → decode to PCM16 → base64 → OpenAI input_audio_buffer.append
///   OpenAI response.audio.delta → base64 → PCM16 → encode to µ-law → MuLawRtpPlayout → RTP
///
/// The playout engine runs on a dedicated high-priority thread with a WaitableTimer-based
/// 20ms tick, providing jitter-free outbound audio instead of sending directly from the
/// WebSocket receive thread.
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
    private readonly CancellationTokenSource _cts = new();

    private ClientWebSocket? _ws;
    private Task? _receiveTask;

    /// <summary>
    /// Jitter-buffered playout engine — all outbound audio is routed through this
    /// instead of calling rtpSession.SendAudioFrame directly.
    /// </summary>
    private readonly MuLawRtpPlayout _playout;

    // Mic gate: suppress caller audio while AI is speaking
    private volatile bool _micGated;

    // Echo-tail buffer: stores audio received while mic is gated
    private readonly List<byte[]> _micGateBuffer = new();
    private readonly object _micGateLock = new();

    public event Action<string>? OnLog;

    public OpenAiRealtimeClient(
        string apiKey,
        string model,
        string voice,
        string callId,
        RTPSession rtpSession,
        CleanCallSession session,
        ILogger logger)
    {
        _apiKey = apiKey;
        _model = model;
        _voice = voice;
        _callId = callId;
        _rtpSession = rtpSession;
        _session = session;
        _logger = logger;

        _playout = new MuLawRtpPlayout(rtpSession);
        _playout.OnLog += msg => Log(msg);
        _playout.OnQueueEmpty += () =>
        {
            // Playout drained — ungate mic (playout-aware ungating)
            if (_micGated)
            {
                _micGated = false;
                FlushMicGateBuffer();
            }
        };
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

        Log("✅ Bidirectional audio bridge active (playout-buffered)");
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

        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                voice = _voice,
                instructions = systemPrompt,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
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
        Log("📋 Session configured: VAD + whisper transcription, no tools");
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

    private void ForwardToOpenAi(byte[] mulawPayload)
    {
        try
        {
            // Decode G.711 µ-law → PCM16
            var pcm16 = new byte[mulawPayload.Length * 2];
            for (int i = 0; i < mulawPayload.Length; i++)
            {
                var sample = MuLawDecode(mulawPayload[i]);
                pcm16[i * 2] = (byte)(sample & 0xFF);
                pcm16[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            var b64 = Convert.ToBase64String(pcm16);
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

            case "response.audio.started":
            case "response.created":
                _micGated = true;
                break;

            // NOTE: mic ungate is now driven by playout drain (OnQueueEmpty),
            // not by response.audio.done — this prevents echo from early ungate.
            case "response.audio.done":
                // Flush any remaining partial frame in the accumulator
                _playout.Flush();
                break;

            case "conversation.item.input_audio_transcription.completed":
                await HandleCallerTranscript(doc.RootElement);
                break;

            case "response.audio_transcript.done":
                var aiText = doc.RootElement.GetProperty("transcript").GetString();
                Log($"🤖 AI: {aiText}");
                break;

            case "input_audio_buffer.speech_started":
                // Barge-in: hard-cut the playout and ungate mic
                _micGated = false;
                _playout.Clear();
                lock (_micGateLock) _micGateBuffer.Clear();
                Log("🎤 Barge-in detected — playout cleared");
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

        var pcm16 = Convert.FromBase64String(b64);

        // Encode PCM16 → G.711 µ-law
        var mulaw = new byte[pcm16.Length / 2];
        for (int i = 0; i < mulaw.Length; i++)
        {
            var sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            mulaw[i] = MuLawEncode(sample);
        }

        // Buffer through playout engine (not sent directly!)
        _playout.BufferMuLaw(mulaw);
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

    // ─── G.711 µ-law Codec ──────────────────────────────────

    private static readonly short[] MuLawDecompressTable = BuildMuLawDecompressTable();

    private static short MuLawDecode(byte mulaw) => MuLawDecompressTable[mulaw];

    private static byte MuLawEncode(short sample)
    {
        const int BIAS = 0x84;
        const int MAX = 32635;

        var sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        if (sample > MAX) sample = MAX;

        sample = (short)(sample + BIAS);

        var exponent = 7;
        for (var expMask = 0x4000; (sample & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }

        var mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)(~(sign | (exponent << 4) | mantissa));
    }

    private static short[] BuildMuLawDecompressTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            var mulaw = (byte)~i;
            var sign = (mulaw & 0x80) != 0;
            var exponent = (mulaw >> 4) & 0x07;
            var mantissa = mulaw & 0x0F;
            var sample = (mantissa << 3) + 0x84;
            sample <<= exponent;
            sample -= 0x84;
            table[i] = (short)(sign ? -sample : sample);
        }
        return table;
    }

    private void Log(string msg)
    {
        _logger.LogInformation(msg);
        OnLog?.Invoke($"[RT:{_callId}] {msg}");
    }
}
