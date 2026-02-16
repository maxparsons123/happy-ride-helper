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
/// Pure A-law RTP playout engine — PERFECT EDITION. Anti-Burst/Anti-Jitter with safe shutdown.
/// </summary>
public sealed class ALawRtpPlayout : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateWaitableTimerExW(IntPtr lpAttr, IntPtr lpName, uint dwFlags, uint dwAccess);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfn, IntPtr lpArg, bool fResume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMs);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObj);

    private const int FRAME_SIZE = 160;
    private const byte ALAW_SILENCE = 0xD5;
    private const int JITTER_BUFFER_START_THRESHOLD = 15; // 300ms Anti-Burst Pillow
    private static readonly double TicksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private readonly byte[] _silenceFrame = new byte[FRAME_SIZE];
    private byte[] _accumulator = new byte[16384];
    private int _accCount;
    private int _queueCount;
    private readonly object _accLock = new();
    private readonly VoIPMediaSession _mediaSession;
    private Thread? _playoutThread;
    private volatile bool _running;
    private volatile bool _isBuffering = true;
    private uint _timestamp;
    private IntPtr _waitableTimer;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;
    public int QueuedFrames => Volatile.Read(ref _queueCount);

    public ALawRtpPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession;
        Array.Fill(_silenceFrame, ALAW_SILENCE);
    }

    public void BufferALaw(byte[] alawData)
    {
        if (alawData == null || alawData.Length == 0) return;
        lock (_accLock)
        {
            if (_accCount + alawData.Length > _accumulator.Length)
                Array.Resize(ref _accumulator, Math.Max(_accumulator.Length * 2, _accCount + alawData.Length));

            Buffer.BlockCopy(alawData, 0, _accumulator, _accCount, alawData.Length);
            _accCount += alawData.Length;

            while (_accCount >= FRAME_SIZE)
            {
                var frame = new byte[FRAME_SIZE];
                Buffer.BlockCopy(_accumulator, 0, frame, 0, FRAME_SIZE);
                _frameQueue.Enqueue(frame);
                Interlocked.Add(ref _accCount, -FRAME_SIZE);
                Interlocked.Increment(ref _queueCount);
                if (_accCount > 0) Buffer.BlockCopy(_accumulator, FRAME_SIZE, _accumulator, 0, _accCount);
            }
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
        if (_running) return;
        _running = true;
        _timestamp = (uint)Random.Shared.Next();

        if (IsWindows)
        {
            timeBeginPeriod(1);
            _waitableTimer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0x00000002, 0x1F0003);
        }

        _playoutThread = new Thread(PlayoutLoop) { IsBackground = true, Priority = ThreadPriority.Highest };
        _playoutThread.Start();
        OnLog?.Invoke("[RTP] PERFECT EDITION Started (Anti-Burst/Anti-Jitter Active)");
    }

    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        long nextTickNs = (long)(sw.ElapsedTicks * TicksToNs);

        while (_running)
        {
            long nowNs = (long)(sw.ElapsedTicks * TicksToNs);
            if (nowNs < nextTickNs)
            {
                long waitNs = nextTickNs - nowNs;
                // Read timer handle once — it may be zeroed by Stop() on another thread
                var timer = _waitableTimer;
                if (waitNs > 1_000_000 && timer != IntPtr.Zero)
                {
                    long dueTime = -(waitNs / 100);
                    SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false);
                    WaitForSingleObject(timer, 100);
                }
                else if (waitNs > 100_000) { Thread.SpinWait(20); }
                continue;
            }

            int count = Volatile.Read(ref _queueCount);
            if (_isBuffering)
            {
                if (count >= JITTER_BUFFER_START_THRESHOLD) _isBuffering = false;
                _mediaSession.SendRtpRaw(SDPMediaTypesEnum.audio, _silenceFrame, _timestamp, 0, 8);
            }
            else if (_frameQueue.TryDequeue(out var frame))
            {
                Interlocked.Decrement(ref _queueCount);
                _mediaSession.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, 0, 8);
                if (count == 1)
                {
                    _isBuffering = true;
                    try { OnQueueEmpty?.Invoke(); } catch { }
                }
            }
            else
            {
                _isBuffering = true;
                _mediaSession.SendRtpRaw(SDPMediaTypesEnum.audio, _silenceFrame, _timestamp, 0, 8);
            }

            _timestamp += FRAME_SIZE;
            nextTickNs += 20_000_000;
        }
    }

    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _)) Interlocked.Decrement(ref _queueCount);
        lock (_accLock) { _accCount = 0; Array.Clear(_accumulator, 0, _accumulator.Length); }
        _isBuffering = true;
    }

    public void Stop()
    {
        _running = false;
        // Wait for playout thread to fully exit BEFORE closing timer handle
        try { _playoutThread?.Join(2000); } catch { }
        _playoutThread = null;

        if (IsWindows)
        {
            try { timeEndPeriod(1); } catch { }
            // Zero handle FIRST so playout thread (if still draining) sees IntPtr.Zero
            var timer = _waitableTimer;
            _waitableTimer = IntPtr.Zero;
            if (timer != IntPtr.Zero)
            {
                try { CloseHandle(timer); } catch { }
            }
        }

        while (_frameQueue.TryDequeue(out _)) { }
        Volatile.Write(ref _queueCount, 0);
    }

    public void Dispose() => Stop();
}
