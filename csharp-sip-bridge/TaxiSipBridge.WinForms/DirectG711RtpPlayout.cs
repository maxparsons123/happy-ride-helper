using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Push-based RTP playout for native G.711 mode with minimal jitter buffer.
/// 
/// DIRECT PASSTHROUGH: No DSP, no resampling.
/// Raw G.711 bytes from OpenAI → small jitter buffer → RTP at 20ms intervals.
/// 
/// Uses Stopwatch-based timing for high precision (sub-ms accuracy).
/// 60ms jitter buffer smooths out WebSocket delivery bursts.
/// </summary>
public sealed class DirectG711RtpPlayout : IDisposable
{
    private const int FRAME_MS = 20;
    private const int FRAME_SIZE = 160; // 20ms @ 8kHz G.711
    private const int JITTER_BUFFER_FRAMES = 3; // 60ms jitter buffer - smooths WebSocket bursts
    private const int MAX_QUEUE_FRAMES = 3000; // ~60s max buffer (safety cap)

    private readonly VoIPMediaSession _mediaSession;
    private readonly byte _silence;
    private readonly byte _payloadType;

    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly byte[] _silenceFrame;
    private Thread? _playoutThread;
    private volatile bool _running;
    private volatile bool _isBuffering; // True while accumulating jitter buffer
    private int _disposed;
    private int _queueCount;
    private int _framesSent;
    private bool _wasPlaying;
    private uint _timestamp;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;

    public int PendingFrameCount => Volatile.Read(ref _queueCount);
    public bool IsPlaying => _wasPlaying;

    public DirectG711RtpPlayout(
        VoIPMediaSession mediaSession,
        OpenAIRealtimeG711Client.G711Codec codec)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _silence = codec == OpenAIRealtimeG711Client.G711Codec.ALaw ? (byte)0xD5 : (byte)0xFF;
        _payloadType = codec == OpenAIRealtimeG711Client.G711Codec.ALaw ? (byte)8 : (byte)0;
        
        // Pre-allocate silence frame
        _silenceFrame = new byte[FRAME_SIZE];
        Array.Fill(_silenceFrame, _silence);
    }

    /// <summary>
    /// Push raw G.711 audio from OpenAI (any length).
    /// Frames are automatically split into 20ms chunks.
    /// </summary>
    public void PushAudio(byte[] g711)
    {
        if (Volatile.Read(ref _disposed) != 0 || g711 == null || g711.Length == 0)
            return;

        for (int i = 0; i < g711.Length; i += FRAME_SIZE)
        {
            // Safety cap: drop oldest frames if buffer overflows
            while (Volatile.Read(ref _queueCount) >= MAX_QUEUE_FRAMES)
            {
                if (_queue.TryDequeue(out _))
                    Interlocked.Decrement(ref _queueCount);
            }

            var frame = new byte[FRAME_SIZE];
            int count = Math.Min(FRAME_SIZE, g711.Length - i);
            Array.Copy(g711, i, frame, 0, count);

            // Pad incomplete frames with silence
            if (count < FRAME_SIZE)
                Array.Fill(frame, _silence, count, FRAME_SIZE - count);

            _queue.Enqueue(frame);
            Interlocked.Increment(ref _queueCount);
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _running) return;

        _framesSent = 0;
        _wasPlaying = false;
        _isBuffering = true; // Start in buffering mode
        _running = true;
        
        // Random start timestamp (RFC 3550 compliant)
        _timestamp = (uint)Random.Shared.Next();

        // High-precision playout thread
        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "G711RtpPlayout"
        };
        _playoutThread.Start();
        
        OnLog?.Invoke($"[DirectG711RtpPlayout] Started (60ms jitter buffer, ts={_timestamp})");
    }

    public void Stop()
    {
        _running = false;
        _playoutThread?.Join(500);
        _playoutThread = null;
        OnLog?.Invoke($"[DirectG711RtpPlayout] Stopped ({_framesSent} frames sent)");
    }

    /// <summary>
    /// High-precision 20ms playout loop using Stopwatch timing.
    /// </summary>
    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameTimeMs = sw.Elapsed.TotalMilliseconds;

        while (_running && Volatile.Read(ref _disposed) == 0)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            // Wait for next frame time with high precision
            if (now < nextFrameTimeMs)
            {
                double waitMs = nextFrameTimeMs - now;
                if (waitMs > 2.0)
                {
                    Thread.Sleep((int)(waitMs - 1));
                }
                else if (waitMs > 0.1)
                {
                    Thread.SpinWait(100);
                }
                continue;
            }

            // Send frame
            SendNextFrame();

            // Schedule next frame (accumulate to avoid drift)
            nextFrameTimeMs += FRAME_MS;

            // Drift correction: if we're way behind, catch up
            now = sw.Elapsed.TotalMilliseconds;
            if (now - nextFrameTimeMs > 40)
            {
                nextFrameTimeMs = now + FRAME_MS;
            }
        }
    }

    private void SendNextFrame()
    {
        int currentCount = Volatile.Read(ref _queueCount);

        // Jitter buffer: wait until we have enough frames before starting playout
        if (_isBuffering)
        {
            if (currentCount >= JITTER_BUFFER_FRAMES)
            {
                _isBuffering = false;
                OnLog?.Invoke($"[DirectG711RtpPlayout] Jitter buffer ready ({currentCount} frames), starting playout");
            }
            else
            {
                // Still buffering - send silence
                SendRtpFrame(_silenceFrame);
                return;
            }
        }

        byte[] frameToSend;

        if (_queue.TryDequeue(out var audioFrame))
        {
            Interlocked.Decrement(ref _queueCount);
            frameToSend = audioFrame;
            _framesSent++;
            _wasPlaying = true;

            if (_framesSent % 500 == 0)
                OnLog?.Invoke($"[DirectG711RtpPlayout] Sent {_framesSent} frames (queue: {Volatile.Read(ref _queueCount)})");
        }
        else
        {
            // Queue empty - send pure silence and re-enter buffering mode
            frameToSend = _silenceFrame;
            
            // Fire OnQueueEmpty once when transitioning from playing to empty
            if (_wasPlaying)
            {
                _wasPlaying = false;
                _isBuffering = true; // Re-enter buffering mode for next audio burst
                OnLog?.Invoke($"[DirectG711RtpPlayout] Queue empty after {_framesSent} frames");
                OnQueueEmpty?.Invoke();
            }
        }

        SendRtpFrame(frameToSend);
    }

    private void SendRtpFrame(byte[] frame)
    {
        try
        {
            _mediaSession.SendRtpRaw(
                SDPMediaTypesEnum.audio,
                frame,
                _timestamp,
                0,  // marker bit
                (int)_payloadType
            );
            _timestamp += FRAME_SIZE;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[DirectG711RtpPlayout] SendRtpRaw error: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear all buffered audio (call on barge-in).
    /// </summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out _))
            Interlocked.Decrement(ref _queueCount);

        _wasPlaying = false;
        _isBuffering = true; // Re-enter buffering mode
        OnLog?.Invoke("[DirectG711RtpPlayout] Queue cleared (barge-in)");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        Stop();
        Clear();
    }
}
