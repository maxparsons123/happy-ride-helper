using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TaxiSipBridge;

/// <summary>
/// Deepgram Nova-2 real-time STT client via WebSocket.
/// Optimized for telephony audio (8kHz PCM or 24kHz PCM).
/// </summary>
public class DeepgramSttClient : IDisposable
{
    private readonly string _apiKey;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _disposed = false;
    
    public event Action<string>? OnTranscript;
    public event Action<string>? OnPartialTranscript;
    public event Action? OnSpeechStarted;
    public event Action? OnSpeechEnded;
    public event Action<string>? OnLog;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    
    public bool IsConnected => _ws?.State == WebSocketState.Open;
    
    public DeepgramSttClient(string apiKey)
    {
        _apiKey = apiKey;
    }
    
    /// <summary>
    /// Connect to Deepgram with specified audio parameters.
    /// </summary>
    public async Task ConnectAsync(int sampleRate = 24000, int channels = 1, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws = new ClientWebSocket();
        
        // Deepgram WebSocket URL with parameters
        var url = $"wss://api.deepgram.com/v1/listen" +
                  $"?model=nova-2-phonecall" +
                  $"&language=en-GB" +
                  $"&punctuate=true" +
                  $"&smart_format=true" +
                  $"&encoding=linear16" +
                  $"&sample_rate={sampleRate}" +
                  $"&channels={channels}" +
                  $"&endpointing=300" +
                  $"&utterance_end_ms=1000" +
                  $"&vad_events=true" +
                  $"&interim_results=true";
        
        _ws.Options.SetRequestHeader("Authorization", $"Token {_apiKey}");
        
        try
        {
            await _ws.ConnectAsync(new Uri(url), _cts.Token);
            OnLog?.Invoke("[Deepgram] Connected to Nova-2");
            OnConnected?.Invoke();
            
            // Start receive loop
            _ = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[Deepgram] Connection failed: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// Send raw PCM16 audio to Deepgram.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcmData)
    {
        if (_ws?.State != WebSocketState.Open) return;
        
        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(pcmData),
                WebSocketMessageType.Binary,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[Deepgram] Send error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Signal end of audio stream.
    /// </summary>
    public async Task CloseStreamAsync()
    {
        if (_ws?.State != WebSocketState.Open) return;
        
        try
        {
            // Send empty JSON to signal end
            var closeMsg = Encoding.UTF8.GetBytes("{\"type\": \"CloseStream\"}");
            await _ws.SendAsync(
                new ArraySegment<byte>(closeMsg),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch { }
    }
    
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new List<byte>();
        
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnLog?.Invoke("[Deepgram] WebSocket closed by server");
                    break;
                }
                
                messageBuffer.AddRange(buffer.Take(result.Count));
                
                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.Clear();
                    ProcessMessage(json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[Deepgram] Receive error: {ex.Message}");
        }
        finally
        {
            OnDisconnected?.Invoke();
        }
    }
    
    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();
            
            switch (type)
            {
                case "SpeechStarted":
                    OnSpeechStarted?.Invoke();
                    OnLog?.Invoke("[Deepgram] Speech started");
                    break;
                    
                case "UtteranceEnd":
                    OnSpeechEnded?.Invoke();
                    OnLog?.Invoke("[Deepgram] Utterance ended");
                    break;
                    
                case "Results":
                    if (root.TryGetProperty("channel", out var channel) &&
                        channel.TryGetProperty("alternatives", out var alts) &&
                        alts.GetArrayLength() > 0)
                    {
                        var alt = alts[0];
                        var transcript = alt.GetProperty("transcript").GetString() ?? "";
                        
                        if (!string.IsNullOrWhiteSpace(transcript))
                        {
                            var isFinal = root.GetProperty("is_final").GetBoolean();
                            
                            if (isFinal)
                            {
                                OnTranscript?.Invoke(transcript);
                                OnLog?.Invoke($"[Deepgram] Final: {transcript}");
                            }
                            else
                            {
                                OnPartialTranscript?.Invoke(transcript);
                            }
                        }
                    }
                    break;
                    
                case "Metadata":
                    OnLog?.Invoke("[Deepgram] Session metadata received");
                    break;
                    
                case "Error":
                    var errorMsg = root.TryGetProperty("message", out var msgEl) 
                        ? msgEl.GetString() : "Unknown error";
                    OnLog?.Invoke($"[Deepgram] Error: {errorMsg}");
                    break;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[Deepgram] Parse error: {ex.Message}");
        }
    }
    
    public async Task DisconnectAsync()
    {
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await CloseStreamAsync();
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }
            catch { }
        }
        
        _cts?.Cancel();
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _cts?.Cancel();
        _cts?.Dispose();
        _ws?.Dispose();
    }
}
