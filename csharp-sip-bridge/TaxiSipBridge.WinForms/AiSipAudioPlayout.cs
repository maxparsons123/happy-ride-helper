using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// AI → SIP Audio Playout Engine.
/// Receives 24kHz PCM from OpenAI, resamples to 8kHz, encodes to G.711 (A-law/μ-law),
/// and sends via VoIPMediaSession at a stable 20ms cadence.
/// 
/// AUDIO PHILOSOPHY: Clean passthrough with high-quality resampling.
/// No gain/limiting/DSP - just proper sample rate conversion.
/// </summary>
public class AiSipAudioPlayout : IDisposable
{
    private const int PCM_FRAME_SAMPLES = 160;  // 20ms @ 8kHz = 160 samples
    private const int FRAME_MS = 20;            // 20ms per frame
    private const int MAX_QUEUE_FRAMES = 500;   // 10 seconds max buffer (500 × 20ms)

    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly VoIPMediaSession _mediaSession;
    private readonly string _negotiatedCodec;

    private Thread? _playoutThread;
    private volatile bool _running;
    private volatile bool _disposed;

    // Stats
    private int _framesSent;
    private int _silenceFrames;
    private int _droppedFrames;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;

    public int QueuedFrames => _frameQueue.Count;
    public int FramesSent => _framesSent;
    public int SilenceFrames => _silenceFrames;
    public int DroppedFrames => _droppedFrames;

