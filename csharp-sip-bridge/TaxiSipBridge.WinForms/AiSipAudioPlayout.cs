using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// AI → SIP Audio Playout Engine.
/// Receives 24kHz PCM from OpenAI, resamples to 8kHz, encodes to G.711 A-law (PCMA),
/// and sends via VoIPMediaSession at a stable 20ms cadence.
/// </summary>
public class AiSipAudioPlayout : IDisposable
{
    private const int PCM_FRAME_SAMPLES = 160;  // 20ms @ 8kHz
    private const int FRAME_MS = 20;            // 20ms per frame
    private const int MAX_QUEUE_FRAMES = 50;    // 1 second max buffer (50 × 20ms)

    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly VoIPMediaSession _mediaSession;

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

    public AiSipAudioPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
    }

    /// <summary>
    /// Buffer AI audio (PCM16 @ 24kHz) for playout.
    /// Resamples to 8kHz and queues 20ms PCM frames.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || _disposed || pcm24kBytes == null || pcm24kBytes.Length == 0)
            return;

        try
        {
            // Convert bytes to shorts
            var pcm24k = BytesToShorts(pcm24kBytes);

            // Resample 24kHz → 8kHz using simple 3:1 decimation with averaging
            var pcm8k = Resample24kTo8k(pcm24k);

            // Split into 20ms frames (160 samples each) and queue
            for (int i = 0; i < pcm8k.Length; i += PCM_FRAME_SAMPLES)
            {
                if (_frameQueue.Count >= MAX_QUEUE_FRAMES)
                {
                    Interlocked.Increment(ref _droppedFrames);
                    continue;
                }

                var frame = new short[PCM_FRAME_SAMPLES];
                int len = Math.Min(PCM_FRAME_SAMPLES, pcm8k.Length - i);
                Array.Copy(pcm8k, i, frame, 0, len);
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
            Name = $"AiSipPlayout-{Environment.CurrentManagedThreadId}"
        };

        _playoutThread.Start();
        Log("▶️ Playout started");
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

        while (_frameQueue.TryDequeue(out _)) { }

        Log($"⏹️ Playout stopped (sent={_framesSent}, silence={_silenceFrames}, dropped={_droppedFrames})");
    }

    /// <summary>
    /// Clear all queued frames (e.g., on barge-in).
    /// </summary>
    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _)) { }
        Log("🗑️ Queue cleared");
    }

    /// <summary>
    /// High-precision 20ms playout loop.
    /// </summary>
    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameMs = sw.Elapsed.TotalMilliseconds;
        bool wasEmpty = false;

        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            // Wait for next frame time
            if (now < nextFrameMs)
            {
                double waitMs = nextFrameMs - now;
                if (waitMs > 2)
                    Thread.Sleep((int)(waitMs - 1));
                else if (waitMs > 0.5)
                    Thread.SpinWait(500);
                continue;
            }

            // Get next frame or silence
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

                if (!wasEmpty)
                {
                    wasEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }

            SendPcmFrame(frame);

            nextFrameMs += FRAME_MS;

            // Drift correction: if we're way behind, reset
            if (now - nextFrameMs > 100)
            {
                Log($"⚠️ Drift correction: {now - nextFrameMs:F1}ms behind");
                nextFrameMs = now + FRAME_MS;
            }
        }
    }

    /// <summary>
    /// Encode PCM16 8kHz to A-law and send.
    /// NOTE: VoIPMediaSession.SendAudio expects the first parameter in 8kHz timestamp units.
    /// For 20ms @ 8kHz, that's 160.
    /// </summary>
    private void SendPcmFrame(short[] pcmFrame)
    {
        try
        {
            var alawBytes = new byte[pcmFrame.Length];
            for (int i = 0; i < pcmFrame.Length; i++)
            {
                alawBytes[i] = G711Codec.EncodeSampleALaw(pcmFrame[i]);
            }

            _mediaSession.SendAudio((uint)PCM_FRAME_SAMPLES, alawBytes);
            Interlocked.Increment(ref _framesSent);
        }
        catch (Exception ex)
        {
            Log($"⚠️ RTP send error: {ex}");
        }
    }

    /// <summary>
    /// Resample 24kHz PCM to 8kHz using simple 3:1 decimation with averaging.
    /// </summary>
    private static short[] Resample24kTo8k(short[] pcm24k)
    {
        int outputLen = pcm24k.Length / 3;
        var output = new short[outputLen];

        for (int i = 0; i < outputLen; i++)
        {
            int srcIdx = i * 3;
            int sum = pcm24k[srcIdx];
            if (srcIdx + 1 < pcm24k.Length) sum += pcm24k[srcIdx + 1];
            if (srcIdx + 2 < pcm24k.Length) sum += pcm24k[srcIdx + 2];
            output[i] = (short)(sum / 3);
        }

        return output;
    }

    /// <summary>
    /// Convert byte array to short array (little-endian).
    /// </summary>
    private static short[] BytesToShorts(byte[] bytes)
    {
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
