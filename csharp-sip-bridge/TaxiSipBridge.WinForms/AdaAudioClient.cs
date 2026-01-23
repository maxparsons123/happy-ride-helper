using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    public event Action? OnCallEnded;            // When AI requests call end

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
            // Ada's full greeting can be 8-10 seconds - ensure enough buffer
            BufferDuration = TimeSpan.FromSeconds(15),
            DiscardOnBufferOverflow = false  // Block rather than silently lose audio
        };

        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
        _waveOut.Init(_playbackBuffer);
        _waveOut.Play();

        Log("🔊 Audio playback initialized (24kHz)");

        _ws = new ClientWebSocket();
        
        // Build URI - only add caller param if not already present
        string uriString;
        if (_wsUrl.Contains("caller=", StringComparison.OrdinalIgnoreCase))
        {
            // URL already has caller param, use as-is
            uriString = _wsUrl;
        }
        else if (_wsUrl.Contains("?"))
        {
            // URL has query params but no caller, append with &
            uriString = $"{_wsUrl}&caller={Uri.EscapeDataString(caller ?? "desktop")}";
        }
        else
        {
            // No query params yet, start with ?
            uriString = $"{_wsUrl}?caller={Uri.EscapeDataString(caller ?? "desktop")}";
        }
        var uri = new Uri(uriString);

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

    /// <summary>
    /// Send PCM16 audio to Ada using binary WebSocket frames (lower overhead than JSON/Base64).
    /// </summary>
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

        try
        {
            // Send raw PCM16 bytes as binary frame - edge function handles this directly
            await _ws.SendAsync(
                new ArraySegment<byte>(audioToSend),
                WebSocketMessageType.Binary,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private int _sentToAda = 0;
    private DateTime _lastSendStats = DateTime.Now;

    /// <summary>
    /// Send telephony audio (µ-law 8kHz) to Ada using binary WebSocket frames.
    /// Uses the same processing pipeline as the browser for consistent quality,
    /// but sends raw PCM16 bytes instead of Base64-encoded JSON (33% less overhead).
    /// </summary>
    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (_disposed || _ws?.State != WebSocketState.Open) return;
        if (_cts?.Token.IsCancellationRequested == true) return;

        // HIGH-QUALITY PIPELINE (matches browser quality):
        // 1. Decode μ-law → PCM16 @ 8kHz
        var pcm8k = AudioCodecs.MuLawDecode(ulawData);
        
        // 2. Apply pre-emphasis to boost consonants (improves STT accuracy)
        //    Using the full 0.97 coefficient for maximum clarity
        var pcm8kEmph = AudioCodecs.ApplyPreEmphasis(pcm8k);
        
        // 3. Apply gentle volume boost (telephony audio is often quieter than browser mic)
        //    1.4x boost brings it closer to browser levels without clipping
        for (int i = 0; i < pcm8kEmph.Length; i++)
        {
            int sample = (int)(pcm8kEmph[i] * 1.4f);
            pcm8kEmph[i] = (short)Math.Clamp(sample, -32768, 32767);
        }
        
        // 4. High-quality resample 8kHz → 24kHz using NAudio WDL resampler
        var pcm24k = AudioCodecs.Resample(pcm8kEmph, 8000, 24000);
        var pcmBytes = AudioCodecs.ShortsToBytes(pcm24k);

        try
        {
            // Send raw PCM16 bytes as binary frame - 33% less data than Base64 JSON
            await _ws.SendAsync(
                new ArraySegment<byte>(pcmBytes),
                WebSocketMessageType.Binary,
                true,
                _cts?.Token ?? CancellationToken.None);
            
            _sentToAda++;
            if (_sentToAda <= 3)
                Log($"🎙️ Sent to Ada #{_sentToAda}: {ulawData.Length}b ulaw → {pcmBytes.Length}b PCM24k (binary)");
            else if ((DateTime.Now - _lastSendStats).TotalSeconds >= 3)
            {
                Log($"📤 Sent to Ada: {_sentToAda} packets (binary mode)");
                _lastSendStats = DateTime.Now;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log($"⚠️ WS send error: {ex.Message}"); }
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
        var messageBuilder = new System.IO.MemoryStream();

        while (!_disposed && _ws?.State == WebSocketState.Open && !(_cts?.Token.IsCancellationRequested ?? true))
        {
            try
            {
                messageBuilder.SetLength(0);  // Clear for new message

                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _cts!.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log("🔌 WebSocket closed by server");
                        OnDisconnected?.Invoke();
                        return;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                        break;

                    messageBuilder.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuilder.GetBuffer(), 0, (int)messageBuilder.Length);
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

    private int _audioDeltas = 0;
    private int _totalAudioBytes = 0;
    private DateTime _lastAudioStats = DateTime.MinValue;

    private async Task SendKeepaliveAckAsync(long? timestamp, string? callId)
    {
        if (_disposed || _ws?.State != WebSocketState.Open) return;

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["type"] = "keepalive_ack",
                ["timestamp"] = timestamp,
                ["call_id"] = callId,
            };

            var json = JsonSerializer.Serialize(payload);
            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch { }
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
                case "keepalive":
                    {
                        long? ts = null;
                        if (doc.RootElement.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number)
                            ts = tsEl.GetInt64();

                        string? callId = null;
                        if (doc.RootElement.TryGetProperty("call_id", out var callIdEl) && callIdEl.ValueKind == JsonValueKind.String)
                            callId = callIdEl.GetString();

                        _ = SendKeepaliveAckAsync(ts, callId);
                        break;
                    }

                case "response.audio.delta":
                case "audio":  // Also handle direct "audio" type from some edge functions
                    // Try both "delta" (OpenAI format) and "audio" (direct format)
                    JsonElement? audioData = null;
                    if (doc.RootElement.TryGetProperty("delta", out var delta))
                        audioData = delta;
                    else if (doc.RootElement.TryGetProperty("audio", out var audio))
                        audioData = audio;
                    
                    if (audioData.HasValue)
                    {
                        var base64 = audioData.Value.GetString();
                        if (!string.IsNullOrEmpty(base64))
                        {
                            var pcmBytes = Convert.FromBase64String(base64);
                            _audioDeltas++;
                            _totalAudioBytes += pcmBytes.Length;
                            
                            // Log first few and then stats every 3 seconds
                            if (_audioDeltas <= 3)
                                Log($"🔊 Ada audio #{_audioDeltas}: {pcmBytes.Length}b");
                            else if ((DateTime.Now - _lastAudioStats).TotalSeconds >= 3)
                            {
                                Log($"📊 WS Audio: deltas={_audioDeltas}, bytes={_totalAudioBytes}");
                                _lastAudioStats = DateTime.Now;
                            }
                            
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
                    Log("🤖 Ada response started");
                    _needsFadeIn = true;
                    _audioDeltas = 0;
                    _totalAudioBytes = 0;
                    OnResponseStarted?.Invoke();  // Notify listeners
                    break;

                case "response.audio.started":
                    Log("🔊 Ada audio started");
                    break;

                case "response.done":
                    Log($"✅ Ada response done (sent {_audioDeltas} audio chunks, {_totalAudioBytes} bytes)");
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

                case "session.created":
                    Log("✅ Session created");
                    break;

                case "session.updated":
                    Log("🔄 Session updated");
                    break;

                default:
                    // Log unknown types for debugging
                    if (!type.StartsWith("rate_limits"))
                        Log($"📨 WS event: {type}");
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

        // Resample to 8kHz and encode to μ-law (skip de-emphasis to avoid crackling)
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
