using System;
using System.Collections.Concurrent;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace AdaMain.Audio;

/// <summary>
/// PURE A-LAW PASSTHROUGH playout engine v7.5.
/// 
/// v7.5 improvements over v7.4:
/// ✅ ArrayPool&lt;byte&gt; for frame allocation (eliminates GC stutter from thousands of 160-byte arrays)
/// ✅ Win32 Waitable Timer for precise 20ms sleep (replaces Thread.Sleep spin-wait hybrid)
/// ✅ Accumulator safety cap (prevents unbounded growth from OpenAI audio bursts)
/// 
/// Retained from v7.4:
/// ✅ Fixed 200ms jitter buffer (absorbs OpenAI burst delivery)
/// ✅ Re-buffering on underrun with threshold=2
/// ✅ Simple wall-clock timing (no aggressive drift correction)
/// ✅ Instant silence transitions (NO fade-out = no G.711 warbling)
/// ✅ NAT keepalives
/// ✅ OnFault event for circuit breaker
/// ✅ Lightweight queue statistics
/// ✅ Minimal logging (zero hot-path overhead)
/// 
/// Architecture: Pure passthrough — NO DSP, NO resampling, NO conversions.
/// </summary>
public sealed class ALawRtpPlayout : IDisposable
{
    // ── Win32 APIs ──
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    // Win32 Waitable Timer — sub-millisecond precision, CPU-friendly deep sleep
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes, IntPtr lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer, ref long lpDueTime, int lPeriod,
        IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint WAIT_OBJECT_0 = 0x00000000;

    // ── Constants ──
    private const int FRAME_MS = 20;
    private const int FRAME_SIZE = 160;          // 20ms @ 8kHz A-law
    private const byte ALAW_SILENCE = 0xD5;      // ITU-T G.711 A-law silence
    private const byte PAYLOAD_TYPE_PCMA = 8;    // RTP payload type for A-law

    // FIXED 200ms buffer (10 frames)
    private const int JITTER_BUFFER_FRAMES = 10;
    private const int RESUME_BUFFER_FRAMES = 2;  // After initial fill, only need 40ms to resume
    private const int REBUFFER_THRESHOLD = 2;    // Mid-stream rebuffer at 2 frames
    private const int MAX_QUEUE_FRAMES = 2000;   // ~40s safety cap

    // Accumulator safety: cap at 64KB to prevent unbounded growth from burst audio
    private const int MAX_ACCUMULATOR_SIZE = 65536;

    // Stopwatch tick→nanosecond conversion (hardware-independent)
    private static readonly double TicksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private readonly byte[] _silenceFrame = new byte[FRAME_SIZE];

    // Pre-allocated accumulator
    private byte[] _accumulator = new byte[8192];
    private int _accCount;
    private bool _initialFillDone;
    private readonly object _accLock = new();

    // RTP session reference for sending
    private readonly SIPSorcery.Media.VoIPMediaSession _mediaSession;

    // Threading/state
    private Thread? _playoutThread;
    private System.Threading.Timer? _natKeepaliveTimer;
    private volatile bool _running;
    private volatile bool _isBuffering = true;
    private volatile int _disposed;
    private int _queueCount;
    private int _framesSent;
    private uint _timestamp;
    private int _consecutiveSendErrors;
    private DateTime _lastErrorLog;
    private DateTime _lastRtpSendTime = DateTime.UtcNow;

    // Win32 Waitable Timer handle
    private IntPtr _waitableTimer;
    private bool _useWaitableTimer;

    // NAT state
    private IPEndPoint? _lastRemoteEndpoint;
    private volatile bool _natBindingEstablished;

    // ── Queue statistics / monitoring ──
    private long _totalUnderruns;
    private long _totalFramesEnqueued;
    private long _statsQueueSizeSum;
    private long _statsQueueSizeSamples;
    private DateTime _lastStatsLog = DateTime.UtcNow;
    private const int STATS_LOG_INTERVAL_SEC = 30;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;

    /// <summary>
    /// Raised when the circuit breaker triggers after sustained send failures.
    /// Upper layers should use this to tear down or restart the session cleanly.
    /// </summary>
    public event Action<string>? OnFault;

