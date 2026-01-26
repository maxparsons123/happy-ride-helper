using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Timer = System.Threading.Timer;

namespace TaxiSipBridge;

/// <summary>
/// Custom audio source that receives PCM audio from Ada (WebSocket) and provides
/// encoded samples to SIPSorcery's RTP transport.
/// 
/// Uses simple linear interpolation for resampling (proven reliable).
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 5000;
    private const int FADE_IN_SAMPLES = 80;
    private const int CROSSFADE_SAMPLES = 40;

    // DSP parameters
    private const float VOLUME_BOOST = 1.4f;
    private const float PRE_EMPHASIS_COEFF = 0.97f;
    private const float LIMITER_THRESHOLD = 28000f;
    private const float LIMITER_CEILING = 32000f;

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;
    private readonly ConcurrentQueue<short[]> _pcmQueue = new();
    private readonly object _sendLock = new();

    // Resampler state (simple linear interpolation - proven to work)
    private double _resamplePhase;
    private short _lastInputSample;

    private Timer? _sendTimer;
    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private bool _needsFadeIn = true;
    private volatile bool _disposed;

    // State tracking
    private short _lastOutputSample;
    private short _prevFrameLastSample;
    private bool _lastFrameWasSilence = true;
    private short[]? _lastAudioFrame;
    private float _preEmphasisPrevSample;
    private float _limiterGain = 1.0f;

    // Jitter & timing
    private AudioMode _audioMode = AudioMode.Standard;
    private int _jitterBufferMs = 80;
    private bool _jitterBufferFilled;
    private int _consecutiveUnderruns;
    private bool _markEndOfSpeech;

    // Test tone (instance field, not static)
    private bool _testToneMode;
    private int _testToneSampleIndex;
    private const double TEST_TONE_FREQ = 440.0;

    // Debug/stats
    private int _enqueuedFrames;
    private int _sentFrames;
    private int _silenceFrames;
    private int _interpolatedFrames;
    private DateTime _lastStatsLog = DateTime.MinValue;
    private bool _wasQueueEmpty = true;

    // Events
    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError;
    event Action<EncodedAudioFrame>? IAudioSource.OnAudioSourceEncodedFrameReady { add { } remove { } }

    public event Action<string>? OnDebugLog;
    public event Action? OnQueueEmpty;

    public AdaAudioSource(AudioMode audioMode = AudioMode.Standard, int jitterBufferMs = 80)
    {
        _audioEncoder = new AudioEncoder();
        _audioFormatManager = new MediaFormatManager<AudioFormat>(_audioEncoder.SupportedFormats);
        _audioMode = audioMode;
        _jitterBufferMs = Math.Max(20, jitterBufferMs);
        _testToneMode = audioMode == AudioMode.TestTone;
    }

    public List<AudioFormat> GetAudioSourceFormats() => _audioFormatManager.GetSourceFormats();

    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        if (_audioFormatManager.SelectedFormat.FormatName == audioFormat.FormatName &&
            _audioFormatManager.SelectedFormat.ClockRate == audioFormat.ClockRate)
            return;

        _audioFormatManager.SetSelectedFormat(audioFormat);

        // Reset resampler state for new format
        _resamplePhase = 0;
        _lastInputSample = 0;

        OnDebugLog?.Invoke($"[AdaAudioSource] 🎵 Format: {audioFormat.FormatName} @ {audioFormat.ClockRate}Hz (linear resample)");
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
        OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 Test tone mode: {(enable ? "ENABLED" : "DISABLED")}");
    }

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        // Not used
    }

    public void EnqueuePcm24(byte[] pcmBytes)
    {
        if (_disposed || _isClosed || _testToneMode || pcmBytes.Length == 0) return;

        var pcm24All = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcm24All, 0, pcmBytes.Length);

        const int PCM24_FRAME_SAMPLES = 24000 / 1000 * AUDIO_SAMPLE_PERIOD_MS; // 480
        int frameCount = 0;

        for (int offset = 0; offset < pcm24All.Length; offset += PCM24_FRAME_SAMPLES)
        {
            int len = Math.Min(PCM24_FRAME_SAMPLES, pcm24All.Length - offset);
            var frame = new short[PCM24_FRAME_SAMPLES];
            Array.Copy(pcm24All, offset, frame, 0, len);

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
            OnDebugLog?.Invoke($"[AdaAudioSource] 📊 enq={_enqueuedFrames}, sent={_sentFrames}, sil={_silenceFrames}, interp={_interpolatedFrames}, q={_pcmQueue.Count}");
            _lastStatsLog = DateTime.Now;
        }
    }

    public void ResetFadeIn() => _needsFadeIn = true;

    public void ClearQueue()
    {
        while (_pcmQueue.TryDequeue(out _)) { }
        _needsFadeIn = true;
        _markEndOfSpeech = false;
    }

    public void MarkEndOfSpeech() => _markEndOfSpeech = true;

    public Task StartAudio()
    {
        if (_audioFormatManager.SelectedFormat.IsEmpty())
            throw new ApplicationException("Audio format not set.");

        if (!_isStarted)
        {
            _isStarted = true;
            _sendTimer = new Timer(SendSample, null, 0, AUDIO_SAMPLE_PERIOD_MS);
            OnDebugLog?.Invoke($"[AdaAudioSource] ▶️ Started ({AUDIO_SAMPLE_PERIOD_MS}ms), format={_audioFormatManager.SelectedFormat.FormatName}");
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
            _sendTimer = null;
            ClearQueue();
            OnDebugLog?.Invoke($"[AdaAudioSource] ⏹️ Closed (enq={_enqueuedFrames}, sent={_sentFrames})");
        }
        return Task.CompletedTask;
    }

    private void SendSample(object? state)
    {
        if (_isClosed || _isPaused || _disposed) return;

        // Prevent timer reentrancy causing stuttering
        if (!Monitor.TryEnter(_sendLock)) return;
        try
        {
            SendSampleCore();
        }
        finally
        {
            Monitor.Exit(_sendLock);
        }
    }

    private void SendSampleCore()
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
            if (_audioMode == AudioMode.JitterBuffer && !_jitterBufferFilled)
            {
                int minFrames = Math.Max(1, _jitterBufferMs / AUDIO_SAMPLE_PERIOD_MS / 2);
                if (_pcmQueue.Count < minFrames)
                {
                    SendSilence();
                    return;
                }
                _jitterBufferFilled = true;
                OnDebugLog?.Invoke($"[AdaAudioSource] 🎯 Jitter buffer primed ({_pcmQueue.Count} frames)");
            }

            if (_pcmQueue.TryDequeue(out var pcm24))
            {
                _consecutiveUnderruns = 0;

                // 1. Volume boost
                ApplyVolumeBoost(pcm24, VOLUME_BOOST);

                // 2. Resample 24kHz → targetRate using simple linear interpolation
                audioFrame = ResampleLinear(pcm24, 24000, targetRate, samplesNeeded);

                // Log first frame to confirm audio is being processed
                if (_sentFrames < 5)
                {
                    short peak = 0;
                    foreach (var s in audioFrame) if (Math.Abs(s) > peak) peak = (short)Math.Abs(s);
                    OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 Frame {_sentFrames}: {audioFrame.Length} samples, peak={peak}");
                }

                // 5. Pre-emphasis for narrowband codecs
                if (ShouldApplyPreEmphasis(_audioFormatManager.SelectedFormat))
                {
                    ApplyPreEmphasis(audioFrame);
                }

                // 6. Limiter + dither
                ApplySoftLimiter(audioFrame);
                ApplyDither(audioFrame);

                // 7. Crossfade
                if (!_lastFrameWasSilence && _lastAudioFrame != null && audioFrame.Length >= CROSSFADE_SAMPLES)
                {
                    ApplyInterFrameCrossfade(audioFrame, _lastAudioFrame);
                }

                if (_lastFrameWasSilence && audioFrame.Length > CROSSFADE_SAMPLES)
                {
                    int fadeLen = Math.Min(CROSSFADE_SAMPLES, audioFrame.Length);
                    for (int i = 0; i < fadeLen; i++)
                    {
                        float t = (float)i / fadeLen;
                        audioFrame[i] = (short)(_lastOutputSample * (1 - t) + audioFrame[i] * t);
                    }
                }

                _lastAudioFrame = (short[])audioFrame.Clone();
            }
            else
            {
                _consecutiveUnderruns++;

                if (_audioMode == AudioMode.JitterBuffer && _jitterBufferFilled && _consecutiveUnderruns > 10)
                {
                    _jitterBufferFilled = false;
                }

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

        if (audioFrame.Length > 0)
        {
            _prevFrameLastSample = audioFrame[^1];
            _lastOutputSample = audioFrame[^1];
            _lastFrameWasSilence = false;
        }

        byte[] encoded = _audioEncoder.EncodeAudio(audioFrame, _audioFormatManager.SelectedFormat);
        uint durationRtpUnits = (uint)samplesNeeded;
        _sentFrames++;

        if (_markEndOfSpeech && _pcmQueue.IsEmpty)
        {
            _markEndOfSpeech = false;
            OnQueueEmpty?.Invoke();
        }

        OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
    }

    /// <summary>
    /// Simple linear interpolation resampling - stateful for smooth frame boundaries.
    /// </summary>
    private short[] ResampleLinear(short[] input, int fromRate, int toRate, int outputLen)
    {
        if (fromRate == toRate)
        {
            // No resampling needed
            var copy = new short[outputLen];
            Array.Copy(input, copy, Math.Min(input.Length, outputLen));
            return copy;
        }

        var output = new short[outputLen];
        double ratio = (double)fromRate / toRate;

        for (int i = 0; i < outputLen; i++)
        {
            double srcPos = _resamplePhase + i * ratio;
            int srcIdx = (int)srcPos;
            double frac = srcPos - srcIdx;

            short s0, s1;
            if (srcIdx < 0)
            {
                s0 = _lastInputSample;
                s1 = input.Length > 0 ? input[0] : (short)0;
            }
            else if (srcIdx >= input.Length - 1)
            {
                s0 = srcIdx < input.Length ? input[srcIdx] : (input.Length > 0 ? input[^1] : (short)0);
                s1 = s0;
            }
            else
            {
                s0 = input[srcIdx];
                s1 = input[srcIdx + 1];
            }

            output[i] = (short)(s0 + (s1 - s0) * frac);
        }

        // Update phase for next frame (wrap around input length)
        _resamplePhase = (_resamplePhase + outputLen * ratio) - input.Length;
        if (input.Length > 0)
            _lastInputSample = input[^1];

        return output;
    }


    {
        return format.FormatName.Equals("PCMU", StringComparison.OrdinalIgnoreCase) ||
               format.FormatName.Equals("PCMA", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyPreEmphasis(short[] samples)
    {
        float prev = _preEmphasisPrevSample;
        for (int i = 0; i < samples.Length; i++)
        {
            float current = samples[i];
            float emphasized = current - PRE_EMPHASIS_COEFF * prev;
            samples[i] = (short)Math.Clamp(emphasized, short.MinValue, short.MaxValue);
            prev = current;
        }
        _preEmphasisPrevSample = prev;
    }

    private static void ApplyVolumeBoost(short[] samples, float boost)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float val = samples[i] * boost;
            samples[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
        }
    }

    private static void ApplyInterFrameCrossfade(short[] currentFrame, short[] previousFrame)
    {
        int fadeLen = Math.Min(CROSSFADE_SAMPLES, Math.Min(currentFrame.Length, previousFrame.Length));
        int prevStart = previousFrame.Length - fadeLen;

        for (int i = 0; i < fadeLen; i++)
        {
            double phase = (double)(i + 1) / (fadeLen + 1);
            float t = (float)((1.0 - Math.Cos(phase * Math.PI)) / 2.0);

            short prevSample = previousFrame[prevStart + i];
            short currSample = currentFrame[i];

            currentFrame[i] = (short)(prevSample * (1.0f - t) + currSample * t);
        }
    }

    private void ApplySoftLimiter(short[] samples)
    {
        if (samples.Length == 0) return;

        float peak = 0;
        foreach (var s in samples)
        {
            float abs = Math.Abs(s);
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

    private static readonly Random _ditherRand = new();

    private static void ApplyDither(short[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            // Triangular probability distribution dither (TPDF)
            float dither = (float)(_ditherRand.NextDouble() + _ditherRand.NextDouble() - 1.0);
            samples[i] = (short)Math.Clamp(samples[i] + dither, short.MinValue, short.MaxValue);
        }
    }

    private static short[] GenerateInterpolatedFrame(short[] lastFrame, int samplesNeeded, int underrunCount)
    {
        var output = new short[samplesNeeded];
        float fadeBase = Math.Max(0, 1.0f - (underrunCount * 0.15f));

        int copyLen = Math.Min(lastFrame.Length, samplesNeeded);
        int startIdx = Math.Max(0, lastFrame.Length - copyLen);

        for (int i = 0; i < copyLen; i++)
        {
            float frameFade = fadeBase * (1.0f - (float)i / copyLen * 0.5f);
            output[i] = (short)(lastFrame[startIdx + i] * frameFade);
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

    private void SendSilence()
    {
        try
        {
            int targetRate = _audioFormatManager.SelectedFormat.ClockRate;
            int samplesNeeded = targetRate / 1000 * AUDIO_SAMPLE_PERIOD_MS;
            var silence = new short[samplesNeeded];

            if (!_lastFrameWasSilence && _lastOutputSample != 0 && samplesNeeded > 0)
            {
                int rampLen = Math.Min(samplesNeeded, Math.Max(1, targetRate / 200));
                for (int i = 0; i < rampLen; i++)
                {
                    float g = 1f - ((float)i / rampLen);
                    silence[i] = (short)(_lastOutputSample * g);
                }
                _needsFadeIn = true;
            }

            byte[] encoded = _audioEncoder.EncodeAudio(silence, _audioFormatManager.SelectedFormat);
            uint durationRtpUnits = (uint)samplesNeeded;
            _silenceFrames++;
            _lastFrameWasSilence = true;
            _lastOutputSample = 0;

            if (!_wasQueueEmpty)
            {
                _wasQueueEmpty = true;
                OnQueueEmpty?.Invoke();
            }

            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch
        {
            // Silent fail for resilience
        }
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
