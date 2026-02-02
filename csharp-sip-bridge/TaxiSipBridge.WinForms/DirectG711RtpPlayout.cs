using System;
using System.Collections.Concurrent;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;

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
/// - NAT-friendly (relies on SIPSorcery's symmetric RTP)
/// </summary>
public sealed class DirectG711RtpPlayout : IDisposable
{
    private const int SAMPLE_RATE = 8000;
    private const int FRAME_MS = 20;
    private const int FRAME_SIZE = 160; // 20ms @ 8kHz G.711
    private const int MIN_BUFFER_FRAMES = 6; // 120ms buffer before starting playout
    private const int MAX_QUEUE_FRAMES = 3000; // ~60s max buffer (safety cap)

    private readonly VoIPMediaSession _mediaSession;
    private readonly byte _silence;

    private readonly ConcurrentQueue<byte[]> _queue = new();
    private Timer? _timer;
    private int _disposed;
    private int _queueCount;
    private bool _isPlaying;
    private int _framesSent;

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

        OnLog?.Invoke($"[DirectG711RtpPlayout] Created (codec={codec})");
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

        // 20ms timer - the heart of telephony timing
        _timer = new Timer(SendFrame, null, 0, FRAME_MS);
        OnLog?.Invoke("[DirectG711RtpPlayout] Started (20ms timer)");
    }

    private void SendFrame(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        byte[] frame;
        var queueCount = Volatile.Read(ref _queueCount);

        // Buffer up before starting playout (absorbs network jitter)
        if (!_isPlaying && queueCount < MIN_BUFFER_FRAMES)
        {
            // Send silence while buffering
            frame = new byte[FRAME_SIZE];
            Array.Fill(frame, _silence);
            SendRtpFrame(frame);
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
            frame = audioFrame;
            _framesSent++;

            if (_framesSent % 100 == 0)
                OnLog?.Invoke($"[DirectG711RtpPlayout] Sent {_framesSent} frames (queue: {Volatile.Read(ref _queueCount)})");
        }
        else
        {
            // Queue empty - send silence
            frame = new byte[FRAME_SIZE];
            Array.Fill(frame, _silence);

            if (_isPlaying)
            {
                _isPlaying = false;
                OnLog?.Invoke($"[DirectG711RtpPlayout] Queue empty after {_framesSent} frames");
                OnQueueEmpty?.Invoke();
            }
        }

        SendRtpFrame(frame);
    }

    private void SendRtpFrame(byte[] frame)
    {
        try
        {
            // Use VoIPMediaSession.SendAudio which handles RTP sequencing and timing internally
            // Duration = 160 samples = 20ms @ 8kHz
            _mediaSession.SendAudio(160, frame);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[DirectG711RtpPlayout] SendAudio error: {ex.Message}");
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

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _timer = null;
        OnLog?.Invoke($"[DirectG711RtpPlayout] Stopped ({_framesSent} frames sent)");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        Stop();
        Clear();
    }
}
