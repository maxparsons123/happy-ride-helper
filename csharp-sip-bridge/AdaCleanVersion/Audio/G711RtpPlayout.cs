using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

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

    private readonly VoIPMediaSession _mediaSession;
    private readonly int _payloadType;
    private readonly byte[] _silence = new byte[FrameSize];
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly byte[] _partial = new byte[FrameSize];
    private int _partialLen;

    private Thread? _thread;
    private volatile bool _running;
    private uint _timestamp;
    private IntPtr _timer;
    private bool _useTimer;

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private static readonly long TicksPerFrame = (long)(20_000_000.0 / NsPerTick);

    public event Action<string>? OnLog;

    public G711RtpPlayout(VoIPMediaSession session, G711CodecType codec)
    {
        _mediaSession = session;
        _payloadType = G711Codec.PayloadType(codec);
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
        Log("RTP v13 started (SendRtpRaw, manual timestamp)");
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

        // If we have a partial frame from a previous call, fill it first
        if (_partialLen > 0)
        {
            int needed = FrameSize - _partialLen;
            int copy = Math.Min(needed, data.Length);
            Buffer.BlockCopy(data, 0, _partial, _partialLen, copy);
            _partialLen += copy;
            offset = copy;

            if (_partialLen >= FrameSize)
            {
                var frame = new byte[FrameSize];
                Buffer.BlockCopy(_partial, 0, frame, 0, FrameSize);
                _queue.Enqueue(frame);
                _partialLen = 0;
            }
        }

        // Enqueue complete frames
        while (offset + FrameSize <= data.Length)
        {
            var frame = new byte[FrameSize];
            Buffer.BlockCopy(data, offset, frame, 0, FrameSize);
            _queue.Enqueue(frame);
            offset += FrameSize;
        }

        // Save any trailing partial data
        int remaining = data.Length - offset;
        if (remaining > 0)
        {
            Buffer.BlockCopy(data, offset, _partial, _partialLen, remaining);
            _partialLen += remaining;
        }
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
        _partialLen = 0;
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
            _mediaSession.SendRtpRaw(
                SDPMediaTypesEnum.audio,
                payload,
                _timestamp,
                0,
                _payloadType);

            _timestamp += FrameSize; // +160 samples
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
