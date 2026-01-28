using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// Direct RTP playout engine for G.711 A-law (PCMA).
/// Uses SendRtpRaw() for complete control over encoding.
/// ✅ NAudio WDL resampler for professional-grade 24kHz→8kHz conversion
/// ✅ 100% control over encoding/timestamps
/// ✅ Works consistently across SIPSorcery versions
/// ✅ Eliminates PCM hiss/static
/// </summary>
public class DirectRtpPlayout : IDisposable
{
    private const int PCM8K_FRAME_SAMPLES = 160;  // 20ms @ 8kHz
    private const int FRAME_MS = 20;
    private const int MAX_QUEUE_FRAMES = 500;     // 10s buffer (OpenAI sends audio in bursts)
    private const int MIN_STARTUP_FRAMES = 10;    // 200ms buffer before starting playout

    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly VoIPMediaSession _mediaSession;
    private readonly byte[] _alawBuffer = new byte[PCM8K_FRAME_SAMPLES];

    // RTP state
    private uint _timestamp;

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

    public DirectRtpPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _timestamp = (uint)new Random().Next(1, int.MaxValue);

        Log($"✅ Direct RTP playout initialized | Resampler: NAudio WDL (per-chunk)");
    }

    /// <summary>
    /// Buffer 24kHz PCM from OpenAI. Uses NAudio WDL resampler per-chunk for high quality.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || _disposed || pcm24kBytes == null || pcm24kBytes.Length == 0)
            return;

        try
        {
            // Use NAudio's high-quality WDL resampler per-chunk (avoids runaway buffering)
            var pcm8kBytes = NAudioResampler.ResampleBytes(pcm24kBytes, 24000, 8000);
            
            // Convert bytes to shorts
            var pcm8k = new short[pcm8kBytes.Length / 2];
            Buffer.BlockCopy(pcm8kBytes, 0, pcm8k, 0, pcm8kBytes.Length);

            // Frame into 160-sample (20ms) chunks
            for (int i = 0; i < pcm8k.Length; i += PCM8K_FRAME_SAMPLES)
            {
                // Drop OLDEST frame on overflow (minimizes latency)
                if (_frameQueue.Count >= MAX_QUEUE_FRAMES)
                {
                    _frameQueue.TryDequeue(out _);
                    Interlocked.Increment(ref _droppedFrames);
                }

                var frame = new short[PCM8K_FRAME_SAMPLES];
                int len = Math.Min(PCM8K_FRAME_SAMPLES, pcm8k.Length - i);
                Array.Copy(pcm8k, i, frame, 0, len);
                _frameQueue.Enqueue(frame);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ NAudio resampling error: {ex.Message}");
        }
    }

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
            Name = "DirectRtpPlayout"
        };
        _playoutThread.Start();

        Log($"▶️ Direct RTP playout STARTED | Codec: PCMA (A-law) | Buffer: {MAX_QUEUE_FRAMES * FRAME_MS}ms");
    }

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
    /// Clear queue on barge-in.
    /// </summary>
    public void Clear()
    {
        int cleared = 0;
        while (_frameQueue.TryDequeue(out _)) cleared++;
        
        if (cleared > 0) Log($"🗑️ Cleared {cleared} frames (barge-in)");
    }

    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameTime = sw.Elapsed.TotalMilliseconds;
        bool wasEmpty = true;
        bool startupBuffering = true;

        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            // Wait for startup buffer before beginning playout (absorbs OpenAI burst jitter)
            if (startupBuffering)
            {
                if (_frameQueue.Count >= MIN_STARTUP_FRAMES)
                {
                    startupBuffering = false;
                    nextFrameTime = sw.Elapsed.TotalMilliseconds;
                    Log($"📦 Startup buffer ready ({_frameQueue.Count} frames)");
                }
                else
                {
                    // Send silence while buffering
                    if (now >= nextFrameTime)
                    {
                        SendRtpPacket(new short[PCM8K_FRAME_SAMPLES]);
                        Interlocked.Increment(ref _silenceFrames);
                        nextFrameTime += FRAME_MS;
                    }
                    Thread.Sleep(1);
                    continue;
                }
            }

            if (now < nextFrameTime)
            {
                double wait = nextFrameTime - now;
                if (wait > 2) Thread.Sleep((int)(wait - 1));
                else if (wait > 0.5) Thread.SpinWait(500);
                continue;
            }

            short[] frame;
            if (_frameQueue.TryDequeue(out var queued))
            {
                frame = queued;
                wasEmpty = false;
            }
            else
            {
                frame = new short[PCM8K_FRAME_SAMPLES];
                Interlocked.Increment(ref _silenceFrames);

                if (!wasEmpty)
                {
                    wasEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }

            SendRtpPacket(frame);
            nextFrameTime += FRAME_MS;

            // Drift correction
            if (now - nextFrameTime > 20)
            {
                Log($"⏱️ Drift correction: {now - nextFrameTime:F1}ms behind");
                nextFrameTime = now + FRAME_MS;
            }
        }
    }

    /// <summary>
    /// ✅ SEND RAW RTP PACKET with properly encoded A-law payload.
    /// Uses SIPSorcery's built-in ALawEncoder for reliable encoding.
    /// </summary>
    private void SendRtpPacket(short[] pcmFrame)
    {
        try
        {
            // 1. Encode PCM → A-law using SIPSorcery's reliable encoder
            for (int i = 0; i < pcmFrame.Length; i++)
            {
                _alawBuffer[i] = ALawEncoder.LinearToALawSample(pcmFrame[i]);
            }

            // 2. Send via SendRtpRaw (bypasses SendAudio encoding ambiguity)
            // PayloadType 8 = PCMA (A-law), MarkerBit 0 = continuation
            _mediaSession.SendRtpRaw(
                SDPMediaTypesEnum.audio,
                _alawBuffer,
                _timestamp,
                markerBit: 0,
                payloadTypeID: 8  // PCMA
            );

            _timestamp += (uint)PCM8K_FRAME_SAMPLES; // Increment by samples @ 8kHz
            Interlocked.Increment(ref _framesSent);

            // Diagnostic: Verify first packet encoding
            if (_framesSent == 1)
            {
                Log($"✅ First RTP packet sent | TS:{_timestamp} | Payload:160 bytes A-law | First byte: 0x{_alawBuffer[0]:X2}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ RTP send error: {ex.Message}");
        }
    }

    private void Log(string msg) => OnLog?.Invoke($"[DirectRtp] {msg}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
