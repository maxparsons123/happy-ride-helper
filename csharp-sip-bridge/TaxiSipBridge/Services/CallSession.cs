using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge.Services;

/// <summary>
/// Manages a single SIP call session with AI WebSocket integration.
/// Uses AdaAudioSource for outbound audio (AI → SIP) with proper codec negotiation,
/// resampling, jitter buffering, and RTP timing.
/// </summary>
public class CallSession : IDisposable
{
    private readonly string _callId;
    private readonly string _callerNumber;
    private readonly string _calledNumber;
    private readonly SipAdaBridgeConfig _config;
    private readonly SIPTransport _sipTransport;
    private readonly SIPEndPoint _localEndPoint;
    private readonly SIPEndPoint _remoteEndPoint;
    private readonly SIPRequest _inviteRequest;
    private readonly ILogger<CallSession> _logger;

    private ClientWebSocket? _webSocket;
    private SIPUserAgent? _sipUserAgent;
    private VoIPMediaSession? _mediaSession;
    private PcmaRtpPlayout? _rtpPlayout;
    private CancellationTokenSource? _cts;
    private readonly AudioDsp _audioDsp = new();
    private bool _isDisposed;
    private DateTime _startTime;
    private int _rtpPacketsReceived;

    // Flush initial RTP packets to avoid stale audio
    private const int FLUSH_PACKETS = 25;
    private int _inboundPacketCount;
    private bool _inboundFlushComplete;

    public event EventHandler? OnCallEnded;

    public CallSession(
        string callId,
        string callerNumber,
        string calledNumber,
        SipAdaBridgeConfig config,
        SIPTransport sipTransport,
        SIPEndPoint localEndPoint,
        SIPEndPoint remoteEndPoint,
        SIPRequest inviteRequest,
        ILogger<CallSession> logger)
    {
        _callId = callId;
        _callerNumber = callerNumber;
        _calledNumber = calledNumber;
        _config = config;
        _sipTransport = sipTransport;
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
        _inviteRequest = inviteRequest;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _startTime = DateTime.UtcNow;

        try
        {
            // Create VoIPMediaSession for SDP negotiation and RTP reception
            // We use a null AudioSource since we'll send RTP directly via PcmaRtpPlayout
            var mediaEndPoints = new MediaEndPoints
            {
                AudioSource = null,
                AudioSink = null  // We handle inbound audio manually via OnRtpPacketReceived
            };
            _mediaSession = new VoIPMediaSession(mediaEndPoints);
            _mediaSession.AcceptRtpFromAny = true;
            _mediaSession.OnRtpPacketReceived += OnRtpPacketReceived;

            _sipUserAgent = new SIPUserAgent(_sipTransport, null);
            var uas = _sipUserAgent.AcceptCall(_inviteRequest);

            // Send 180 Ringing
            try
            {
                var ringing = SIPResponse.GetResponse(_inviteRequest, SIPResponseStatusCodesEnum.Ringing, null);
                await _sipTransport.SendResponseAsync(ringing);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to send 180 Ringing: {Error}", ex.Message);
            }

            await Task.Delay(200);

            bool answered = await _sipUserAgent.Answer(uas, _mediaSession);
            if (!answered)
            {
                _logger.LogError("Failed to answer call {CallId}", _callId);
                await EndAsync();
                return;
            }

            // Start the media session
            await _mediaSession.Start();

            // Create PcmaRtpPlayout with dedicated timing thread for outbound audio
            // Pass the media session for RTP sending
            _rtpPlayout = new PcmaRtpPlayout(_mediaSession);
            _rtpPlayout.OnDebugLog += msg => _logger.LogDebug("{Message}", msg);
            _rtpPlayout.Start();
            _logger.LogInformation("Call {CallId} - PcmaRtpPlayout started", _callId);

            // Log negotiated codec
            var selectedFormat = _mediaSession.AudioLocalTrack?.Capabilities?.FirstOrDefault();
            if (selectedFormat.HasValue && !selectedFormat.Value.IsEmpty())
            {
                _logger.LogInformation("Call {CallId} answered - Codec: {Codec} @ {Rate}Hz",
                    _callId, selectedFormat.Value.Name, selectedFormat.Value.ClockRate);
            }
            else
            {
                _logger.LogInformation("Call {CallId} answered", _callId);
            }

            // Connect to AI WebSocket
            await ConnectWebSocketAsync();

            // Start WebSocket receive loop
            _ = Task.Run(() => ReceiveWebSocketAsync(_cts.Token));

            // Handle call hangup
            _sipUserAgent.OnCallHungup += dialogue =>
            {
                _logger.LogInformation("Call {CallId} hung up by remote", _callId);
                _ = EndAsync();
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting call {CallId}", _callId);
            await EndAsync();
        }
    }

    private async Task ConnectWebSocketAsync()
    {
        _webSocket = new ClientWebSocket();

        var wsUrl = $"{_config.AdaWsUrl}?call_id={_callId}&source=sip&caller={Uri.EscapeDataString(_callerNumber)}";
        _logger.LogInformation("Connecting to WebSocket: {Url}", wsUrl);

        await _webSocket.ConnectAsync(new Uri(wsUrl), _cts!.Token);
        _logger.LogInformation("WebSocket connected for call {CallId}", _callId);

        // Send session start
        var startMessage = new
        {
            type = "session.start",
            call_id = _callId,
            caller_phone = _callerNumber,
            format = "ulaw",
            sample_rate = 8000
        };

        await SendWebSocketMessage(startMessage);
    }

    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || _webSocket?.State != WebSocketState.Open)
            return;

        _rtpPacketsReceived++;
        _inboundPacketCount++;

