using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Client for Simli.ai real-time avatar API.
/// Sends audio to Simli and receives lip-synced video frames.
/// </summary>
public class SimliAvatarClient : IDisposable
{
    private readonly string _apiKey;
    private readonly string _faceId;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _isConnected;

    /// <summary>Fired when a video frame is received (H.264/VP8 encoded).</summary>
    public event Action<byte[]>? OnVideoFrame;
    
    /// <summary>Fired when audio is received back (for sync verification).</summary>
    public event Action<byte[]>? OnAudioFrame;
    
    /// <summary>Fired when connected to Simli.</summary>
    public event Action? OnConnected;
    
    /// <summary>Fired when disconnected from Simli.</summary>
    public event Action? OnDisconnected;
    
    /// <summary>Fired for log messages.</summary>
    public event Action<string>? OnLog;

    public bool IsConnected => _isConnected;

    /// <summary>
    /// Create a new Simli avatar client.
    /// </summary>
    /// <param name="apiKey">Simli API key</param>
    /// <param name="faceId">Face ID for the avatar to use</param>
    public SimliAvatarClient(string apiKey, string faceId)
    {
        _apiKey = apiKey;
        _faceId = faceId;
    }

    /// <summary>
    /// Connect to the Simli WebSocket API.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_isConnected) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws = new ClientWebSocket();
        
        // Simli uses WebSocket for real-time communication
        var uri = new Uri($"wss://api.simli.ai/ws?api_key={_apiKey}");
        
        try
        {
            OnLog?.Invoke($"🎭 Connecting to Simli avatar service...");
            await _ws.ConnectAsync(uri, _cts.Token);
            
            // Send initialization message with face ID
            var initMsg = JsonSerializer.Serialize(new
            {
                type = "init",
                face_id = _faceId,
                audio_sample_rate = 16000,
                audio_format = "pcm16"
            });
            
            var initBytes = Encoding.UTF8.GetBytes(initMsg);
            await _ws.SendAsync(new ArraySegment<byte>(initBytes), 
                WebSocketMessageType.Text, true, _cts.Token);
            
            _isConnected = true;
            OnConnected?.Invoke();
            OnLog?.Invoke($"🎭 Connected to Simli (face: {_faceId})");
            
            // Start receive loop
            _receiveTask = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"🎭 Simli connection failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Send audio data to Simli for lip-sync processing.
    /// Audio should be 16kHz PCM16 mono.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcm16Audio)
    {
        if (!_isConnected || _ws == null) return;
        
        try
        {
            // Send audio as binary WebSocket message
            var audioMsg = new
            {
                type = "audio",
                data = Convert.ToBase64String(pcm16Audio)
            };
            
            var msgBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(audioMsg));
            await _ws.SendAsync(new ArraySegment<byte>(msgBytes), 
                WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"🎭 Simli send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Send audio as PCM24 (24kHz) - will be resampled to 16kHz for Simli.
    /// </summary>
    public async Task SendPcm24AudioAsync(byte[] pcm24Audio)
    {
        // Resample 24kHz to 16kHz (2:3 ratio)
        // Use fully qualified name to avoid conflict with TaxiSipBridge.Audio.AudioCodecs
        var samples24 = TaxiSipBridge.AudioCodecs.BytesToShorts(pcm24Audio);
        var samples16 = TaxiSipBridge.AudioCodecs.Resample(samples24, 24000, 16000);
        var pcm16 = TaxiSipBridge.AudioCodecs.ShortsToBytes(samples16);
        
        await SendAudioAsync(pcm16);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024]; // 64KB buffer for video frames
        
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnLog?.Invoke("🎭 Simli connection closed by server");
                    break;
                }
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessMessage(json);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Binary video frame
                    var frameData = new byte[result.Count];
                    Buffer.BlockCopy(buffer, 0, frameData, 0, result.Count);
                    OnVideoFrame?.Invoke(frameData);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"🎭 Simli receive error: {ex.Message}");
        }
        finally
        {
            _isConnected = false;
            OnDisconnected?.Invoke();
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("type", out var typeEl))
            {
                var type = typeEl.GetString();
                
                switch (type)
                {
                    case "video":
                        if (root.TryGetProperty("data", out var videoData))
                        {
                            var frameBytes = Convert.FromBase64String(videoData.GetString()!);
                            OnVideoFrame?.Invoke(frameBytes);
                        }
                        break;
                        
                    case "audio":
                        if (root.TryGetProperty("data", out var audioData))
                        {
                            var audioBytes = Convert.FromBase64String(audioData.GetString()!);
                            OnAudioFrame?.Invoke(audioBytes);
                        }
                        break;
                        
                    case "error":
                        if (root.TryGetProperty("message", out var errMsg))
                        {
                            OnLog?.Invoke($"🎭 Simli error: {errMsg.GetString()}");
                        }
                        break;
                        
                    case "ready":
                        OnLog?.Invoke("🎭 Simli avatar ready");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"🎭 Failed to parse Simli message: {ex.Message}");
        }
    }

    /// <summary>
    /// Disconnect from Simli.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (!_isConnected) return;
        
        _cts?.Cancel();
        
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None);
            }
            catch { }
        }
        
        _isConnected = false;
        OnLog?.Invoke("🎭 Disconnected from Simli");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
