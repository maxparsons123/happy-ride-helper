using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Net;

namespace AdaCleanVersion.Audio;

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

    private const int FrameSize = 160; // 20ms @ 8kHz

    private readonly RTPSession _rtpSession;
    private readonly int _payloadType;
    private readonly uint _ssrc;
    private readonly byte[] _silence = new byte[FrameSize];
    private readonly ConcurrentQueue<byte[]> _queue = new();

    private Thread? _thread;
    private volatile bool _running;
    private ushort _sequence;
    private uint _timestamp;
    private IntPtr _timer;
    private bool _useTimer;

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private static readonly long TicksPerFrame = (long)(20_000_000.0 / NsPerTick);

    public event Action<string>? OnLog;

    public G711RtpPlayout(RTPSession session, G711CodecType codec)
    {
        _rtpSession = session;
        _payloadType = G711Codec.PayloadType(codec);
        _ssrc = session.GetRtpChannel().Ssrc;
        _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);
        _timestamp = (uint)Random.Shared.Next();
        Array.Fill(_silence, G711Codec.SilenceByte(codec));
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

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
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
        Log("RTP v13 started (manual timestamp control)");
    }

    public void Stop()
    {
        _running = false;
        try { _thread?.Join(500); } catch { }
        if (IsWindows)
        {
            try { timeEndPeriod(1); } catch { }
            if (_timer != IntPtr.Zero) CloseHandle(_timer);
        }
    }

    public void Dispose() => Stop();

    public void BufferG711(byte[] data)
    {
        if (data == null || data.Length == 0) return;

        int offset = 0;
        while (offset + FrameSize <= data.Length)
        {
            var frame = new byte[FrameSize];
            Buffer.BlockCopy(data, offset, frame, 0, FrameSize);
            _queue.Enqueue(frame);
            offset += FrameSize;
        }
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
    }

    private void Loop()
    {
        long nextTick = Stopwatch.GetTimestamp();

        while (_running)
        {
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
                    Thread.SpinWait(50);
                }

                continue;
            }

            Tick();
            nextTick += TicksPerFrame;

            long drift = Stopwatch.GetTimestamp() - nextTick;
            if (drift > TicksPerFrame * 5)
                nextTick = Stopwatch.GetTimestamp() + TicksPerFrame;
        }
    }

    private void Tick()
    {
        if (!_queue.TryDequeue(out var frame))
            frame = _silence;

        SendRtp(frame);
    }

    private void SendRtp(byte[] payload)
    {
        try
        {
            var header = new RTPHeader(
                _payloadType,
                _sequence++,
                _timestamp,
                _ssrc);

            _timestamp += FrameSize; // +160 samples

            var packet = new RTPPacket(header, payload);
            _rtpSession.SendRtpPacket(packet);
        }
        catch (Exception ex)
        {
            Log($"RTP send error: {ex.Message}");
        }
    }

    private void Log(string msg)
    {
        try { OnLog?.Invoke(msg); } catch { }
    }
}
