using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using NAudio.Wave;

namespace TaxiSipBridge;

/// <summary>
/// NAudio-based client for Ada AI interaction.
/// Captures audio from microphone or SIP, sends to Ada, plays responses through speakers.
/// MEMORY LEAK FIXES: Bounded queues, proper disposal, explicit cleanup.
/// </summary>
public class AdaAudioClient : IDisposable
{
    private readonly string _wsUrl;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed = false;

    // NAudio playback (24kHz PCM16 mono from Ada)
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playbackBuffer;

    // NAudio recording (for microphone test mode)
    private WaveInEvent? _waveIn;
    private volatile bool _isRecording = false;

    // Audio queues - BOUNDED to prevent memory leaks
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 500; // ~10 seconds at 20ms/frame

    // Fade-in to prevent pops
    private bool _needsFadeIn = true;
    private const int FadeInSamples = 48;

    // Events
    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnAdaSpeaking;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public AdaAudioClient(string wsUrl)
    {
        _wsUrl = wsUrl;
    }

    /// <summary>
    /// Connect to Ada WebSocket and start audio playback.
    /// </summary>
    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdaAudioClient));
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Initialize playback (24kHz mono PCM16 - Ada's output format)
        // MEMORY LEAK FIX: Bound buffer and enable discard on overflow
        var playbackFormat = new WaveFormat(24000, 16, 1);
        _playbackBuffer = new BufferedWaveProvider(playbackFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true // CRITICAL: Prevent unbounded growth
        };

        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
        _waveOut.Init(_playbackBuffer);
        _waveOut.Play();

        Log("🔊 Audio playback initialized (24kHz, bounded buffer)");

        // Connect WebSocket
        _ws = new ClientWebSocket();
        var uri = new Uri($"{_wsUrl}?caller={Uri.EscapeDataString(caller ?? "desktop")}");

        Log($"🔌 Connecting to Ada: {uri}");
        await _ws.ConnectAsync(uri, _cts.Token);
        Log("✅ Connected to Ada");

        OnConnected?.Invoke();

        // Start receive loop
        _ = ReceiveLoopAsync();
    }

    /// <summary>
    /// Start capturing from microphone (test mode without SIP).
    /// </summary>
    public void StartMicrophoneCapture(int deviceIndex = 0)
    {
        if (_disposed) return;
        if (_isRecording) return;

        // Capture at 24kHz to match Ada's expected input
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

    /// <summary>
    /// Stop microphone capture.
    /// </summary>
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

    /// <summary>
    /// Send audio data to Ada (from SIP or microphone).
    /// Expects PCM16 at the source sample rate.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (_disposed) return;
        if (_ws?.State != WebSocketState.Open) return;
        if (_cts?.Token.IsCancellationRequested == true) return;

        byte[] audioToSend;

        // Resample if not 24kHz
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

        // Send as base64 in input_audio_buffer.append format
        var base64 = Convert.ToBase64String(audioToSend);
        var msg = JsonSerializer.Serialize(new
        {
            type = "input_audio_buffer.append",
            audio = base64
        });

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

    /// <summary>
    /// Send µ-law audio from SIP (8kHz).
    /// Converts to PCM16 24kHz before sending.
    /// </summary>
    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (_disposed) return;
        if (_ws?.State != WebSocketState.Open) return;
        if (_cts?.Token.IsCancellationRequested == true) return;

        // Decode µ-law to PCM16
        var pcm8k = AudioCodecs.MuLawDecode(ulawData);

        // Resample 8kHz → 24kHz
        var pcm24k = AudioCodecs.Resample(pcm8k, 8000, 24000);

        // Convert to bytes
        var pcmBytes = AudioCodecs.ShortsToBytes(pcm24k);

        // Send as base64
        var base64 = Convert.ToBase64String(pcmBytes);
        var msg = JsonSerializer.Serialize(new
        {
            type = "input_audio_buffer.append",
            audio = base64
        });

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

    /// <summary>
    /// Get the next µ-law frame for SIP playback (160 bytes = 20ms).
    /// Returns null if no audio available.
    /// </summary>
    public byte[]? GetNextMuLawFrame()
    {
        if (_outboundQueue.TryDequeue(out var frame))
            return frame;
        return null;
    }

    /// <summary>
    /// Check if there's audio waiting for SIP playback.
    /// </summary>
    public int PendingFrameCount => _outboundQueue.Count;

    /// <summary>
    /// Clear all pending audio frames (useful when call ends).
    /// </summary>
    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;
        if (e.BytesRecorded == 0) return;

        var data = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);

        // Send async (fire and forget for real-time)
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
            catch (OperationCanceledException)
            {
                break;
            }
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
                            ProcessAdaAudio(pcmBytes);
                        }
                    }
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var text))
                    {
                        var transcript = text.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                        {
                            OnAdaSpeaking?.Invoke(transcript);
                        }
                    }
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var fullText))
                    {
                        var transcript = fullText.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                        {
                            OnTranscript?.Invoke($"Ada: {transcript}");
                        }
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
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                    {
                        var transcript = userText.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                        {
                            OnTranscript?.Invoke($"You: {transcript}");
                        }
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

        // Apply fade-in to prevent pops
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

        // Play through speakers (24kHz) - buffer handles overflow via DiscardOnBufferOverflow
        var playbackBytes = AudioCodecs.ShortsToBytes(pcm24k);
        _playbackBuffer?.AddSamples(playbackBytes, 0, playbackBytes.Length);

        // Also convert to µ-law for SIP playback
        var pcm8k = AudioCodecs.Resample(pcm24k, 24000, 8000);
        var ulaw = AudioCodecs.MuLawEncode(pcm8k);

        // Queue as 20ms frames (160 bytes each)
        for (int i = 0; i < ulaw.Length; i += 160)
        {
            int len = Math.Min(160, ulaw.Length - i);
            var frame = new byte[160];
            Buffer.BlockCopy(ulaw, i, frame, 0, len);
            
            // MEMORY LEAK FIX: Bound queue size - drop oldest if full
            if (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
            {
                _outboundQueue.TryDequeue(out _);
            }
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
        
        // Cancel ongoing operations first
        try { _cts?.Cancel(); } catch { }

        // Close WebSocket gracefully
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", closeCts.Token);
            }
            catch { }
        }

        // Stop microphone
        StopMicrophoneCapture();

        // MEMORY LEAK FIX: Stop and dispose WaveOut properly
        try
        {
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
        }
        catch { }

        // Clear playback buffer reference
        _playbackBuffer = null;

        // MEMORY LEAK FIX: Dispose WebSocket
        try
        {
            _ws?.Dispose();
            _ws = null;
        }
        catch { }

        // Clear audio queue
        ClearPendingFrames();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        // Synchronous cleanup
        try { _cts?.Cancel(); } catch { }
        
        StopMicrophoneCapture();
        
        try
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
        }
        catch { }
        
        try { _ws?.Dispose(); } catch { }
        
        // MEMORY LEAK FIX: Dispose CancellationTokenSource
        try { _cts?.Dispose(); } catch { }
        
        ClearPendingFrames();
        
        GC.SuppressFinalize(this);
    }
}
