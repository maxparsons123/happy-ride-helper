using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge.Audio;

/// <summary>
/// µ-law audio source for SIPSorcery RTP transport.
/// Receives pre-encoded µ-law frames and paces them at 20ms intervals.
/// </summary>
public class MuLawAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int FRAME_SIZE_BYTES = 160; // 8kHz * 20ms = 160 samples
    private const int MAX_QUEUED_FRAMES = 5000; // ~100 seconds buffer
    private const int CROSSFADE_SAMPLES = 8; // Smooth transitions

    private static readonly byte[] MuLawSilence;
    private static readonly AudioFormat PcmuFormat;

    private readonly ConcurrentQueue<byte[]> _ulawQueue = new();
    private readonly object _sendLock = new();

    private Timer? _timer;
    private volatile bool _disposed;
    private volatile bool _isStarted;

    // Boundary smoothing to prevent clicks
    private byte _lastOutputSample = 0xFF; // Start with silence
    private bool _lastFrameWasSilence = true;

    // Debug counters
    private int _enqueuedFrames;
    private int _sentFrames;
    private int _silenceFrames;
    private DateTime _lastStatsLog = DateTime.MinValue;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError;
    event Action<EncodedAudioFrame>? IAudioSource.OnAudioSourceEncodedFrameReady { add { } remove { } }

    public event Action<string>? OnDebugLog;

    static MuLawAudioSource()
    {
        // Pre-allocate silence buffer (0xFF = µ-law silence / near-zero PCM)
        MuLawSilence = new byte[FRAME_SIZE_BYTES];
        Array.Fill(MuLawSilence, (byte)0xFF);

        // Standard PCMU format
        PcmuFormat = new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU);
    }

    public List<AudioFormat> GetAudioSourceFormats()
    {
        return new List<AudioFormat> { PcmuFormat };
    }

    public void SetAudioSourceFormat(AudioFormat format)
    {
        OnDebugLog?.Invoke($"[MuLawAudioSource] 🎵 Format set: {format.FormatName}");
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter) { }

    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;

    public bool IsAudioSourcePaused() => false;

    /// <summary>
    /// Queue a pre-encoded µ-law frame (must be exactly 160 bytes).
    /// </summary>
    public void EnqueueMuLaw(byte[] frame)
    {
        if (_disposed || frame == null || frame.Length != FRAME_SIZE_BYTES) return;

        // Bound the queue to prevent memory issues
        while (_ulawQueue.Count >= MAX_QUEUED_FRAMES)
            _ulawQueue.TryDequeue(out _);

        _ulawQueue.Enqueue(frame);
        _enqueuedFrames++;

        // Log first enqueue
        if (_enqueuedFrames == 1)
            OnDebugLog?.Invoke($"[MuLawAudioSource] 📥 First frame enqueued");
    }

    /// <summary>
    /// Clear all queued frames.
    /// </summary>
    public void ClearQueue()
    {
        while (_ulawQueue.TryDequeue(out _)) { }
        _lastFrameWasSilence = true;
        _lastOutputSample = 0xFF;
    }

    public Task StartAudio()
    {
        if (!_isStarted && !_disposed)
        {
            _isStarted = true;
            _timer = new Timer(SendFrame, null, 0, AUDIO_SAMPLE_PERIOD_MS);
            OnDebugLog?.Invoke($"[MuLawAudioSource] ▶️ Timer started ({AUDIO_SAMPLE_PERIOD_MS}ms)");
        }
        return Task.CompletedTask;
    }

    private void SendFrame(object? state)
    {
        if (_disposed) return;

        // Prevent timer callback overlap
        if (!Monitor.TryEnter(_sendLock)) return;

        try
        {
            byte[] frame;
            bool isSilence;

            if (_ulawQueue.TryDequeue(out var queuedFrame))
            {
                frame = queuedFrame;
                isSilence = false;
                _sentFrames++;

                // Crossfade from silence to audio to prevent clicks
                if (_lastFrameWasSilence && frame.Length >= CROSSFADE_SAMPLES)
                {
                    for (int i = 0; i < CROSSFADE_SAMPLES; i++)
                    {
                        // Blend from silence (0xFF) to actual audio
                        float t = (float)i / CROSSFADE_SAMPLES;
                        int blended = (int)(0xFF * (1 - t) + frame[i] * t);
                        frame[i] = (byte)Math.Clamp(blended, 0, 255);
                    }
                }
            }
            else
            {
                // Send silence when queue is empty (maintains RTP timing)
                frame = MuLawSilence;
                isSilence = true;
                _silenceFrames++;

                // Crossfade from audio to silence
                if (!_lastFrameWasSilence)
                {
                    // Create a transitional frame that fades from last sample to silence
                    var fadeFrame = new byte[FRAME_SIZE_BYTES];
                    for (int i = 0; i < FRAME_SIZE_BYTES; i++)
                    {
                        float t = (float)i / FRAME_SIZE_BYTES;
                        int blended = (int)(_lastOutputSample * (1 - t) + 0xFF * t);
                        fadeFrame[i] = (byte)Math.Clamp(blended, 0, 255);
                    }
                    frame = fadeFrame;
                }
            }

            // Track last sample for next crossfade
            if (frame.Length > 0)
                _lastOutputSample = frame[^1];
            _lastFrameWasSilence = isSilence;

            // Send to RTP transport (duration = 160 samples at 8kHz = 20ms)
            OnAudioSourceEncodedSample?.Invoke(FRAME_SIZE_BYTES, frame);

            // Log stats every 3 seconds
            if ((DateTime.Now - _lastStatsLog).TotalSeconds >= 3)
            {
                OnDebugLog?.Invoke($"[MuLawAudioSource] 📊 enq={_enqueuedFrames}, sent={_sentFrames}, sil={_silenceFrames}, q={_ulawQueue.Count}");
                _lastStatsLog = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            OnAudioSourceError?.Invoke($"MuLawAudioSource error: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_sendLock);
        }
    }

    public Task PauseAudio() => Task.CompletedTask;
    public Task ResumeAudio() => Task.CompletedTask;

    public Task CloseAudio()
    {
        _timer?.Dispose();
        _timer = null;
        ClearQueue();
        OnDebugLog?.Invoke($"[MuLawAudioSource] ⏹️ Closed (enq={_enqueuedFrames}, sent={_sentFrames}, sil={_silenceFrames})");
        return Task.CompletedTask;
    }

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        // Not used - we receive pre-encoded µ-law
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();
        ClearQueue();

        GC.SuppressFinalize(this);
    }
}

    public Task PauseAudio() => Task.CompletedTask;

    public Task ResumeAudio() => Task.CompletedTask;

    public Task CloseAudio()
    {
        _timer?.Dispose();
        _timer = null;
        OnDebugLog?.Invoke($"[MuLawAudioSource] ⏹️ Closed (sent={_sentFrames}, silence={_silenceFrames})");
        return Task.CompletedTask;
    }

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        // Not used - we receive pre-encoded μ-law
    }

    /// <summary>
    /// Clear all queued frames.
    /// </summary>
    public void ClearQueue()
    {
        while (_ulawQueue.TryDequeue(out _)) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();
        ClearQueue();

        GC.SuppressFinalize(this);
    }
}
