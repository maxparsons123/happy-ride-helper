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
/// Pure A-law RTP playout engine v11.0 — SMOOTH EDITION.
///
/// Key improvements over v10.2:
///   ✅ 200ms hysteresis start buffer (10 frames) — eliminates grumble/jitter
///   ✅ Rebuffer only when queue hits 0 — prevents false underruns
///   ✅ _wasPlaying guard — suppresses spurious OnQueueEmpty events
///   ✅ Drift clamping — resets clock if >100ms behind
///   ✅ Circuit breaker — fires OnFault after 10 consecutive send errors
///   ✅ 30s stats logging — avg queue depth + underrun count
///   ✅ NAT keepalive — sends silence if no RTP sent for 20s
///   ✅ Overflow protection — caps queue at 1500 frames
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

    private const int FRAME_SIZE = 160;
    private const byte ALAW_SILENCE = 0xD5;
    private const byte PAYLOAD_TYPE_PCMA = 8;

    private const int JITTER_BUFFER_START_THRESHOLD = 10; // 200ms hysteresis
    private const int MAX_QUEUE_FRAMES = 1500;
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
    private volatile int _started;
    private int _queueCount;
    private int _framesSent;
    private uint _timestamp;
    private IntPtr _waitableTimer;
    private bool _useWaitableTimer;

    private bool _wasPlaying;
    private int _consecutiveSendErrors;
    private DateTime _lastErrorLog;
    private DateTime _lastRtpSendTime = DateTime.UtcNow;

    private IPEndPoint? _lastRemoteEndpoint;
    private volatile bool _natBindingEstablished;

    private long _totalUnderruns;
    private long _totalFramesEnqueued;
    private long _statsQueueSizeSum;
    private long _statsQueueSizeSamples;
    private DateTime _lastStatsLog = DateTime.UtcNow;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;
    public event Action<string>? OnFault;

    public int QueuedFrames => Volatile.Read(ref _queueCount);
    public int FramesSent => Volatile.Read(ref _framesSent);
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
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

        _running = true;
        _isBuffering = true;
        _wasPlaying = false;
        _framesSent = 0;
        _timestamp = (uint)Random.Shared.Next();
        _totalUnderruns = 0;
        _totalFramesEnqueued = 0;
        _lastStatsLog = DateTime.UtcNow;

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
            Name = "ALawPlayout-v11.0"
        };
        _playoutThread.Start();
        SafeLog($"[RTP] v11.0 SMOOTH EDITION started (hysteresis={JITTER_BUFFER_START_THRESHOLD * 20}ms, " +
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

            // Drift clamping: snap forward if >100ms behind
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

        if (_isBuffering)
        {
            if (queueCount < JITTER_BUFFER_START_THRESHOLD)
            {
                SendRtpFrame(_silenceFrame);
                return;
            }
            _isBuffering = false;
            SafeLog($"[RTP] 🔊 Buffer ready ({queueCount} frames), resuming playout");
        }

        if (_frameQueue.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            SendRtpFrame(frame);
            Interlocked.Increment(ref _framesSent);
            _wasPlaying = true;

            if (Volatile.Read(ref _queueCount) == 0)
            {
                _isBuffering = true;
                Interlocked.Increment(ref _totalUnderruns);
                try { ThreadPool.UnsafeQueueUserWorkItem(_ => OnQueueEmpty?.Invoke(), null); } catch { }
            }
        }
        else
        {
            _isBuffering = true;
            SendRtpFrame(_silenceFrame);
            Interlocked.Increment(ref _totalUnderruns);

            if (_wasPlaying)
            {
                _wasPlaying = false;
                try { ThreadPool.UnsafeQueueUserWorkItem(_ => OnQueueEmpty?.Invoke(), null); } catch { }
            }
        }

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
                SafeLog($"[RTP] 🔴 Circuit breaker triggered after {_consecutiveSendErrors} errors");
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
        Volatile.Write(ref _started, 0);
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
