using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Zaffiqbal247RadioCars.Audio;

/// <summary>
/// PURE A-LAW PASSTHROUGH playout engine — ported from TaxiSipBridge ALawRtpPlayout v7.4.
/// 
/// ✅ Fixed 100ms jitter buffer (absorbs OpenAI burst delivery)
/// ✅ Re-buffering on underrun (prevents fast→slow pacing artifacts)
/// ✅ Simple wall-clock timing (no aggressive drift correction = no micro-stutters)
/// ✅ Instant silence transitions (NO fade-out = no G.711 warbling)
/// ✅ NAT keepalives (reliability without audio impact)
/// ✅ Windows multimedia timer for 1ms precision
/// ✅ Minimal logging (zero hot-path overhead)
/// 
/// Architecture: Pure passthrough - NO DSP, NO resampling, NO conversions.
/// </summary>
public sealed class ALawRtpPlayout : IDisposable
{
    // Windows multimedia timer for 1ms precision
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    private const int FRAME_MS = 20;
    private const int FRAME_SIZE = 160;          // 20ms @ 8kHz A-law
    private const byte ALAW_SILENCE = 0xD5;      // ITU-T G.711 A-law silence
    private const byte PAYLOAD_TYPE_PCMA = 8;    // RTP payload type for A-law

    // FIXED 100ms buffer (5 frames) - absorbs OpenAI burst delivery
    private const int JITTER_BUFFER_FRAMES = 5;
    private const int REBUFFER_THRESHOLD = 2;    // Re-buffer if queue drops below this
    private const int MAX_QUEUE_FRAMES = 1500;   // ~30s safety cap
    private const int MAX_LATENCY_FRAMES = 150;   // 3s max playout latency — trim excess

    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private readonly byte[] _silenceFrame = new byte[FRAME_SIZE];

    // Pre-allocated accumulator (avoids GC pressure → smoother audio)
    private byte[] _accumulator = new byte[8192];
    private int _accCount;
    private readonly object _accLock = new();

    // RTP session reference for sending
    private readonly VoIPMediaSession _mediaSession;

    // Threading/state
    private Thread? _playoutThread;
    private System.Threading.Timer? _natKeepaliveTimer;
    private volatile bool _running;
    private volatile bool _isBuffering = true;
    private volatile int _disposed;
    private int _queueCount;
    private int _framesSent;
    private uint _timestamp;
    private DateTime _lastRtpSendTime = DateTime.UtcNow;

    // NAT state
    private IPEndPoint? _lastRemoteEndpoint;
    private volatile bool _natBindingEstablished;

    // Playout state
    private bool _wasPlaying;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;

    public int QueuedFrames => Volatile.Read(ref _queueCount);
    public int FramesSent => _framesSent;

    public ALawRtpPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _mediaSession.AcceptRtpFromAny = true;
        _mediaSession.OnRtpPacketReceived += HandleSymmetricRtp;

        Array.Fill(_silenceFrame, ALAW_SILENCE);

