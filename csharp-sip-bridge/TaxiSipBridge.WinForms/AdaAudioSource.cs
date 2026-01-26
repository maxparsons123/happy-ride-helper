using System.Collections.Concurrent;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Timer = System.Threading.Timer;

namespace TaxiSipBridge;

/// <summary>
/// Custom audio source that receives PCM audio from Ada (WebSocket) and provides
/// encoded samples to SIPSorcery's RTP transport. Based on AudioExtrasSource pattern.
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 5000; // ~100 seconds buffer - OpenAI sends faster than real-time
    private const int FADE_IN_SAMPLES = 80;  // Smooth fade-in
    private const int CROSSFADE_SAMPLES = 40; // For silence-to-audio transitions
    
    // DSP constants matching edge function
    private const float VOLUME_BOOST = 1.4f;       // Match edge function boost
    private const float PRE_EMPHASIS_COEFF = 0.97f; // High-frequency boost for clarity
    private const float DE_EMPHASIS_COEFF = 0.95f;  // Reverse pre-emphasis for natural sound

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
    private int _interpolatedFrames;
    private DateTime _lastStatsLog = DateTime.MinValue;

    // Boundary smoothing to prevent clicks/crackling
    private short _lastOutputSample;
    private short _prevFrameLastSample; // For inter-frame crossfade
    private bool _lastFrameWasSilence = true;
    private short[]? _lastAudioFrame; // Keep last frame for interpolation
    private short _preEmphasisPrevSample; // For pre-emphasis filter state

    // Soft-knee limiter to prevent clipping
    private float _limiterGain = 1.0f;
    private const float LIMITER_THRESHOLD = 28000f;
    private const float LIMITER_CEILING = 32000f;

    // Audio mode configuration
    private AudioMode _audioMode = AudioMode.Standard;
    private int _jitterBufferMs = 80; // Increased default for better buffering
    private bool _jitterBufferFilled;
    private int _consecutiveUnderruns;

    // Test tone mode
    private bool _testToneMode;
    private int _testToneSampleIndex;
    private const double TEST_TONE_FREQ = 440.0; // A4 note

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError;
    event Action<EncodedAudioFrame>? IAudioSource.OnAudioSourceEncodedFrameReady
    {
        add { }
        remove { }
    }

    // Debug logging
    public event Action<string>? OnDebugLog;

    // Fires when audio queue becomes empty (bot finished speaking)
    public event Action? OnQueueEmpty;
    private bool _wasQueueEmpty = true; // Track state to fire only on transition

    public AdaAudioSource(AudioMode audioMode = AudioMode.Standard, int jitterBufferMs = 80)
    {
        _audioEncoder = new AudioEncoder();
        _audioFormatManager = new MediaFormatManager<AudioFormat>(_audioEncoder.SupportedFormats);
        _audioMode = audioMode;
        _jitterBufferMs = jitterBufferMs;

        // Test tone mode is set via audio mode
        if (_audioMode == AudioMode.TestTone)
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

    /// <summary>
    /// Enable test tone mode - sends a 440Hz sine wave instead of Ada audio.
    /// Useful for debugging codec/format issues.
    /// </summary>
    public void EnableTestTone(bool enable = true)
    {
        _testToneMode = enable;
        _testToneSampleIndex = 0;
        OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 Test tone mode: {(enable ? "ENABLED" : "DISABLED")}");
    }

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        // Not used - we receive audio via EnqueuePcm24
    }

    /// <summary>
    /// Queue PCM audio from Ada (24kHz, 16-bit signed, little-endian).
    /// This will be resampled and encoded based on negotiated codec.
    /// </summary>
    public void EnqueuePcm24(byte[] pcmBytes)
    {
        if (_disposed || _isClosed || pcmBytes.Length == 0) return;
        if (_testToneMode) return; // Ignore Ada audio in test mode

        // Convert bytes to shorts (24kHz, 16-bit mono)
        var pcm24All = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcm24All, 0, pcmBytes.Length);

        // IMPORTANT: packetise into 20ms frames.
        // Ada deltas can be variable length; RTP expects consistent 20ms frames.
        const int PCM24_FRAME_SAMPLES = 24000 / 1000 * AUDIO_SAMPLE_PERIOD_MS; // 480

        int frameCount = 0;
        for (int offset = 0; offset < pcm24All.Length; offset += PCM24_FRAME_SAMPLES)
        {
            int len = Math.Min(PCM24_FRAME_SAMPLES, pcm24All.Length - offset);

            // Pad last frame to full 20ms to keep timestamps stable.
            var frame = new short[PCM24_FRAME_SAMPLES];
            Array.Copy(pcm24All, offset, frame, 0, len);

            // Apply fade-in only on the first frame after a reset.
            if (_needsFadeIn && frame.Length > 0)
            {
                int fadeLen = Math.Min(FADE_IN_SAMPLES, frame.Length);
                for (int i = 0; i < fadeLen; i++)
                {
                    float gain = (float)i / fadeLen;
                    frame[i] = (short)(frame[i] * gain);
                }
                _needsFadeIn = false;
            }

            // Bound the queue
            while (_pcmQueue.Count >= MAX_QUEUED_FRAMES)
                _pcmQueue.TryDequeue(out _);

            _pcmQueue.Enqueue(frame);
            _enqueuedFrames++;
            _wasQueueEmpty = false; // Mark that we have audio
            frameCount++;
        }

        // Log first enqueue
        if (_enqueuedFrames > 0 && _enqueuedFrames == frameCount)
            OnDebugLog?.Invoke($"[AdaAudioSource] 📥 First audio: {pcm24All.Length} samples split into {frameCount}x{PCM24_FRAME_SAMPLES}");

        // Log stats every 3 seconds
        if ((DateTime.Now - _lastStatsLog).TotalSeconds >= 3)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] 📊 enq={_enqueuedFrames}, sent={_sentFrames}, sil={_silenceFrames}, interp={_interpolatedFrames}, q={_pcmQueue.Count}");
            _lastStatsLog = DateTime.Now;
        }
    }

    /// <summary>
    /// Signal that a new response is starting (for fade-in).
    /// </summary>
    public void ResetFadeIn()
    {
        _needsFadeIn = true;
    }

    /// <summary>
    /// Clear all queued audio.
    /// </summary>
    public void ClearQueue()
    {
        while (_pcmQueue.TryDequeue(out _)) { }
        _needsFadeIn = true;
    }

    public Task StartAudio()
    {
        if (_audioFormatManager.SelectedFormat.IsEmpty())
            throw new ApplicationException("Audio format not set. Cannot start AdaAudioSource.");

        if (!_isStarted)
        {
            _isStarted = true;
            _sendTimer = new Timer(SendSample, null, 0, AUDIO_SAMPLE_PERIOD_MS);
            OnDebugLog?.Invoke($"[AdaAudioSource] ▶️ Timer started ({AUDIO_SAMPLE_PERIOD_MS}ms), format={_audioFormatManager.SelectedFormat.FormatName}");
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
            _sendTimer?.Dispose();
            _sendTimer = null;
            ClearQueue();
            OnDebugLog?.Invoke($"[AdaAudioSource] ⏹️ Closed (enqueued={_enqueuedFrames}, sent={_sentFrames}, silence={_silenceFrames})");
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
                // Generate a 440Hz test tone
                audioFrame = GenerateTestTone(samplesNeeded, targetRate);
            }
            else
            {
                // Check jitter buffer mode - wait until we have enough frames buffered
                if (_audioMode == AudioMode.JitterBuffer && !_jitterBufferFilled)
                {
                    int framesNeeded = _jitterBufferMs / AUDIO_SAMPLE_PERIOD_MS; // e.g., 60ms = 3 frames
                    if (_pcmQueue.Count < framesNeeded)
                    {
                        SendSilence();
                        return;
                    }
                    _jitterBufferFilled = true;
                    OnDebugLog?.Invoke($"[AdaAudioSource] 🎯 Jitter buffer filled ({_pcmQueue.Count} frames)");
                }

                if (_pcmQueue.TryDequeue(out var pcm24))
                {
                    _consecutiveUnderruns = 0;

                    // === DSP PIPELINE (matches edge function) ===
                    
                    // 1. Apply volume boost
                    ApplyVolumeBoost(pcm24, VOLUME_BOOST);
                    
                    // 2. Apply low-pass anti-aliasing filter before resampling
                    var filtered = ApplyLowPassFilter(pcm24);

                    // 3. Resample based on target rate
                    if (_audioMode == AudioMode.SimpleResample)
                    {
                        audioFrame = SimpleResample(filtered, 24000, targetRate);
                    }
                    else if (targetRate == 8000)
                    {
                        audioFrame = Downsample24kTo8k(filtered);
                    }
                    else
                    {
                        audioFrame = AudioCodecs.Resample(filtered, 24000, targetRate);
                    }

                    // 4. Hard-enforce exact 20ms frame size for RTP
                    if (audioFrame.Length != samplesNeeded)
                    {
                        var fixedFrame = new short[samplesNeeded];
                        Array.Copy(audioFrame, fixedFrame, Math.Min(audioFrame.Length, samplesNeeded));
                        audioFrame = fixedFrame;
                    }

                    // 5. Apply soft-knee limiter to prevent clipping
                    ApplySoftLimiter(audioFrame);
                    
                    // 6. Apply inter-frame cosine crossfade for smooth transitions
                    if (!_lastFrameWasSilence && _lastAudioFrame != null && audioFrame.Length >= CROSSFADE_SAMPLES)
                    {
                        ApplyInterFrameCrossfade(audioFrame, _lastAudioFrame);
                    }
                    
                    // 7. If transitioning from silence, crossfade from last sample
                    if (_lastFrameWasSilence && audioFrame.Length > CROSSFADE_SAMPLES)
                    {
                        int fadeLen = Math.Min(CROSSFADE_SAMPLES, audioFrame.Length);
                        for (int i = 0; i < fadeLen; i++)
                        {
                            float t = (float)i / fadeLen;
                            audioFrame[i] = (short)(_lastOutputSample * (1 - t) + audioFrame[i] * t);
                        }
                    }

                    // Store for interpolation on underrun
                    _lastAudioFrame = (short[])audioFrame.Clone();
                }
                else
                {
                    _consecutiveUnderruns++;

                    // Reset jitter buffer state after prolonged underrun
                    if (_audioMode == AudioMode.JitterBuffer && _jitterBufferFilled && _consecutiveUnderruns > 10)
                    {
                        _jitterBufferFilled = false;
                    }

                    // On first several underruns, interpolate from last frame to prevent stuttering
                    // OpenAI sends audio in bursts, so we need ~120ms tolerance
                    if (_consecutiveUnderruns <= 6 && _lastAudioFrame != null)
                    {
                        audioFrame = GenerateInterpolatedFrame(_lastAudioFrame, samplesNeeded, _consecutiveUnderruns);
                        _interpolatedFrames++;
                    }
                    else
                    {
                        SendSilence();
                        return;
                    }
                }
            }

            // Track samples for next frame's crossfade
            if (audioFrame.Length > 0)
            {
                _prevFrameLastSample = audioFrame[^1];
                _lastOutputSample = audioFrame[^1];
                _lastFrameWasSilence = false;
            }

            // Encode using negotiated codec
            byte[] encoded = _audioEncoder.EncodeAudio(audioFrame, _audioFormatManager.SelectedFormat);

            // Calculate RTP duration
            uint durationRtpUnits = (uint)samplesNeeded;

            _sentFrames++;

            // Log first few Ada audio frames to confirm they're being sent
            if (_sentFrames <= 10 && !_testToneMode)
            {
                OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 ADA frame {_sentFrames}: {encoded.Length}b {_audioFormatManager.SelectedFormat.FormatName}, hasSubscribers={OnAudioSourceEncodedSample != null}");
            }

            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch (Exception ex)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] ❌ Error: {ex.Message}");
            OnAudioSourceError?.Invoke($"AdaAudioSource error: {ex.Message}");
        }
    }

    /// <summary>
    /// Generate a sine wave test tone at 440Hz.
    /// </summary>
    private short[] GenerateTestTone(int sampleCount, int sampleRate)
    {
        var samples = new short[sampleCount];
        double angularFreq = 2.0 * Math.PI * TEST_TONE_FREQ / sampleRate;

        for (int i = 0; i < sampleCount; i++)
        {
            double sample = Math.Sin(angularFreq * _testToneSampleIndex);
            samples[i] = (short)(sample * 16000); // ~50% volume
            _testToneSampleIndex++;
        }

        return samples;
    }

    /// <summary>
    /// High-quality 24kHz to 8kHz downsampling with 11-tap FIR anti-aliasing filter.
    /// Matches the edge function's resampling quality to prevent metallic/robotic sound.
    /// Uses Sinc filter with Blackman window, cutting off above 3.5kHz.
    /// </summary>
    private static short[] Downsample24kTo8k(short[] pcm24)
    {
        if (pcm24.Length < 3) return Array.Empty<short>();

        int outLen = pcm24.Length / 3;
        var output = new short[outLen];

        // 11-tap FIR filter coefficients (Sinc with Blackman window)
        // This cuts off frequencies above 3.5kHz to prevent aliasing
        float[] h = { -0.005f, -0.012f, 0.010f, 0.080f, 0.180f, 0.240f, 0.180f, 0.080f, 0.010f, -0.012f, -0.005f };
        float sum = 0.746f; // Sum of coefficients for normalization

        for (int i = 0; i < outLen; i++)
        {
            float acc = 0;
            int center = i * 3;
            for (int j = 0; j < h.Length; j++)
            {
                int idx = center + j - 5; // Center the filter
                if (idx >= 0 && idx < pcm24.Length) 
                    acc += pcm24[idx] * h[j];
            }
            output[i] = (short)Math.Clamp(acc / sum, short.MinValue, short.MaxValue);
        }

        return output;
    }

    /// <summary>
    /// Apply volume boost to increase clarity.
    /// </summary>
    private static void ApplyVolumeBoost(short[] samples, float boost)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float val = samples[i] * boost;
            samples[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// Low-pass anti-aliasing filter before downsampling.
    /// 5-tap moving average provides smooth frequency rolloff.
    /// </summary>
    private static short[] ApplyLowPassFilter(short[] input)
    {
        if (input.Length < 5) return input;
        
        var output = new short[input.Length];
        
        // 5-tap weighted filter: [1, 2, 3, 2, 1] / 9
        for (int i = 0; i < input.Length; i++)
        {
            int sum = input[i] * 3; // Center weight
            
            if (i >= 1) sum += input[i - 1] * 2;
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