    public AiSipAudioPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));

        // Get negotiated codec from AudioLocalTrack (correct SIPSorcery API)
        var localFormat = _mediaSession.AudioLocalTrack?.Capabilities?.FirstOrDefault();
        _negotiatedCodec = localFormat?.Name() ?? "NONE";

        // Log codec info (don't throw - the session may still work)
        if (_negotiatedCodec == "PCMA" || _negotiatedCodec == "PCMU")
        {
            Log($"✅ Codec negotiated: {_negotiatedCodec} (A-law={_negotiatedCodec == "PCMA"})");
        }
        else
        {
            Log($"⚠️ Unexpected codec: {_negotiatedCodec} - expected PCMA/PCMU for G.711");
        }
    }

    /// <summary>
    /// Buffer AI audio (PCM16 @ 24kHz) for playout.
    /// Resamples to 8kHz PCM with proper anti-aliasing.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || _disposed || pcm24kBytes == null || pcm24kBytes.Length == 0)
            return;

        try
        {
            // Convert bytes to shorts (little-endian)
            var pcm24k = BytesToShorts(pcm24kBytes);

            // Resample 24kHz → 8kHz with proper anti-aliasing (clean passthrough, no DSP)
            var pcm8k = Resample24kTo8k(pcm24k);

            // Split into 20ms frames (160 samples each) and queue
            for (int i = 0; i < pcm8k.Length; i += PCM_FRAME_SAMPLES)
            {
                // Drop OLDEST frame on overflow to minimize latency (not newest)
                if (_frameQueue.Count >= MAX_QUEUE_FRAMES)
                {
                    _frameQueue.TryDequeue(out _);
                    Interlocked.Increment(ref _droppedFrames);
                }

                var frame = new short[PCM_FRAME_SAMPLES];
                int len = Math.Min(PCM_FRAME_SAMPLES, pcm8k.Length - i);
                Array.Copy(pcm8k, i, frame, 0, len);
                // Remaining samples stay 0 (silence)

                _frameQueue.Enqueue(frame);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ BufferAiAudio error: {ex.Message}");
        }
    }

    /// <summary>
    /// Start the playout thread.
    /// </summary>
    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;

        _framesSent = 0;
        _silenceFrames = 0;
        _droppedFrames = 0;

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "AiSipPlayoutThread"
        };

        _playoutThread.Start();
        Log($"▶️ Playout started (codec={_negotiatedCodec}, buffer={MAX_QUEUE_FRAMES * FRAME_MS}ms)");
    }

    /// <summary>
    /// Stop the playout thread and clear queue.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;

        try { _playoutThread?.Join(500); } catch { }
        _playoutThread = null;

        // Clear queue
        while (_frameQueue.TryDequeue(out _)) { }

        Log($"⏹️ Playout stopped (sent={_framesSent}, silence={_silenceFrames}, dropped={_droppedFrames})");
    }

    /// <summary>
    /// Clear all queued frames (e.g., on barge-in).
    /// </summary>
    public void Clear()
    {
        int count = 0;
        while (_frameQueue.TryDequeue(out _)) count++;
        if (count > 0)
            Log($"🗑️ Cleared {count} frames from queue");
    }

    /// <summary>
    /// High-precision 20ms playout loop with diagnostics.
    /// </summary>
    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameTimeMs = sw.Elapsed.TotalMilliseconds;
        bool wasEmpty = false;
        int diagnosticCounter = 0;
        double lastDiagnosticTime = 0;

        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            // Wait for next frame time
            if (now < nextFrameTimeMs)
            {
                double waitMs = nextFrameTimeMs - now;
                if (waitMs > 2.0)
                    Thread.Sleep((int)(waitMs - 1));
                else if (waitMs > 0.5)
                    Thread.SpinWait(500);
                continue;
            }

            // Get next frame or generate silence on underrun
            short[] frame;
            if (_frameQueue.TryDequeue(out var queuedFrame))
            {
                frame = queuedFrame;
                wasEmpty = false;
                diagnosticCounter++;
                
                // Log every 250 frames (5 seconds) for rate diagnostics
                if (diagnosticCounter % 250 == 0)
                {
                    double elapsed = now - lastDiagnosticTime;
                    double framesPerSec = 250.0 / (elapsed / 1000.0);
                    Log($"📊 Playout rate: {framesPerSec:F1} fps (target: 50), queue: {_frameQueue.Count}");
                    lastDiagnosticTime = now;
                }
            }
            else
            {
                frame = new short[PCM_FRAME_SAMPLES];
                Interlocked.Increment(ref _silenceFrames);

                // Notify once when queue empties (useful for barge-in detection)
                if (!wasEmpty)
                {
                    wasEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }

            // Send encoded G.711 payload (160 samples -> 160 bytes)
            SendPcmFrame(frame);

            // Schedule next frame
            nextFrameTimeMs += FRAME_MS;

            // Drift correction: reset if >40ms behind (prevents audible glitches)
            if (now - nextFrameTimeMs > 40)
            {
                Log($"⏱️ Drift correction: {now - nextFrameTimeMs:F1}ms behind, resetting timer");
                nextFrameTimeMs = now + FRAME_MS;
            }
        }
    }

    /// <summary>
    /// Encode PCM to A-law/μ-law and send via VoIPMediaSession.
    /// Uses correct RTP duration (160 timestamp units for 20ms @ 8kHz).
    /// </summary>
    private void SendPcmFrame(short[] pcmFrame)
    {
        try
        {
            // Encode PCM to G.711 (160 samples → 160 bytes)
            var encodedBytes = new byte[pcmFrame.Length];
            
            if (_negotiatedCodec == "PCMA")
            {
                // A-law encoding
                for (int i = 0; i < pcmFrame.Length; i++)
                    encodedBytes[i] = G711Codec.EncodeSampleALaw(pcmFrame[i]);
            }
            else
            {
                // μ-law encoding (PCMU)
                for (int i = 0; i < pcmFrame.Length; i++)
                    encodedBytes[i] = G711Codec.EncodeSample(pcmFrame[i]);
            }

            // RTP duration = 160 timestamp units (samples @ 8kHz clock rate)
            // This MUST match the number of samples, not bytes (though they're equal for G.711)
            const uint RTP_DURATION = 160; // 20ms @ 8kHz = 160 samples
            _mediaSession.SendAudio(RTP_DURATION, encodedBytes);

            Interlocked.Increment(ref _framesSent);
        }
        catch (Exception ex)
        {
            Log($"⚠️ RTP send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resample 24kHz → 8kHz using proper anti-aliasing filter.
    /// Clean passthrough - uses weighted 3-tap FIR [0.25, 0.5, 0.25] before 3:1 decimation.
    /// No aggressive gain/limiting - just proper sample rate conversion.
    /// </summary>
    private static short[] Resample24kTo8k(short[] pcm24k)
    {
        if (pcm24k.Length < 3) return Array.Empty<short>();

        int outputLen = pcm24k.Length / 3;
        var output = new short[outputLen];

        for (int i = 0; i < outputLen; i++)
        {
            int srcIdx = i * 3;
            
            // 3-tap FIR filter with coefficients [0.25, 0.5, 0.25] for anti-aliasing
            // This preserves energy better than simple averaging (coeffs sum to 1.0)
            int s0 = srcIdx > 0 ? pcm24k[srcIdx - 1] : pcm24k[srcIdx];
            int s1 = pcm24k[srcIdx];
            int s2 = srcIdx + 1 < pcm24k.Length ? pcm24k[srcIdx + 1] : pcm24k[srcIdx];
            
            // Weighted sum: 0.25*s0 + 0.5*s1 + 0.25*s2 = (s0 + 2*s1 + s2) / 4
            int filtered = (s0 + (s1 << 1) + s2) >> 2;
            
            // Clamp to short range
            output[i] = (short)Math.Clamp(filtered, short.MinValue, short.MaxValue);
        }

        return output;
    }

    /// <summary>
    /// Convert byte array to short array (little-endian).
    /// </summary>
    private static short[] BytesToShorts(byte[] bytes)
    {
        if (bytes.Length % 2 != 0)
            throw new ArgumentException("Byte array length must be even", nameof(bytes));

        var shorts = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, shorts, 0, bytes.Length);
        return shorts;
    }

    private void Log(string msg) => OnLog?.Invoke($"[AiPlayout] {msg}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        GC.SuppressFinalize(this);
    }
}
