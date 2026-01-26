using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge.Audio;

/// <summary>
/// Simple audio source that queues pre-encoded μ-law frames and sends them via RTP.
/// Used when audio is already in μ-law format (no resampling/encoding needed).
/// </summary>
public class MuLawAudioSource : IAudioSource, IDisposable
{
    private const int FRAME_SIZE_BYTES = 160; // 20ms @ 8kHz μ-law
    private const int FRAME_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 5000;

    // Pre-allocated silence buffer (0xFF is μ-law encoding of near-zero PCM)
    private static readonly byte[] MuLawSilence;

    private readonly ConcurrentQueue<byte[]> _ulawQueue = new();
    private readonly object _sendLock = new();
    private readonly MediaFormatManager<AudioFormat> _formatManager;

    private Timer? _timer;
    private volatile bool _disposed;
    private volatile bool _started;

    // Debug counters
    private int _sentFrames;
    private int _silenceFrames;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError;
    event Action<EncodedAudioFrame>? IAudioSource.OnAudioSourceEncodedFrameReady { add { } remove { } }

    public event Action<string>? OnDebugLog;

    static MuLawAudioSource()
    {
        MuLawSilence = new byte[FRAME_SIZE_BYTES];
        Array.Fill(MuLawSilence, (byte)0xFF); // μ-law silence
    }

    public MuLawAudioSource()
    {
        // Create format manager with PCMU (G.711 μ-law) as the only supported format
        var pcmuFormat = new AudioFormat(
            SDPWellKnownMediaFormatsEnum.PCMU.GetHashCode(), // Payload type 0
            "PCMU",
            8000,  // Clock rate
            8000,  // RTP clock rate
            1,     // Channels
            null   // Parameters
        );

        _formatManager = new MediaFormatManager<AudioFormat>(new List<AudioFormat> { pcmuFormat });
    }

    public List<AudioFormat> GetAudioSourceFormats() => _formatManager.GetSourceFormats();

    public void SetAudioSourceFormat(AudioFormat format)
    {
        _formatManager.SetSelectedFormat(format);
        OnDebugLog?.Invoke($"[MuLawAudioSource] Format set: {format.FormatName} @ {format.ClockRate}Hz");
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        _formatManager.RestrictFormats(filter);
    }

    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;

    public bool IsAudioSourcePaused() => false;

    /// <summary>
    /// Queue a pre-encoded μ-law frame (must be exactly 160 bytes for 20ms).
    /// </summary>
    public void EnqueueMuLaw(byte[] frame)
    {
        if (_disposed) return;

        if (frame?.Length != FRAME_SIZE_BYTES)
        {
            OnDebugLog?.Invoke($"[MuLawAudioSource] ⚠️ Invalid frame size: {frame?.Length ?? 0} (expected {FRAME_SIZE_BYTES})");
            return;
        }

        // Bound the queue to prevent memory issues
        while (_ulawQueue.Count >= MAX_QUEUED_FRAMES)
            _ulawQueue.TryDequeue(out _);

        _ulawQueue.Enqueue(frame);
    }

    public Task StartAudio()
    {
        if (_disposed || _started) return Task.CompletedTask;

        _started = true;
        _timer = new Timer(SendFrame, null, 0, FRAME_PERIOD_MS);
        OnDebugLog?.Invoke($"[MuLawAudioSource] ▶️ Started ({FRAME_PERIOD_MS}ms timer)");

        return Task.CompletedTask;
    }

    private void SendFrame(object? state)
    {
        if (_disposed) return;

        // Prevent overlapping callbacks
        if (!Monitor.TryEnter(_sendLock)) return;

        try
        {
            byte[] frame;

            if (_ulawQueue.TryDequeue(out var queuedFrame))
            {
                frame = queuedFrame;
                _sentFrames++;

                // Log first few frames
                if (_sentFrames <= 5)
                    OnDebugLog?.Invoke($"[MuLawAudioSource] 🔊 Frame {_sentFrames}: {frame.Length}b");
            }
            else
            {
                // Send silence to maintain RTP timing
                frame = MuLawSilence;
                _silenceFrames++;
            }

            // Duration in RTP timestamp units (8kHz = 160 samples per 20ms)
            OnAudioSourceEncodedSample?.Invoke(FRAME_SIZE_BYTES, frame);
        }
        catch (Exception ex)
        {
            OnDebugLog?.Invoke($"[MuLawAudioSource] ❌ Error: {ex.Message}");
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
