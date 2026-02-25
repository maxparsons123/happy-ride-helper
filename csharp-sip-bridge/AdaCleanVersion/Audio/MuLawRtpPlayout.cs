using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Net;

namespace AdaCleanVersion.Audio;

/// <summary>
/// µ-law RTP playout engine v11.2 — WaitableTimer-based jitter buffer for PCMU (payload type 0).
///
/// Features:
///   - High-precision 20ms tick via Windows WaitableTimer (falls back to Thread.Sleep on Linux)
///   - ConcurrentQueue frame pool with bounded size to prevent memory growth
///   - Split cold-start (80ms) vs mid-stream resume (100ms) thresholds
///   - Circuit breaker: stops sending after 10 consecutive RTP failures
///   - Hard-cut barge-in via _clearRequested flag (synchronous drain at top of loop)
///   - Typing sound fill during buffering pauses
///   - Epoch guard: Clear() increments _clearEpoch; frames stamped with stale epoch are dropped
/// </summary>
public sealed class MuLawRtpPlayout : IDisposable
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

    private const int FrameSize = 160;          // 20ms @ 8kHz
    private const int PayloadTypePcmu = 0;
    private const byte MuLawSilence = 0xFF;     // µ-law silence (zero amplitude)

    private const int ColdStartThresholdFrames = 4;   // 80ms — fast greeting start
    private const int ResumeThresholdFrames = 5;       // 100ms for mid-stream resume
    private const int MaxPoolSize = 200;
    private const int MaxSendErrors = 10;

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private static readonly long TicksPerFrame = (long)(20_000_000.0 / NsPerTick);

    private readonly RTPSession _rtpSession;
    private readonly ConcurrentQueue<(byte[] data, int epoch)> _q = new();

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
    private volatile int _clearEpoch;
    private uint _ts;
    private bool _buffering = true;
    private bool _hasPlayedAudio;
    private IntPtr _timer;
    private bool _useTimer;

    private int _sendErrorCount;

    private readonly TypingSoundGenerator _typingSound = new();
    private volatile bool _typingSoundsEnabled = true;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public int QueuedFrames => Volatile.Read(ref _queueCount);
    public bool TypingSoundsEnabled { get => _typingSoundsEnabled; set => _typingSoundsEnabled = value; }

    public MuLawRtpPlayout(RTPSession rtpSession)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
        Array.Fill(_silence, MuLawSilence);
    }

    // ─── Public API ─────────────────────────────────────────

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

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "MuLawRtpPlayout-v11.2"
        };
        _thread.Start();
        SafeLog("[RTP] MuLawRtpPlayout v11.2 started");
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

        while (_q.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _queueCount);
            ReturnFrame(item.data);
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Hard-cut barge-in: increments epoch and signals synchronous drain.
    /// </summary>
    public void Clear()
    {
        Interlocked.Increment(ref _clearEpoch);
        Volatile.Write(ref _clearRequested, true);
        lock (_accLock) _accCount = 0;
        _typingSound.Reset();
    }

    public void Flush()
    {
        lock (_accLock)
        {
            if (_accCount <= 0) return;
            var frame = RentFrame();
            Array.Fill(frame, MuLawSilence);
            Buffer.BlockCopy(_acc, 0, frame, 0, _accCount);
            EnqueueFrame(frame);
            _accCount = 0;
        }
    }

    /// <summary>
    /// Buffer µ-law encoded audio data. Automatically frames into 160-byte chunks.
    /// </summary>
    public void BufferMuLaw(byte[] mulawData)
    {
        if (mulawData == null || mulawData.Length == 0) return;

        var epoch = Volatile.Read(ref _clearEpoch);

        lock (_accLock)
        {
            int needed = _accCount + mulawData.Length;
            if (needed > _acc.Length)
            {
                int newSize = Math.Min(Math.Max(_acc.Length * 2, needed), 65536);
                Array.Resize(ref _acc, newSize);
            }

            Buffer.BlockCopy(mulawData, 0, _acc, _accCount, mulawData.Length);
            _accCount += mulawData.Length;

            while (_accCount >= FrameSize)
            {
                var frame = RentFrame();
                Buffer.BlockCopy(_acc, 0, frame, 0, FrameSize);
                _q.Enqueue((frame, epoch));
                Interlocked.Increment(ref _queueCount);

                _accCount -= FrameSize;
                if (_accCount > 0) Buffer.BlockCopy(_acc, FrameSize, _acc, 0, _accCount);
            }
        }
    }

    // ─── Playout Loop ───────────────────────────────────────

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

            // Drift guard: snap forward if we're >100ms behind
            long after = Stopwatch.GetTimestamp();
            if ((after - nextTick) * NsPerTick > 100_000_000)
                nextTick = after + TicksPerFrame;
        }
    }

    private void TickOnce()
    {
        int q = Volatile.Read(ref _queueCount);
        int currentEpoch = Volatile.Read(ref _clearEpoch);

        if (_buffering)
        {
            int threshold = _hasPlayedAudio ? ResumeThresholdFrames : ColdStartThresholdFrames;
            if (q < threshold)
            {
                var fillFrame = _typingSoundsEnabled && !_hasPlayedAudio
                    ? _typingSound.NextFrame()
                    : _silence;
                Send(fillFrame);
                return;
            }
            _buffering = false;
            _hasPlayedAudio = true;
        }

        if (_q.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _queueCount);

            // Epoch guard: drop stale frames from before barge-in
            if (item.epoch != currentEpoch)
            {
                ReturnFrame(item.data);
                // Try next frame immediately
                if (_q.TryDequeue(out var next))
                {
                    Interlocked.Decrement(ref _queueCount);
                    if (next.epoch == currentEpoch)
                    {
                        Send(next.data);
                        ReturnFrame(next.data);
                    }
                    else
                    {
                        ReturnFrame(next.data);
                        Send(_silence);
                    }
                }
                else
                {
                    _buffering = true;
                    Send(_silence);
                }
                return;
            }

            Send(item.data);
            ReturnFrame(item.data);

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
            if (_hasPlayedAudio)
            {
                try { OnQueueEmpty?.Invoke(); } catch { }
            }
        }
    }

    private void Send(byte[] payload160)
    {
        if (_sendErrorCount >= MaxSendErrors) return;

        try
        {
            _rtpSession.SendAudioFrame(_ts, PayloadTypePcmu, payload160);
            _ts += FrameSize;
            _sendErrorCount = 0;
        }
        catch (Exception ex)
        {
            _ts += FrameSize;
            if (Interlocked.Increment(ref _sendErrorCount) == MaxSendErrors)
            {
                SafeLog($"[RTP] Circuit breaker tripped after {MaxSendErrors} errors: {ex.Message}");
            }
        }
    }

    private void ExecuteClear()
    {
        while (_q.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _queueCount);
            ReturnFrame(item.data);
        }
        _buffering = true;
        _hasPlayedAudio = false;
        Volatile.Write(ref _clearRequested, false);
    }

    // ─── Frame Pool ─────────────────────────────────────────

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
        if (f.Length != FrameSize) return;
        if (Volatile.Read(ref _poolCount) >= MaxPoolSize) return;
        _framePool.Enqueue(f);
        Interlocked.Increment(ref _poolCount);
    }

    private void EnqueueFrame(byte[] frame160)
    {
        var epoch = Volatile.Read(ref _clearEpoch);
        _q.Enqueue((frame160, epoch));
        Interlocked.Increment(ref _queueCount);
    }

    private void SafeLog(string msg) { try { OnLog?.Invoke(msg); } catch { } }
}
