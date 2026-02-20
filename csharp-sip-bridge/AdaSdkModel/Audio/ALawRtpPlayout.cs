// Last updated: 2026-02-20 (ALawRtpPlayout v8.3 — Production Best)
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace AdaSdkModel.Audio;

/// <summary>
/// PURE A-LAW PASSTHROUGH playout engine v8.3 — PRODUCTION BEST.
///
/// v8.3 — definitive production version:
/// ✅ HYSTERESIS buffering: 200ms (10 frames) to START, only re-buffer when queue hits 0
///    Eliminates "grumble" caused by rapid Play→Silence→Play toggling at 50Hz
/// ✅ Reduced SpinWait intensity (prevents starving network thread on desktop CPUs)
/// ✅ Win32 Waitable Timer for precise 20ms sleep
/// ✅ Accumulator safety cap (prevents unbounded growth from OpenAI audio bursts)
/// ✅ Instant silence transitions (NO fade-out = no G.711 warbling)
/// ✅ NAT keepalives, OnFault circuit breaker, lightweight queue statistics
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

    // ── Constants ──
    private const int FRAME_MS   = 20;
    private const int FRAME_SIZE = 160;          // 20ms @ 8kHz A-law
    private const byte ALAW_SILENCE      = 0xD5; // ITU-T G.711 A-law silence
    private const byte PAYLOAD_TYPE_PCMA = 8;    // RTP payload type for A-law

    // HYSTERESIS CALIBRATION (v8.3):
    // Start threshold: require 200ms (10 frames) before first playout
    // Stop threshold: only re-buffer when queue actually hits 0 (not 2)
    private const int JITTER_BUFFER_START_THRESHOLD = 10; // 200ms to start/resume
    private const int MAX_QUEUE_FRAMES     = 2000;        // ~40s safety cap
    private const int MAX_ACCUMULATOR_SIZE = 65536;       // 64KB burst cap
    private const int STATS_LOG_INTERVAL_SEC = 30;

    private static readonly double TicksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private readonly byte[] _silenceFrame = new byte[FRAME_SIZE];

    private byte[] _accumulator = new byte[8192];
    private int _accCount;
    private readonly object _accLock = new();

    private readonly VoIPMediaSession _mediaSession;
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

    private IntPtr _waitableTimer;
    private bool _useWaitableTimer;

    private IPEndPoint? _lastRemoteEndpoint;
    private volatile bool _natBindingEstablished;
    private bool _wasPlaying;

    private long _totalUnderruns;
    private long _totalFramesEnqueued;
    private long _statsQueueSizeSum;
    private long _statsQueueSizeSamples;
    private DateTime _lastStatsLog = DateTime.UtcNow;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;
    public event Action<string>? OnFault;

    public int QueuedFrames  => Volatile.Read(ref _queueCount);
    public int FramesSent    => _framesSent;
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    // ── Stubs for SipServer.cs compatibility ──
    public bool TypingSoundsEnabled { get; set; } = false;
    public void NotifyAdaHasSpoken() { } // no-op — typing sounds not in v8.3

    public ALawRtpPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _mediaSession.AcceptRtpFromAny = true;
        _mediaSession.OnRtpPacketReceived += HandleSymmetricRtp;
        Array.Fill(_silenceFrame, ALAW_SILENCE);

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

    public void BufferALaw(byte[] alawData)
    {
        if (Volatile.Read(ref _disposed) != 0 || alawData == null || alawData.Length == 0) return;

        lock (_accLock)
        {
            int needed = _accCount + alawData.Length;

            if (needed > MAX_ACCUMULATOR_SIZE)
            {
                int available = MAX_ACCUMULATOR_SIZE - _accCount;
                if (available <= 0)
                {
                    DrainAccumulatorToQueue();
                    available = MAX_ACCUMULATOR_SIZE - _accCount;
                    if (available <= 0) return;
                }
                alawData = alawData.AsSpan(0, Math.Min(alawData.Length, available)).ToArray();
                needed = _accCount + alawData.Length;
            }

            if (needed > _accumulator.Length)
            {
                int newSize = Math.Min(Math.Max(_accumulator.Length * 2, needed), MAX_ACCUMULATOR_SIZE);
                var newAcc = new byte[newSize];
                Buffer.BlockCopy(_accumulator, 0, newAcc, 0, _accCount);
                _accumulator = newAcc;
            }

            Buffer.BlockCopy(alawData, 0, _accumulator, _accCount, alawData.Length);
            _accCount += alawData.Length;
            DrainAccumulatorToQueue();
        }
    }

    private void DrainAccumulatorToQueue()
    {
        while (_accCount >= FRAME_SIZE)
        {
            while (Volatile.Read(ref _queueCount) >= MAX_QUEUE_FRAMES)
            {
                if (_frameQueue.TryDequeue(out _))
                    Interlocked.Decrement(ref _queueCount);
            }

            var frame = new byte[FRAME_SIZE];
            Buffer.BlockCopy(_accumulator, 0, frame, 0, FRAME_SIZE);
            _frameQueue.Enqueue(frame);
            Interlocked.Increment(ref _queueCount);
            Interlocked.Increment(ref _totalFramesEnqueued);

            _accCount -= FRAME_SIZE;
            if (_accCount > 0)
                Buffer.BlockCopy(_accumulator, FRAME_SIZE, _accumulator, 0, _accCount);
        }
    }

    /// <summary>Flush any partial frame in the accumulator (padded with silence) — called by SipServer on response end.</summary>
    public void Flush()
    {
        lock (_accLock)
        {
            if (_accCount > 0)
            {
                var frame = new byte[FRAME_SIZE];
                Array.Fill(frame, ALAW_SILENCE);
                Buffer.BlockCopy(_accumulator, 0, frame, 0, _accCount);
                _frameQueue.Enqueue(frame);
                Interlocked.Increment(ref _queueCount);
                _accCount = 0;
            }
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _running) return;

        _running     = true;
        _isBuffering = true;
        _framesSent  = 0;
        _timestamp   = (uint)Random.Shared.Next();
        _totalUnderruns      = 0;
        _totalFramesEnqueued = 0;
        _statsQueueSizeSum   = 0;
        _statsQueueSizeSamples = 0;
        _lastStatsLog = DateTime.UtcNow;

        if (IsWindows)
        {
            try { TimeBeginPeriod(1); } catch { }
            try
            {
                _waitableTimer = CreateWaitableTimerExW(
                    IntPtr.Zero, IntPtr.Zero,
                    CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
                if (_waitableTimer != IntPtr.Zero)
                {
                    _useWaitableTimer = true;
                    SafeLog("[RTP] ⚡ High-resolution waitable timer active");
                }
            }
            catch { }
        }

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal,
            Name         = "ALawPlayout-v8.3"
        };
        _playoutThread.Start();
        SafeLog($"[RTP] v8.3 started (pure A-law, {JITTER_BUFFER_START_THRESHOLD * FRAME_MS}ms hysteresis buffer, " +
                $"timer={(_useWaitableTimer ? "WaitableTimer" : "Sleep+SpinWait")})");
    }

    public void Stop()
    {
        _running = false;
        _playoutThread?.Join(500);
        _playoutThread = null;

        if (IsWindows)
            try { TimeEndPeriod(1); } catch { }

        if (_waitableTimer != IntPtr.Zero)
        {
            try { CloseHandle(_waitableTimer); } catch { }
            _waitableTimer    = IntPtr.Zero;
            _useWaitableTimer = false;
        }

        while (_frameQueue.TryDequeue(out _)) { }
        Volatile.Write(ref _queueCount, 0);
    }

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

                if (_useWaitableTimer && waitNs > 1_000_000)
                    WaitHighResolution(waitNs);
                else if (waitNs > 2_000_000)
                    Thread.Sleep((int)(waitNs / 1_000_000) - 1);
                else if (waitNs > 100_000)
                    Thread.SpinWait(20);

                continue;
            }

            SendNextFrame();

            nextFrameNs += 20_000_000;

            long currentNs = (long)(sw.ElapsedTicks * TicksToNs);
            if (currentNs - nextFrameNs > 100_000_000)
                nextFrameNs = currentNs + 20_000_000;
        }
    }

    private void WaitHighResolution(long waitNs)
    {
        long dueTime = -(waitNs / 100);
        if (dueTime >= 0) dueTime = -1;
        if (SetWaitableTimer(_waitableTimer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(_waitableTimer, 100);
    }

    private void SendNextFrame()
    {
        int queueCount = Volatile.Read(ref _queueCount);

        Interlocked.Add(ref _statsQueueSizeSum, queueCount);
        Interlocked.Increment(ref _statsQueueSizeSamples);

        // ── HYSTERESIS (v8.3) ──
        // Wait for full 200ms pillow before starting playout.
        // Only re-buffer when queue hits 0 — prevents Play→Silence→Play grumble.
        if (_isBuffering)
        {
            if (queueCount < JITTER_BUFFER_START_THRESHOLD)
            {
                SendRtpFrame(_silenceFrame, false);
                return;
            }
            _isBuffering = false;
            SafeLog($"[RTP] 🔊 Buffer ready ({queueCount} frames), resuming playout");
        }

        if (_frameQueue.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            SendRtpFrame(frame, false);
            Interlocked.Increment(ref _framesSent);
            _wasPlaying = true;

            // Re-enter buffering the moment queue hits 0 (hysteresis gate)
            if (Volatile.Read(ref _queueCount) == 0)
            {
                _isBuffering = true;
                Interlocked.Increment(ref _totalUnderruns);
                try { OnQueueEmpty?.Invoke(); } catch { }
            }
        }
        else
        {
            // Emergency fallback
            _isBuffering = true;
            SendRtpFrame(_silenceFrame, false);
            Interlocked.Increment(ref _totalUnderruns);
            if (_wasPlaying)
            {
                _wasPlaying = false;
                try { OnQueueEmpty?.Invoke(); } catch { }
            }
        }

        if ((DateTime.UtcNow - _lastStatsLog).TotalSeconds >= STATS_LOG_INTERVAL_SEC)
        {
            var samples  = Interlocked.Exchange(ref _statsQueueSizeSamples, 0);
            var sizeSum  = Interlocked.Exchange(ref _statsQueueSizeSum, 0);
            var underruns = Interlocked.Read(ref _totalUnderruns);
            var enqueued  = Interlocked.Read(ref _totalFramesEnqueued);
            if (samples > 0)
                SafeLog($"[RTP] 📈 sent={_framesSent} enqueued={enqueued} " +
                        $"avgQueue={(double)sizeSum / samples:F1} underruns={underruns}");
            _lastStatsLog = DateTime.UtcNow;
        }
    }

    private void SendRtpFrame(byte[] frame, bool marker)
    {
        try
        {
            _mediaSession.SendRtpRaw(
                SDPMediaTypesEnum.audio, frame, _timestamp,
                marker ? 1 : 0, PAYLOAD_TYPE_PCMA);
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
                var msg = "[RTP] ❌ Circuit breaker — stopping after 100 consecutive send failures";
                SafeLog(msg);
                try { OnFault?.Invoke(msg); } catch { }
            }
        }
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
        _wasPlaying  = false;
    }

    // Alias kept for any callers using the method form
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
