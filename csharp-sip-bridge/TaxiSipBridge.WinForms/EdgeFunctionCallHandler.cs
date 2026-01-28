using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Call handler that routes audio to Edge Function (Supabase).
/// </summary>
public class EdgeFunctionCallHandler : ICallHandler
{
    private readonly string _wsUrl;
    private readonly AudioMode _audioMode;
    private readonly int _jitterBufferMs;
    
    private volatile bool _isInCall;
    private volatile bool _disposed;
    private volatile bool _isBotSpeaking;
    
    private VoIPMediaSession? _currentMediaSession;
    private AdaAudioSource? _adaAudioSource;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _callCts;

    private const int FLUSH_PACKETS = 25;
    private const int RMS_NOISE_FLOOR = 650;
    private const int RMS_ECHO_CEILING = 20000;
    private const int GREETING_PROTECTION_PACKETS = 150;

    public event Action<string>? OnLog;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnCallerAudioMonitor;

    public bool IsInCall => _isInCall;

    public EdgeFunctionCallHandler(string wsUrl, AudioMode audioMode = AudioMode.Standard, int jitterBufferMs = 60)
    {
        _wsUrl = wsUrl;
        _audioMode = audioMode;
        _jitterBufferMs = jitterBufferMs;
    }

    public async Task HandleIncomingCallAsync(SIPTransport transport, SIPUserAgent ua, SIPRequest req, string caller)
    {
        if (_disposed) return;

        if (_isInCall)
        {
            Log("⚠️ Already in a call, rejecting");
            var busyResponse = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null);
            await transport.SendResponseAsync(busyResponse);
            return;
        }

        _isInCall = true;
        var callId = Guid.NewGuid().ToString("N")[..8];
        _callCts = new CancellationTokenSource();
        var cts = _callCts;

        int inboundPacketCount = 0;
        bool inboundFlushComplete = false;

        OnCallStarted?.Invoke(callId, caller);

        try
        {
            // Setup media session
            _adaAudioSource = new AdaAudioSource(_audioMode, _jitterBufferMs);
            _adaAudioSource.OnDebugLog += msg => Log(msg);
            _adaAudioSource.OnQueueEmpty += () => _isBotSpeaking = false;

            var mediaEndPoints = new MediaEndPoints { AudioSource = _adaAudioSource };
            _currentMediaSession = new VoIPMediaSession(mediaEndPoints);
            _currentMediaSession.AcceptRtpFromAny = true;

            Log($"☎️ [{callId}] Sending 180 Ringing...");
            var uas = ua.AcceptCall(req);

            try
            {
                var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
                await transport.SendResponseAsync(ringing);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [{callId}] Ringing send failed: {ex.Message}");
            }

            await Task.Delay(200, cts.Token);

            Log($"📞 [{callId}] Answering call...");
            bool answered = await ua.Answer(uas, _currentMediaSession);
            if (!answered)
            {
                Log($"❌ [{callId}] Failed to answer");
                return;
            }

            await _currentMediaSession.Start();
            Log($"📗 [{callId}] Call answered and RTP started");

            // Connect to WebSocket
            _ws = new ClientWebSocket();
            var wsUri = new Uri($"{_wsUrl}?caller={Uri.EscapeDataString(caller)}");
            Log($"🔌 [{callId}] Connecting WS → {wsUri}");

            await _ws.ConnectAsync(wsUri, cts.Token);
            Log($"🟢 [{callId}] WS Connected");

            // Wire hangup handler
            ua.OnCallHungup += dialogue =>
            {
                Log($"📕 [{callId}] Caller hung up");
                try { cts.Cancel(); } catch { }
            };

            // Handle inbound RTP (caller → Edge Function)
            _currentMediaSession.OnRtpPacketReceived += (ep, mt, rtp) =>
            {
                if (mt != SDPMediaTypesEnum.audio || _ws?.State != WebSocketState.Open) return;

                inboundPacketCount++;
                if (!inboundFlushComplete)
                {
                    if (inboundPacketCount <= FLUSH_PACKETS)
                    {
                        if (inboundPacketCount == 1)
                            Log($"🧹 [{callId}] Flushing inbound audio...");
                        return;
                    }
                    inboundFlushComplete = true;
                    Log($"✅ [{callId}] Inbound flush complete");
                }

                if (_isBotSpeaking) return;

                var payload = rtp.Payload;
                if (payload == null || payload.Length == 0) return;

                // Send µ-law directly to WebSocket
                _ = _ws.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            };

            // WebSocket receive loop (Edge Function → caller)
            _ = Task.Run(async () =>
            {
                var buffer = new byte[1024 * 64];
                while (_ws?.State == WebSocketState.Open && !cts.Token.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        var result = await _ws.ReceiveAsync(buffer, cts.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Log($"🔌 [{callId}] WS closed by server");
                            break;
                        }

                        if (result.MessageType != WebSocketMessageType.Text) continue;

                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        ProcessWebSocketMessage(callId, json);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [{callId}] WS receive error: {ex.Message}");
                        break;
                    }
                }
                Log($"🔚 [{callId}] WS read loop ended");
            }, cts.Token);

