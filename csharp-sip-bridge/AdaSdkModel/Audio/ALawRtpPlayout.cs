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
/// Pure A-law RTP playout engine v12.0 — GRACE EDITION.
///
/// Key improvements over v11.0 (SMOOTH EDITION):
///   ✅ 60ms underrun grace window (3 frames) — absorbs inter-burst gaps from OpenAI without triggering rebuffer
///   ✅ Dual resume thresholds — 200ms cold start vs 100ms warm restart for mid-call gaps
///   ✅ OnQueueEmpty suppressed during grace window — prevents premature NotifyPlayoutComplete mid-sentence
///   ✅ _hasPlayedAtLeastOneFrame flag — distinguishes cold start from warm resume for threshold selection
///   ✅ _consecutiveUnderruns counter — only genuine 60ms+ stalls count as real underruns in stats
///
/// Retained from v11.0:
///   ✅ 200ms hysteresis start buffer (10 frames) — eliminates grumble on first word
///   ✅ Drift clamping — resets clock if >100ms behind instead of accumulating slip
///   ✅ Circuit breaker — fires OnFault after 10 consecutive send errors
///   ✅ 30s stats logging — avg queue depth + underrun count for diagnostics
///   ✅ NAT keepalive — sends silence if no RTP sent for 20s
///   ✅ Overflow protection — caps queue at 1500 frames (~30s)
/// </summary>
public sealed class ALawRtpPlayout : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr lpAttr, IntPtr lpName, uint dwFlags, uint dwAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod,
        IntPtr pfn, IntPtr lpArg, [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMs);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObj);

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    private const int FRAME_SIZE  = 160;         // 20ms @ 8kHz
    private const byte ALAW_SILENCE = 0xD5;      // ITU-T G.711 A-law silence
    private const byte PAYLOAD_TYPE_PCMA = 8;

    // ── Hysteresis calibration ──
    // 10 frames (200ms) cold start — eliminates first-word grumble
    // 5 frames (100ms) warm resume — faster restart after inter-burst gap
    // 3 frames (60ms) grace window — absorbs OpenAI inter-burst gaps without rebuffering
    private const int JITTER_BUFFER_START_THRESHOLD  = 10; // 200ms cold start
    private const int JITTER_BUFFER_RESUME_THRESHOLD = 5;  // 100ms warm restart
    private const int UNDERRUN_GRACE_FRAMES          = 3;  // 60ms gap absorption
    private const int MAX_QUEUE_FRAMES = 1500;             // ~30s safety cap
    private const int MAX_ACCUMULATOR_SIZE = 65536;
    private const int CIRCUIT_BREAKER_LIMIT = 10;
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
    private IntPtr _waitableTimer;
    private bool _useWaitableTimer;

    // Jitter/state tracking
    private bool _wasPlaying;
    private int _consecutiveSendErrors;
    private int _consecutiveUnderruns;      // Grace window counter
    private bool _hasPlayedAtLeastOneFrame; // Distinguishes cold start from warm resume
    private DateTime _lastErrorLog;
    private DateTime _lastRtpSendTime = DateTime.UtcNow;

    // NAT state
    private IPEndPoint? _lastRemoteEndpoint;
    private volatile bool _natBindingEstablished;

    // Stats
    private long _totalUnderruns;
    private long _totalFramesEnqueued;
    private long _statsQueueSizeSum;
    private long _statsQueueSizeSamples;
    private DateTime _lastStatsLog = DateTime.UtcNow;

    // Typing sound effect — plays during "thinking" pauses, only after Ada has spoken once
    private readonly TypingSoundGenerator _typingSound = new();
    private volatile bool _typingSoundsEnabled = true;
    private volatile bool _adaHasSpoken = false; // Gate: only click after first response

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;
    public event Action<string>? OnFault;

    public int QueuedFrames => Volatile.Read(ref _queueCount);
    public int FramesSent => _framesSent;
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    /// <summary>Enable/disable keyboard tapping sounds during thinking pauses.</summary>
    public bool TypingSoundsEnabled { get => _typingSoundsEnabled; set => _typingSoundsEnabled = value; }

    /// <summary>
    /// Call once after Ada's first response has been delivered.
    /// Typing sounds will not play until this is called.
    /// </summary>
    public void NotifyAdaHasSpoken() => _adaHasSpoken = true;

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
        try { SendRtpFrame(_silenceFrame); } catch { }
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

        _running = true;
        _isBuffering = true;
        _wasPlaying = false;
        _framesSent = 0;
        _timestamp = (uint)Random.Shared.Next();
        _totalUnderruns = 0;
        _totalFramesEnqueued = 0;
        _lastStatsLog = DateTime.UtcNow;
        _consecutiveUnderruns = 0;
        _hasPlayedAtLeastOneFrame = false;
        _typingSound.Reset();

        if (IsWindows)
        {
            try { TimeBeginPeriod(1); } catch { }
            try
            {
                _waitableTimer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
                    CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
                _useWaitableTimer = _waitableTimer != IntPtr.Zero;
                if (_useWaitableTimer) SafeLog("[RTP] ⚡ High-resolution waitable timer active");
            }
            catch { }
        }

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "ALawPlayout-v12.0"
        };
        _playoutThread.Start();
        SafeLog($"[RTP] v12.0 GRACE EDITION started (cold={JITTER_BUFFER_START_THRESHOLD * 20}ms, " +
                $"resume={JITTER_BUFFER_RESUME_THRESHOLD * 20}ms, grace={UNDERRUN_GRACE_FRAMES * 20}ms, " +
                $"timer={(_useWaitableTimer ? "WaitableTimer" : "Sleep+SpinWait")})");
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
                {
                    long dueTime = -(waitNs / 100);
                    if (dueTime >= 0) dueTime = -1;
                    if (SetWaitableTimer(_waitableTimer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                        WaitForSingleObject(_waitableTimer, 100);
                }
                else if (waitNs > 2_000_000)
                {
                    Thread.Sleep((int)(waitNs / 1_000_000) - 1);
                }
                else if (waitNs > 100_000)
                {
                    Thread.SpinWait(20);
                }

                continue;
            }

            SendNextFrame();

            nextFrameNs += 20_000_000;

            // Drift clamping: snap forward if we fall >100ms behind
            long currentNs = (long)(sw.ElapsedTicks * TicksToNs);
            if (currentNs - nextFrameNs > 100_000_000)
                nextFrameNs = currentNs + 20_000_000;
        }
    }

    private void SendNextFrame()
    {
        int queueCount = Volatile.Read(ref _queueCount);

        Interlocked.Add(ref _statsQueueSizeSum, queueCount);
        Interlocked.Increment(ref _statsQueueSizeSamples);

        // ── HYSTERESIS: wait for buffer before starting/resuming ──
        // Cold start: 200ms (10 frames) — prevents first-word grumble
        // Warm resume: 100ms (5 frames) — faster restart after inter-burst gap
        if (_isBuffering)
        {
            int resumeThreshold = _hasPlayedAtLeastOneFrame
                ? JITTER_BUFFER_RESUME_THRESHOLD
                : JITTER_BUFFER_START_THRESHOLD;

            if (queueCount < resumeThreshold)
            {
                var fillFrame = _typingSoundsEnabled && _adaHasSpoken ? _typingSound.NextFrame() : _silenceFrame;
                SendRtpFrame(fillFrame);
                return;
            }

            _isBuffering = false;
            _consecutiveUnderruns = 0;
            _typingSound.Reset();
            SafeLog($"[RTP] 🔊 Buffer ready ({queueCount} frames), resuming playout " +
                    $"({(_hasPlayedAtLeastOneFrame ? "warm" : "cold")} start)");
        }

        if (_frameQueue.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            SendRtpFrame(frame);
            Interlocked.Increment(ref _framesSent);
            _wasPlaying = true;
            _hasPlayedAtLeastOneFrame = true;
            _consecutiveUnderruns = 0; // Reset grace window on successful dequeue

            // Queue drained — enter grace window, don't rebuffer yet
            if (Volatile.Read(ref _queueCount) == 0)
                _consecutiveUnderruns = 1; // Mark first empty tick
        }
        else
        {
            // Queue empty — apply 60ms grace window before rebuffering
            _consecutiveUnderruns++;
            SendRtpFrame(_silenceFrame); // Always fill the RTP slot

            if (_consecutiveUnderruns >= UNDERRUN_GRACE_FRAMES)
            {
                // Genuine stall: sustained 60ms+ gap — trigger rebuffer
                if (!_isBuffering)
                {
                    _isBuffering = true;
                    _typingSound.Reset();
                    Interlocked.Increment(ref _totalUnderruns);
                    SafeLog($"[RTP] ⚠ Genuine underrun after {_consecutiveUnderruns} frames — rebuffering");

                    if (_wasPlaying)
                    {
                        _wasPlaying = false;
                        try { ThreadPool.UnsafeQueueUserWorkItem(_ => OnQueueEmpty?.Invoke(), null); } catch { }
                    }
                }
            }
            // else: absorbing inter-burst gap silently (within grace window)
        }

        // Periodic stats
        if ((DateTime.UtcNow - _lastStatsLog).TotalSeconds >= STATS_LOG_INTERVAL_SEC)
        {
            var samples = Interlocked.Exchange(ref _statsQueueSizeSamples, 0);
            var sizeSum  = Interlocked.Exchange(ref _statsQueueSizeSum, 0);
            if (samples > 0)
            {
                SafeLog($"[RTP] 📈 sent={_framesSent} enqueued={_totalFramesEnqueued} " +
                        $"avgQueue={(double)sizeSum / samples:F1} underruns={_totalUnderruns}");
            }
            _lastStatsLog = DateTime.UtcNow;
        }
    }

    private void SendRtpFrame(byte[] frame)
    {
        try
        {
            _mediaSession.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, 0, PAYLOAD_TYPE_PCMA);
            _timestamp += FRAME_SIZE;
            _lastRtpSendTime = DateTime.UtcNow;
            _consecutiveSendErrors = 0;
        }
        catch (Exception ex)
        {
            _consecutiveSendErrors++;
            if (_consecutiveSendErrors <= 3 || (DateTime.UtcNow - _lastErrorLog).TotalSeconds > 5)
            {
                SafeLog($"[RTP] ⚠️ Send error #{_consecutiveSendErrors}: {ex.Message}");
                _lastErrorLog = DateTime.UtcNow;
            }

            if (_consecutiveSendErrors >= CIRCUIT_BREAKER_LIMIT)
            {
                SafeLog($"[RTP] 🔴 Circuit breaker: {_consecutiveSendErrors} consecutive errors — halting");
                _running = false;
                try { OnFault?.Invoke("RTP circuit breaker triggered"); } catch { }
            }
        }
    }

    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _)) Interlocked.Decrement(ref _queueCount);
        lock (_accLock) { _accCount = 0; Array.Clear(_accumulator, 0, _accumulator.Length); }
        _isBuffering = true;
        _wasPlaying = false;
        _consecutiveUnderruns = 0;
        _typingSound.Reset();
    }

    public void Stop()
    {
        _running = false;
        try { _playoutThread?.Join(2000); } catch { }
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

    private void SafeLog(string msg) { try { OnLog?.Invoke(msg); } catch { } }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Stop();
        _natKeepaliveTimer?.Dispose();
        _natKeepaliveTimer = null;
        _mediaSession.OnRtpPacketReceived -= HandleSymmetricRtp;
        Clear();
    }
}
