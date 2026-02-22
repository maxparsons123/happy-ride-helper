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
/// ALawRtpPlayout v11 — fixed 160-byte frames ONLY (no ArrayPool tail-noise bug).
/// Uses ConcurrentBag frame pool for exact-size buffer reuse (zero GC on hot path).
/// Absorbs OpenAI burst audio and outputs perfect 20ms RTP cadence.
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

    // Safer hysteresis for "premium" SIP: start after 200ms, rebuffer only when empty.
    private const int StartThresholdFrames = 10;  // 200ms

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private static readonly long TicksPerFrame = (long)(20_000_000.0 / NsPerTick);

    private readonly VoIPMediaSession _media;
    private readonly ConcurrentQueue<byte[]> _q = new();
    private readonly ConcurrentBag<byte[]> _framePool = new(); // exact 160-byte buffers only
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
    private IntPtr _timer;
    private bool _useTimer;

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

        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ALawRtpPlayout-v11" };
        _thread.Start();
        SafeLog("[RTP] Playout v11 started (ConcurrentBag frame pool, fixed 160-byte frames).");
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

        // Fast path: perfect 160-byte frame with empty accumulator → zero-copy enqueue
        if (alaw.Length == FrameSize && Volatile.Read(ref _accCount) == 0)
        {
            EnqueueFrame(alaw);
            return;
        }

        // Slow path: accumulate and split (handles non-aligned sizes)
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
            if (q < StartThresholdFrames)
            {
                Send(_silence);
                return;
            }
            _buffering = false;
        }

        if (_q.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            Send(frame);
            ReturnFrame(frame);

            if (Volatile.Read(ref _queueCount) == 0)
            {
                _buffering = true;
                try { OnQueueEmpty?.Invoke(); } catch { }
            }
        }
        else
        {
            _buffering = true;
            Send(_silence);
            try { OnQueueEmpty?.Invoke(); } catch { }
        }
    }

    private void Send(byte[] payload160)
    {
        // payload160 MUST be exactly 160 bytes — guaranteed by RentFrame().
        _media.SendRtpRaw(SDPMediaTypesEnum.audio, payload160, _ts, 0, PayloadTypePcma);
        _ts += FrameSize;
    }

    private void ExecuteClear()
    {
        while (_q.TryDequeue(out var f))
        {
            Interlocked.Decrement(ref _queueCount);
            ReturnFrame(f);
        }
        _buffering = true;
        Volatile.Write(ref _clearRequested, false);
    }

    private byte[] RentFrame() => _framePool.TryTake(out var f) ? f : new byte[FrameSize];

    private void ReturnFrame(byte[] f)
    {
        if (f.Length == FrameSize) _framePool.Add(f);
        // if some external code passed wrong sized arrays, we silently drop them
    }

    private void EnqueueFrame(byte[] frame160)
    {
        _q.Enqueue(frame160);
        Interlocked.Increment(ref _queueCount);
    }

    private void SafeLog(string msg) { try { OnLog?.Invoke(msg); } catch { }  }
}
