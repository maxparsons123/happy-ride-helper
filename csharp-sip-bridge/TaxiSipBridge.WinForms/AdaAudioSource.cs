using System.Collections.Concurrent;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Timer = System.Threading.Timer;

namespace TaxiSipBridge;

/// <summary>
/// Audio playback mode for AdaAudioSource.
/// </summary>
public enum AudioMode
{
    Standard,       // Use NAudio WDL resampler (professional quality)
    JitterBuffer,   // Add jitter buffer before playback
    TestTone        // Send 440Hz test tone instead of audio
}

/// <summary>
/// Custom audio source that receives PCM audio from OpenAI (24kHz) and provides
/// encoded samples to SIPSorcery's RTP transport.
/// 
/// Uses NAudio's professional WDL resampler for high-quality audio conversion.
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 5000; // ~100 seconds buffer
    private const int FADE_IN_SAMPLES = 80;

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;
    private readonly ConcurrentQueue<short[]> _pcmQueue = new();

    private Timer? _sendTimer;
    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private bool _needsFadeIn = true;
    private volatile bool _disposed;

    // Debug counters
    private int _enqueuedFrames;
    private int _sentFrames;
    private int _silenceFrames;
    private DateTime _lastStatsLog = DateTime.MinValue;

    // Jitter buffer
    private AudioMode _audioMode;
    private int _jitterBufferMs;
    private bool _jitterBufferFilled;
    private int _consecutiveUnderruns;

    // Last sample for smooth transitions
    private short _lastOutputSample;
    private short[]? _lastAudioFrame;
    private bool _lastFrameWasSilence = true;

    // Test tone
    private bool _testToneMode;
    private int _testToneSampleIndex;
    private const double TEST_TONE_FREQ = 440.0;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError;
    event Action<EncodedAudioFrame>? IAudioSource.OnAudioSourceEncodedFrameReady { add { } remove { } }

    public event Action<string>? OnDebugLog;
    public event Action? OnQueueEmpty;
    private bool _wasQueueEmpty = true;

    public AdaAudioSource(AudioMode audioMode = AudioMode.Standard, int jitterBufferMs = 80)
    {
        _audioEncoder = new AudioEncoder();
        _audioFormatManager = new MediaFormatManager<AudioFormat>(_audioEncoder.SupportedFormats);
        _audioMode = audioMode;
        _jitterBufferMs = jitterBufferMs;
        _testToneMode = audioMode == AudioMode.TestTone;
    }

    public List<AudioFormat> GetAudioSourceFormats() => _audioFormatManager.GetSourceFormats();

    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        _audioFormatManager.SetSelectedFormat(audioFormat);
        OnDebugLog?.Invoke($"[AdaAudioSource] 🎵 Format: {audioFormat.FormatName} @ {audioFormat.ClockRate}Hz");
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter) => _audioFormatManager.RestrictFormats(filter);
    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
    public bool IsAudioSourcePaused() => _isPaused;

    public void EnableTestTone(bool enable = true)
    {
        _testToneMode = enable;
        _testToneSampleIndex = 0;
        OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 Test tone: {(enable ? "ON" : "OFF")}");
    }

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) { }

    /// <summary>
    /// Queue PCM audio from OpenAI (24kHz, 16-bit signed, little-endian).
    /// </summary>
    public void EnqueuePcm24(byte[] pcmBytes)
    {
        if (_disposed || _isClosed || pcmBytes.Length == 0 || _testToneMode) return;

        // Convert bytes to shorts
        var pcm24All = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcm24All, 0, pcmBytes.Length);

        // Split into 20ms frames (480 samples @ 24kHz)
        const int FRAME_SAMPLES = 480;
        int frameCount = 0;

        for (int offset = 0; offset < pcm24All.Length; offset += FRAME_SAMPLES)
        {
            int len = Math.Min(FRAME_SAMPLES, pcm24All.Length - offset);
            var frame = new short[FRAME_SAMPLES];
            Array.Copy(pcm24All, offset, frame, 0, len);

            // Fade-in on first frame
            if (_needsFadeIn)
            {
                int fadeLen = Math.Min(FADE_IN_SAMPLES, frame.Length);
                for (int i = 0; i < fadeLen; i++)
                    frame[i] = (short)(frame[i] * ((float)i / fadeLen));
                _needsFadeIn = false;
            }

            while (_pcmQueue.Count >= MAX_QUEUED_FRAMES)
                _pcmQueue.TryDequeue(out _);

            _pcmQueue.Enqueue(frame);
            _enqueuedFrames++;
            _wasQueueEmpty = false;
            frameCount++;
        }

        if (_enqueuedFrames == frameCount)
            OnDebugLog?.Invoke($"[AdaAudioSource] 📥 First audio: {pcm24All.Length} samples → {frameCount} frames");

        if ((DateTime.Now - _lastStatsLog).TotalSeconds >= 3)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] 📊 enq={_enqueuedFrames}, sent={_sentFrames}, sil={_silenceFrames}, q={_pcmQueue.Count}");
            _lastStatsLog = DateTime.Now;
        }
    }

    public void ResetFadeIn() => _needsFadeIn = true;
    public void ClearQueue() { while (_pcmQueue.TryDequeue(out _)) { } _needsFadeIn = true; }

    public Task StartAudio()
    {
        if (_audioFormatManager.SelectedFormat.IsEmpty())
            throw new ApplicationException("Audio format not set.");

        if (!_isStarted)
        {
            _isStarted = true;
            _sendTimer = new Timer(SendSample, null, 0, AUDIO_SAMPLE_PERIOD_MS);
            OnDebugLog?.Invoke($"[AdaAudioSource] ▶️ Started, format={_audioFormatManager.SelectedFormat.FormatName}");
        }
        return Task.CompletedTask;
    }

    public Task PauseAudio() { _isPaused = true; return Task.CompletedTask; }
    public Task ResumeAudio() { _isPaused = false; return Task.CompletedTask; }

    public Task CloseAudio()
    {
        if (!_isClosed)
        {
            _isClosed = true;
            _sendTimer?.Dispose();
            ClearQueue();
            OnDebugLog?.Invoke($"[AdaAudioSource] ⏹️ Closed (sent={_sentFrames})");
        }
        return Task.CompletedTask;
    }

    private void SendSample(object? state)
    {
        if (_isClosed || _isPaused || _disposed) return;

        try
        {
            int targetRate = _audioFormatManager.SelectedFormat.ClockRate;
            int samplesNeeded = targetRate / 1000 * AUDIO_SAMPLE_PERIOD_MS;
            short[] audioFrame;

            if (_testToneMode)
            {
                audioFrame = GenerateTestTone(samplesNeeded, targetRate);
            }
            else
            {
                // Jitter buffer: wait for buffer to fill
                if (_audioMode == AudioMode.JitterBuffer && !_jitterBufferFilled)
                {
                    int framesNeeded = _jitterBufferMs / AUDIO_SAMPLE_PERIOD_MS;
                    if (_pcmQueue.Count < framesNeeded)
                    {
                        SendSilence(samplesNeeded);
                        return;
                    }
                    _jitterBufferFilled = true;
                    OnDebugLog?.Invoke($"[AdaAudioSource] 🎯 Jitter buffer filled ({_pcmQueue.Count} frames)");
                }

                if (_pcmQueue.TryDequeue(out var pcm24))
                {
                    _consecutiveUnderruns = 0;

                    // *** USE NAUDIO WDL RESAMPLER - professional quality ***
                    audioFrame = AudioCodecs.ResampleNAudio(pcm24, 24000, targetRate);

                    // Ensure exact frame size
                    if (audioFrame.Length != samplesNeeded)
                    {
                        var fixedFrame = new short[samplesNeeded];
                        Array.Copy(audioFrame, fixedFrame, Math.Min(audioFrame.Length, samplesNeeded));
                        audioFrame = fixedFrame;
                    }

                    // Simple crossfade from silence
                    if (_lastFrameWasSilence && audioFrame.Length > 20)
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            float t = i / 20f;
                            audioFrame[i] = (short)(_lastOutputSample * (1 - t) + audioFrame[i] * t);
                        }
                    }

                    _lastAudioFrame = (short[])audioFrame.Clone();
                }
                else
                {
                    _consecutiveUnderruns++;

                    if (_audioMode == AudioMode.JitterBuffer && _jitterBufferFilled && _consecutiveUnderruns > 10)
                        _jitterBufferFilled = false;

                    // Brief interpolation on underrun
                    if (_consecutiveUnderruns <= 3 && _lastAudioFrame != null)
                    {
                        audioFrame = new short[samplesNeeded];
                        float fadeOut = 1f - (_consecutiveUnderruns * 0.3f);
                        for (int i = 0; i < samplesNeeded && i < _lastAudioFrame.Length; i++)
                            audioFrame[i] = (short)(_lastAudioFrame[i] * fadeOut * (1f - (float)i / samplesNeeded));
                    }
                    else
                    {
                        SendSilence(samplesNeeded);
                        return;
                    }
                }
            }

            if (audioFrame.Length > 0)
            {
                _lastOutputSample = audioFrame[^1];
                _lastFrameWasSilence = false;
            }

            // Encode and send
            byte[] encoded = _audioEncoder.EncodeAudio(audioFrame, _audioFormatManager.SelectedFormat);
            _sentFrames++;

            if (_sentFrames <= 5 && !_testToneMode)
                OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 Frame {_sentFrames}: {encoded.Length}b");

            OnAudioSourceEncodedSample?.Invoke((uint)samplesNeeded, encoded);
        }
        catch (Exception ex)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] ❌ {ex.Message}");
            OnAudioSourceError?.Invoke(ex.Message);
        }
    }

    private short[] GenerateTestTone(int sampleCount, int sampleRate)
    {
        var samples = new short[sampleCount];
        double angularFreq = 2.0 * Math.PI * TEST_TONE_FREQ / sampleRate;

        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = (short)(Math.Sin(angularFreq * _testToneSampleIndex) * 16000);
            _testToneSampleIndex++;
        }
        return samples;
    }

    private void SendSilence(int samplesNeeded = 160)
    {
        _silenceFrames++;
        _lastFrameWasSilence = true;

        if (!_wasQueueEmpty && _pcmQueue.IsEmpty)
        {
            _wasQueueEmpty = true;
            OnQueueEmpty?.Invoke();
        }

        var silence = new short[samplesNeeded];
        
        // Fade out from last sample
        if (_lastOutputSample != 0)
        {
            int fadeLen = Math.Min(20, samplesNeeded);
            for (int i = 0; i < fadeLen; i++)
                silence[i] = (short)(_lastOutputSample * (1f - (float)i / fadeLen));
            _lastOutputSample = 0;
        }

        byte[] encoded = _audioEncoder.EncodeAudio(silence, _audioFormatManager.SelectedFormat);
        OnAudioSourceEncodedSample?.Invoke((uint)samplesNeeded, encoded);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sendTimer?.Dispose();
        ClearQueue();
        GC.SuppressFinalize(this);
    }
}
            else sum += input[i] * 2;
            
            if (i >= 2) sum += input[i - 2];
            else sum += input[i];
            
            if (i < input.Length - 1) sum += input[i + 1] * 2;
            else sum += input[i] * 2;
            
            if (i < input.Length - 2) sum += input[i + 2];
            else sum += input[i];
            
            output[i] = (short)(sum / 9);
        }
        
        return output;
    }

    /// <summary>
    /// Apply cosine crossfade between consecutive frames for seamless transitions.
    /// </summary>
    private static void ApplyInterFrameCrossfade(short[] currentFrame, short[] previousFrame)
    {
        int fadeLen = Math.Min(CROSSFADE_SAMPLES, Math.Min(currentFrame.Length, previousFrame.Length));
        int prevStart = previousFrame.Length - fadeLen;
        
        for (int i = 0; i < fadeLen; i++)
        {
            // Cosine interpolation for smoother power-level transitions
            double phase = (double)(i + 1) / (fadeLen + 1);
            float t = (float)((1.0 - Math.Cos(phase * Math.PI)) / 2.0);
            
            short prevSample = previousFrame[prevStart + i];
            short currSample = currentFrame[i];
            
            currentFrame[i] = (short)(prevSample * (1.0f - t) + currSample * t);
        }
    }

    /// <summary>
    /// Soft-knee limiter prevents clipping while preserving dynamics
    /// </summary>
    private void ApplySoftLimiter(short[] samples)
    {
        if (samples.Length == 0) return;

        float peak = 0;
        foreach (var sample in samples)
        {
            float abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
        }

        if (peak < LIMITER_THRESHOLD)
        {
            _limiterGain = Math.Min(1.0f, _limiterGain + 0.01f);
            return;
        }

        float targetGain = LIMITER_CEILING / peak;
        float alpha = peak > LIMITER_CEILING ? 0.3f : 0.05f;
        _limiterGain = _limiterGain * (1 - alpha) + targetGain * alpha;

        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i] * _limiterGain;

            if (Math.Abs(sample) > LIMITER_THRESHOLD)
            {
                float sign = Math.Sign(sample);
                float abs = Math.Abs(sample);
                float over = (abs - LIMITER_THRESHOLD) / (LIMITER_CEILING - LIMITER_THRESHOLD);
                float compressed = LIMITER_THRESHOLD + (LIMITER_CEILING - LIMITER_THRESHOLD) * (float)Math.Tanh(over);
                sample = sign * compressed;
            }

            samples[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// Generate an interpolated frame during underrun to prevent stuttering.
    /// Fades out the last frame's characteristics over consecutive underruns.
    /// </summary>
    private static short[] GenerateInterpolatedFrame(short[] lastFrame, int samplesNeeded, int underrunCount)
    {
        var output = new short[samplesNeeded];

        // Calculate fade factor based on underrun count (fade out over ~60ms)
        float fadeBase = 1.0f - (underrunCount * 0.15f);
        if (fadeBase < 0) fadeBase = 0;

        // Use tail of last frame with progressive fade
        int copyLen = Math.Min(lastFrame.Length, samplesNeeded);
        int startIdx = Math.Max(0, lastFrame.Length - copyLen);

        for (int i = 0; i < copyLen; i++)
        {
            // Progressive fade within the frame
            float frameFade = fadeBase * (1.0f - (float)i / copyLen * 0.5f);
            output[i] = (short)(lastFrame[startIdx + i] * frameFade);
        }

        return output;
    }

    private void SendSilence()
    {
        try
        {
            int targetRate = _audioFormatManager.SelectedFormat.ClockRate;
            int samplesNeeded = targetRate / 1000 * AUDIO_SAMPLE_PERIOD_MS;
            var silence = new short[samplesNeeded];

            // Avoid click when we transition from non-zero audio to silence.
            if (!_lastFrameWasSilence && _lastOutputSample != 0 && samplesNeeded > 0)
            {
                // ~5ms ramp to zero
                int rampLen = Math.Min(samplesNeeded, Math.Max(1, targetRate / 200));
                for (int i = 0; i < rampLen; i++)
                {
                    float g = 1f - ((float)i / rampLen);
                    silence[i] = (short)(_lastOutputSample * g);
                }
                // Ensure next real audio fades in smoothly.
                _needsFadeIn = true;
            }

            byte[] encoded = _audioEncoder.EncodeAudio(silence, _audioFormatManager.SelectedFormat);
            uint durationRtpUnits = (uint)samplesNeeded;

            _silenceFrames++;
            _lastFrameWasSilence = true;
            _lastOutputSample = 0;

            // Fire OnQueueEmpty event when transitioning from audio to silence
            if (!_wasQueueEmpty)
            {
                _wasQueueEmpty = true;
                OnQueueEmpty?.Invoke();
            }

            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch { }
    }

    /// <summary>
    /// Simple linear interpolation resampling (no filters).
    /// Fastest option, may have some aliasing on downsampling.
    /// </summary>
    private static short[] SimpleResample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;
        if (input.Length == 0) return input;

        double ratio = (double)fromRate / toRate;
        int outputLength = (int)(input.Length / ratio);
        var output = new short[outputLength];

        for (int i = 0; i < output.Length; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            if (srcIndex + 1 < input.Length)
                output[i] = (short)(input[srcIndex] * (1 - frac) + input[srcIndex + 1] * frac);
            else if (srcIndex < input.Length)
                output[i] = input[srcIndex];
        }

        return output;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sendTimer?.Dispose();
        ClearQueue();

        GC.SuppressFinalize(this);
    }
}
