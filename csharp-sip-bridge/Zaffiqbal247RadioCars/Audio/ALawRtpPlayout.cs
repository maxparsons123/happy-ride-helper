// Last updated: 2026-02-22 (ALawRtpPlayout v8.8 — ArrayPool + Ring Buffer + RTP Marker)
// Synced from AdaSdkModel/Audio/ALawRtpPlayout.cs

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Zaffiqbal247RadioCars.Audio;

public sealed class ALawRtpPlayout : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("winmm.dll")] private static extern uint TimeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint TimeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes, IntPtr lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer, ref long lpDueTime, int lPeriod,
        IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    private const int FRAME_MS = 20;
    private const int FRAME_SIZE = 160;
    private const byte ALAW_SILENCE = 0xD5;
    private const byte PAYLOAD_TYPE_PCMA = 8;

    private const int JITTER_BUFFER_START_THRESHOLD = 10;
    private const int MAX_QUEUE_FRAMES = 500;
    private const int UNDERRUN_GRACE_FRAMES = 5;
    private const int RING_BUFFER_SIZE = 65536;
    private const int STATS_LOG_INTERVAL_SEC = 30;

    private static readonly double TicksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private readonly byte[] _silenceFrame = new byte[FRAME_SIZE];
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;

    private readonly byte[] _ringBuffer = new byte[RING_BUFFER_SIZE];
    private int _ringHead;
    private int _ringTail;
    private readonly object _ringLock = new();

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
    private int _drainSignaled;
    private int _underrunGraceRemaining;
    private bool _markerPending;

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

    public void Flush()
    {
        lock (_ringLock)
        {
            int available = RingCount();
            if (available > 0 && available < FRAME_SIZE)
            {
                var frame = _pool.Rent(FRAME_SIZE);
                Array.Fill(frame, ALAW_SILENCE, 0, FRAME_SIZE);
                RingRead(frame, available);
                _frameQueue.Enqueue(frame);
                Interlocked.Increment(ref _queueCount);
            }
        }
    }

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
        try { SendRtpFrame(_silenceFrame, marker: false); } catch { }
    }

    private int RingCount()
        => _ringTail >= _ringHead ? _ringTail - _ringHead : RING_BUFFER_SIZE - _ringHead + _ringTail;

    private int RingFree() => RING_BUFFER_SIZE - 1 - RingCount();

    private void RingWrite(ReadOnlySpan<byte> data)
    {
        int remaining = data.Length;
        int src = 0;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, RING_BUFFER_SIZE - _ringTail);
            data.Slice(src, chunk).CopyTo(_ringBuffer.AsSpan(_ringTail, chunk));
            _ringTail = (_ringTail + chunk) % RING_BUFFER_SIZE;
            src += chunk;
            remaining -= chunk;
        }
    }

    private void RingRead(byte[] dest, int count)
    {
        int remaining = count;
        int dst = 0;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, RING_BUFFER_SIZE - _ringHead);
            Buffer.BlockCopy(_ringBuffer, _ringHead, dest, dst, chunk);
            _ringHead = (_ringHead + chunk) % RING_BUFFER_SIZE;
            dst += chunk;
            remaining -= chunk;
        }
    }

    private void RingDiscard(int count)
    {
        _ringHead = (_ringHead + count) % RING_BUFFER_SIZE;
    }

    public void BufferALaw(byte[] alawData)
    {
        if (Volatile.Read(ref _disposed) != 0 || alawData == null || alawData.Length == 0) return;

        lock (_ringLock)
        {
            int free = RingFree();
            int toWrite = alawData.Length;

            if (toWrite > free)
            {
                int drop = toWrite - free;
                SafeLog($"[BUFFER] overflow, dropping {drop} bytes");
                RingDiscard(drop);
            }

            RingWrite(alawData.AsSpan(0, Math.Min(toWrite, RingFree())));
            DrainRingToQueue();
        }
    }

    private void DrainRingToQueue()
    {
        while (RingCount() >= FRAME_SIZE)
        {
            while (Volatile.Read(ref _queueCount) >= MAX_QUEUE_FRAMES)
            {
                if (_frameQueue.TryDequeue(out var old))
                {
                    Interlocked.Decrement(ref _queueCount);
                    _pool.Return(old);
                }
                else break;
            }

            var frame = _pool.Rent(FRAME_SIZE);
            RingRead(frame, FRAME_SIZE);
            _frameQueue.Enqueue(frame);
            Interlocked.Increment(ref _queueCount);
            Interlocked.Increment(ref _totalFramesEnqueued);
        }
    }

    public void Start()
    {
        if (_running || Volatile.Read(ref _disposed) != 0) return;

        _running = true;
        _isBuffering = true;
        _framesSent = 0;
        _timestamp = (uint)Random.Shared.Next();
        _wasPlaying = false;
        _markerPending = true;
        Interlocked.Exchange(ref _drainSignaled, 0);
        _totalUnderruns = 0;
        _totalFramesEnqueued = 0;
        _statsQueueSizeSum = 0;
        _statsQueueSizeSamples = 0;
        _lastStatsLog = DateTime.UtcNow;

        if (IsWindows)
        {
            try
            {
                TimeBeginPeriod(1);
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
            Priority = ThreadPriority.AboveNormal,
            Name = "ALawPlayout-v8.8"
        };
        _playoutThread.Start();
        string timerType = _useWaitableTimer ? "WaitableTimer" : "Sleep+SpinWait";
        SafeLog($"[RTP] v8.8 started (pure A-law, {JITTER_BUFFER_START_THRESHOLD * FRAME_MS}ms hysteresis, " +
                $"MAX_QUEUE={MAX_QUEUE_FRAMES}, catch-up=40ms, ring={RING_BUFFER_SIZE / 1024}KB, " +
                $"pool=ArrayPool, marker=RFC3550, timer={timerType})");
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
                else
                    Thread.SpinWait(20);
                continue;
            }

            SendNextFrame();
            nextFrameNs += 20_000_000;

            long currentNs = (long)(sw.ElapsedTicks * TicksToNs);
            if (currentNs - nextFrameNs > 40_000_000)
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

        if (_isBuffering)
        {
            if (queueCount < JITTER_BUFFER_START_THRESHOLD)
            {
                SendRtpFrame(_silenceFrame, marker: false);
                return;
            }
            _isBuffering = false;
            _underrunGraceRemaining = 0;
            _markerPending = true;
            Interlocked.Exchange(ref _drainSignaled, 0);
            SafeLog($"[RTP] 🔊 Buffer ready ({queueCount} frames), resuming playout");
        }

        if (_frameQueue.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            SendRtpFrame(frame, marker: _markerPending);
            _markerPending = false;
            Interlocked.Increment(ref _framesSent);
            _wasPlaying = true;
            _underrunGraceRemaining = UNDERRUN_GRACE_FRAMES;
            _pool.Return(frame);
        }
        else if (_underrunGraceRemaining > 0)
        {
            _underrunGraceRemaining--;
            SendRtpFrame(_silenceFrame, marker: false);
            if (_underrunGraceRemaining == 0)
            {
                _isBuffering = true;
                _markerPending = true;
                Interlocked.Increment(ref _totalUnderruns);
                FireDrainOnce();
            }
        }
        else
        {
            _isBuffering = true;
            _markerPending = true;
            SendRtpFrame(_silenceFrame, marker: false);
            Interlocked.Increment(ref _totalUnderruns);
            FireDrainOnce();
        }

        if ((DateTime.UtcNow - _lastStatsLog).TotalSeconds >= STATS_LOG_INTERVAL_SEC)
        {
            var samples = Interlocked.Exchange(ref _statsQueueSizeSamples, 0);
            var sizeSum = Interlocked.Exchange(ref _statsQueueSizeSum, 0);
            var underruns = Interlocked.Read(ref _totalUnderruns);
            var enqueued = Interlocked.Read(ref _totalFramesEnqueued);
            if (samples > 0)
                SafeLog($"[RTP] 📈 sent={_framesSent} enqueued={enqueued} " +
                        $"avgQueue={(double)sizeSum / samples:F1} underruns={underruns}");
            _lastStatsLog = DateTime.UtcNow;
        }
    }

    private void FireDrainOnce()
    {
        if (!_wasPlaying) return;
        if (Interlocked.Exchange(ref _drainSignaled, 1) == 1) return;
        _wasPlaying = false;
        try { ThreadPool.UnsafeQueueUserWorkItem(_ => OnQueueEmpty?.Invoke(), null); } catch { }
    }

    private void SendRtpFrame(byte[] frame, bool marker)
    {
        try
        {
            _mediaSession.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, marker ? 1 : 0, PAYLOAD_TYPE_PCMA);
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
        while (_frameQueue.TryDequeue(out var old))
        {
            Interlocked.Decrement(ref _queueCount);
            _pool.Return(old);
        }
        lock (_ringLock) { _ringHead = 0; _ringTail = 0; }
        _isBuffering = true;
        _wasPlaying = false;
        _markerPending = true;
        _underrunGraceRemaining = 0;
        Interlocked.Exchange(ref _drainSignaled, 0);
    }

    public void Stop()
    {
        _running = false;
        try { _playoutThread?.Join(2000); } catch { }
        _playoutThread = null;
        if (IsWindows)
        {
            try { TimeEndPeriod(1); } catch { }
            if (_waitableTimer != IntPtr.Zero)
            {
                try { CloseHandle(_waitableTimer); } catch { }
                _waitableTimer = IntPtr.Zero;
                _useWaitableTimer = false;
            }
        }
        while (_frameQueue.TryDequeue(out var old)) _pool.Return(old);
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