            // Keep call alive
            while (!cts.IsCancellationRequested && _ws?.State == WebSocketState.Open && !_disposed)
                await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"❌ [{callId}] Error: {ex.Message}");
        }
        finally
        {
            await CleanupAsync(callId, ua);
        }
    }

    private void ProcessWebSocketMessage(string callId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var t)) return;

            var typeStr = t.GetString();

            if (typeStr == "response.audio.delta" && doc.RootElement.TryGetProperty("delta", out var deltaEl))
            {
                var base64 = deltaEl.GetString();
                if (!string.IsNullOrEmpty(base64))
                {
                    _isBotSpeaking = true;
                    var pcmBytes = Convert.FromBase64String(base64);
                    _adaAudioSource?.EnqueuePcm24(pcmBytes);
                }
            }
            else if (typeStr == "audio" && doc.RootElement.TryGetProperty("audio", out var audioEl))
            {
                var base64 = audioEl.GetString();
                if (!string.IsNullOrEmpty(base64))
                {
                    _isBotSpeaking = true;
                    var pcmBytes = Convert.FromBase64String(base64);
                    _adaAudioSource?.EnqueuePcm24(pcmBytes);
                }
            }
            else if (typeStr == "response.audio_transcript.delta" && doc.RootElement.TryGetProperty("delta", out var textEl))
            {
                var text = textEl.GetString();
                if (!string.IsNullOrEmpty(text))
                    OnTranscript?.Invoke($"🤖 {text}");
            }
            else if (typeStr == "response.created" || typeStr == "response.audio.started")
            {
                _adaAudioSource?.ResetFadeIn();
            }
            else if (typeStr == "keepalive")
            {
                _ = SendKeepaliveAck(callId, doc);
            }
        }
        catch { }
    }

    private async Task SendKeepaliveAck(string callId, JsonDocument doc)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            long? ts = null;
            if (doc.RootElement.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number)
                ts = tsEl.GetInt64();

            var ack = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "keepalive_ack",
                ["timestamp"] = ts,
                ["call_id"] = callId,
            });

            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(ack)),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
        catch { }
    }

    private async Task CleanupAsync(string callId, SIPUserAgent ua)
    {
        Log($"📴 [{callId}] Cleanup...");
        _isInCall = false;

        try { ua.Hangup(); } catch { }

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token);
                }
            }
            catch { }
            try { _ws.Dispose(); } catch { }
            _ws = null;
        }

        if (_currentMediaSession != null)
        {
            try { _currentMediaSession.Close("call ended"); } catch { }
            _currentMediaSession = null;
        }

        try { _adaAudioSource?.Dispose(); } catch { }
        _adaAudioSource = null;

        try { _callCts?.Dispose(); } catch { }
        _callCts = null;

        OnCallEnded?.Invoke(callId);
    }

    private void Log(string msg)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _callCts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        try { _currentMediaSession?.Close("disposed"); } catch { }
        try { _adaAudioSource?.Dispose(); } catch { }

        GC.SuppressFinalize(this);
    }
}
