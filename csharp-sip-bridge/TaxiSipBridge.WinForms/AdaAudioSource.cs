using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;
using Timer = System.Threading.Timer;

namespace TaxiSipBridge;

/// <summary>
/// Audio source that receives 24kHz PCM from OpenAI and delivers encoded RTP.
/// Uses NAudio WDL resampler for high-quality anti-aliased downsampling.
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 5000;
    
    // Outbound DSP configuration - MATCHES EDGE FUNCTION
    private const int FADE_IN_SAMPLES = 80;      // Same as edge function
    private const int CROSSFADE_SAMPLES = 40;    // Same as edge function
    private const float LIMITER_THRESHOLD = 28000f; // Same as edge function
    private const float LIMITER_CEILING = 32000f;   // Same as edge function

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;
    private readonly ConcurrentQueue<short[]> _pcmQueue = new();
    private readonly object _sendLock = new();

    // Resampler state (stateful for smooth frame-to-frame transitions)
    private double _resamplePhase;
    private short _lastInputSample;
    
    // Anti-aliasing filter state (IIR low-pass for 24kHz→8kHz)
    private float _lpfState1;
    private float _lpfState2;

    private Timer? _sendTimer;
    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private bool _needsFadeIn = true;
    private bool _isFirstPacket = true;
    private volatile bool _disposed;
    
    // DSP control flags
    private bool _bypassDsp = false;        // Use edge function DSP
    private float _limiterGain = 1.0f;      // Soft limiter gain state

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
        _lpfState1 = 0;
        _lpfState2 = 0;
        
        // Reset DSP/audio state
        _needsFadeIn = true;
        _isFirstPacket = true;
        _limiterGain = 1.0f;
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
            // Jitter buffer priming for ALL codecs to prevent early underruns
            // OpenAI sends audio in bursts - need buffer before starting playback
            if (!_jitterBufferFilled)
            {
                // Buffer frames before starting: 100ms for Opus, 60ms for narrowband
                int minFrames = targetRate >= 48000 ? 5 : 3; // 5 frames (100ms) for 48kHz, 3 frames (60ms) for 8kHz
                if (_pcmQueue.Count < minFrames)
                {
                    SendSilence();
                    return;
                }
                _jitterBufferFilled = true;
                OnDebugLog?.Invoke($"[AdaAudioSource] 🎯 Jitter buffer primed: {_pcmQueue.Count} frames @ {targetRate}Hz");
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
            OnDebugLog?.Invoke($"[AdaAudioSource] 🔊 Frame {_sentFrames}: {audioFrame.Length} samples @ {targetRate}Hz, peak={peak}");
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
        
        // Log first encoded frame to verify size
        if (_sentFrames == 0)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] 📦 First encoded: {audioFrame.Length} samples → {encoded.Length} bytes {_audioFormatManager.SelectedFormat.FormatName}");
        }
        
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
    /// Stateful resampler with IIR anti-aliasing filter for 24kHz→8kHz.
    /// Maintains state across frames to prevent warbling/discontinuities.
    /// </summary>
    private short[] ResampleLinear(short[] input, int fromRate, int toRate, int outputLen)
    {
        // Log first resample
        if (_sentFrames == 0)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] 🔧 Resample (Stateful IIR): {input.Length} samples @ {fromRate}Hz → {outputLen} samples @ {toRate}Hz");
        }
        
        if (fromRate == toRate)
        {
            var copy = new short[outputLen];
            Array.Copy(input, copy, Math.Min(input.Length, outputLen));
            return copy;
        }

        // For downsampling, apply anti-aliasing low-pass filter first
        short[] filtered = input;
        if (toRate < fromRate)
        {
            filtered = ApplyStatefulLowPass(input, fromRate, toRate);
        }

        var output = new short[outputLen];

        // Special case: exact 2x upsample (24kHz → 48kHz)
        if (toRate == fromRate * 2)
        {
            for (int i = 0; i < outputLen; i++)
            {
                int srcIdx = i / 2;
                bool isInterpolated = (i % 2) == 1;

                if (srcIdx >= filtered.Length)
                {
                    output[i] = filtered.Length > 0 ? filtered[^1] : (short)0;
                }
                else if (isInterpolated)
                {
                    short s0 = filtered[srcIdx];
                    short s1 = (srcIdx + 1 < filtered.Length) ? filtered[srcIdx + 1] : s0;
                    output[i] = (short)((s0 + s1) / 2);
                }
                else
                {
                    output[i] = filtered[srcIdx];
                }
            }
            
            if (filtered.Length > 0)
                _lastInputSample = filtered[^1];
            
            return output;
        }

        // Stateful linear interpolation for other ratios
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
                s1 = filtered.Length > 0 ? filtered[0] : (short)0;
            }
            else if (srcIdx >= filtered.Length - 1)
            {
                s0 = srcIdx < filtered.Length ? filtered[srcIdx] : (filtered.Length > 0 ? filtered[^1] : (short)0);
                s1 = s0;
            }
            else
            {
                s0 = filtered[srcIdx];
                s1 = filtered[srcIdx + 1];
            }

            output[i] = (short)(s0 + (s1 - s0) * frac);
        }

        // Update phase for next frame (maintain continuity)
        _resamplePhase = (_resamplePhase + outputLen * ratio) - filtered.Length;
        while (_resamplePhase < 0) _resamplePhase += 1.0;
        while (_resamplePhase >= 1.0) _resamplePhase -= 1.0;
        
        if (filtered.Length > 0)
            _lastInputSample = filtered[^1];

        return output;
    }

    /// <summary>
    /// Stateful 2nd-order IIR Butterworth low-pass filter.
    /// Cutoff at 3.5kHz for 24kHz input (below 4kHz Nyquist for 8kHz output).
    /// State maintained across frames to prevent discontinuities.
    /// </summary>
    private short[] ApplyStatefulLowPass(short[] input, int inputRate, int outputRate)
    {
        if (input.Length == 0) return input;
        
        // Butterworth coefficients for ~3.5kHz cutoff at 24kHz sample rate
        // This removes frequencies that would alias when decimating to 8kHz
        // Coefficients: fc = 3500Hz, fs = 24000Hz, Q = 0.707
        const float b0 = 0.0675f;
        const float b1 = 0.1349f;
        const float b2 = 0.0675f;
        const float a1 = -1.1430f;
        const float a2 = 0.4128f;
        
        var output = new short[input.Length];
        
        for (int i = 0; i < input.Length; i++)
        {
            float x = input[i];
            
            // Direct Form II Transposed
            float y = b0 * x + _lpfState1;
            _lpfState1 = b1 * x - a1 * y + _lpfState2;
            _lpfState2 = b2 * x - a2 * y;
            
            output[i] = (short)Math.Clamp(y, short.MinValue, short.MaxValue);
        }
        
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
    /// Outbound DSP pipeline - MATCHES EDGE FUNCTION
    /// Fade-in → Soft limiter → Crossfade
    /// </summary>
    private short[] ApplyOutboundDsp(short[] samples, int sampleRate)
    {
        if (samples.Length == 0) return samples;
        
        // 1. Apply fade-in on first packet
        if (_needsFadeIn)
        {
            int fadeLen = Math.Min(FADE_IN_SAMPLES, samples.Length);
            for (int i = 0; i < fadeLen; i++)
            {
                float gain = (float)i / fadeLen;
                samples[i] = (short)(samples[i] * gain);
            }
            _needsFadeIn = false;
        }
        
        // 2. Apply soft limiter (matches edge function)
        ApplySoftLimiter(samples);
        
        // 3. Apply crossfade from previous sample
        if (!_isFirstPacket && samples.Length >= CROSSFADE_SAMPLES)
        {
            for (int i = 0; i < CROSSFADE_SAMPLES; i++)
            {
                float t = (float)i / CROSSFADE_SAMPLES;
                samples[i] = (short)(_lastOutputSample * (1f - t) + samples[i] * t);
            }
        }
        _isFirstPacket = false;
        
        return samples;
    }

    /// <summary>
    /// Soft limiter - MATCHES EDGE FUNCTION EXACTLY
    /// Stateful gain with slow recovery and tanh compression
    /// </summary>
    private void ApplySoftLimiter(short[] samples)
    {
        if (samples.Length == 0) return;

        // Find peak
        int peak = 0;
        foreach (short s in samples)
        {
            int abs = s == short.MinValue ? 32768 : Math.Abs(s);
            if (abs > peak) peak = abs;
        }

        // Below threshold - slowly recover gain
        if (peak < LIMITER_THRESHOLD)
        {
            _limiterGain = Math.Min(1.0f, _limiterGain + 0.01f);
            return;
        }

        // Calculate target gain
        float targetGain = LIMITER_CEILING / peak;
        float alpha = peak > LIMITER_CEILING ? 0.3f : 0.05f;
        _limiterGain = _limiterGain * (1f - alpha) + targetGain * alpha;

        // Apply gain and soft-knee compression
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i] * _limiterGain;

            if (Math.Abs(sample) > LIMITER_THRESHOLD)
            {
                float sign = sample >= 0 ? 1f : -1f;
                float abs = Math.Abs(sample);
                float over = (abs - LIMITER_THRESHOLD) / (LIMITER_CEILING - LIMITER_THRESHOLD);
                float compressed = LIMITER_THRESHOLD + (LIMITER_CEILING - LIMITER_THRESHOLD) * (float)Math.Tanh(over);
                sample = sign * compressed;
            }

            samples[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        }
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