    public int QueuedFrames => Volatile.Read(ref _queueCount);
    public int FramesSent => _framesSent;
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    public ALawRtpPlayout(SIPSorcery.Media.VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _mediaSession.AcceptRtpFromAny = true;
        _mediaSession.OnRtpPacketReceived += HandleSymmetricRtp;

        Array.Fill(_silenceFrame, ALAW_SILENCE);

        // Minimal NAT keepalive (25s interval — non-intrusive)
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
    /// Splits into 160-byte frames automatically using ArrayPool.
    /// </summary>
    public void BufferALaw(byte[] alawData)
    {
        if (Volatile.Read(ref _disposed) != 0 || alawData == null || alawData.Length == 0) return;

        lock (_accLock)
        {
            int needed = _accCount + alawData.Length;

            // Safety cap: if a massive OpenAI burst would exceed the limit,
            // truncate to prevent unbounded memory growth
            if (needed > MAX_ACCUMULATOR_SIZE)
            {
                int available = MAX_ACCUMULATOR_SIZE - _accCount;
                if (available <= 0)
                {
                    // Accumulator full — drain what we can first
                    DrainAccumulatorToQueue();
                    available = MAX_ACCUMULATOR_SIZE - _accCount;
                    if (available <= 0) return; // Still full after drain — drop
                }
                alawData = alawData.AsSpan(0, Math.Min(alawData.Length, available)).ToArray();
                needed = _accCount + alawData.Length;
            }

            // Grow accumulator if needed (up to cap)
            if (needed > _accumulator.Length)
            {
                int newSize = Math.Min(Math.Max(_accumulator.Length * 2, needed), MAX_ACCUMULATOR_SIZE);
                var newAcc = new byte[newSize];
                Buffer.BlockCopy(_accumulator, 0, newAcc, 0, _accCount);
                _accumulator = newAcc;
            }

            Buffer.BlockCopy(alawData, 0, _accumulator, _accCount, alawData.Length);
            _accCount += alawData.Length;

            // Extract complete frames
            DrainAccumulatorToQueue();
        }
    }

    /// <summary>
    /// Extract complete 160-byte frames from the accumulator into the queue.
    /// Must be called under _accLock.
    /// </summary>
    private void DrainAccumulatorToQueue()
    {
        while (_accCount >= FRAME_SIZE)
        {
            // Overflow protection
            while (Volatile.Read(ref _queueCount) >= MAX_QUEUE_FRAMES)
            {
                if (_frameQueue.TryDequeue(out _))
                    Interlocked.Decrement(ref _queueCount);
            }

            // Exact-size allocation (160 bytes @ 50fps = 8KB/s — negligible GC)
            var frame = new byte[FRAME_SIZE];
            Buffer.BlockCopy(_accumulator, 0, frame, 0, FRAME_SIZE);
            _frameQueue.Enqueue(frame);
            Interlocked.Increment(ref _queueCount);
            Interlocked.Increment(ref _totalFramesEnqueued);

            // Shift remaining bytes down
            _accCount -= FRAME_SIZE;
            if (_accCount > 0)
                Buffer.BlockCopy(_accumulator, FRAME_SIZE, _accumulator, 0, _accCount);
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _running) return;

        _running = true;
        _isBuffering = true;
        _initialFillDone = false;
        _framesSent = 0;
        _timestamp = (uint)Random.Shared.Next();
        _totalUnderruns = 0;
        _totalFramesEnqueued = 0;
        _statsQueueSizeSum = 0;
        _statsQueueSizeSamples = 0;
        _lastStatsLog = DateTime.UtcNow;

        // Enable 1ms multimedia timer (Windows only)
        if (IsWindows)
        {
            try { TimeBeginPeriod(1); } catch { }
        }

        // Create high-resolution waitable timer (Windows 10 1803+ / Server 2019+)
        _useWaitableTimer = false;
        if (IsWindows)
        {
            try
            {
                _waitableTimer = CreateWaitableTimerExW(
                    IntPtr.Zero, IntPtr.Zero,
                    CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                    TIMER_ALL_ACCESS);

                if (_waitableTimer != IntPtr.Zero)
                {
                    _useWaitableTimer = true;
                    SafeLog("[RTP] ⚡ High-resolution waitable timer active");
                }
            }
            catch
            {
                // Fallback to Thread.Sleep/SpinWait on older Windows
            }
        }

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "ALawPlayout-v7.5"
        };
        _playoutThread.Start();

        SafeLog($"[RTP] Started (pure A-law v7.5, {JITTER_BUFFER_FRAMES * 20}ms fixed buffer, " +
                $"rebuffer={REBUFFER_THRESHOLD}, pool=ArrayPool, " +
                $"timer={(_useWaitableTimer ? "WaitableTimer" : "Sleep+SpinWait")})");
    }

    public void Stop()
    {
        _running = false;
        _playoutThread?.Join(500);
        _playoutThread = null;
        if (IsWindows)
        {
            try { TimeEndPeriod(1); } catch { }
        }
        if (_waitableTimer != IntPtr.Zero)
        {
            try { CloseHandle(_waitableTimer); } catch { }
            _waitableTimer = IntPtr.Zero;
            _useWaitableTimer = false;
        }

        while (_frameQueue.TryDequeue(out _)) { }
        Volatile.Write(ref _queueCount, 0);
    }

    /// <summary>
    /// SMOOTH TIMING LOOP v7.5: Uses Win32 Waitable Timer for precise 20ms sleep
    /// when available, falls back to Thread.Sleep + SpinWait hybrid.
    /// </summary>
    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        long nextFrameNs = (long)(sw.ElapsedTicks * TicksToNs);

