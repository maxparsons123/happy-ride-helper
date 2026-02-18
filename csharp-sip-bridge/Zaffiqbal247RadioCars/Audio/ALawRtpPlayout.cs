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
/// Pure A-law RTP playout engine v10.1.
/// Zero-delay start, exact-size frames (no ArrayPool crackling), corrected catch-up, NAT keepalive.
/// </summary>
public sealed class ALawRtpPlayout : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateWaitableTimerExW(IntPtr lpAttr, IntPtr lpName, uint dwFlags, uint dwAccess);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfn, IntPtr lpArg, bool fResume);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMs);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObj);

    private const int FRAME_SIZE = 160;
    private const byte ALAW_SILENCE = 0xD5;
    private const byte PAYLOAD_TYPE_PCMA = 8;
    private const int CATCHUP_THRESHOLD = 50;
    private const int MAX_QUEUE_FRAMES = 1500;
    private static readonly double TicksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private readonly byte[] _silenceFrame = new byte[FRAME_SIZE];

    private byte[] _accumulator = new byte[32768];
    private int _accCount;
    private int _queueCount;
    private readonly object _accLock = new();

    private readonly VoIPMediaSession _mediaSession;
    private Thread? _playoutThread;
    private System.Threading.Timer? _natKeepaliveTimer;
    private volatile bool _running;
    private volatile bool _isBuffering = true;
    private volatile int _disposed;
    private volatile int _started;
    private int _framesSent;
    private uint _timestamp;
    private IntPtr _waitableTimer;
    private long _lastRtpSendTimeTicks = DateTime.UtcNow.Ticks;

    private IPEndPoint? _lastRemoteEndpoint;
    private volatile bool _natBindingEstablished;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;
    public int QueuedFrames => Volatile.Read(ref _queueCount);
    public int FramesSent => Volatile.Read(ref _framesSent);

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
        var elapsed = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastRtpSendTimeTicks);
        if (_natBindingEstablished || elapsed < TimeSpan.TicksPerSecond * 20) return;
        try { SendFrame(_silenceFrame); } catch { }
    }

    public void BufferALaw(byte[] alawData)
    {
        if (Volatile.Read(ref _disposed) != 0 || alawData == null || alawData.Length == 0) return;
        lock (_accLock)
        {
            if (_accCount + alawData.Length > _accumulator.Length)
            {
                var newAcc = new byte[Math.Max(_accumulator.Length * 2, _accCount + alawData.Length)];
                Buffer.BlockCopy(_accumulator, 0, newAcc, 0, _accCount);
                _accumulator = newAcc;
            }

            Buffer.BlockCopy(alawData, 0, _accumulator, _accCount, alawData.Length);
            _accCount += alawData.Length;

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

                _accCount -= FRAME_SIZE;
                if (_accCount > 0)
                    Buffer.BlockCopy(_accumulator, FRAME_SIZE, _accumulator, 0, _accCount);
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
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

        _running = true;
        _isBuffering = true;
        _framesSent = 0;
        _timestamp = (uint)Random.Shared.Next();

        if (IsWindows)
        {
            try { timeBeginPeriod(1); } catch { }
            _waitableTimer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0x00000002, 0x1F0003);
        }

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "RTP_v10.1"
        };
        _playoutThread.Start();
        SafeLog("[RTP] v10.1 Started (Zero-Delay + Exact Frames + Catch-up + NAT)");
    }

    public void Stop()
    {
        _running = false;
        try { _playoutThread?.Join(2000); } catch { }
        _playoutThread = null;

        if (IsWindows)
        {
            try { timeEndPeriod(1); } catch { }
            var timer = _waitableTimer;
            _waitableTimer = IntPtr.Zero;
            if (timer != IntPtr.Zero)
            {
                try { CloseHandle(timer); } catch { }
            }
        }

        Flush();
        Clear();
        Volatile.Write(ref _started, 0);
    }

    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        long nextTickNs = (long)(sw.ElapsedTicks * TicksToNs);

        while (_running && Volatile.Read(ref _disposed) == 0)
        {
            long nowNs = (long)(sw.ElapsedTicks * TicksToNs);
            if (nowNs < nextTickNs)
            {
                long waitNs = nextTickNs - nowNs;
                var timer = _waitableTimer;
                if (waitNs > 1_200_000 && timer != IntPtr.Zero)
                {
                    long dueTime = -(waitNs / 100);
                    SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false);
                    WaitForSingleObject(timer, 100);
                }
                else if (waitNs > 5_000) { Thread.SpinWait(10); }
                continue;
            }

            ProcessPlayout();
            _timestamp += FRAME_SIZE;
            nextTickNs += 20_000_000;

            long currentNs = (long)(sw.ElapsedTicks * TicksToNs);
            if (currentNs - nextTickNs > 100_000_000)
                nextTickNs = currentNs + 20_000_000;
        }
    }

    private void ProcessPlayout()
    {
        int count = Volatile.Read(ref _queueCount);
        int framesToSend = count > CATCHUP_THRESHOLD ? 2 : 1;

        if (_isBuffering)
        {
            if (count > 0)
            {
                _isBuffering = false;
            }
            else
            {
                SendFrame(_silenceFrame);
                return;
            }
        }

        for (int i = 0; i < framesToSend; i++)
        {
            if (!TrySendRealFrame())
            {
                _isBuffering = true;
                SendFrame(_silenceFrame);
                try { ThreadPool.UnsafeQueueUserWorkItem(_ => OnQueueEmpty?.Invoke(), null); } catch { }
                return;
            }
            if (i < framesToSend - 1) _timestamp += FRAME_SIZE;
        }
    }

    private bool TrySendRealFrame()
    {
        if (_frameQueue.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            _mediaSession.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, 0, PAYLOAD_TYPE_PCMA);
            Interlocked.Exchange(ref _lastRtpSendTimeTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _framesSent);
            return true;
        }
        return false;
    }

    private void SendFrame(byte[] frame)
    {
        try
        {
            _mediaSession.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, 0, PAYLOAD_TYPE_PCMA);
            Interlocked.Exchange(ref _lastRtpSendTimeTicks, DateTime.UtcNow.Ticks);
        }
        catch { }
    }

    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _)) { }
        Volatile.Write(ref _queueCount, 0);
        lock (_accLock) { _accCount = 0; }
        _isBuffering = true;
    }

    private void SafeLog(string msg)
    {
        try { OnLog?.Invoke(msg); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Stop();
        _natKeepaliveTimer?.Dispose();
        _mediaSession.OnRtpPacketReceived -= HandleSymmetricRtp;
    }
}
