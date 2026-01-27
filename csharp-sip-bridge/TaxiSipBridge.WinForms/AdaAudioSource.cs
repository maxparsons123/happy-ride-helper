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
/// Audio source that receives 24kHz PCM from OpenAI and delivers encoded RTP.
/// Supports Opus (48kHz), G.722 (16kHz), and G.711 (8kHz) via UnifiedAudioEncoder.
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 5000;
    private const int FADE_IN_SAMPLES = 160; // ~3.3ms at 48kHz for smoother onset

    // Outbound DSP configuration
    private const float NARROWBAND_VOLUME_BOOST = 1.4f;  // Boost for G.711 (8kHz)
    private const float SOFT_LIMITER_THRESHOLD = 28000f; // Start limiting here
    private const float SOFT_LIMITER_CEILING = 32000f;   // Hard ceiling

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;
    private readonly ConcurrentQueue<short[]> _pcmQueue = new();
    private readonly object _sendLock = new();

    // Resampler state (stateful linear interpolation)
    private double _resamplePhase;
    private short _lastInputSample;

    private Timer? _sendTimer;
    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private bool _needsFadeIn = true;
    private volatile bool _disposed;
    
    // DSP control flags
    private bool _bypassDsp = false;        // Full DSP enabled
    private bool _enableFadeIn = true;      // Keep fade-in to prevent pops
    private bool _enableSoftLimiter = true; // Prevent clipping
    private bool _enableNarrowbandBoost = true; // Volume boost for G.711

    // State tracking
    private short _lastOutputSample;
    private short[]? _lastAudioFrame;
    private bool _lastFrameWasSilence = true;

    // Jitter buffer
    private AudioMode _audioMode = AudioMode.Standard;
    private int _jitterBufferMs = 80;
    private bool _jitterBufferFilled;
    private int _consecutiveUnderruns;
    private bool _markEndOfSpeech;

    // Test tone
    private bool _testToneMode;
    private int _testToneSampleIndex;
    private const double TEST_TONE_FREQ = 440.0;

    // Stats
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
        _audioEncoder = new UnifiedAudioEncoder();
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

        // Reset resampler state
        _resamplePhase = 0;
        _lastInputSample = 0;

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
    /// Enqueue 24kHz PCM bytes from OpenAI. Will be chunked into 20ms frames.
    /// </summary>
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

            // Fade-in to prevent pops (always enabled unless explicitly disabled)
            if (_enableFadeIn && _needsFadeIn && frame.Length > 0)
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

    /// <summary>
    /// Reset all state for a new call. Call this when Ada ends a call.
    /// </summary>
    public void Reset()
    {
        ClearQueue();
        
        // Reset resampler state
        _resamplePhase = 0;
        _lastInputSample = 0;
        
        // Reset DSP/audio state
        _needsFadeIn = true;
        _lastOutputSample = 0;
        _lastAudioFrame = null;
        _lastFrameWasSilence = true;
        
        // Reset jitter buffer
        _jitterBufferFilled = false;
        _consecutiveUnderruns = 0;
        _markEndOfSpeech = false;
        
        // Reset stats
        _enqueuedFrames = 0;
        _sentFrames = 0;
        _silenceFrames = 0;
        _interpolatedFrames = 0;
        _lastStatsLog = DateTime.MinValue;
        _wasQueueEmpty = true;
        
        // Reset test tone
        _testToneSampleIndex = 0;
        
        OnDebugLog?.Invoke("[AdaAudioSource] 🔄 Reset for new call");
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
        if (!Monitor.TryEnter(_sendLock)) return;
        try { SendSampleCore(); }
        finally { Monitor.Exit(_sendLock); }
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
            // Jitter buffer priming for wideband codecs (Opus 48kHz) - always enabled
            if (!_jitterBufferFilled && targetRate >= 48000)
            {
                // Buffer 100ms (5 frames) before starting playback for Opus
                int minFrames = 5;
                if (_pcmQueue.Count < minFrames)
                {
                    SendSilence();
                    return;
                }
                _jitterBufferFilled = true;
                OnDebugLog?.Invoke($"[AdaAudioSource] 🎯 Opus jitter buffer primed ({_pcmQueue.Count} frames, 100ms)");
            }

            if (_pcmQueue.TryDequeue(out var pcm24))
            {
                _consecutiveUnderruns = 0;
                audioFrame = ResampleLinear(pcm24, 24000, targetRate, samplesNeeded);
                
                // Crossfade from silence to audio to prevent spluttering
                if (_lastFrameWasSilence && audioFrame.Length > 0)
                {
                    int crossfadeLen = Math.Min(40, audioFrame.Length); // ~0.8ms at 48kHz
                    for (int i = 0; i < crossfadeLen; i++)
                    {
                        float t = (float)i / crossfadeLen;
                        audioFrame[i] = (short)(audioFrame[i] * t);
                    }
                }
                
                _lastAudioFrame = (short[])audioFrame.Clone();
            }
            else
            {
                _consecutiveUnderruns++;

                if (_audioMode == AudioMode.JitterBuffer && _jitterBufferFilled && _consecutiveUnderruns > 10)
                    _jitterBufferFilled = false;

                // On underrun: send silence immediately to prevent artifacts
                // Interpolation was causing spluttering - simpler is better
                SendSilence();
                return;
            }
        }

        // Log first few frames
        if (_sentFrames < 5)
        {
            int peak = 0;
            foreach (short s in audioFrame)
            {
                int abs = s == short.MinValue ? 32768 : Math.Abs((int)s);
                if (abs > peak) peak = abs;
            }
            OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 Frame {_sentFrames}: {audioFrame.Length} samples, peak={peak}");
        }

        // Apply outbound DSP pipeline
        if (!_bypassDsp)
        {
            audioFrame = ApplyOutboundDsp(audioFrame, targetRate);
        }

        if (audioFrame.Length > 0)
        {
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
    /// Stateful linear interpolation resampler (24kHz → target rate).
    /// Uses exact 2x upsample for 24kHz→48kHz to avoid boundary glitches.
    /// </summary>
    private short[] ResampleLinear(short[] input, int fromRate, int toRate, int outputLen)
    {
        if (fromRate == toRate)
        {
            var copy = new short[outputLen];
            Array.Copy(input, copy, Math.Min(input.Length, outputLen));
            return copy;
        }

        var output = new short[outputLen];

        // Special case: exact 2x upsample (24kHz → 48kHz) - no phase drift
        if (toRate == fromRate * 2)
        {
            for (int i = 0; i < outputLen; i++)
            {
                int srcIdx = i / 2;
                bool isInterpolated = (i % 2) == 1;

                if (srcIdx >= input.Length)
                {
                    // Past end - hold last sample
                    output[i] = input.Length > 0 ? input[^1] : (short)0;
                }
                else if (isInterpolated)
                {
                    // Interpolate between current and next (or hold if at end)
                    short s0 = input[srcIdx];
                    short s1 = (srcIdx + 1 < input.Length) ? input[srcIdx + 1] : s0;
                    output[i] = (short)((s0 + s1) / 2);
                }
                else
                {
                    // Direct copy
                    output[i] = input[srcIdx];
                }
            }
            
            if (input.Length > 0)
                _lastInputSample = input[^1];
            
            return output;
        }

        // General case: stateful linear interpolation
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

        _resamplePhase = (_resamplePhase + outputLen * ratio) - input.Length;
        if (input.Length > 0)
            _lastInputSample = input[^1];

        return output;
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
            samples[i] = (short)(Math.Sin(angularFreq * _testToneSampleIndex) * 16000);
            _testToneSampleIndex++;
        }

        return samples;
    }

    /// <summary>
    /// Outbound DSP pipeline: volume boost (narrowband) → soft limiter
    /// </summary>
    private short[] ApplyOutboundDsp(short[] samples, int sampleRate)
    {
        var output = new short[samples.Length];
        bool isNarrowband = sampleRate <= 8000;

        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];

            // Volume boost for narrowband codecs (G.711)
            if (_enableNarrowbandBoost && isNarrowband)
            {
                sample *= NARROWBAND_VOLUME_BOOST;
            }

            // Soft limiter to prevent clipping
            if (_enableSoftLimiter)
            {
                sample = ApplySoftLimiter(sample);
            }

            output[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        }

        return output;
    }

    /// <summary>
    /// Soft limiter using tanh compression for smooth clipping prevention
    /// </summary>
    private static float ApplySoftLimiter(float sample)
    {
        float absSample = Math.Abs(sample);
        
        if (absSample <= SOFT_LIMITER_THRESHOLD)
        {
            return sample;
        }

        // Soft-knee compression using tanh
        float sign = sample >= 0 ? 1f : -1f;
        float excess = absSample - SOFT_LIMITER_THRESHOLD;
        float headroom = SOFT_LIMITER_CEILING - SOFT_LIMITER_THRESHOLD;
        
        // Compress excess using tanh curve
        float compressed = (float)(headroom * Math.Tanh(excess / headroom));
        
        return sign * (SOFT_LIMITER_THRESHOLD + compressed);
    }

    private void SendSilence()
    {
        try
        {
            int targetRate = _audioFormatManager.SelectedFormat.ClockRate;
            int samplesNeeded = targetRate / 1000 * AUDIO_SAMPLE_PERIOD_MS;
            var silence = new short[samplesNeeded];

            // Ramp down from last sample to avoid click (skip if bypass mode)
            if (!_bypassDsp && !_lastFrameWasSilence && _lastOutputSample != 0 && samplesNeeded > 0)
            {
                int rampLen = Math.Min(samplesNeeded, Math.Max(1, targetRate / 200));
                for (int i = 0; i < rampLen; i++)
                    silence[i] = (short)(_lastOutputSample * (1f - (float)i / rampLen));
            }
            
            // Always request fade-in after silence to prevent spikes when audio resumes
            if (!_lastFrameWasSilence)
                _needsFadeIn = true;

            byte[] encoded = _audioEncoder.EncodeAudio(silence, _audioFormatManager.SelectedFormat);
            _silenceFrames++;
            _lastFrameWasSilence = true;
            _lastOutputSample = 0;

            if (!_wasQueueEmpty)
            {
                _wasQueueEmpty = true;
                OnQueueEmpty?.Invoke();
            }

            OnAudioSourceEncodedSample?.Invoke((uint)samplesNeeded, encoded);
        }
        catch { /* Silent fail for resilience */ }
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
