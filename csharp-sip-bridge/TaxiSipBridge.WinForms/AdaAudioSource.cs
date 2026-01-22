using System.Collections.Concurrent;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Custom audio source that receives PCM audio from Ada (WebSocket) and provides
/// encoded samples to SIPSorcery's RTP transport.
/// 
/// Uses ConcurrentQueue + System.Threading.Timer for reliable 20ms pacing.
/// Simple approach: queue 24kHz frames, downsample to 8kHz inline, encode to G.711.
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 500;
    private const int FADE_IN_SAMPLES = 80;
    private const int CROSSFADE_SAMPLES = 40;

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;
    private readonly ConcurrentQueue<short[]> _pcmQueue = new();

    private System.Threading.Timer? _sendTimer;
    private readonly object _timerLock = new();
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

    // Boundary smoothing
    private short _lastOutputSample;
    private bool _lastFrameWasSilence = true;
    private short[]? _lastAudioFrame;

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
        _audioEncoder = preferOpus ? new OpusAudioEncoder() : new AudioEncoder();
        _audioFormatManager = new MediaFormatManager<AudioFormat>(_audioEncoder.SupportedFormats);

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
    /// Queue PCM audio from Ada (24kHz, 16-bit signed, little-endian).
    /// Packetizes into 20ms frames for the timer to consume.
    /// </summary>
    public void EnqueuePcm24(byte[] pcmBytes)
    {
        if (_disposed || _isClosed || pcmBytes.Length == 0) return;
        if (_testToneMode) return;

        // Convert bytes to shorts (24kHz, 16-bit mono)
        var pcm24All = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcm24All, 0, pcmBytes.Length);

        // Packetize into 20ms frames at 24kHz = 480 samples per frame
        const int PCM24_FRAME_SAMPLES = 24000 / 1000 * AUDIO_SAMPLE_PERIOD_MS; // 480

        int frameCount = 0;
        for (int offset = 0; offset < pcm24All.Length; offset += PCM24_FRAME_SAMPLES)
        {
            int len = Math.Min(PCM24_FRAME_SAMPLES, pcm24All.Length - offset);

            var frame = new short[PCM24_FRAME_SAMPLES];
            Array.Copy(pcm24All, offset, frame, 0, len);

            // Apply fade-in on first frame after reset
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
            frameCount++;
        }

        _wasQueueEmpty = false;

        // Log first enqueue
        if (_enqueuedFrames > 0 && _enqueuedFrames == frameCount)
            OnDebugLog?.Invoke($"[AdaAudioSource] 📥 First audio: {pcm24All.Length} samples → {frameCount} frames");

        // Log stats every 3 seconds
        if ((DateTime.Now - _lastStatsLog).TotalSeconds >= 3)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] 📊 enq={_enqueuedFrames}, sent={_sentFrames}, sil={_silenceFrames}, q={_pcmQueue.Count}");
            _lastStatsLog = DateTime.Now;
        }
    }

    public void ResetFadeIn()
    {
        _needsFadeIn = true;
        _lastAudioFrame = null;
    }

    public void ClearQueue()
    {
        while (_pcmQueue.TryDequeue(out _)) { }
        _needsFadeIn = true;
        _lastAudioFrame = null;
    }

    public Task StartAudio()
    {
        if (_audioFormatManager.SelectedFormat.IsEmpty())
            throw new ApplicationException("Audio format not set. Cannot start AdaAudioSource.");

        if (!_isStarted)
        {
            _isStarted = true;
            _sendTimer = new System.Threading.Timer(SendSample, null, 0, AUDIO_SAMPLE_PERIOD_MS);
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
        OnDebugLog?.Invoke($"[AdaAudioSource] 📢 Audio resumed (frame {_sentFrames})");
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
            ClearQueue();
            OnDebugLog?.Invoke($"[AdaAudioSource] ⏹️ Closed (enq={_enqueuedFrames}, sent={_sentFrames}, sil={_silenceFrames})");
        }
        return Task.CompletedTask;
    }

    private void SendSample(object? state)
    {
        if (_isClosed || _isPaused || _disposed) return;

        // Prevent re-entrancy
        if (!Monitor.TryEnter(_timerLock)) return;

        try
        {
            int targetRate = _audioFormatManager.SelectedFormat.ClockRate;
            if (targetRate == 0) return;

            int samplesNeeded = targetRate / 1000 * AUDIO_SAMPLE_PERIOD_MS;
            short[] audioFrame;

            if (_testToneMode)
            {
                audioFrame = GenerateTestTone(samplesNeeded, targetRate);
            }
            else if (_pcmQueue.TryDequeue(out var pcm24))
            {
                // Got audio from queue - resample to target rate
                if (targetRate == 8000)
                {
                    audioFrame = Downsample24kTo8k(pcm24);
                }
                else if (targetRate == 48000)
                {
                    audioFrame = Upsample24kTo48k(pcm24);
                }
                else
                {
                    audioFrame = ResampleSamples(pcm24, 24000, targetRate);
                }

                // Ensure exact frame size
                if (audioFrame.Length != samplesNeeded)
                {
                    var fixedFrame = new short[samplesNeeded];
                    Array.Copy(audioFrame, fixedFrame, Math.Min(audioFrame.Length, samplesNeeded));
                    audioFrame = fixedFrame;
                }

                // Crossfade from silence to audio
                if (_lastFrameWasSilence && audioFrame.Length > CROSSFADE_SAMPLES)
                {
                    int fadeLen = Math.Min(CROSSFADE_SAMPLES, audioFrame.Length);
                    for (int i = 0; i < fadeLen; i++)
                    {
                        float t = (float)i / fadeLen;
                        audioFrame[i] = (short)(_lastOutputSample * (1 - t) + audioFrame[i] * t);
                    }
                }

                _lastAudioFrame = audioFrame;
                _lastFrameWasSilence = false;

                if (_wasQueueEmpty)
                {
                    _wasQueueEmpty = false;
                    OnDebugLog?.Invoke($"[AdaAudioSource] 📢 Audio resumed (frame {_sentFrames})");
                }
            }
            else
            {
                // No data in queue - interpolate or send silence
                if (_lastAudioFrame != null && _silenceFrames < 3)
                {
                    // Fade out last frame
                    audioFrame = FadeFrame(_lastAudioFrame, 0.6f);
                    _lastAudioFrame = audioFrame;
                    _interpolatedFrames++;
                }
                else
                {
                    // Send silence with ramp-down
                    audioFrame = GenerateSilence(samplesNeeded, _lastOutputSample);
                    _lastAudioFrame = null;
                }

                _silenceFrames++;
                
                if (!_wasQueueEmpty)
                {
                    _wasQueueEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }

            // Track last sample for smooth transitions
            if (audioFrame.Length > 0)
            {
                _lastOutputSample = audioFrame[^1];
                if (audioFrame.Any(s => s != 0))
                {
                    _lastFrameWasSilence = false;
                    _silenceFrames = 0;
                }
            }

            // Log first few frames
            if (_sentFrames < 5)
            {
                OnDebugLog?.Invoke($"[AdaAudioSource] 📤 Frame {_sentFrames}: {audioFrame.Length} samples @{targetRate}Hz");
            }

            // Encode using the appropriate codec
            byte[] encoded;
            var format = _audioFormatManager.SelectedFormat;
            
            if (format.FormatName == "PCMU" || format.ID == 0)
            {
                // Use lookup table encoder for G.711 µ-law
                encoded = AudioCodecs.MuLawEncode(audioFrame);
            }
            else if (format.FormatName == "PCMA" || format.ID == 8)
            {
                // A-law encoding
                encoded = AudioCodecs.ALawEncode(audioFrame);
            }
            else
            {
                // Opus or other - use SIPSorcery encoder
                encoded = _audioEncoder.EncodeAudio(audioFrame, format);
            }

            uint durationRtpUnits = (uint)samplesNeeded;
            _sentFrames++;
            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch (Exception ex)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] ❌ Error: {ex.Message}");
            OnAudioSourceError?.Invoke($"AdaAudioSource error: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_timerLock);
        }
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

    private static short[] GenerateSilence(int sampleCount, short lastSample)
    {
        var silence = new short[sampleCount];
        
        // Ramp to zero to avoid click
        if (lastSample != 0 && sampleCount > 0)
        {
            int rampLen = Math.Min(sampleCount, 40);
            for (int i = 0; i < rampLen; i++)
            {
                float g = 1f - ((float)i / rampLen);
                silence[i] = (short)(lastSample * g);
            }
        }
        
        return silence;
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

    /// <summary>
    /// Downsample 24kHz to 8kHz (3:1 ratio) with simple low-pass filter.
    /// </summary>
    private static short[] Downsample24kTo8k(short[] pcm24)
    {
        if (pcm24.Length < 3) return new short[0];

        int outLen = pcm24.Length / 3;
        var output = new short[outLen];

        for (int i = 0; i < outLen; i++)
        {
            int idx = i * 3;
            int s0 = pcm24[idx];
            int s1 = pcm24[idx + 1];
            int s2 = pcm24[idx + 2];

            // 3-tap low-pass: (1,2,1)/4
            output[i] = (short)((s0 + (s1 * 2) + s2) / 4);
        }

        return output;
    }

    /// <summary>
    /// Upsample 24kHz to 48kHz (1:2 ratio) with linear interpolation.
    /// </summary>
    private static short[] Upsample24kTo48k(short[] pcm24)
    {
        int outLen = pcm24.Length * 2;
        var output = new short[outLen];

        for (int i = 0; i < pcm24.Length; i++)
        {
            int outIdx = i * 2;
            output[outIdx] = pcm24[i];
            
            // Interpolate next sample
            if (i < pcm24.Length - 1)
            {
                output[outIdx + 1] = (short)((pcm24[i] + pcm24[i + 1]) / 2);
            }
            else
            {
                output[outIdx + 1] = pcm24[i];
            }
        }

        return output;
    }

    /// <summary>
    /// Generic linear resampling for other rates.
    /// </summary>
    private static short[] ResampleSamples(short[] input, int fromRate, int toRate)
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

        lock (_timerLock)
        {
            _sendTimer?.Dispose();
            _sendTimer = null;
        }
        
        ClearQueue();
        GC.SuppressFinalize(this);
    }
}
