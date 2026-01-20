using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge.Services;

public class CallSession : IDisposable
{
    private readonly string _callId;
    private readonly string _callerNumber;
    private readonly string _calledNumber;
    private readonly BridgeConfig _config;
    private readonly SIPTransport _sipTransport;
    private readonly SIPEndPoint _localEndPoint;
    private readonly SIPEndPoint _remoteEndPoint;
    private readonly SIPRequest _inviteRequest;
    private readonly ILogger<CallSession> _logger;

    private ClientWebSocket? _webSocket;
    private RTPSession? _rtpSession;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<byte[]> _audioQueue = new();
    private bool _isDisposed;
    private DateTime _startTime;
    private int _rtpPacketsSent;
    private int _rtpPacketsReceived;

    public event EventHandler? OnCallEnded;

    public CallSession(
        string callId,
        string callerNumber,
        string calledNumber,
        BridgeConfig config,
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
            // Set up RTP session
            _rtpSession = new RTPSession(false, false, false);
            
            var audioFormat = new SDPAudioVideoMediaFormat(
                SDPWellKnownMediaFormatsEnum.PCMU); // G.711 μ-law
            
            _rtpSession.AcceptRtpFromAny = true;
            
            var track = new MediaStreamTrack(audioFormat);
            _rtpSession.addTrack(track);

            _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;
            _rtpSession.OnTimeout += (mt) =>
            {
                _logger.LogWarning("RTP timeout on {MediaType}", mt);
            };

            // Parse SDP from INVITE
            var sdpOffer = SDP.ParseSDPDescription(_inviteRequest.Body);
            var setRemoteResult = _rtpSession.SetRemoteDescription(SdpType.offer, sdpOffer);
            
            if (setRemoteResult != SetDescriptionResultEnum.OK)
            {
                _logger.LogError("Failed to set remote SDP: {Result}", setRemoteResult);
                await SendResponse(SIPResponseStatusCodesEnum.NotAcceptableHere);
                return;
            }

            // Create SDP answer
            var sdpAnswer = _rtpSession.CreateAnswer(null);

            // Send 200 OK with SDP
            var okResponse = SIPResponse.GetResponse(
                _inviteRequest,
                SIPResponseStatusCodesEnum.Ok,
                null);
            okResponse.Header.ContentType = "application/sdp";
            okResponse.Body = sdpAnswer.ToString();

            await _sipTransport.SendResponseAsync(okResponse);
            _logger.LogInformation("Sent 200 OK for call {CallId}", _callId);

            // Start RTP
            await _rtpSession.Start();

            // Connect to AI WebSocket
            await ConnectWebSocketAsync();

            // Start audio processing tasks
            _ = Task.Run(() => ProcessAudioQueueAsync(_cts.Token));
            _ = Task.Run(() => ReceiveWebSocketAsync(_cts.Token));

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
        
        var wsUrl = $"{_config.WebSocketUrl}?call_id={_callId}&source=sip&caller={_callerNumber}";
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
        _rtpPacketsReceived++;

        try
        {
            var payload = rtpPacket.Payload;
            
            // Convert μ-law to PCM16
            var pcm16 = G711Codec.UlawToPcm16(payload);
            
            // Resample 8kHz to 24kHz
            var resampled = AudioResampler.Resample(pcm16, 8000, 24000);
            
            // BINARY PATH: Send raw PCM bytes directly (no base64 overhead)
            // This reduces CPU usage and bandwidth by ~33%
            _ = SendBinaryAudio(resampled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing RTP packet");
        }
    }

    private async Task ReceiveWebSocketAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
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
                    var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.Clear();
                    
                    await ProcessWebSocketMessage(json);
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

    private async Task ProcessWebSocketMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "audio":
                case "response.audio.delta":
                    var audioData = root.TryGetProperty("audio", out var audioProp) 
                        ? audioProp.GetString() 
                        : root.GetProperty("delta").GetString();
                    
                    if (!string.IsNullOrEmpty(audioData))
                    {
                        var pcm24k = Convert.FromBase64String(audioData);
                        
                        // Resample 24kHz to 8kHz
                        var pcm8k = AudioResampler.Resample(pcm24k, 24000, 8000);
                        
                        // Convert PCM16 to μ-law
                        var ulaw = G711Codec.Pcm16ToUlaw(pcm8k);
                        
                        _audioQueue.Enqueue(ulaw);
                    }
                    break;

                case "transcript":
                    var text = root.GetProperty("text").GetString();
                    var role = root.TryGetProperty("role", out var roleProp) 
                        ? roleProp.GetString() 
                        : "unknown";
                    _logger.LogInformation("[{Role}] {Text}", role, text);
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
            _logger.LogError(ex, "Error processing WebSocket message: {Json}", json);
        }
    }

    private async Task ProcessAudioQueueAsync(CancellationToken cancellationToken)
    {
        const int FrameSizeBytes = 160; // 20ms at 8kHz μ-law
        const int FrameDurationMs = 20;
        var silenceFrame = new byte[FrameSizeBytes];
        Array.Fill(silenceFrame, (byte)0xFF); // μ-law silence

        var frameBuffer = new List<byte>();
        var lastFrameTime = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Collect audio from queue
                while (_audioQueue.TryDequeue(out var chunk))
                {
                    frameBuffer.AddRange(chunk);
                }

                // Send frames at real-time pace
                var now = DateTime.UtcNow;
                var elapsed = (now - lastFrameTime).TotalMilliseconds;

                if (elapsed >= FrameDurationMs)
                {
                    byte[] frame;
                    
                    if (frameBuffer.Count >= FrameSizeBytes)
                    {
                        frame = frameBuffer.Take(FrameSizeBytes).ToArray();
                        frameBuffer.RemoveRange(0, FrameSizeBytes);
                    }
                    else
                    {
                        // Send silence if no audio available
                        frame = silenceFrame;
                    }

                    SendRtpFrame(frame);
                    lastFrameTime = now;
                }

                await Task.Delay(5, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio queue");
        }
    }

    private void SendRtpFrame(byte[] frame)
    {
        try
        {
            _rtpSession?.SendAudioFrame(
                (uint)frame.Length * 8, // Timestamp increment
                (int)SDPWellKnownMediaFormatsEnum.PCMU,
                frame);
            
            _rtpPacketsSent++;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending RTP frame");
        }
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

    /// <summary>
    /// Send raw PCM audio as binary WebSocket frame (no base64 encoding)
    /// This is ~33% more efficient than JSON/base64
    /// </summary>
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

    private async Task SendResponse(SIPResponseStatusCodesEnum statusCode)
    {
        var response = SIPResponse.GetResponse(_inviteRequest, statusCode, null);
        await _sipTransport.SendResponseAsync(response);
    }

    public async Task EndAsync()
    {
        if (_isDisposed) return;

        var duration = DateTime.UtcNow - _startTime;
        _logger.LogInformation(
            "Ending call {CallId} - Duration: {Duration:mm\\:ss}, RTP Sent: {Sent}, Received: {Received}",
            _callId, duration, _rtpPacketsSent, _rtpPacketsReceived);

        _cts?.Cancel();

        // Close WebSocket
        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Call ended",
                    CancellationToken.None);
            }
            catch { }
        }

        // Close RTP session
        _rtpSession?.Close("Call ended");

        OnCallEnded?.Invoke(this, EventArgs.Empty);
        Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts?.Dispose();
        _webSocket?.Dispose();
        _rtpSession?.Dispose();
    }
}
