using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace AdaSdkModel.Audio;

/// <summary>
/// ALawRtpPlayout v11.1 — fixes over v11:
///   1. ConcurrentBag→capped ConcurrentQueue pool (cross-thread friendly, bounded)
///   2. Send() error handling with circuit breaker
///   3. OnQueueEmpty only fires after audio has actually played (not during initial buffering)
///   4. Split cold-start vs mid-stream resume thresholds
///   5. Clear() drains queue synchronously at top of loop (no stale-frame tick)
/// </summary>
public sealed class ALawRtpPlayout : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr lpAttr, IntPtr lpName, uint dwFlags, uint dwAccess);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfn, IntPtr lpArg, bool fResume);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMs);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObj);

    private const int FrameSize = 160;         // 20ms @ 8kHz
    private const int PayloadTypePcma = 8;
    private const byte AlawSilence = 0xD5;

    // Fix #4: split thresholds — cold start needs deeper buffer, resume is immediate
    private const int ColdStartThresholdFrames = 10;  // 200ms for initial buffering
    private const int ResumeThresholdFrames = 1;       // 20ms for mid-stream resume

    // Fix #1: pool cap to prevent unbounded memory growth after bursts
    private const int MaxPoolSize = 200;

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private static readonly long TicksPerFrame = (long)(20_000_000.0 / NsPerTick);

    private readonly VoIPMediaSession _media;
    private readonly ConcurrentQueue<byte[]> _q = new();

    // Fix #1: ConcurrentQueue pool instead of ConcurrentBag — better cross-thread perf
    private readonly ConcurrentQueue<byte[]> _framePool = new();
    private volatile int _poolCount;

    private readonly byte[] _silence = new byte[FrameSize];

    private byte[] _acc = new byte[8192];
    private int _accCount;
    private readonly object _accLock = new();

    private Thread? _thread;
    private volatile bool _running;
    private volatile int _queueCount;
    private volatile bool _clearRequested;
    private uint _ts;
    private bool _buffering = true;
    private bool _hasPlayedAudio;  // Fix #3: tracks whether we've ever played real audio
    private IntPtr _timer;
    private bool _useTimer;

    // Fix #2: circuit breaker for dead sessions
    private int _sendErrorCount;
    private const int MaxSendErrors = 10;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public int QueuedFrames => Volatile.Read(ref _queueCount);

    public ALawRtpPlayout(VoIPMediaSession mediaSession)
    {
        _media = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _media.AcceptRtpFromAny = true;
        Array.Fill(_silence, AlawSilence);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _buffering = true;
        _hasPlayedAudio = false;
        _sendErrorCount = 0;
        _ts = (uint)Random.Shared.Next();

        if (IsWindows)
        {
            try { timeBeginPeriod(1); } catch { }
            try
            {
                _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0x00000002, 0x1F0003);
                _useTimer = _timer != IntPtr.Zero;
            }
            catch { _useTimer = false; }
        }

        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ALawRtpPlayout-v11.1" };
        _thread.Start();
        SafeLog("[RTP] Playout v11.1 started.");
    }

    public void Stop()
    {
        _running = false;
        try { _thread?.Join(500); } catch { }
        _thread = null;

        if (IsWindows)
        {
            try { timeEndPeriod(1); } catch { }
            var t = _timer;
            _timer = IntPtr.Zero;
            if (t != IntPtr.Zero) { try { CloseHandle(t); } catch { } }
        }

        while (_q.TryDequeue(out var f))
        {
            Interlocked.Decrement(ref _queueCount);
            ReturnFrame(f);
        }
    }

    public void Dispose() => Stop();

    public void Clear()
    {
        // Fix #5: signal clear — ExecuteClear runs synchronously at top of next loop tick
        // so no stale frames can play after this returns
        Volatile.Write(ref _clearRequested, true);
        lock (_accLock) _accCount = 0;
    }

    public void Flush()
    {
        lock (_accLock)
        {
            if (_accCount <= 0) return;
            var frame = RentFrame();
            Array.Fill(frame, AlawSilence);
            Buffer.BlockCopy(_acc, 0, frame, 0, _accCount);
            EnqueueFrame(frame);
            _accCount = 0;
        }
    }

    public void BufferALaw(byte[] alaw)
    {
        if (alaw == null || alaw.Length == 0) return;

        lock (_accLock)
        {
            int needed = _accCount + alaw.Length;
            if (needed > _acc.Length)
            {
                int newSize = Math.Min(Math.Max(_acc.Length * 2, needed), 65536);
                Array.Resize(ref _acc, newSize);
            }

            Buffer.BlockCopy(alaw, 0, _acc, _accCount, alaw.Length);
            _accCount += alaw.Length;

            while (_accCount >= FrameSize)
            {
                var frame = RentFrame();
                Buffer.BlockCopy(_acc, 0, frame, 0, FrameSize);
                EnqueueFrame(frame);
                _accCount -= FrameSize;
                if (_accCount > 0) Buffer.BlockCopy(_acc, FrameSize, _acc, 0, _accCount);
            }
        }
    }

    private void Loop()
    {
        long nextTick = Stopwatch.GetTimestamp();

        while (_running)
        {
            // Fix #5: drain queue synchronously before any tick — ensures no stale audio after barge-in
            if (Volatile.Read(ref _clearRequested))
                ExecuteClear();

            long now = Stopwatch.GetTimestamp();
            long wait = nextTick - now;

            if (wait > 0)
            {
                long waitNs = (long)(wait * NsPerTick);
                if (_useTimer && waitNs > 1_000_000)
                {
                    long due = -(waitNs / 100);
                    if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                        WaitForSingleObject(_timer, 100);
                }
                else if (waitNs > 2_000_000)
                {
                    Thread.Sleep((int)(waitNs / 1_000_000) - 1);
                }
                else
                {
                    Thread.Yield();
                }
                continue;
            }

            TickOnce();
            nextTick += TicksPerFrame;

            // if we drift badly, snap forward
            long after = Stopwatch.GetTimestamp();
            if ((after - nextTick) * NsPerTick > 100_000_000)
                nextTick = after + TicksPerFrame;
        }
    }

    private void TickOnce()
    {
        int q = Volatile.Read(ref _queueCount);

        if (_buffering)
        {
            // Fix #4: use deeper threshold on cold start, shallow on mid-stream resume
            int threshold = _hasPlayedAudio ? ResumeThresholdFrames : ColdStartThresholdFrames;
            if (q < threshold)
            {
                Send(_silence);
                return;
            }
            _buffering = false;
            _hasPlayedAudio = true;
        }

        if (_q.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            Send(frame);
            ReturnFrame(frame);

            if (Volatile.Read(ref _queueCount) == 0)
            {
                _buffering = true;
                // Fix #3: only fire OnQueueEmpty after real audio has played
                try { OnQueueEmpty?.Invoke(); } catch { }
            }
        }
        else
        {
            _buffering = true;
            Send(_silence);
            // Fix #3: only fire if we've played real audio — prevents spam during startup fill
            if (_hasPlayedAudio)
            {
                try { OnQueueEmpty?.Invoke(); } catch { }
            }
        }
    }

    private void Send(byte[] payload160)
    {
        // Fix #2: catch dead session / network errors, circuit-break after repeated failures
        if (_sendErrorCount >= MaxSendErrors) return;

        try
        {
            _media.SendRtpRaw(SDPMediaTypesEnum.audio, payload160, _ts, 0, PayloadTypePcma);
            _ts += FrameSize;
            _sendErrorCount = 0; // reset on success
        }
        catch (Exception ex)
        {
            _ts += FrameSize; // keep timestamp advancing even on error
            if (Interlocked.Increment(ref _sendErrorCount) == MaxSendErrors)
            {
                SafeLog($"[RTP] Send circuit-breaker tripped after {MaxSendErrors} errors: {ex.Message}");
            }
        }
    }

    private void ExecuteClear()
    {
        // Fix #5: synchronous drain — runs at top of loop before any TickOnce
        while (_q.TryDequeue(out var f))
        {
            Interlocked.Decrement(ref _queueCount);
            ReturnFrame(f);
        }
        _buffering = true;
        _hasPlayedAudio = false; // reset so cold-start threshold applies on next speech
        Volatile.Write(ref _clearRequested, false);
    }

    // Fix #1: ConcurrentQueue pool with size cap
    private byte[] RentFrame()
    {
        if (_framePool.TryDequeue(out var f))
        {
            Interlocked.Decrement(ref _poolCount);
            return f;
        }
        return new byte[FrameSize];
    }

    private void ReturnFrame(byte[] f)
    {
        if (f.Length != FrameSize) return; // drop wrong-sized arrays
        if (Volatile.Read(ref _poolCount) >= MaxPoolSize) return; // cap reached, let GC collect
        _framePool.Enqueue(f);
        Interlocked.Increment(ref _poolCount);
    }

    private void EnqueueFrame(byte[] frame160)
    {
        _q.Enqueue(frame160);
        Interlocked.Increment(ref _queueCount);
    }

    private void SafeLog(string msg) { try { OnLog?.Invoke(msg); } catch { } }
}