        // Minimal NAT keepalive (25s interval - non-intrusive)
        _natKeepaliveTimer = new System.Threading.Timer(KeepaliveNAT, null,
            TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(25));
    }

    private void HandleSymmetricRtp(IPEndPoint ep, SDPMediaTypesEnum media, RTPPacket pkt)
    {
        if (media != SDPMediaTypesEnum.audio || Volatile.Read(ref _disposed) != 0) return;

        if (_lastRemoteEndpoint == null || !_lastRemoteEndpoint.Equals(ep))
        {
            _lastRemoteEndpoint = ep;
            try
            {
                _mediaSession.SetDestination(SDPMediaTypesEnum.audio, ep, ep);
                _natBindingEstablished = true;
                SafeLog($"[NAT] ✓ RTP locked to {ep}");
            }
            catch { }
        }
    }

    private void KeepaliveNAT(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_natBindingEstablished || (DateTime.UtcNow - _lastRtpSendTime).TotalSeconds < 20) return;

        try { SendRtpFrame(_silenceFrame, false); } catch { }
    }

    /// <summary>
    /// Buffer raw A-law bytes (any length) with lock-free accumulator.
    /// Splits into 160-byte frames automatically.
    /// </summary>
    public void BufferALaw(byte[] alawData)
    {
        if (Volatile.Read(ref _disposed) != 0 || alawData == null || alawData.Length == 0) return;

        lock (_accLock)
        {
            // Append to pre-allocated buffer (reallocate only if truly needed)
            if (_accCount + alawData.Length > _accumulator.Length)
            {
                var newAcc = new byte[Math.Max(_accumulator.Length * 2, _accCount + alawData.Length)];
                Buffer.BlockCopy(_accumulator, 0, newAcc, 0, _accCount);
                _accumulator = newAcc;
            }

            Buffer.BlockCopy(alawData, 0, _accumulator, _accCount, alawData.Length);
            _accCount += alawData.Length;

            // Extract complete frames
            while (_accCount >= FRAME_SIZE)
            {
                // Overflow protection
                while (Volatile.Read(ref _queueCount) >= MAX_QUEUE_FRAMES)
                {
                    if (_frameQueue.TryDequeue(out _))
                        Interlocked.Decrement(ref _queueCount);
                }

                var frame = new byte[FRAME_SIZE];
                Buffer.BlockCopy(_accumulator, 0, frame, 0, FRAME_SIZE);
                _frameQueue.Enqueue(frame);
                Interlocked.Increment(ref _queueCount);

                // Shift remaining bytes down
                _accCount -= FRAME_SIZE;
                if (_accCount > 0)
                    Buffer.BlockCopy(_accumulator, FRAME_SIZE, _accumulator, 0, _accCount);
            }
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _running) return;

        _running = true;
        _isBuffering = true;
        _framesSent = 0;
        _timestamp = (uint)Random.Shared.Next();

        // Enable 1ms multimedia timer (smooth timing baseline)
        try { TimeBeginPeriod(1); } catch { }

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "ALawPlayout"
        };

        _playoutThread.Start();
        SafeLog($"[RTP] Started (pure A-law, {JITTER_BUFFER_FRAMES * 20}ms fixed buffer)");
    }

    public void Stop()
    {
        _running = false;
        _playoutThread?.Join(500);
        _playoutThread = null;

        try { TimeEndPeriod(1); } catch { }

        while (_frameQueue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// SMOOTH TIMING LOOP: Simple wall-clock sync without aggressive correction.
    /// Aggressive drift correction causes micro-stutters → perceived as jitter.
    /// </summary>
    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        long nextFrameNs = sw.ElapsedTicks * 100; // 100ns units

        while (_running && Volatile.Read(ref _disposed) == 0)
        {
            long nowNs = sw.ElapsedTicks * 100;

            // Smooth wait: sleep most of the time, spin only for final precision
            if (nowNs < nextFrameNs)
            {
                long waitNs = nextFrameNs - nowNs;
                if (waitNs > 2_000_000) // >2ms
                    Thread.Sleep((int)(waitNs / 1_000_000) - 1);
                else if (waitNs > 100_000) // >0.1ms
                    Thread.SpinWait(50);
                continue;
            }

            SendNextFrame();

            // Schedule next frame based on WALL CLOCK (no accumulation)
            nextFrameNs += 20_000_000; // 20ms in 100ns units

            // Gentle drift correction: only snap if >100ms behind (prevents micro-stutters)
            long currentNs = sw.ElapsedTicks * 100;
            if (currentNs - nextFrameNs > 100_000_000) // >100ms behind
                nextFrameNs = currentNs + 20_000_000;
        }
    }

    private void SendNextFrame()
    {
        int queueCount = Volatile.Read(ref _queueCount);

        // ── LATENCY TRIM ──
        // If queue exceeds 1 second, drop oldest frames to prevent garbled/laggy audio
        if (queueCount > MAX_LATENCY_FRAMES)
        {
            int toDrop = queueCount - MAX_LATENCY_FRAMES;
            int dropped = 0;
            while (dropped < toDrop && _frameQueue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queueCount);
                dropped++;
            }
            queueCount = Volatile.Read(ref _queueCount);
            SafeLog($"[RTP] ✂ Trimmed {dropped} frames (latency cap 1s)");
        }

        // Re-buffer if queue gets too low (prevents burst→slow pacing)
        if (!_isBuffering && queueCount < REBUFFER_THRESHOLD && queueCount > 0)
        {
            _isBuffering = true;
        }

        // Fixed jitter buffer: wait until we have enough frames
        if (_isBuffering && queueCount < JITTER_BUFFER_FRAMES)
        {
            SendRtpFrame(_silenceFrame, false);
            return;
        }

        _isBuffering = false;

        // Get frame or send instant silence (NO fade-out → prevents G.711 warbling)
        if (_frameQueue.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            SendRtpFrame(frame, false);
            Interlocked.Increment(ref _framesSent);
            _wasPlaying = true;
        }
        else
        {
            SendRtpFrame(_silenceFrame, false);
            if (_wasPlaying)
            {
                _wasPlaying = false;
                _isBuffering = true;
                try { OnQueueEmpty?.Invoke(); } catch { }
            }
        }
    }

    private void SendRtpFrame(byte[] frame, bool marker)
    {
        try
        {
            _mediaSession.SendRtpRaw(
                SDPMediaTypesEnum.audio,
                frame,
                _timestamp,
                marker ? 1 : 0,
                PAYLOAD_TYPE_PCMA
            );

            _timestamp += FRAME_SIZE;
            _lastRtpSendTime = DateTime.UtcNow;
        }
        catch { }
    }

    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _))
            Interlocked.Decrement(ref _queueCount);

        lock (_accLock)
        {
            _accCount = 0;
            Array.Clear(_accumulator, 0, _accumulator.Length);
        }

        _isBuffering = true;
        _wasPlaying = false;
    }

    private void SafeLog(string msg)
    {
        try { OnLog?.Invoke(msg); }
        catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        Stop();
        _natKeepaliveTimer?.Dispose();
        _mediaSession.OnRtpPacketReceived -= HandleSymmetricRtp;
        Clear();

        GC.SuppressFinalize(this);
    }
}
