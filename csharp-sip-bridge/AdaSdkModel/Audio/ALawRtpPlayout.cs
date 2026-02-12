using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace AdaSdkModel.Audio;

/// <summary>
/// Pure A-law passthrough playout engine v8.3 with Win32 high-resolution timing.
/// Uses timeBeginPeriod(1) for sub-millisecond Sleep accuracy and a 240ms
/// hysteresis start threshold to absorb OpenAI's bursty audio delivery.
/// </summary>
public sealed class ALawRtpPlayout : IDisposable
{
    private const int FRAME_SIZE = 160;          // 20ms @ 8kHz
    private const byte ALAW_SILENCE = 0xD5;
    private const byte PAYLOAD_TYPE_PCMA = 8;
    private const int JITTER_BUFFER_START_THRESHOLD = 12;  // 240ms — proven sweet spot
    private const int MAX_QUEUE_FRAMES = 2000;
    private const int MAX_ACCUMULATOR_SIZE = 65536;

    private static readonly double TicksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    // Win32 multimedia timer for 1ms Sleep resolution
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private readonly byte[] _silenceFrame = new byte[FRAME_SIZE];
    private byte[] _accumulator = new byte[8192];
    private int _accCount;
    private readonly object _accLock = new();

    private readonly SIPSorcery.Media.VoIPMediaSession _mediaSession;
    private Thread? _playoutThread;
    private volatile bool _running;
    private volatile bool _isBuffering = true;
    private volatile int _disposed;
    private int _queueCount;
    private int _framesSent;
    private uint _timestamp;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;
    public int QueuedFrames => Volatile.Read(ref _queueCount);

    public ALawRtpPlayout(SIPSorcery.Media.VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _mediaSession.AcceptRtpFromAny = true;
        Array.Fill(_silenceFrame, ALAW_SILENCE);
    }

    public void BufferALaw(byte[] alawData)
    {
        if (Volatile.Read(ref _disposed) != 0 || alawData == null || alawData.Length == 0) return;
        lock (_accLock)
        {
            int needed = _accCount + alawData.Length;
            if (needed > MAX_ACCUMULATOR_SIZE) return;
            if (needed > _accumulator.Length)
            {
                var newAcc = new byte[Math.Min(Math.Max(_accumulator.Length * 2, needed), MAX_ACCUMULATOR_SIZE)];
                Buffer.BlockCopy(_accumulator, 0, newAcc, 0, _accCount);
                _accumulator = newAcc;
            }
            Buffer.BlockCopy(alawData, 0, _accumulator, _accCount, alawData.Length);
            _accCount += alawData.Length;
            while (_accCount >= FRAME_SIZE)
            {
                while (Volatile.Read(ref _queueCount) >= MAX_QUEUE_FRAMES)
                    if (_frameQueue.TryDequeue(out _)) Interlocked.Decrement(ref _queueCount);
                var frame = new byte[FRAME_SIZE];
                Buffer.BlockCopy(_accumulator, 0, frame, 0, FRAME_SIZE);
                _frameQueue.Enqueue(frame);
                Interlocked.Increment(ref _queueCount);
                _accCount -= FRAME_SIZE;
                if (_accCount > 0) Buffer.BlockCopy(_accumulator, FRAME_SIZE, _accumulator, 0, _accCount);
            }
        }
    }

    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _)) { }
        Volatile.Write(ref _queueCount, 0);
        _isBuffering = true;
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _running) return;
        _running = true;
        _isBuffering = true;
        _framesSent = 0;
        _timestamp = (uint)Random.Shared.Next();
        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "ALawPlayout-SDK-v8.3"
        };
        _playoutThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _playoutThread?.Join(500);
        Clear();
    }

    private void PlayoutLoop()
    {
        // Request 1ms timer resolution from Windows — critical for smooth 20ms pacing
        TimeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            long nextFrameNs = (long)(sw.ElapsedTicks * TicksToNs);

            while (_running && Volatile.Read(ref _disposed) == 0)
            {
                long nowNs = (long)(sw.ElapsedTicks * TicksToNs);

                if (nowNs < nextFrameNs)
                {
                    long remainNs = nextFrameNs - nowNs;
                    if (remainNs > 2_000_000)      // >2ms away → sleep 1ms
                        Thread.Sleep(1);
                    else if (remainNs > 500_000)    // >0.5ms away → yield
                        Thread.Yield();
                    else                            // <0.5ms → spin
                        Thread.SpinWait(20);
                    continue;
                }

                SendNextFrame();
                nextFrameNs += 20_000_000;  // 20ms in nanoseconds

                // Drift guard: if we fell behind by >100ms, reset to now
                long currentNs = (long)(sw.ElapsedTicks * TicksToNs);
                if (currentNs - nextFrameNs > 100_000_000)
                    nextFrameNs = currentNs + 20_000_000;
            }
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    private void SendNextFrame()
    {
        int qc = Volatile.Read(ref _queueCount);

        if (_isBuffering)
        {
            if (qc < JITTER_BUFFER_START_THRESHOLD)
            {
                SendRtp(_silenceFrame);
                return;
            }
            _isBuffering = false;
            OnLog?.Invoke($"▶ Playout started ({qc} frames buffered)");
        }

        if (_frameQueue.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _queueCount);
            SendRtp(frame);
            Interlocked.Increment(ref _framesSent);

            if (Volatile.Read(ref _queueCount) == 0)
            {
                _isBuffering = true;
                try { OnQueueEmpty?.Invoke(); } catch { }
            }
        }
        else
        {
            _isBuffering = true;
            SendRtp(_silenceFrame);
            try { OnQueueEmpty?.Invoke(); } catch { }
        }
    }

    private void SendRtp(byte[] frame)
    {
        try
        {
            _mediaSession.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, 0, PAYLOAD_TYPE_PCMA);
            _timestamp += FRAME_SIZE;
        }
        catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Stop();
    }
}
