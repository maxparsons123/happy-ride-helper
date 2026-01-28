using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace TaxiSipBridge;

/// <summary>
/// AI → SIP Audio Playout Engine.
/// Receives 24kHz PCM from OpenAI, resamples to 8kHz PCM, and sends via VoIPMediaSession.
/// ⚠️ IMPORTANT: VoIPMediaSession.SendAudio() EXPECTS RAW PCM and handles G.711 (A-law/μ-law)
/// encoding internally based on negotiated SDP codec. DO NOT pre-encode to A-law.
/// </summary>
public class AiSipAudioPlayout : IDisposable
{
    private const int PCM_FRAME_SAMPLES = 160;  // 20ms @ 8kHz = 160 samples
    private const int FRAME_MS = 20;            // 20ms per frame
    private const int MAX_QUEUE_FRAMES = 25;    // 500ms max buffer (25 × 20ms)

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
    /// Resamples to 8kHz PCM with anti-aliasing filter.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || _disposed || pcm24kBytes == null || pcm24kBytes.Length == 0)
            return;

        try
        {
            // Convert bytes to shorts (little-endian)
            var pcm24k = BytesToShorts(pcm24kBytes);

            // Resample 24kHz → 8kHz with anti-aliasing
            var pcm8k = Resample24kTo8kWithAntiAliasing(pcm24k);

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
    /// High-precision 20ms playout loop.
    /// </summary>
    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameTimeMs = sw.Elapsed.TotalMilliseconds;
        bool wasEmpty = false;

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

            // SEND RAW PCM - VoIPMediaSession handles G.711 encoding internally
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
    /// Send raw PCM frame to VoIPMediaSession.
    /// ⚠️ DO NOT pre-encode to A-law - SendAudio() handles G.711 encoding internally.
    /// </summary>
    private void SendPcmFrame(short[] pcmFrame)
    {
        try
        {
            // Convert shorts to bytes (little-endian)
            var pcmBytes = new byte[pcmFrame.Length * 2];
            Buffer.BlockCopy(pcmFrame, 0, pcmBytes, 0, pcmBytes.Length);

            // VoIPMediaSession.SendAudio EXPECTS RAW PCM and will encode to G.711
            // based on negotiated codec (PCMA = A-law, PCMU = μ-law)
            _mediaSession.SendAudio((uint)FRAME_MS, pcmBytes);

            Interlocked.Increment(ref _framesSent);
        }
        catch (Exception ex)
        {
            Log($"⚠️ RTP send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resample 24kHz → 8kHz with basic anti-aliasing FIR filter.
    /// Simple 3-tap [0.25, 0.5, 0.25] low-pass before 3:1 decimation.
    /// Prevents high-frequency aliasing artifacts.
    /// </summary>
    private static short[] Resample24kTo8kWithAntiAliasing(short[] pcm24k)
    {
        if (pcm24k.Length < 3) return Array.Empty<short>();

        int outputLen = pcm24k.Length / 3;
        var output = new short[outputLen];

        // Process in chunks of 3 samples
        for (int i = 0; i < outputLen; i++)
        {
            int srcIdx = i * 3;

            // Apply 3-tap FIR low-pass filter: [0.25, 0.5, 0.25]
            int sum = 0;
            int taps = 0;

            if (srcIdx > 0)
            {
                sum += pcm24k[srcIdx - 1] >> 2; // *0.25
                taps++;
            }

            sum += pcm24k[srcIdx] >> 1;         // *0.5
            taps++;

            if (srcIdx + 1 < pcm24k.Length)
            {
                sum += pcm24k[srcIdx + 1] >> 2; // *0.25
                taps++;
            }

            // Normalize by actual taps used (handles edges)
            output[i] = (short)(sum / taps);
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