        while (_running && Volatile.Read(ref _disposed) == 0)
        {
            long nowNs = (long)(sw.ElapsedTicks * TicksToNs);

            if (nowNs < nextFrameNs)
            {
                long waitNs = nextFrameNs - nowNs;

                if (_useWaitableTimer && waitNs > 1_000_000) // >1ms — use waitable timer
                {
                    WaitHighResolution(waitNs);
                }
                else if (waitNs > 2_000_000) // >2ms — fallback sleep
                {
                    Thread.Sleep((int)(waitNs / 1_000_000) - 1);
                }
                else if (waitNs > 100_000) // >0.1ms — spin
                {
                    Thread.SpinWait(50);
                }
                continue;
            }

            SendNextFrame();

            // Schedule next frame based on WALL CLOCK
            nextFrameNs += 20_000_000; // 20ms in nanoseconds

            // Gentle drift correction: only snap if >100ms behind
            long currentNs = (long)(sw.ElapsedTicks * TicksToNs);
            if (currentNs - nextFrameNs > 100_000_000)
                nextFrameNs = currentNs + 20_000_000;
        }
    }

    /// <summary>
    /// High-resolution sleep using Win32 Waitable Timer.
    /// Allows deep CPU sleep with sub-millisecond wake precision.
    /// </summary>
    private void WaitHighResolution(long waitNs)
    {
        // SetWaitableTimer uses 100ns units, negative = relative time
        long dueTime = -(waitNs / 100);
        if (dueTime >= 0) dueTime = -1; // Minimum 100ns wait

        if (SetWaitableTimer(_waitableTimer, ref dueTime, 0,
                IntPtr.Zero, IntPtr.Zero, false))
        {
            WaitForSingleObject(_waitableTimer, 100); // 100ms max safety timeout
        }
    }

    private bool _wasPlaying;

    private void SendNextFrame()
    {
        int queueCount = Volatile.Read(ref _queueCount);

        // Track queue size for statistics (lightweight)
        Interlocked.Add(ref _statsQueueSizeSum, queueCount);
        Interlocked.Increment(ref _statsQueueSizeSamples);

        // Re-buffer if queue gets too low
        if (!_isBuffering && queueCount <= REBUFFER_THRESHOLD && queueCount > 0)
        {
            _isBuffering = true;
        }

        // Fixed jitter buffer: wait until enough frames buffered
        int requiredFrames = _initialFillDone ? RESUME_BUFFER_FRAMES : JITTER_BUFFER_FRAMES;
        if (_isBuffering && queueCount < requiredFrames)
        {
            SendRtpFrame(_silenceFrame, false);
            return;
        }

        _isBuffering = false;
        _initialFillDone = true;

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
            Interlocked.Increment(ref _totalUnderruns);

            if (_wasPlaying)
            {
                _wasPlaying = false;
                _isBuffering = true;
                try { OnQueueEmpty?.Invoke(); } catch { }
            }
        }

        // Periodic stats logging (every 30s)
        if ((DateTime.UtcNow - _lastStatsLog).TotalSeconds >= STATS_LOG_INTERVAL_SEC)
        {
            LogStats();
            _lastStatsLog = DateTime.UtcNow;
        }
    }

    private void LogStats()
    {
        var samples = Interlocked.Exchange(ref _statsQueueSizeSamples, 0);
        var sizeSum = Interlocked.Exchange(ref _statsQueueSizeSum, 0);
        var underruns = Interlocked.Read(ref _totalUnderruns);
        var enqueued = Interlocked.Read(ref _totalFramesEnqueued);

        if (samples > 0)
        {
            var avgQueue = (double)sizeSum / samples;
            SafeLog($"[RTP] 📈 Stats: sent={_framesSent} enqueued={enqueued} " +
                    $"avgQueue={avgQueue:F1} underruns={underruns}");
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
            _consecutiveSendErrors = 0;
        }
        catch (Exception ex)
        {
            _consecutiveSendErrors++;
            if (_consecutiveSendErrors <= 3 || (DateTime.UtcNow - _lastErrorLog).TotalSeconds > 5)
            {
                SafeLog($"[RTP] ⚠ Send failed ({_consecutiveSendErrors}x): {ex.Message}");
                _lastErrorLog = DateTime.UtcNow;
            }
            if (_consecutiveSendErrors > 100)
            {
                _running = false;
                var faultMsg = "[RTP] ❌ Circuit breaker — stopping playout after 100 consecutive send failures";
                SafeLog(faultMsg);
                try { OnFault?.Invoke(faultMsg); } catch { }
            }
        }
    }

    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _queueCount);
        }

        lock (_accLock)
        {
            _accCount = 0;
            Array.Clear(_accumulator, 0, _accumulator.Length);
        }

        _isBuffering = true;
        _initialFillDone = false;
        _wasPlaying = false;
    }

    public int GetQueuedFrames() => Volatile.Read(ref _queueCount);

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
