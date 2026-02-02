using System;
using System.Collections.Concurrent;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Clean push-based RTP playout for native G.711 mode.
/// 
/// Golden rule: Never send RTP from the WebSocket thread.
///              Always send RTP from a 20ms timer.
/// 
/// Features:
/// - Push-based API via PushAudio() for OnG711Audio events
/// - Fixed 20ms timer pacing (PBX-friendly)
/// - No decoding, no resampling, no DSP
/// - Barge-in support via Clear()
/// - Direct RTP with proper timestamp/sequence management
/// </summary>
public sealed class DirectG711RtpPlayout : IDisposable
{
    private const int FRAME_MS = 20;
    private const int FRAME_SIZE = 160; // 20ms @ 8kHz G.711
    private const int MIN_BUFFER_FRAMES = 4; // 80ms buffer before starting playout (reduced for lower latency)
    private const int MAX_QUEUE_FRAMES = 3000; // ~60s max buffer (safety cap)

    private readonly VoIPMediaSession _mediaSession;
    private readonly byte _silence;
    private readonly byte _payloadType;

    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly byte[] _frameBuffer = new byte[FRAME_SIZE];
    private Timer? _timer;
    private int _disposed;
    private int _queueCount;
    private bool _isPlaying;
    private int _framesSent;
    private uint _timestamp;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;

    public int PendingFrameCount => Volatile.Read(ref _queueCount);
    public bool IsPlaying => _isPlaying;

    public DirectG711RtpPlayout(
        VoIPMediaSession mediaSession,
        OpenAIRealtimeG711Client.G711Codec codec)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _silence = codec == OpenAIRealtimeG711Client.G711Codec.ALaw ? (byte)0xD5 : (byte)0xFF;
        _payloadType = codec == OpenAIRealtimeG711Client.G711Codec.ALaw ? (byte)8 : (byte)0;
        _timestamp = 0;

        OnLog?.Invoke($"[DirectG711RtpPlayout] Created (codec={codec}, PT={_payloadType})");
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
        if (Volatile.Read(ref _disposed) != 0) return;

        _framesSent = 0;
        _isPlaying = false;
        _timestamp = 0;

        // 20ms timer - the heart of telephony timing
        _timer = new Timer(SendFrame, null, 0, FRAME_MS);
        OnLog?.Invoke("[DirectG711RtpPlayout] Started (20ms timer, SendRtpRaw)");
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _timer = null;
        OnLog?.Invoke($"[DirectG711RtpPlayout] Stopped ({_framesSent} frames sent)");
    }

    private void SendFrame(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var queueCount = Volatile.Read(ref _queueCount);

        // Buffer up before starting playout (absorbs network jitter)
        if (!_isPlaying && queueCount < MIN_BUFFER_FRAMES)
        {
            // Send silence while buffering
            Array.Fill(_frameBuffer, _silence);
            SendRtpFrame(_frameBuffer);
            return;
        }

        if (!_isPlaying && queueCount >= MIN_BUFFER_FRAMES)
        {
            _isPlaying = true;
            OnLog?.Invoke($"[DirectG711RtpPlayout] Buffered {queueCount} frames, starting playout");
        }

        if (_queue.TryDequeue(out var audioFrame))
        {
            Interlocked.Decrement(ref _queueCount);
            Array.Copy(audioFrame, _frameBuffer, FRAME_SIZE);
            _framesSent++;

            if (_framesSent % 100 == 0)
                OnLog?.Invoke($"[DirectG711RtpPlayout] Sent {_framesSent} frames (queue: {Volatile.Read(ref _queueCount)})");
        }
        else
        {
            // Queue empty - send silence
            Array.Fill(_frameBuffer, _silence);

            if (_isPlaying)
            {
                _isPlaying = false;
                OnLog?.Invoke($"[DirectG711RtpPlayout] Queue empty after {_framesSent} frames");
                OnQueueEmpty?.Invoke();
            }
        }

        SendRtpFrame(_frameBuffer);
    }

    private void SendRtpFrame(byte[] frame)
    {
        try
        {
            // Use SendRtpRaw for direct G.711 byte passthrough (no encoding)
            // This matches the proven DirectRtpPlayout pattern
            _mediaSession.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, 0, (int)_payloadType);
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

        _isPlaying = false;
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
