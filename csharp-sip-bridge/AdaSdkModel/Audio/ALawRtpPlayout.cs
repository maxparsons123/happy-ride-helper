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
/// ALawRtpPlayout v9.0 — Consolidated engine.
/// Combines v9.0 clean structure + v7.4 proactive re-buffering + v8.x hardening.
///
/// ✅ 200ms hysteresis start (10 frames) — stable first playout
/// ✅ Proactive re-buffer at ≤2 frames — catches gaps BEFORE queue empties
/// ✅ Win32 Waitable Timer for sub-ms 20ms precision
/// ✅ Symmetric RTP / NAT punch-through
/// ✅ Accumulator emergency flush (prevents unbounded growth)
/// ✅ Circuit breaker (50 consecutive send failures)
/// ✅ ThreadPool-offloaded event handlers (protect timing loop)
/// ✅ Drift correction (snap if >100ms behind)
/// ✅ Clear() for barge-in
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
        IntPtr lpAttrs, IntPtr lpName, uint dwFlags, uint dwAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer, ref long lpDueTime, int lPeriod,
        IntPtr pfn, IntPtr lpArg,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    // ── Audio Constants ──
    private const int FRAME_SIZE = 160;          // 20ms @ 8kHz A-law
    private const byte ALAW_SILENCE = 0xD5;      // ITU-T G.711 A-law silence
    private const byte PAYLOAD_TYPE_PCMA = 8;

    // ── Buffer Calibration ──
    private const int START_THRESHOLD = 6;        // 120ms to start playout (was 200ms)
    private const int REBUFFER_THRESHOLD = 2;     // proactive re-buffer before queue empties
    private const int MAX_QUEUE_FRAMES = 1500;    // ~30s safety cap
    private const int MAX_LATENCY_FRAMES = 15;    // 300ms max playout latency — trim excess (was 500ms)
    private const int MAX_ACCUMULATOR_SIZE = 65536;

    private static readonly double TicksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    // ── State ──
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
    private bool _wasPlaying;
    private int _consecutiveSendErrors;

    private IntPtr _waitableTimer;
    private bool _useWaitableTimer;
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
    private const int STATS_LOG_INTERVAL_SEC = 30;

    // ── Events ──
    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;
    public event Action<string>? OnFault;

    public int QueuedFrames => Volatile.Read(ref _queueCount);
    public int FramesSent => _framesSent;
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    public ALawRtpPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _mediaSession.AcceptRtpFromAny = true;
        _mediaSession.OnRtpPacketReceived += HandleSymmetricRtp;

        Array.Fill(_silenceFrame, ALAW_SILENCE);

        _natKeepaliveTimer = new System.Threading.Timer(KeepaliveNAT, null,
            TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(25));
    }

    // ── NAT Handling ──

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
                Log($"[NAT] ✓ RTP locked to {ep}");
            }
            catch { }
        }
    }

    private void KeepaliveNAT(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_natBindingEstablished || (DateTime.UtcNow - _lastRtpSendTime).TotalSeconds < 20) return;
        try { SendRtp(_silenceFrame); } catch { }
    }

    // ── Accumulator ──

    public void BufferALaw(byte[] alawData)
    {
        if (Volatile.Read(ref _disposed) != 0 || alawData == null || alawData.Length == 0) return;

        lock (_accLock)
        {
            // Emergency flush on overflow (v9.0: simpler than partial drain)
            if (_accCount + alawData.Length > MAX_ACCUMULATOR_SIZE)
            {
                _accCount = 0;
                Array.Clear(_accumulator, 0, _accumulator.Length);
            }

            // Grow if needed
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
                if (Volatile.Read(ref _queueCount) < MAX_QUEUE_FRAMES)
                {
                    var frame = new byte[FRAME_SIZE];
                    Buffer.BlockCopy(_accumulator, 0, frame, 0, FRAME_SIZE);
                    _frameQueue.Enqueue(frame);
                    Interlocked.Increment(ref _queueCount);
                    Interlocked.Increment(ref _totalFramesEnqueued);
                }

                _accCount -= FRAME_SIZE;
                if (_accCount > 0)
                    Buffer.BlockCopy(_accumulator, FRAME_SIZE, _accumulator, 0, _accCount);
            }
        }
    }

    // ── Lifecycle ──

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _running) return;

        _running = true;
        _isBuffering = true;
        _framesSent = 0;
        _timestamp = (uint)Random.Shared.Next();
        _totalUnderruns = 0;
        _totalFramesEnqueued = 0;
        _statsQueueSizeSum = 0;
        _statsQueueSizeSamples = 0;
        _lastStatsLog = DateTime.UtcNow;

        if (IsWindows)
        {
            try { TimeBeginPeriod(1); } catch { }

            try
            {
                _waitableTimer = CreateWaitableTimerExW(
                    IntPtr.Zero, IntPtr.Zero,
                    CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                    TIMER_ALL_ACCESS);
                _useWaitableTimer = _waitableTimer != IntPtr.Zero;
                if (_useWaitableTimer)
                    Log("[RTP] ⚡ High-resolution waitable timer active");
            }
            catch { _useWaitableTimer = false; }
        }

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "ALawPlayout-v9.0"
        };
        _playoutThread.Start();

        Log($"[RTP] v9.0 Started ({START_THRESHOLD * 20}ms start, " +
            $"rebuffer≤{REBUFFER_THRESHOLD}, timer={(_useWaitableTimer ? "WaitableTimer" : "Sleep+Spin")})");
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

    // ── Timing Loop ──

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
                    WaitHighRes(waitNs);
                else if (waitNs > 2_000_000)
                    Thread.Sleep((int)(waitNs / 1_000_000) - 1);
                else if (waitNs > 100_000)
                    Thread.SpinWait(20);
                continue;
            }

            SendNextFrame();

            nextFrameNs += 20_000_000; // 20ms

            // Drift correction: snap if >100ms behind
            long currentNs = (long)(sw.ElapsedTicks * TicksToNs);
            if (currentNs - nextFrameNs > 100_000_000)
                nextFrameNs = currentNs + 20_000_000;
        }
    }

    private void WaitHighRes(long waitNs)
    {
        long dueTime = -(waitNs / 100);
        if (dueTime >= 0) dueTime = -1;
        if (SetWaitableTimer(_waitableTimer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(_waitableTimer, 100);
    }

    // ── Frame Dispatch ──

    private void SendNextFrame()
    {
        int count = Volatile.Read(ref _queueCount);

        // Stats tracking (lightweight)
        Interlocked.Add(ref _statsQueueSizeSum, count);
        Interlocked.Increment(ref _statsQueueSizeSamples);

        // Latency trim: discard oldest frames if queue exceeds 500ms
        if (count > MAX_LATENCY_FRAMES)
        {
            int toDrop = count - MAX_LATENCY_FRAMES;
            int dropped = 0;
            while (dropped < toDrop && _frameQueue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queueCount);
                dropped++;
            }
            count = Volatile.Read(ref _queueCount);
        }

        // Proactive re-buffer: catch low queue BEFORE it empties
        if (!_isBuffering && count <= REBUFFER_THRESHOLD && count > 0)
            _isBuffering = true;

        // Hysteresis: wait for full buffer before starting
        if (_isBuffering)
        {
            if (count < START_THRESHOLD) { SendRtp(_silenceFrame); return; }
            _isBuffering = false;
            Log($"[RTP] 🔊 Buffer ready ({count} frames), resuming playout");
        }

        if (_frameQueue.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            SendRtp(frame);
            Interlocked.Increment(ref _framesSent);
            _wasPlaying = true;
        }
        else
        {
            SendRtp(_silenceFrame);
            Interlocked.Increment(ref _totalUnderruns);

            if (_wasPlaying)
            {
                _wasPlaying = false;
                _isBuffering = true;
                ThreadPool.QueueUserWorkItem(_ => { try { OnQueueEmpty?.Invoke(); } catch { } });
            }
        }

        // Periodic stats (every 30s)
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
        if (samples > 0)
        {
            var avg = (double)sizeSum / samples;
            Log($"[RTP] 📈 Stats: sent={_framesSent} enqueued={Interlocked.Read(ref _totalFramesEnqueued)} " +
                $"avgQueue={avg:F1} underruns={Interlocked.Read(ref _totalUnderruns)}");
        }
    }

    private void SendRtp(byte[] frame)
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
            if (++_consecutiveSendErrors > 50)
            {
                _running = false;
                var msg = $"[RTP] ❌ Circuit breaker — 50 consecutive send failures: {ex.Message}";
                Log(msg);
                ThreadPool.QueueUserWorkItem(_ => { try { OnFault?.Invoke(msg); } catch { } });
            }
        }
    }

    private void Log(string m) { try { OnLog?.Invoke(m); } catch { } }

    public int GetQueuedFrames() => Volatile.Read(ref _queueCount);

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
