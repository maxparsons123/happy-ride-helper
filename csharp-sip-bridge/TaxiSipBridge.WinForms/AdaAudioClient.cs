using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using NAudio.Wave;

namespace TaxiSipBridge;

public class AdaAudioClient : IDisposable
{
    private readonly string _wsUrl;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed = false;

    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playbackBuffer;
    private WaveInEvent? _waveIn;
    private volatile bool _isRecording = false;

    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 500;

    private bool _needsFadeIn = true;
    private const int FadeInSamples = 48;

    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnAdaSpeaking;
    
    // New events for AdaAudioSource integration
    public event Action<byte[]>? OnPcm24Audio;  // Raw PCM24 bytes from Ada
    public event Action? OnResponseStarted;      // For fade-in reset

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public AdaAudioClient(string wsUrl)
    {
        _wsUrl = wsUrl;
    }

    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdaAudioClient));
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var playbackFormat = new WaveFormat(24000, 16, 1);
        _playbackBuffer = new BufferedWaveProvider(playbackFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
        _waveOut.Init(_playbackBuffer);
        _waveOut.Play();

        Log("🔊 Audio playback initialized (24kHz)");

        _ws = new ClientWebSocket();
        var uri = new Uri($"{_wsUrl}?caller={Uri.EscapeDataString(caller ?? "desktop")}");

        Log($"🔌 Connecting to Ada: {uri}");
        await _ws.ConnectAsync(uri, _cts.Token);
        Log("✅ Connected to Ada");

        OnConnected?.Invoke();
        _ = ReceiveLoopAsync();
    }

    public void StartMicrophoneCapture(int deviceIndex = 0)
    {
        if (_disposed || _isRecording) return;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(24000, 16, 1),
            BufferMilliseconds = 20
        };

        _waveIn.DataAvailable += OnMicrophoneData;
        _waveIn.StartRecording();
        _isRecording = true;

        Log("🎤 Microphone capture started (24kHz)");
    }

    public void StopMicrophoneCapture()
    {
        if (!_isRecording) return;

        try
        {
            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= OnMicrophoneData;
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }
        }
        catch { }
        
        _isRecording = false;
        Log("🎤 Microphone capture stopped");
    }

    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (_disposed || _ws?.State != WebSocketState.Open) return;
        if (_cts?.Token.IsCancellationRequested == true) return;

        byte[] audioToSend;

        if (sampleRate != 24000)
        {
            var samples = AudioCodecs.BytesToShorts(pcmData);
            var resampled = AudioCodecs.Resample(samples, sampleRate, 24000);
            audioToSend = AudioCodecs.ShortsToBytes(resampled);
        }
        else
        {
            audioToSend = pcmData;
        }

        var base64 = Convert.ToBase64String(audioToSend);
        var msg = JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = base64 });

        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (_disposed || _ws?.State != WebSocketState.Open) return;
        if (_cts?.Token.IsCancellationRequested == true) return;

        var pcm8k = AudioCodecs.MuLawDecode(ulawData);
        var pcm24k = AudioCodecs.Resample(pcm8k, 8000, 24000);
        var pcmBytes = AudioCodecs.ShortsToBytes(pcm24k);

        var base64 = Convert.ToBase64String(pcmBytes);
        var msg = JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = base64 });

        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public byte[]? GetNextMuLawFrame()
    {
        if (_outboundQueue.TryDequeue(out var frame))
            return frame;
        return null;
    }

    public int PendingFrameCount => _outboundQueue.Count;

    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs e)
    {
        if (_disposed || e.BytesRecorded == 0) return;

        var data = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);
        _ = SendAudioAsync(data, 24000);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1024 * 64];

        while (!_disposed && _ws?.State == WebSocketState.Open && !(_cts?.Token.IsCancellationRequested ?? true))
        {
            try
            {
                var result = await _ws.ReceiveAsync(buffer, _cts!.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log("🔌 WebSocket closed by server");
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                ProcessMessage(json);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                Log($"⚠️ WebSocket error: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                Log($"⚠️ Receive error: {ex.Message}");
                break;
            }
        }

        OnDisconnected?.Invoke();
    }

    private void ProcessMessage(string json)
    {
        if (_disposed) return;
        
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                return;

            var type = typeEl.GetString();

            switch (type)
            {
                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var delta))
                    {
                        var base64 = delta.GetString();
                        if (!string.IsNullOrEmpty(base64))
                        {
                            var pcmBytes = Convert.FromBase64String(base64);
                            OnPcm24Audio?.Invoke(pcmBytes);  // Notify listeners
                            ProcessAdaAudio(pcmBytes);
                        }
                    }
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var text))
                    {
                        var transcript = text.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                            OnAdaSpeaking?.Invoke(transcript);
                    }
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var fullText))
                    {
                        var transcript = fullText.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                            OnTranscript?.Invoke($"Ada: {transcript}");
                    }
                    break;

                case "input_audio_buffer.speech_started":
                    Log("🎤 User speaking...");
                    break;

                case "input_audio_buffer.speech_stopped":
                    Log("🎤 User stopped speaking");
                    break;

                case "response.created":
                case "response.audio.started":
                    _needsFadeIn = true;
                    OnResponseStarted?.Invoke();  // Notify listeners
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                    {
                        var transcript = userText.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                            OnTranscript?.Invoke($"You: {transcript}");
                    }
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                        Log($"❌ Ada error: {msg}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Parse error: {ex.Message}");
        }
    }

    private void ProcessAdaAudio(byte[] pcm24kBytes)
    {
        if (_disposed) return;
        
        var pcm24k = AudioCodecs.BytesToShorts(pcm24kBytes);

        if (_needsFadeIn && pcm24k.Length > 0)
        {
            int fadeLen = Math.Min(FadeInSamples, pcm24k.Length);
            for (int i = 0; i < fadeLen; i++)
            {
                float gain = (float)i / fadeLen;
                pcm24k[i] = (short)(pcm24k[i] * gain);
            }
            _needsFadeIn = false;
        }

        var playbackBytes = AudioCodecs.ShortsToBytes(pcm24k);
        _playbackBuffer?.AddSamples(playbackBytes, 0, playbackBytes.Length);

        var pcm8k = AudioCodecs.Resample(pcm24k, 24000, 8000);
        var ulaw = AudioCodecs.MuLawEncode(pcm8k);

        for (int i = 0; i < ulaw.Length; i += 160)
        {
            int len = Math.Min(160, ulaw.Length - i);
            var frame = new byte[160];
            Buffer.BlockCopy(ulaw, i, frame, 0, len);
            
            if (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                _outboundQueue.TryDequeue(out _);
            
            _outboundQueue.Enqueue(frame);
        }
    }

    private void Log(string message)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public async Task DisconnectAsync()
    {
        if (_disposed) return;
        
        try { _cts?.Cancel(); } catch { }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", closeCts.Token);
            }
            catch { }
        }

        StopMicrophoneCapture();

        try { _waveOut?.Stop(); _waveOut?.Dispose(); _waveOut = null; } catch { }
        _playbackBuffer = null;
        try { _ws?.Dispose(); _ws = null; } catch { }

        ClearPendingFrames();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        try { _cts?.Cancel(); } catch { }
        
        StopMicrophoneCapture();
        
        try { _waveOut?.Stop(); _waveOut?.Dispose(); } catch { }
        try { _ws?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        
        ClearPendingFrames();
        
        GC.SuppressFinalize(this);
    }
}
