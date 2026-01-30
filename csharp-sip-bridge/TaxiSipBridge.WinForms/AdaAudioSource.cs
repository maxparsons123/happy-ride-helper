using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// Audio source that receives 24kHz PCM from OpenAI and delivers encoded RTP.
/// Uses NAudio WDL resampler for high-quality anti-aliased downsampling.
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 5000;
    
    // Overflow control - drop frames to prevent lag buildup
    private const int OVERFLOW_THRESHOLD_FRAMES = 25;  // 500ms max queue depth
    private const int OVERFLOW_TARGET_FRAMES = 15;     // Drop to 300ms on overflow
    
    // Outbound DSP configuration - MATCHES EDGE FUNCTION
    private const int FADE_IN_SAMPLES = 80;      // Same as edge function
    private const int CROSSFADE_SAMPLES = 40;    // Same as edge function
    private const float LIMITER_THRESHOLD = 28000f; // Same as edge function
    private const float LIMITER_CEILING = 32000f;   // Same as edge function

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;
    private readonly ConcurrentQueue<short[]> _pcmQueue = new();
    private readonly object _sendLock = new();

    // High-quality resamplers (FFmpeg fallback, polyphase FIR primary)
    private FfmpegStreamingResampler? _ffmpegResampler8k;
    private PolyphaseFirResampler? _resampler8k;
    private PolyphaseFirResampler? _resampler16k;
    private PolyphaseFirResampler? _resampler48k;
    private bool _ffmpegAvailable = true;  // Try FFmpeg as optional enhancement
    
    // Streaming preprocessor for 8kHz telephony (DC removal + lowpass + normalize + downsample)
    private readonly TtsPreConditioner _preConditioner8k = new();
    
    // Fallback state for rates without dedicated resampler
    private double _resamplePhase;
    private short _lastInputSample;
    
    // Gentle high-frequency softening filter (single-pole IIR at ~6.5kHz)
    // Makes audio "rounder" without causing phase artifacts
    private float _softenerState = 0f;
    private const float SOFTENER_ALPHA = 0.65f;  // ~6.5kHz cutoff at 24kHz sample rate

    
    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private bool _needsFadeIn = true;
    private bool _isFirstPacket = true;
    private volatile bool _disposed;
    private bool _enableFadeIn = false;      // DISABLED: fade-in causes artifacts
    
    // DSP control flags - ALL BYPASSED for pure passthrough
    private bool _bypassDsp = true;          // Force DSP bypass
    private float _limiterGain = 1.0f;       // Soft limiter gain state (unused)

    // State tracking
    private short _lastOutputSample;
    private short[]? _lastAudioFrame;
    private bool _lastFrameWasSilence = true;

    // Jitter buffer - 10 frames for lower latency (accepts higher underrun risk)
    private AudioMode _audioMode = AudioMode.Standard;
    private int _jitterBufferMs = 200;  // 200ms (10 frames) - lower latency
    private bool _jitterBufferFilled;
    private int _consecutiveUnderruns;
    private bool _markEndOfSpeech;

    // High-precision audio thread (replaces System.Threading.Timer)
    private Thread? _audioThread;
    private CancellationTokenSource? _audioCts;
    private readonly Stopwatch _audioStopwatch = new();

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
        
        // Reset FFmpeg resampler
        _ffmpegResampler8k?.Reset();
        
        // Reset polyphase FIR resamplers
        _resampler8k?.Reset();
        _resampler16k?.Reset();
        _resampler48k?.Reset();
        
        // Reset streaming preprocessor for 8kHz
        _preConditioner8k.Reset();
        
        // Reset fallback resampler state
        _resamplePhase = 0;
        _lastInputSample = 0;
        
        // Reset DSP/audio state
        _needsFadeIn = true;
        _isFirstPacket = true;
        _limiterGain = 1.0f;
        _lastOutputSample = 0;
        _lastAudioFrame = null;
        _lastFrameWasSilence = true;
        _softenerState = 0f;  // Reset high-frequency softener
        
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
            _audioCts = new CancellationTokenSource();
            _audioThread = new Thread(AudioThreadLoop)
            {
                Name = "AdaAudioSource",
                Priority = ThreadPriority.Highest,
                IsBackground = true
            };
            _audioThread.Start();
            OnDebugLog?.Invoke($"[AdaAudioSource] ▶️ Started (high-precision {AUDIO_SAMPLE_PERIOD_MS}ms), format={_audioFormatManager.SelectedFormat.FormatName}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// High-precision audio thread using Stopwatch for accurate 20ms intervals.
    /// Uses double accumulation to prevent integer drift over long calls.
    /// </summary>
    private void AudioThreadLoop()
    {
        var token = _audioCts?.Token ?? CancellationToken.None;
        var sw = Stopwatch.StartNew();
        double next = 0.0;
        const double frame = AUDIO_SAMPLE_PERIOD_MS;  // 20ms
        
        while (!token.IsCancellationRequested && !_isClosed && !_disposed)
        {
            try
            {
                double now = sw.ElapsedMilliseconds;
                
                if (now >= next)
                {
                    if (!_isPaused)
                        SendSampleCore();
                    
                    next += frame;
                }
                else
                {
                    double sleep = next - now;
                    if (sleep > 1)
                        Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                OnAudioSourceError?.Invoke($"Audio thread error: {ex.Message}");
            }
        }
        
        sw.Stop();
    }

    public Task PauseAudio() { _isPaused = true; return Task.CompletedTask; }
    public Task ResumeAudio() { _isPaused = false; return Task.CompletedTask; }

    public Task CloseAudio()
    {
        if (!_isClosed)
        {
            _isClosed = true;
            
            // Stop the high-precision audio thread
            _audioCts?.Cancel();
            _audioThread?.Join(100);  // Wait up to 100ms for clean shutdown
            _audioCts?.Dispose();
            _audioCts = null;
            _audioThread = null;
            
            // Drain remaining audio before closing (up to 500ms worth)
            int remaining = _pcmQueue.Count;
            int drained = 0;
            while (_pcmQueue.TryDequeue(out _) && drained < 25) // 25 frames = 500ms max
            {
                drained++;
            }
            
            ClearQueue();
            OnDebugLog?.Invoke($"[AdaAudioSource] ⏹️ Closed (enq={_enqueuedFrames}, sent={_sentFrames}, drained={drained}/{remaining})");
        }
        return Task.CompletedTask;
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
            // Jitter buffer priming - ensures smooth audio delivery
            if (!_jitterBufferFilled)
            {
                int minFrames = 10;  // 200ms - lower latency, accepts underrun risk
                if (_audioMode == AudioMode.JitterBuffer)
                    minFrames = Math.Max(minFrames, _jitterBufferMs / AUDIO_SAMPLE_PERIOD_MS);
                
                if (_pcmQueue.Count < minFrames)
                {
                    SendSilence();
                    return;
                }
                _jitterBufferFilled = true;
                OnDebugLog?.Invoke($"[AdaAudioSource] 🎯 Jitter buffer primed: {_pcmQueue.Count} frames ({minFrames * AUDIO_SAMPLE_PERIOD_MS}ms) @ {targetRate}Hz");
            }
            
            // Overflow control - drop oldest frames to prevent lag buildup
            if (_pcmQueue.Count > OVERFLOW_THRESHOLD_FRAMES)
            {
                int dropped = 0;
                while (_pcmQueue.Count > OVERFLOW_TARGET_FRAMES && _pcmQueue.TryDequeue(out _))
                    dropped++;
                
                if (dropped > 0)
                    OnDebugLog?.Invoke($"[AdaAudioSource] ⚡ Overflow: dropped {dropped} frames ({dropped * AUDIO_SAMPLE_PERIOD_MS}ms) to prevent lag");
            }

            if (_pcmQueue.TryDequeue(out var pcm24))
            {
                _consecutiveUnderruns = 0;
                
                // TESTING: Softener disabled - pure passthrough to resampler
                // ApplySoftener(pcm24);
                
                // Polyphase FIR resampling for high-quality telephony audio
                audioFrame = ResamplePolyphase(pcm24, 24000, targetRate, samplesNeeded);
                
                _lastAudioFrame = (short[])audioFrame.Clone();
            }
            else
            {
                _consecutiveUnderruns++;

                // Re-prime after 3 consecutive underruns (more tolerant)
                if (_jitterBufferFilled && _consecutiveUnderruns >= 3)
                {
                    _jitterBufferFilled = false;
                    // Keep 10 frames in buffer for stable SIP timing
                    while (_pcmQueue.Count > 10) _pcmQueue.TryDequeue(out _);
                    OnDebugLog?.Invoke($"[AdaAudioSource] ⚠️ Underrun ({_consecutiveUnderruns}x), re-priming");
                }

                // On underrun: send silence immediately
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

        // Audio is already preprocessed (8kHz via PreConditioner, others via PreprocessPcm16)

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
    /// High-quality resampling using polyphase FIR (primary) or FFmpeg (optional enhancement).
    /// Pure C# implementation with no external DLL dependencies.
    /// </summary>
    private short[] ResamplePolyphase(short[] input, int fromRate, int toRate, int outputLen)
    {
        if (fromRate == toRate)
        {
            var copy = new short[outputLen];
            Array.Copy(input, copy, Math.Min(input.Length, outputLen));
            return copy;
        }

        // Try FFmpeg for 8kHz downsampling (optional enhancement)
        if (_ffmpegAvailable && toRate == 8000)
        {
            try
            {
                _ffmpegResampler8k ??= new FfmpegStreamingResampler(fromRate, toRate);
                if (!_ffmpegResampler8k.IsRunning)
                {
                    _ffmpegResampler8k.OnDebugLog += msg => OnDebugLog?.Invoke(msg);
                    _ffmpegResampler8k.OnError += err => OnDebugLog?.Invoke($"[FFmpeg] {err}");
                    if (!_ffmpegResampler8k.Start())
                    {
                        _ffmpegAvailable = false;
                        OnDebugLog?.Invoke("[AdaAudioSource] ⚠️ FFmpeg not available, using polyphase FIR");
                    }
                }
                
                if (_ffmpegResampler8k.IsRunning)
                {
                    var resampled = _ffmpegResampler8k.Resample(input);
                    
                    if (_sentFrames == 0)
                    {
                        OnDebugLog?.Invoke($"[AdaAudioSource] 🔧 Resample (FFmpeg soxr): {input.Length} samples @ {fromRate}Hz → {outputLen} samples @ {toRate}Hz");
                    }
                    
                    if (resampled.Length == outputLen) return resampled;
                    var result = new short[outputLen];
                    Array.Copy(resampled, result, Math.Min(resampled.Length, outputLen));
                    return result;
                }
            }
            catch (Exception ex)
            {
                _ffmpegAvailable = false;
                OnDebugLog?.Invoke($"[AdaAudioSource] ⚠️ FFmpeg error: {ex.Message}, using polyphase FIR");
            }
        }

        // Primary: polyphase FIR resampler (pure C#, always works)
        PolyphaseFirResampler resampler = GetOrCreateResampler(fromRate, toRate);
        
        if (_sentFrames == 0)
        {
            OnDebugLog?.Invoke($"[AdaAudioSource] 🔧 Resample (Polyphase FIR): {input.Length} samples @ {fromRate}Hz → {outputLen} samples @ {toRate}Hz");
        }

        return resampler.Resample(input, outputLen);
    }
    
    /// <summary>
    /// Get or create a polyphase FIR resampler for the given rate conversion.
    /// </summary>
    private PolyphaseFirResampler GetOrCreateResampler(int fromRate, int toRate)
    {
        // Use cached resamplers for common conversions
        if (fromRate == 24000 && toRate == 8000)
        {
            _resampler8k ??= new PolyphaseFirResampler(24000, 8000);
            return _resampler8k;
        }
        
        if (fromRate == 24000 && toRate == 16000)
        {
            _resampler16k ??= new PolyphaseFirResampler(24000, 16000);
            return _resampler16k;
        }
        
        if (fromRate == 24000 && toRate == 48000)
        {
            _resampler48k ??= new PolyphaseFirResampler(24000, 48000);
            return _resampler48k;
        }
        
        // Fallback: create new resampler (less efficient but handles any rate)
        return new PolyphaseFirResampler(fromRate, toRate);
    }
    
    /// <summary>
    /// Gentle high-frequency softening filter.
    /// Single-pole IIR at ~6.5kHz - makes audio "rounder" without phase artifacts.
    /// </summary>
    private void ApplySoftener(short[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float input = samples[i];
            _softenerState = SOFTENER_ALPHA * input + (1f - SOFTENER_ALPHA) * _softenerState;
            samples[i] = (short)Math.Clamp(_softenerState, short.MinValue, short.MaxValue);
        }
    }

    // Legacy methods removed - now using PolyphaseFirResampler
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
        
        // Stop the high-precision audio thread
        _audioCts?.Cancel();
        _audioThread?.Join(100);
        _audioCts?.Dispose();
        
        // Stop FFmpeg resampler
        _ffmpegResampler8k?.Dispose();
        
        ClearQueue();
        GC.SuppressFinalize(this);
    }
}
