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
    private const int MAX_QUEUED_FRAMES = 500;
    private const int FADE_IN_SAMPLES = 80;  // Increased for smoother fade
    private const int CROSSFADE_SAMPLES = 40; // For silence-to-audio transitions

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

    // Boundary smoothing to prevent clicks/crackling during underruns
    private short _lastOutputSample;
    private bool _lastFrameWasSilence = true;
    private short[]? _lastAudioFrame; // Keep last frame for interpolation

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

    /// <summary>
    /// Exposes the encoder for use by SipToAdaDecoder (decode path).
    /// </summary>
    public IAudioEncoder Encoder => _audioEncoder;

    /// <summary>
    /// Exposes the negotiated format for use by SipToAdaDecoder.
    /// </summary>
    public AudioFormat SelectedFormat => _audioFormatManager.SelectedFormat;

    public AdaAudioSource(AudioMode audioMode = AudioMode.Standard, int jitterBufferMs = 60)
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

                    // Select resampling method based on audio mode
                    if (_audioMode == AudioMode.SimpleResample)
                    {
                        // Simple linear interpolation only
                        audioFrame = SimpleResample(pcm24, 24000, targetRate);
                    }
                    else if (targetRate == 8000)
                    {
                        // Use optimized 3:1 decimator for 8kHz
                        audioFrame = Downsample24kTo8k(pcm24);
                    }
                    else
                    {
                        // Use AudioCodecs high-quality resampler
                        audioFrame = AudioCodecs.Resample(pcm24, 24000, targetRate);
                    }

                    // Hard-enforce exact 20ms frame size for RTP
                    if (audioFrame.Length != samplesNeeded)
                    {
                        var fixedFrame = new short[samplesNeeded];
                        Array.Copy(audioFrame, fixedFrame, Math.Min(audioFrame.Length, samplesNeeded));
                        audioFrame = fixedFrame;
                    }

                    // If we were in silence, crossfade from last sample to new audio
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
                    _lastAudioFrame = audioFrame;
                }
                else
                {
                    _consecutiveUnderruns++;

                    // Reset jitter buffer state
                    if (_audioMode == AudioMode.JitterBuffer && _jitterBufferFilled)
                    {
                        _jitterBufferFilled = false;
                    }

                    // On first few underruns, try to interpolate from last frame
                    // This prevents stuttering on brief gaps
                    if (_consecutiveUnderruns <= 3 && _lastAudioFrame != null)
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

            // Track last sample to avoid clicks on underrun transitions
            if (audioFrame.Length > 0)
            {
                _lastOutputSample = audioFrame[^1];
                _lastFrameWasSilence = false;
            }

            // Log first few frames
            if (_sentFrames < 5)
            {
                var mode = _testToneMode ? "TONE" : "ADA";
                OnDebugLog?.Invoke($"[AdaAudioSource] 📤 [{mode}] Frame {_sentFrames}: {audioFrame.Length} samples @{targetRate}Hz, codec={_audioFormatManager.SelectedFormat.FormatName}");
            }

            // Encode using negotiated codec
            byte[] encoded = _audioEncoder.EncodeAudio(audioFrame, _audioFormatManager.SelectedFormat);

            // Calculate RTP duration
            uint durationRtpUnits = (uint)samplesNeeded;

            _sentFrames++;
            
            // Check if we have RTP subscribers
            bool hasSubscribers = OnAudioSourceEncodedSample != null;
            if (_sentFrames <= 5)
            {
                OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 RTP out #{_sentFrames}: {encoded.Length}b, hasSubscribers={hasSubscribers}");
            }
            
            if (hasSubscribers)
            {
                OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
            }
            else if (_sentFrames == 1)
            {
                OnDebugLog?.Invoke($"[AdaAudioSource] ⚠️ NO RTP SUBSCRIBERS! Audio won't reach SIP!");
            }
            
            // Log RTP stats every 5 seconds
            if ((DateTime.Now - _lastStatsLog).TotalSeconds >= 5)
            {
                OnDebugLog?.Invoke($"[AdaAudioSource] 📊 RTP stats: sent={_sentFrames}, encoded={encoded.Length}b, subscribers={hasSubscribers}");
                _lastStatsLog = DateTime.Now;
            }
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

    private static short[] Downsample24kTo8k(short[] pcm24)
    {
        // 24kHz → 8kHz using NAudio's WDL resampler
        if (pcm24.Length < 3) return new short[0];

        var resampled = AudioCodecs.Resample(pcm24, 24000, 8000);
        
        // Apply soft limiter instead of hard clipping
        // This prevents harsh clipping artifacts while maintaining volume
        const float threshold = 24000f;  // Start limiting here
        const float ceiling = 32000f;    // Max output
        
        for (int i = 0; i < resampled.Length; i++)
        {
            float sample = resampled[i];
            float absSample = Math.Abs(sample);
            
            if (absSample > threshold)
            {
                // Soft knee compression above threshold
                float excess = absSample - threshold;
                float compressed = threshold + (excess * 0.3f); // 3:1 ratio
                compressed = Math.Min(compressed, ceiling);
                sample = sample > 0 ? compressed : -compressed;
            }
            
            resampled[i] = (short)sample;
        }

        return resampled;
    }

    /// <summary>
    /// Generate an interpolated frame during underrun to prevent stuttering.
    /// Fades out the last frame's characteristics over consecutive underruns.
    /// </summary>
    private static short[] GenerateInterpolatedFrame(short[] lastFrame, int samplesNeeded, int underrunCount)
    {
        var output = new short[samplesNeeded];

        // Calculate fade factor based on underrun count (fade out over ~60ms)
        float fadeBase = 1.0f - (underrunCount * 0.3f);
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

    /// <summary>
    /// Resample with anti-aliasing filter for better quality.
    /// Uses a simple moving average low-pass before downsampling.
    /// </summary>
    private static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;

        // For downsampling (24kHz → 8kHz), apply anti-aliasing filter first
        short[] filtered = input;
        if (fromRate > toRate)
        {
            int filterSize = (int)Math.Ceiling((double)fromRate / toRate);
            filtered = ApplyLowPassFilter(input, filterSize);
        }

        double ratio = (double)fromRate / toRate;
        int outputLength = (int)(filtered.Length / ratio);
        var output = new short[outputLength];

        for (int i = 0; i < output.Length; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            if (srcIndex + 1 < filtered.Length)
                output[i] = (short)(filtered[srcIndex] * (1 - frac) + filtered[srcIndex + 1] * frac);
            else if (srcIndex < filtered.Length)
                output[i] = filtered[srcIndex];
        }

        return output;
    }

    /// <summary>
    /// Simple moving average low-pass filter to prevent aliasing.
    /// </summary>
    private static short[] ApplyLowPassFilter(short[] input, int windowSize)
    {
        if (windowSize <= 1) return input;

        var output = new short[input.Length];
        int halfWindow = windowSize / 2;

        for (int i = 0; i < input.Length; i++)
        {
            long sum = 0;
            int count = 0;

            for (int j = -halfWindow; j <= halfWindow; j++)
            {
                int idx = i + j;
                if (idx >= 0 && idx < input.Length)
                {
                    sum += input[idx];
                    count++;
                }
            }

            output[i] = (short)(sum / count);
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
