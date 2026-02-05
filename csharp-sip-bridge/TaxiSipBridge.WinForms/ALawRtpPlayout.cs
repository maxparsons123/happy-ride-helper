using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Direct A-law RTP playout for OpenAI g711_alaw output.
/// 
/// v6.2: High-precision timing with Windows multimedia timer (1ms granularity)
/// and non-blocking AsyncLogger for zero-jitter playout.
/// 
/// Receives raw A-law bytes from OpenAI, frames into 20ms chunks (160 bytes),
/// and sends directly via VoIPMediaSession's SendRtpRaw (payload type 8 = PCMA).
/// 
/// No decoding. No resampling. Just raw RTP passthrough.
/// </summary>
public sealed class ALawRtpPlayout : IDisposable
{
    // Windows multimedia timer for 1ms precision (vs 15.6ms default)
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);
    
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    private const int FRAME_MS = 20;
    private const int SAMPLES_PER_FRAME = 160; // 20ms @ 8kHz = 160 bytes for A-law
    private const byte ALAW_SILENCE = 0xD5;    // A-law silence byte
    private const byte PAYLOAD_TYPE_PCMA = 8;  // RTP payload type for G.711 A-law
    private const int MAX_QUEUE_FRAMES = 1000; // ~20 seconds buffer

    private readonly VoIPMediaSession _mediaSession;
    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private readonly AsyncLogger _asyncLog = new();  // v6.2: Non-blocking logging
    
    // Accumulator for incoming A-law bytes (may arrive in odd sizes)
    private byte[] _accumulator = Array.Empty<byte>();
    private readonly object _accLock = new();

    private Thread? _playoutThread;
    private volatile bool _running;
    private volatile bool _disposed;
    private bool _mmTimerActive;

    private uint _timestamp;
    private int _framesSent;
    private int _silenceFrames;
    private int _droppedFrames;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public int QueuedFrames => _frameQueue.Count;
    public int FramesSent => _framesSent;
    public int SilenceFrames => _silenceFrames;

    public ALawRtpPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
    }

    /// <summary>
    /// Buffer raw A-law bytes from OpenAI for playout.
    /// Frames into 160-byte (20ms) chunks automatically.
    /// </summary>
    public void BufferALaw(byte[] alawData)
    {
        if (!_running || _disposed || alawData == null || alawData.Length == 0)
            return;

        lock (_accLock)
        {
            // Append to accumulator
            var newAcc = new byte[_accumulator.Length + alawData.Length];
            Buffer.BlockCopy(_accumulator, 0, newAcc, 0, _accumulator.Length);
            Buffer.BlockCopy(alawData, 0, newAcc, _accumulator.Length, alawData.Length);
            _accumulator = newAcc;

            // Extract complete 160-byte frames
            while (_accumulator.Length >= SAMPLES_PER_FRAME)
            {
                var frame = new byte[SAMPLES_PER_FRAME];
                Buffer.BlockCopy(_accumulator, 0, frame, 0, SAMPLES_PER_FRAME);

                // Overflow protection: drop oldest frame if queue is full
                if (_frameQueue.Count >= MAX_QUEUE_FRAMES)
                {
                    _frameQueue.TryDequeue(out _);
                    Interlocked.Increment(ref _droppedFrames);
                }

                _frameQueue.Enqueue(frame);

                // Remove consumed bytes from accumulator
                var remaining = new byte[_accumulator.Length - SAMPLES_PER_FRAME];
                Buffer.BlockCopy(_accumulator, SAMPLES_PER_FRAME, remaining, 0, remaining.Length);
                _accumulator = remaining;
            }
        }
    }

    /// <summary>
    /// Start the playout thread with high-precision timing.
    /// </summary>
    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;

        _timestamp = (uint)new Random().Next(0, 65535); // Random starting timestamp per RFC 3550
        _framesSent = 0;
        _silenceFrames = 0;
        _droppedFrames = 0;

        // Enable Windows multimedia timer for 1ms precision (v6.2)
        try
        {
            TimeBeginPeriod(1);
            _mmTimerActive = true;
        }
        catch { /* Non-Windows or no permission */ }

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,  // v6.2: Maximum priority for timing
            Name = "ALawRtpPlayoutThread"
        };

        _playoutThread.Start();
        _asyncLog.Log("[ALawRtpPlayout] Started (v6.2, mmTimer=1ms)");
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

        // Disable multimedia timer
        if (_mmTimerActive)
        {
            try { TimeEndPeriod(1); } catch { }
            _mmTimerActive = false;
        }

        while (_frameQueue.TryDequeue(out _)) { }
        
        _asyncLog.Log($"[ALawRtpPlayout] Stopped ({_framesSent} frames sent)");
    }

    /// <summary>
    /// Clear all queued frames (e.g., on barge-in).
    /// </summary>
    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _)) { }
        lock (_accLock) { _accumulator = Array.Empty<byte>(); }
    }

    /// <summary>
    /// High-precision 20ms playout loop with drift correction.
    /// Uses Stopwatch for sub-millisecond timing accuracy.
    /// </summary>
    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameTimeMs = sw.Elapsed.TotalMilliseconds;
        bool wasEmpty = true;
        int frameCounter = 0;

        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            // Wait for next frame time with hybrid sleep/spin strategy
            if (now < nextFrameTimeMs)
            {
                double waitMs = nextFrameTimeMs - now;
                if (waitMs > 2.0)
                    Thread.Sleep((int)(waitMs - 1));  // Sleep most of the wait
                else if (waitMs > 0.5)
                    Thread.SpinWait(500);  // Spin for final sub-ms precision
                continue;
            }

            // Get next frame or generate silence on underrun
            byte[] frame;
            if (_frameQueue.TryDequeue(out var queuedFrame))
            {
                frame = queuedFrame;
                frameCounter++;
                wasEmpty = false;

                // Non-blocking diagnostic log every 500 frames (~10 seconds)
                if (frameCounter % 500 == 0)
                    _asyncLog.Log($"[ALawRtpPlayout] Sent {frameCounter} frames (queue: {_frameQueue.Count})");
            }
            else
            {
                // Generate silence frame
                frame = new byte[SAMPLES_PER_FRAME];
                Array.Fill(frame, ALAW_SILENCE);
                Interlocked.Increment(ref _silenceFrames);

                if (!wasEmpty)
                {
                    wasEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }

            // Send raw A-law frame via RTP (payload type 8 = PCMA)
            try
            {
                _mediaSession.SendRtpRaw(
                    SDPMediaTypesEnum.audio,
                    frame,
                    _timestamp,
                    0,  // marker bit
                    PAYLOAD_TYPE_PCMA
                );

                _timestamp += SAMPLES_PER_FRAME;
                Interlocked.Increment(ref _framesSent);
            }
            catch { }

            // Schedule next frame
            nextFrameTimeMs += FRAME_MS;

            // Drift correction: reset if >40ms behind
            now = sw.Elapsed.TotalMilliseconds;
            if (now - nextFrameTimeMs > 40)
                nextFrameTimeMs = now + FRAME_MS;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _asyncLog.Dispose();

        GC.SuppressFinalize(this);
    }
}
