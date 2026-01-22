using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Custom audio source that receives PCM audio from Ada (WebSocket) and provides
/// encoded samples to SIPSorcery's RTP transport.
/// 
/// Uses NAudio BufferedWaveProvider + pull-loop architecture for smooth audio:
/// - No timer jitter - clock follows audio data
/// - WDL resampler for high-quality rate conversion
/// - 40ms frames for Opus, 20ms for G.711
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    // Frame durations
    private const int G711_FRAME_MS = 20;   // 160 samples @ 8kHz
    private const int OPUS_FRAME_MS = 40;   // 1920 samples @ 48kHz
    
    // Jitter buffer settings
    private const int JITTER_BUFFER_MS = 80;  // Pre-fill before starting playback
    private const int MAX_CONSECUTIVE_SILENCE = 5;  // Max silence frames before interpolating
    
    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;
    
    // NAudio buffering
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveFormat _sourceFormat;
    private WdlResamplingSampleProvider? _resampler;
    
    // Pull loop
    private Task? _pullLoopTask;
    private CancellationTokenSource? _cts;
    
    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private volatile bool _disposed;
    private bool _jitterBufferFilled;
    private int _consecutiveSilence;
    private short[]? _lastAudioFrame;

    // Debug counters
    private int _enqueuedBytes;
    private int _sentFrames;
    private int _silenceFrames;
    private int _underruns;
    private int _interpolatedFrames;
    private DateTime _lastStatsLog = DateTime.MinValue;

    // Test tone mode
    private bool _testToneMode;
    private int _testToneSampleIndex;
    private const double TEST_TONE_FREQ = 440.0;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError;
    
    event Action<EncodedAudioFrame>? IAudioSource.OnAudioSourceEncodedFrameReady
    {
        add { }
        remove { }
    }
    
    public event Action<string>? OnDebugLog;
    public event Action? OnQueueEmpty;
    private bool _wasQueueEmpty = true;

    public AdaAudioSource(AudioMode audioMode = AudioMode.Standard, int jitterBufferMs = 80, bool preferOpus = true)
    {
        // Use OpusAudioEncoder for high-quality Opus support (48kHz), falls back to G.711
        _audioEncoder = preferOpus ? new OpusAudioEncoder() : new AudioEncoder();
        _audioFormatManager = new MediaFormatManager<AudioFormat>(_audioEncoder.SupportedFormats);
        
        // NAudio buffer for 24kHz 16-bit mono input from Ada
        _sourceFormat = new WaveFormat(24000, 16, 1);
        _buffer = new BufferedWaveProvider(_sourceFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };
        
        if (audioMode == AudioMode.TestTone)
        {
            _testToneMode = true;
        }
    }

    public List<AudioFormat> GetAudioSourceFormats() => _audioFormatManager.GetSourceFormats();

    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        _audioFormatManager.SetSelectedFormat(audioFormat);
        OnDebugLog?.Invoke($"[AdaAudioSource] 🎵 Format: {audioFormat.FormatName} (ID={audioFormat.ID}) @ {audioFormat.ClockRate}Hz");
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        _audioFormatManager.RestrictFormats(filter);
    }

    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;

    public bool IsAudioSourcePaused() => _isPaused;

    public void EnableTestTone(bool enable = true)
    {
        _testToneMode = enable;
        _testToneSampleIndex = 0;
        OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 Test tone: {(enable ? "ON" : "OFF")}");
    }

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        // Not used - we receive audio via EnqueuePcm24
    }

    /// <summary>
    /// Feed Ada PCM audio (24kHz, 16-bit mono) into NAudio buffer.
    /// </summary>
    public void EnqueuePcm24(byte[] pcmBytes)
    {
        if (_disposed || _isClosed || pcmBytes.Length == 0) return;
        if (_testToneMode) return;

        _buffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
        _enqueuedBytes += pcmBytes.Length;
        _wasQueueEmpty = false;

        // Log first enqueue
        if (_enqueuedBytes == pcmBytes.Length)
            OnDebugLog?.Invoke($"[AdaAudioSource] 📥 First audio: {pcmBytes.Length}b → NAudio buffer");

        // Log stats every 3 seconds
        if ((DateTime.Now - _lastStatsLog).TotalSeconds >= 3)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] 📊 enq={_enqueuedBytes / 1000}KB, sent={_sentFrames}, sil={_silenceFrames}, interp={_interpolatedFrames}, buf={_buffer.BufferedBytes}b");
            _lastStatsLog = DateTime.Now;
        }
    }

    public void ResetFadeIn()
    {
        // Not needed with NAudio buffering - handled automatically
    }

    public void ClearQueue()
    {
        _buffer.ClearBuffer();
    }

    public Task StartAudio()
    {
        if (_audioFormatManager.SelectedFormat.IsEmpty())
            throw new ApplicationException("Audio format not set. Cannot start AdaAudioSource.");

        if (!_isStarted)
        {
            _isStarted = true;
            _cts = new CancellationTokenSource();
            
            int targetRate = _audioFormatManager.SelectedFormat.ClockRate;
            
            // Create WDL resampler: 24kHz → target rate (8kHz or 48kHz)
            _resampler = new WdlResamplingSampleProvider(_buffer.ToSampleProvider(), targetRate);
            
            // Start pull loop
            _pullLoopTask = Task.Run(() => PullLoopAsync(_cts.Token));
            
            OnDebugLog?.Invoke($"[AdaAudioSource] ▶️ Pull loop started, 24kHz → {targetRate}Hz, format={_audioFormatManager.SelectedFormat.FormatName}");
        }

        return Task.CompletedTask;
    }

    public Task PauseAudio()
    {
        _isPaused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAudio()
    {
        _isPaused = false;
        return Task.CompletedTask;
    }

    public Task CloseAudio()
    {
        if (!_isClosed)
        {
            _isClosed = true;
            _cts?.Cancel();
            
            try
            {
                _pullLoopTask?.Wait(500);
            }
            catch { }
            
            _buffer.ClearBuffer();
            OnDebugLog?.Invoke($"[AdaAudioSource] ⏹️ Closed (enqueued={_enqueuedBytes / 1000}KB, sent={_sentFrames}, silence={_silenceFrames})");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pull loop - reads from resampler when data is available.
    /// Uses jitter buffer pre-fill and frame interpolation to prevent stuttering.
    /// </summary>
    private async Task PullLoopAsync(CancellationToken ct)
    {
        try
        {
            var format = _audioFormatManager.SelectedFormat;
            int targetRate = format.ClockRate;
            bool isOpus = targetRate == 48000;
            int frameMs = isOpus ? OPUS_FRAME_MS : G711_FRAME_MS;
            int frameSamples = targetRate * frameMs / 1000;
            
            // Calculate jitter buffer threshold in bytes (24kHz input)
            int jitterBufferBytes = 24000 * 2 * JITTER_BUFFER_MS / 1000;  // 24kHz * 2 bytes * ms
            
            var floatBuffer = new float[frameSamples];
            var frameStartTime = DateTime.UtcNow;
            
            OnDebugLog?.Invoke($"[AdaAudioSource] 🔄 Pull loop: {frameSamples} samples ({frameMs}ms) @ {targetRate}Hz, jitter={JITTER_BUFFER_MS}ms");

            while (!ct.IsCancellationRequested && !_isClosed)
            {
                if (_isPaused)
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                // Jitter buffer pre-fill: wait until we have enough data before starting
                if (!_jitterBufferFilled)
                {
                    if (_buffer.BufferedBytes < jitterBufferBytes)
                    {
                        // Not enough data yet - wait without sending anything
                        await Task.Delay(5, ct);
                        continue;
                    }
                    _jitterBufferFilled = true;
                    OnDebugLog?.Invoke($"[AdaAudioSource] ✅ Jitter buffer filled: {_buffer.BufferedBytes}b >= {jitterBufferBytes}b");
                }

                short[] audioFrame;

                if (_testToneMode)
                {
                    audioFrame = GenerateTestTone(frameSamples, targetRate);
                    _consecutiveSilence = 0;
                }
                else if (_resampler != null)
                {
                    int samplesRead = _resampler.Read(floatBuffer, 0, frameSamples);

                    if (samplesRead == frameSamples)
                    {
                        // Full frame available - convert to shorts
                        audioFrame = FloatToShort(floatBuffer);
                        _lastAudioFrame = audioFrame;
                        _consecutiveSilence = 0;
                    }
                    else if (samplesRead > 0)
                    {
                        // Partial frame - pad with zeros
                        audioFrame = new short[frameSamples];
                        var partial = FloatToShort(floatBuffer, samplesRead);
                        Array.Copy(partial, audioFrame, samplesRead);
                        _lastAudioFrame = audioFrame;
                        _underruns++;
                        _consecutiveSilence = 0;
                    }
                    else
                    {
                        // No data available
                        _consecutiveSilence++;
                        
                        if (_consecutiveSilence <= MAX_CONSECUTIVE_SILENCE && _lastAudioFrame != null)
                        {
                            // Interpolate: fade out last frame to prevent abrupt silence
                            audioFrame = InterpolateFrame(_lastAudioFrame, frameSamples, _consecutiveSilence);
                            _interpolatedFrames++;
                        }
                        else
                        {
                            // Too many underruns - reset jitter buffer and send silence
                            if (_jitterBufferFilled && _consecutiveSilence > MAX_CONSECUTIVE_SILENCE)
                            {
                                _jitterBufferFilled = false;
                                OnDebugLog?.Invoke($"[AdaAudioSource] ⚠️ Buffer underrun, refilling jitter buffer...");
                            }
                            
                            // Send silence but maintain frame timing
                            audioFrame = new short[frameSamples];
                            _silenceFrames++;
                            
                            if (!_wasQueueEmpty)
                            {
                                _wasQueueEmpty = true;
                                OnQueueEmpty?.Invoke();
                            }
                        }
                    }
                }
                else
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                // Log first few frames
                if (_sentFrames < 5)
                {
                    OnDebugLog?.Invoke($"[AdaAudioSource] 📤 Frame {_sentFrames}: {audioFrame.Length} samples @{targetRate}Hz");
                }

                // Encode and send
                byte[] encoded;
                if (format.FormatName == "PCMU" || format.ID == 0)
                {
                    // Use NAudio-based lookup table encoder for G.711
                    encoded = AudioCodecs.MuLawEncode(audioFrame);
                }
                else
                {
                    // Use SIPSorcery encoder for Opus
                    encoded = _audioEncoder.EncodeAudio(audioFrame, format);
                }

                uint durationRtpUnits = (uint)frameSamples;
                _sentFrames++;
                _wasQueueEmpty = false;
                OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);

                // Pace the loop - ALWAYS wait the full frame duration for consistent timing
                var elapsed = DateTime.UtcNow - frameStartTime;
                var targetDuration = TimeSpan.FromMilliseconds(frameMs);
                if (elapsed < targetDuration)
                {
                    await Task.Delay(targetDuration - elapsed, ct);
                }
                frameStartTime = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] ❌ Pull loop error: {ex.Message}");
            OnAudioSourceError?.Invoke($"AdaAudioSource error: {ex.Message}");
        }
    }

    /// <summary>
    /// Generate interpolated frame by fading out the last audio frame.
    /// Prevents abrupt silence during brief underruns.
    /// </summary>
    private static short[] InterpolateFrame(short[] lastFrame, int targetSamples, int underrunCount)
    {
        var output = new short[targetSamples];
        
        // Fade factor: 0.7 → 0.5 → 0.3 → 0.1 → 0
        float fadeFactor = Math.Max(0, 1.0f - (underrunCount * 0.25f));
        
        int copyLen = Math.Min(lastFrame.Length, targetSamples);
        for (int i = 0; i < copyLen; i++)
        {
            // Additional fade across the frame
            float frameFade = 1.0f - ((float)i / copyLen * 0.3f);
            output[i] = (short)(lastFrame[i] * fadeFactor * frameFade);
        }
        
        return output;
    }

    private static short[] FloatToShort(float[] floats, int? count = null)
    {
        int len = count ?? floats.Length;
        var shorts = new short[len];
        for (int i = 0; i < len; i++)
        {
            shorts[i] = (short)Math.Clamp(floats[i] * 32767f, -32768, 32767);
        }
        return shorts;
    }

    private short[] GenerateTestTone(int sampleCount, int sampleRate)
    {
        var samples = new short[sampleCount];
        double angularFreq = 2.0 * Math.PI * TEST_TONE_FREQ / sampleRate;

        for (int i = 0; i < sampleCount; i++)
        {
            double sample = Math.Sin(angularFreq * _testToneSampleIndex);
            samples[i] = (short)(sample * 16000);
            _testToneSampleIndex++;
        }

        return samples;
    }

    private void SendSilence(int frameSamples, AudioFormat format)
    {
        try
        {
            var silence = new short[frameSamples];
            
            byte[] encoded;
            if (format.FormatName == "PCMU" || format.ID == 0)
            {
                encoded = AudioCodecs.MuLawEncode(silence);
            }
            else
            {
                encoded = _audioEncoder.EncodeAudio(silence, format);
            }

            uint durationRtpUnits = (uint)frameSamples;
            _silenceFrames++;

            if (!_wasQueueEmpty)
            {
                _wasQueueEmpty = true;
                OnQueueEmpty?.Invoke();
            }

            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _buffer.ClearBuffer();

        GC.SuppressFinalize(this);
    }
}
