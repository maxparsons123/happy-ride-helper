using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;

using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge.Services;

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
    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<byte[]> _audioQueue = new();
    private readonly AudioDsp _audioDsp = new(); // DSP pipeline from Python bridge
    private bool _isDisposed;
    private DateTime _startTime;
    private int _rtpPacketsSent;
    private int _rtpPacketsReceived;

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
            // Use VoIPMediaSession (matches Minimal/WinForms projects and SIPSorcery 6.x API surface)
            _mediaSession = new VoIPMediaSession(fmt => fmt.Codec == AudioCodecsEnum.PCMU);
            _mediaSession.AcceptRtpFromAny = true;
            _mediaSession.OnRtpPacketReceived += OnRtpPacketReceived;

            _sipUserAgent = new SIPUserAgent(_sipTransport, null);
            var uas = _sipUserAgent.AcceptCall(_inviteRequest);
            bool answered = await _sipUserAgent.Answer(uas, _mediaSession);

            if (!answered)
            {
                _logger.LogError("Failed to answer call {CallId}", _callId);
                await EndAsync();
                return;
            }

            _logger.LogInformation("Call answered {CallId}", _callId);

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
        
        var wsUrl = $"{_config.AdaWsUrl}?call_id={_callId}&source=sip&caller={_callerNumber}";
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
            var pcm16 = TaxiSipBridge.Audio.G711Codec.UlawToPcm16(payload);
            
            // Apply DSP pipeline (high-pass, noise gate, AGC) - from Python bridge
            var (processed, _) = _audioDsp.ApplyNoiseReduction(pcm16);
            
            // Resample 8kHz to 24kHz
            var resampled = TaxiSipBridge.Audio.AudioResampler.Resample(processed, 8000, 24000);
            
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
                        var pcm8k = TaxiSipBridge.Audio.AudioResampler.Resample(pcm24k, 24000, 8000);
                        
                        // Convert PCM16 to μ-law
                        var ulaw = TaxiSipBridge.Audio.G711Codec.Pcm16ToUlaw(pcm8k);
                        
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
            // VoIPMediaSession handles RTP timing/packetization internally.
            _mediaSession?.SendAudio((uint)frame.Length, frame);
            
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

        // Hang up SIP dialog if still active.
        try { _sipUserAgent?.Hangup(); } catch { }

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
        _sipUserAgent?.Hangup();
    }
}
