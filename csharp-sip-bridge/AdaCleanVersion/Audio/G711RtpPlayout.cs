using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Net;

namespace AdaCleanVersion.Audio;

/// <summary>
/// G.711 RTP playout engine v12.1 — codec-agnostic (supports PCMU and PCMA).
///
/// v12.1 improvements over v12.0:
///   - ArrayPool&lt;byte&gt; replaces custom ConcurrentQueue frame pool (flat memory under 50+ calls)
///   - ManualResetEventSlim for zero-latency barge-in wake (no waiting for sleep/timer to expire)
///   - Multi-frame stale epoch drain (handles OpenAI bursts during barge-in)
///   - Adaptive jitter buffer (EWMA-based, 100-200ms dynamic resume threshold)
///   - Post-barge-in mid-call awareness preserved (no cold-start regression)
///
/// Retained from v12.0:
///   - High-precision 20ms tick via Windows WaitableTimer (Thread.Sleep fallback on Linux)
///   - Circuit breaker: stops sending after 10 consecutive RTP failures
///   - Epoch guard: Clear() increments _clearEpoch; stale frames are dropped
///   - Typing sound fill during buffering pauses
/// </summary>
public sealed class G711RtpPlayout : IDisposable
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

    private const int ColdStartThresholdFrames = 4;   // 80ms — fast greeting start
    private const int MinResumeThresholdFrames = 5;    // 100ms minimum for mid-stream resume
    private const int MaxResumeThresholdFrames = 10;   // 200ms maximum adaptive ceiling
    private const int MaxSendErrors = 10;

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private static readonly long TicksPerFrame = (long)(20_000_000.0 / NsPerTick);

    private readonly RTPSession _rtpSession;
    private readonly G711CodecType _codec;
    private readonly int _payloadType;
    private readonly byte _silenceByte;

    private readonly ConcurrentQueue<(byte[] data, int epoch)> _q = new();
    private readonly byte[] _silence = new byte[FrameSize];

    private byte[] _acc = new byte[8192];
    private int _accCount;
    private readonly object _accLock = new();

    private Thread? _thread;
    private volatile bool _running;
    private volatile int _queueCount;
    private volatile bool _clearRequested;
    private volatile int _clearEpoch;
    private bool _buffering = true;
    private bool _hasPlayedAudio;
    private bool _hadPlayedBeforeClear;   // preserves mid-call awareness across barge-in
    private IntPtr _timer;
    private bool _useTimer;

    private int _sendErrorCount;

    // ─── Zero-latency barge-in wake ─────────────────────────
    private readonly ManualResetEventSlim _wakeFence = new(false);

    // ─── Adaptive jitter tracking ───────────────────────────
    private long _lastEnqueueTick;
    private double _jitterEwma;                      // EWMA of inter-arrival deviation (ms)
    private const double JitterAlpha = 0.15;         // smoothing factor
    private int _adaptiveResumeFrames = MinResumeThresholdFrames;

    private readonly TypingSoundGenerator _typingSound;
    private volatile bool _typingSoundsEnabled = true;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public int QueuedFrames => Volatile.Read(ref _queueCount);
    public bool TypingSoundsEnabled { get => _typingSoundsEnabled; set => _typingSoundsEnabled = value; }
    public G711CodecType Codec => _codec;

    public G711RtpPlayout(RTPSession rtpSession, G711CodecType codec = G711CodecType.PCMU)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
        _codec = codec;
        _payloadType = G711Codec.PayloadType(codec);
        _silenceByte = G711Codec.SilenceByte(codec);
        _typingSound = new TypingSoundGenerator(codec);
        Array.Fill(_silence, _silenceByte);
    }

    // ─── Public API ─────────────────────────────────────────

    public void Start()
    {
        if (_running) return;
        _running = true;
        _buffering = true;
        _hasPlayedAudio = false;
        _hadPlayedBeforeClear = false;
        _sendErrorCount = 0;

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
            Name = $"G711RtpPlayout-v12.1-{_codec}"
        };
        _thread.Start();
        SafeLog($"[RTP] G711RtpPlayout v12.1 started ({_codec}, PT={_payloadType})");
    }

    public void Stop()
    {
        _running = false;
        _wakeFence.Set(); // wake the loop so it can exit
        try { _thread?.Join(500); } catch { }
        _thread = null;

        if (IsWindows)
        {
            try { timeEndPeriod(1); } catch { }
            var t = _timer;
            _timer = IntPtr.Zero;
            if (t != IntPtr.Zero) { try { CloseHandle(t); } catch { } }
        }

        DrainQueue();
        _wakeFence.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Hard-cut barge-in: increments epoch, signals drain, and wakes the loop immediately.
    /// </summary>
    public void Clear()
    {
        Interlocked.Increment(ref _clearEpoch);
        Volatile.Write(ref _clearRequested, true);
        lock (_accLock) _accCount = 0;
        _typingSound.Reset();
        _wakeFence.Set(); // wake the loop NOW — zero-latency barge-in
    }

    public void Flush()
    {
        lock (_accLock)
        {
            if (_accCount <= 0) return;
            var frame = RentFrame();
            Array.Fill(frame, _silenceByte);
            Buffer.BlockCopy(_acc, 0, frame, 0, _accCount);
            EnqueueFrame(frame);
            _accCount = 0;
        }
    }

    /// <summary>
    /// Buffer G.711 encoded audio data. Automatically frames into 160-byte chunks.
    /// </summary>
    public void BufferG711(byte[] g711Data)
    {
        if (g711Data == null || g711Data.Length == 0) return;

        // ── Adaptive jitter measurement ──
        long nowTick = Stopwatch.GetTimestamp();
        long prev = Interlocked.Exchange(ref _lastEnqueueTick, nowTick);
        if (prev > 0)
        {
            double deltaMs = (nowTick - prev) * NsPerTick / 1_000_000.0;
            double deviation = Math.Abs(deltaMs - 20.0); // ideal is 20ms per frame
            _jitterEwma = (_jitterEwma == 0)
                ? deviation
                : _jitterEwma * (1 - JitterAlpha) + deviation * JitterAlpha;

            // Adapt resume threshold: more jitter → larger buffer (5-10 frames = 100-200ms)
            int newThreshold = MinResumeThresholdFrames + (int)Math.Min(_jitterEwma / 10.0, MaxResumeThresholdFrames - MinResumeThresholdFrames);
            _adaptiveResumeFrames = Math.Clamp(newThreshold, MinResumeThresholdFrames, MaxResumeThresholdFrames);
        }

        var epoch = Volatile.Read(ref _clearEpoch);

        lock (_accLock)
        {
            int needed = _accCount + g711Data.Length;
            if (needed > _acc.Length)
            {
                int newSize = Math.Min(Math.Max(_acc.Length * 2, needed), 65536);
                Array.Resize(ref _acc, newSize);
            }

            Buffer.BlockCopy(g711Data, 0, _acc, _accCount, g711Data.Length);
            _accCount += g711Data.Length;

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

    /// <summary>Legacy compat alias.</summary>
    public void BufferMuLaw(byte[] data) => BufferG711(data);

    // ─── Playout Loop ───────────────────────────────────────

    private void Loop()
    {
        long nextTick = Stopwatch.GetTimestamp();

        while (_running)
        {
            // Check barge-in FIRST — before any sleep
            if (Volatile.Read(ref _clearRequested))
                ExecuteClear();

            long now = Stopwatch.GetTimestamp();
            long wait = nextTick - now;

            if (wait > 0)
            {
                long waitNs = (long)(wait * NsPerTick);
                int waitMs = Math.Max(1, (int)(waitNs / 1_000_000) - 1);

                if (_useTimer && waitNs > 1_000_000)
                {
                    long due = -(waitNs / 100);
                    if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                        WaitForSingleObject(_timer, (uint)waitMs);
                }
                else if (waitNs > 2_000_000)
                {
                    // Interruptible wait — barge-in wakes us immediately via _wakeFence
                    _wakeFence.Wait(waitMs);
                }
                else
                {
                    Thread.Yield();
                }

                // Reset the fence after waking (whether from timeout or signal)
                _wakeFence.Reset();

                // Re-check barge-in after any sleep
                if (Volatile.Read(ref _clearRequested))
                    ExecuteClear();

                continue;
            }

            TickOnce();
            nextTick += TicksPerFrame;

            long after = Stopwatch.GetTimestamp();
            if ((after - nextTick) * NsPerTick > 100_000_000)
                nextTick = after + TicksPerFrame;
        }
    }

    private void TickOnce()
    {
        int currentEpoch = Volatile.Read(ref _clearEpoch);

        // ── Drain ALL stale frames from previous epochs in one tick ──
        while (_q.TryPeek(out var peek) && peek.epoch != currentEpoch)
        {
            if (_q.TryDequeue(out var stale))
            {
                Interlocked.Decrement(ref _queueCount);
                ReturnFrame(stale.data);
            }
        }

        int q = Volatile.Read(ref _queueCount);

        if (_buffering)
        {
            int threshold = (_hasPlayedAudio || _hadPlayedBeforeClear) ? _adaptiveResumeFrames : ColdStartThresholdFrames;
            if (q < threshold)
            {
                var fillFrame = _typingSoundsEnabled && !_hasPlayedAudio && !_hadPlayedBeforeClear
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

            // Epoch check (should be current after the drain above, but guard anyway)
            if (item.epoch != currentEpoch)
            {
                ReturnFrame(item.data);
                _buffering = true;
                Send(_silence);
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
            _rtpSession.SendAudio(FrameSize, payload160);
            _sendErrorCount = 0;
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _sendErrorCount) == MaxSendErrors)
            {
                SafeLog($"[RTP] Circuit breaker tripped after {MaxSendErrors} errors: {ex.Message}");
            }
        }
    }

    private void ExecuteClear()
    {
        DrainQueue();
        _buffering = true;
        _hadPlayedBeforeClear = _hasPlayedAudio || _hadPlayedBeforeClear;
        _hasPlayedAudio = false;
        Volatile.Write(ref _clearRequested, false);
    }

    private void DrainQueue()
    {
        while (_q.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _queueCount);
            ReturnFrame(item.data);
        }
    }

    // ─── ArrayPool Frame Management ─────────────────────────

    private byte[] RentFrame() => ArrayPool<byte>.Shared.Rent(FrameSize);

    private void ReturnFrame(byte[] f)
    {
        if (f.Length >= FrameSize)
            ArrayPool<byte>.Shared.Return(f);
    }

    private void EnqueueFrame(byte[] frame160)
    {
        var epoch = Volatile.Read(ref _clearEpoch);
        _q.Enqueue((frame160, epoch));
        Interlocked.Increment(ref _queueCount);
    }

    private void SafeLog(string msg) { try { OnLog?.Invoke(msg); } catch { } }
}
