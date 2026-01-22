using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Custom audio source that receives PCM audio from Ada (WebSocket) and provides
/// encoded samples to SIPSorcery's RTP transport.
/// 
/// Uses System.Threading.Timer for rock-solid 20ms/40ms timing.
/// RTP requires constant packet rate - we ALWAYS send packets, even if silence.
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    // Frame durations
    private const int G711_FRAME_MS = 20;   // 160 samples @ 8kHz
    private const int OPUS_FRAME_MS = 40;   // 1920 samples @ 48kHz

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;

    // NAudio buffering
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveFormat _sourceFormat;
    private WdlResamplingSampleProvider? _resampler;

    // Timer-based send (more reliable than Task.Delay for RTP)
    private System.Threading.Timer? _sendTimer;
    private readonly object _timerLock = new();

    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private volatile bool _disposed;

    // Frame state
    private int _frameSamples;
    private float[]? _floatBuffer;
    private short[]? _lastGoodFrame;

    // Debug counters
    private int _enqueuedBytes;
    private int _sentFrames;
    private int _silenceFrames;
    private int _underruns;
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

    /// <summary>
    /// Exposes the encoder for use by SipToAdaDecoder (decode path).
    /// </summary>
    public IAudioEncoder Encoder => _audioEncoder;

    /// <summary>
    /// Exposes the negotiated format for use by SipToAdaDecoder.
    /// </summary>
    public AudioFormat SelectedFormat => _audioFormatManager.SelectedFormat;

    public AdaAudioSource(AudioMode audioMode = AudioMode.Standard, int jitterBufferMs = 60, bool preferOpus = true)
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
        OnDebugLog?.Invoke($"[AdaAudioSource] 🎵 Format: {audioFormat.FormatName} (ID={audioFormat.FormatID}) @ {audioFormat.ClockRate}Hz");
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
            OnDebugLog?.Invoke($"[AdaAudioSource] 📊 enq={_enqueuedBytes / 1000}KB, sent={_sentFrames}, sil={_silenceFrames}, buf={_buffer.BufferedBytes}b");
            _lastStatsLog = DateTime.Now;
        }
    }

    public void ResetFadeIn()
    {
        // Clear last good frame to prevent stale audio on new response
        _lastGoodFrame = null;
    }

    public void ClearQueue()
    {
        _buffer.ClearBuffer();
        _lastGoodFrame = null;
    }

    public Task StartAudio()
    {
        if (_audioFormatManager.SelectedFormat.IsEmpty())
            throw new ApplicationException("Audio format not set. Cannot start AdaAudioSource.");

        if (!_isStarted)
        {
            _isStarted = true;

            var format = _audioFormatManager.SelectedFormat;
            int targetRate = format.ClockRate;
            bool isOpus = targetRate == 48000;
            int frameMs = isOpus ? OPUS_FRAME_MS : G711_FRAME_MS;
            _frameSamples = targetRate * frameMs / 1000;
            _floatBuffer = new float[_frameSamples];

            // Create WDL resampler: 24kHz → target rate (8kHz or 48kHz)
            _resampler = new WdlResamplingSampleProvider(_buffer.ToSampleProvider(), targetRate);

            // Start fixed-rate timer - this is more reliable than Task.Delay for RTP
            _sendTimer = new System.Threading.Timer(
                OnTimerTick,
                null,
                0,
                frameMs
            );

            OnDebugLog?.Invoke($"[AdaAudioSource] ▶️ Timer started ({frameMs}ms), 24kHz → {targetRate}Hz, format={format.FormatName}");
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

            lock (_timerLock)
            {
                _sendTimer?.Dispose();
                _sendTimer = null;
            }

            _buffer.ClearBuffer();
            OnDebugLog?.Invoke($"[AdaAudioSource] ⏹️ Closed (enqueued={_enqueuedBytes / 1000}KB, sent={_sentFrames}, silence={_silenceFrames})");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Timer callback - fires every 20ms (G.711) or 40ms (Opus).
    /// ALWAYS sends a packet to maintain constant RTP rate.
    /// </summary>
    private void OnTimerTick(object? state)
    {
        if (_isClosed || _isPaused || _disposed) return;

        // Prevent re-entrancy
        if (!Monitor.TryEnter(_timerLock)) return;

        try
        {
            var format = _audioFormatManager.SelectedFormat;
            if (format.IsEmpty()) return;

            short[] audioFrame;

            if (_testToneMode)
            {
                audioFrame = GenerateTestTone(_frameSamples, format.ClockRate);
            }
            else if (_resampler != null && _floatBuffer != null)
            {
                int samplesRead = _resampler.Read(_floatBuffer, 0, _frameSamples);

                if (samplesRead >= _frameSamples)
                {
                    // Full frame available
                    audioFrame = FloatToShort(_floatBuffer);
                    _lastGoodFrame = audioFrame;

                    if (_wasQueueEmpty)
                    {
                        _wasQueueEmpty = false;
                        OnDebugLog?.Invoke($"[AdaAudioSource] 📢 Audio resumed (frame {_sentFrames})");
                    }
                }
                else if (samplesRead > 0)
                {
                    // Partial frame - pad with silence
                    audioFrame = new short[_frameSamples];
                    var partial = FloatToShort(_floatBuffer, samplesRead);
                    Array.Copy(partial, audioFrame, samplesRead);
                    _lastGoodFrame = audioFrame;
                    _underruns++;
                }
                else
                {
                    // No data - use last frame with fade or silence
                    if (_lastGoodFrame != null)
                    {
                        // Fade out last frame gradually
                        audioFrame = FadeFrame(_lastGoodFrame, 0.7f);
                        _lastGoodFrame = audioFrame;  // Keep fading
                    }
                    else
                    {
                        audioFrame = new short[_frameSamples];
                    }

                    _silenceFrames++;

                    if (!_wasQueueEmpty)
                    {
                        _wasQueueEmpty = true;
                        OnQueueEmpty?.Invoke();
                    }
                }
            }
            else
            {
                // No resampler - send silence
                audioFrame = new short[_frameSamples];
                _silenceFrames++;
            }

            // Log first few frames
            if (_sentFrames < 5)
            {
                OnDebugLog?.Invoke($"[AdaAudioSource] 📤 Frame {_sentFrames}: {audioFrame.Length} samples @{format.ClockRate}Hz");
            }

            // Encode
            byte[] encoded;
            if (format.FormatName == "PCMU" || format.FormatID == 0)
            {
                // Use NAudio-based lookup table encoder for G.711
                encoded = AudioCodecs.MuLawEncode(audioFrame);
            }
            else
            {
                // Use SIPSorcery encoder for Opus
                encoded = _audioEncoder.EncodeAudio(audioFrame, format);
            }

            // Send
            uint durationRtpUnits = (uint)_frameSamples;
            _sentFrames++;
            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch (Exception ex)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] ❌ Timer error: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_timerLock);
        }
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

    private static short[] FadeFrame(short[] frame, float factor)
    {
        var output = new short[frame.Length];
        for (int i = 0; i < frame.Length; i++)
        {
            output[i] = (short)(frame[i] * factor);
        }
        return output;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_timerLock)
        {
            _sendTimer?.Dispose();
            _sendTimer = null;
        }

        _buffer.ClearBuffer();
        GC.SuppressFinalize(this);
    }
}