        // Flush initial packets to avoid stale audio
        if (!_inboundFlushComplete)
        {
            if (_inboundPacketCount <= FLUSH_PACKETS)
            {
                if (_inboundPacketCount == 1)
                    _logger.LogDebug("Flushing first {Count} inbound packets", FLUSH_PACKETS);
                return;
            }
            _inboundFlushComplete = true;
            _logger.LogDebug("Inbound flush complete");
        }

        try
        {
            var payload = rtpPacket.Payload;
            if (payload == null || payload.Length == 0) return;

            // 1. Decode μ-law to PCM16 bytes
            var pcm16Bytes = G711Codec.UlawToPcm16(payload);

            // 2. Apply DSP pipeline (high-pass, noise gate, AGC) - takes and returns byte[]
            var (processed, _) = _audioDsp.ApplyNoiseReduction(pcm16Bytes);

            // 3. Resample 8kHz to 24kHz for Ada using NAudioResampler
            var resampled = NAudioResampler.Resample(processed, 8000, 24000);

            // 4. Send raw PCM bytes via binary WebSocket (33% more efficient than base64)
            _ = SendBinaryAudio(resampled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing RTP packet");
        }
    }

    private async Task ReceiveWebSocketAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var messageBuffer = new List<byte>();

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _webSocket?.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("WebSocket closed by server");
                    break;
                }

                messageBuffer.AddRange(buffer.Take(result.Count));

                if (result.EndOfMessage)
                {
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();
                        ProcessWebSocketMessage(json);
                    }
                    else
                    {
                        messageBuffer.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving WebSocket messages");
        }
    }

    private void ProcessWebSocketMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var type = typeProp.GetString();

            switch (type)
            {
                case "audio":
                case "response.audio.delta":
                    // Ada sends 24kHz PCM16 audio as base64
                    string? audioData = null;
                    if (root.TryGetProperty("delta", out var deltaProp))
                        audioData = deltaProp.GetString();
                    else if (root.TryGetProperty("audio", out var audioProp))
                        audioData = audioProp.GetString();

                    if (!string.IsNullOrEmpty(audioData))
                    {
                        var pcm24kBytes = Convert.FromBase64String(audioData);
                        // Feed to PcmaRtpPlayout - handles resampling (24k→8k), PCMA encoding, and RTP timing
                        _rtpPlayout?.EnqueuePcm24(pcm24kBytes);
                    }
                    break;

                case "response.created":
                case "response.audio.started":
                    // Clear queue on new response to prevent stale audio
                    _rtpPlayout?.Clear();
                    break;

                case "response.audio_transcript.delta":
                    if (root.TryGetProperty("delta", out var textDelta))
                    {
                        var text = textDelta.GetString();
                        if (!string.IsNullOrEmpty(text))
                            _logger.LogDebug("[AI] {Text}", text);
                    }
                    break;

                case "transcript":
                    var transcriptText = root.TryGetProperty("text", out var txtProp) ? txtProp.GetString() : null;
                    var role = root.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : "unknown";
                    if (!string.IsNullOrEmpty(transcriptText))
                        _logger.LogInformation("[{Role}] {Text}", role, transcriptText);
                    break;

                case "keepalive":
                    // Respond to keepalive
                    _ = SendKeepaliveAck(root);
                    break;

                case "session.ready":
                case "session_ready":
                    _logger.LogInformation("AI session ready for call {CallId}", _callId);
                    break;

                case "response.done":
                case "response_done":
                    _logger.LogDebug("AI response complete");
                    break;

                case "error":
                    var errorMsg = root.TryGetProperty("message", out var msgProp)
                        ? msgProp.GetString()
                        : "Unknown error";
                    _logger.LogError("AI error: {Error}", errorMsg);
                    break;

                default:
                    _logger.LogDebug("Unhandled message type: {Type}", type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WebSocket message");
        }
    }

    private async Task SendKeepaliveAck(JsonElement root)
    {
        try
        {
            long? ts = null;
            if (root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number)
                ts = tsEl.GetInt64();

            var ack = new Dictionary<string, object?>
            {
                ["type"] = "keepalive_ack",
                ["timestamp"] = ts,
                ["call_id"] = _callId
            };

            await SendWebSocketMessage(ack);
        }
        catch { }
    }

    private async Task SendWebSocketMessage(object message)
    {
        if (_webSocket?.State != WebSocketState.Open)
            return;

        try
        {
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending WebSocket message");
        }
    }

    private async Task SendBinaryAudio(byte[] pcmData)
    {
        if (_webSocket?.State != WebSocketState.Open)
            return;

        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(pcmData),
                WebSocketMessageType.Binary,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending binary audio");
        }
    }

    public async Task EndAsync()
    {
        if (_isDisposed) return;

        var duration = DateTime.UtcNow - _startTime;
        _logger.LogInformation(
            "Ending call {CallId} - Duration: {Duration:mm\\:ss}, RTP Received: {Received}",
            _callId, duration, _rtpPacketsReceived);

        _cts?.Cancel();

        // Hang up SIP dialog
        try { _sipUserAgent?.Hangup(); } catch { }

        // Close WebSocket
        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Call ended",
                    closeCts.Token);
            }
            catch { }
        }

        // Stop RTP playout
        try { _rtpPlayout?.Dispose(); } catch { }

        // Stop media session
        try { _mediaSession?.Close("Call ended"); } catch { }

        OnCallEnded?.Invoke(this, EventArgs.Empty);
        Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts?.Dispose();
        _webSocket?.Dispose();
        _mediaSession?.Dispose();
        _rtpPlayout?.Dispose();

        GC.SuppressFinalize(this);
    }
}
