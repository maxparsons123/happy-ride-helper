using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaCleanVersion.Session;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Bridges OpenAI Realtime API (WebSocket) ↔ SIPSorcery RTPSession.
///
/// Audio flow:
///   Caller → RTP (G.711 µ-law) → decode to PCM16 → base64 → OpenAI input_audio_buffer.append
///   OpenAI response.audio.delta → base64 → PCM16 → encode to G.711 µ-law → RTP → Caller
///
/// Transcript flow:
///   OpenAI conversation.item.input_audio_transcription.completed → session.ProcessCallerResponseAsync
///   OpenAI response.audio_transcript.done → logged only (AI output)
///
/// Session instructions:
///   CleanCallSession.OnAiInstruction → session.update with new instructions
///
/// Key design:
///   - One instance per call, disposed on hangup
///   - No AI tools registered — voice-only
///   - Mic gate during AI speech to prevent echo
///   - Hard-cut barge-in via _clearEpoch guard on playout
/// </summary>
public sealed class OpenAiRealtimeClient : IAsyncDisposable
{
    private const string RealtimeUrl = "wss://api.openai.com/v1/realtime";
    private const int SendBufferSize = 4096;
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

    // Mic gate: suppress caller audio while AI is speaking
    private volatile bool _micGated;

    // Playout epoch for barge-in hard-cut
    private volatile int _playoutEpoch;

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
    }

    // ─── Lifecycle ──────────────────────────────────────────

    /// <summary>
    /// Connect to OpenAI Realtime, configure session, and start bidirectional streaming.
    /// </summary>
    public async Task ConnectAsync()
    {
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var url = $"{RealtimeUrl}?model={_model}";
        await _ws.ConnectAsync(new Uri(url), _cts.Token);

        Log("🔌 Connected to OpenAI Realtime");

        // Configure session: voice, VAD, input transcription, no tools
        await SendSessionConfig();

        // Wire RTP → OpenAI (caller audio in)
        _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;

        // Wire CleanCallSession instructions → OpenAI session.update
        _session.OnAiInstruction += OnAiInstruction;

        // Start receive loop
        _receiveTask = Task.Run(ReceiveLoopAsync);

        Log("✅ Bidirectional audio bridge active");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        _rtpSession.OnRtpPacketReceived -= OnRtpPacketReceived;
        _session.OnAiInstruction -= OnAiInstruction;

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { /* swallow */ }
        }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "call ended",
                    CancellationToken.None);
            }
            catch { /* best effort */ }
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
                tools = Array.Empty<object>() // No tools — voice-only
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
        if (_micGated) return; // Suppress echo during AI speech

        try
        {
            // Decode G.711 µ-law → PCM16
            var payload = rtpPacket.Payload;
            var pcm16 = new byte[payload.Length * 2];
            for (int i = 0; i < payload.Length; i++)
            {
                var sample = MuLawDecode(payload[i]);
                pcm16[i * 2] = (byte)(sample & 0xFF);
                pcm16[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            // Send as base64 to OpenAI
            var b64 = Convert.ToBase64String(pcm16);
            var msg = new { type = "input_audio_buffer.append", audio = b64 };

            // Fire-and-forget (non-blocking for RTP thread)
            _ = SendJsonAsync(msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to forward RTP to OpenAI");
        }
    }

    // ─── OpenAI → RTP (AI Audio Out) ────────────────────────

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
        catch (OperationCanceledException) { /* expected on dispose */ }
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
            // ── AI audio chunk → decode and send via RTP ──
            case "response.audio.delta":
                HandleAudioDelta(doc.RootElement);
                break;

            // ── AI started speaking → gate the mic ──
            case "response.audio.started":
            case "response.created":
                _micGated = true;
                break;

            // ── AI finished speaking → ungate the mic ──
            case "response.audio.done":
                // Small delay to let playout drain before ungating
                await Task.Delay(200);
                _micGated = false;
                break;

            // ── Caller transcript (from Whisper) → feed to session ──
            case "conversation.item.input_audio_transcription.completed":
                await HandleCallerTranscript(doc.RootElement);
                break;

            // ── AI transcript (what AI said) → log only ──
            case "response.audio_transcript.done":
                var aiText = doc.RootElement.GetProperty("transcript").GetString();
                Log($"🤖 AI: {aiText}");
                break;

            // ── VAD: speech started → barge-in: hard-cut playout ──
            case "input_audio_buffer.speech_started":
                _micGated = false;
                Interlocked.Increment(ref _playoutEpoch); // Invalidate queued audio
                Log("🎤 Barge-in detected");
                break;

            // ── Errors ──
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

        var epoch = _playoutEpoch; // Capture epoch before processing

        var pcm16 = Convert.FromBase64String(b64);

        // Encode PCM16 → G.711 µ-law for RTP
        var mulaw = new byte[pcm16.Length / 2];
        for (int i = 0; i < mulaw.Length; i++)
        {
            var sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            mulaw[i] = MuLawEncode(sample);
        }

        // Hard-cut guard: if epoch changed (barge-in), drop this frame
        if (epoch != _playoutEpoch) return;

        // Send via RTP
        _rtpSession.SendAudioFrame(
            (uint)(mulaw.Length), // timestamp increment (8kHz, 1 sample = 1 unit)
            (int)SDPWellKnownMediaFormatsEnum.PCMU,
            mulaw);
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
        Log($"📋 Sending instruction update");

        var msg = new
        {
            type = "session.update",
            session = new
            {
                instructions = instruction
            }
        };

        _ = SendJsonAsync(msg);

        // Also create a response to make AI speak the instruction
        var responseMsg = new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" }
            }
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
            await _ws.SendAsync(
                bytes, WebSocketMessageType.Text, true, _cts.Token);
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
        var mulaw = (byte)(~(sign | (exponent << 4) | mantissa));

        return mulaw;
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
