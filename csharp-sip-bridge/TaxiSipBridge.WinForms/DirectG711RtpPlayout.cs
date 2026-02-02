using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Production-grade RTP playout for native G.711 mode.
/// 
/// ZERO DSP: No resampling, no filtering, no volume processing.
/// Raw G.711 bytes from OpenAI → jitter buffer → RTP at 20ms intervals.
/// 
/// Features:
/// ✅ High-precision Stopwatch-based timing (sub-ms accuracy)
/// ✅ Adaptive jitter buffer (60ms → 120ms on underruns)
/// ✅ NAT keepalives for strict NATs (25s interval)
/// ✅ Symmetric RTP locking (dynamic endpoint detection)
/// ✅ Smooth fade-out on underruns (click-free transitions)
/// ✅ Thread-safe disposal
/// </summary>
public sealed class DirectG711RtpPlayout : IDisposable
{
    private const int FRAME_MS = 20;
    private const int FRAME_SIZE = 160; // 20ms @ 8kHz G.711
    private const int MAX_QUEUE_FRAMES = 3000; // ~60s max buffer (safety cap)
    
    // Adaptive jitter buffer: starts at 60ms, grows to 120ms on underruns
    private const int JITTER_BUFFER_MIN = 3;  // 60ms initial
    private const int JITTER_BUFFER_MAX = 6;  // 120ms after underruns
    private const int UNDERRUN_THRESHOLD = 3; // Grow buffer after 3 consecutive underruns

    private readonly VoIPMediaSession _mediaSession;
    private readonly byte _silence;
    private readonly byte _payloadType;
    private readonly byte[] _silenceFrame;
    private readonly byte[] _fadeFrame; // For smooth fade-out
    private readonly ConcurrentQueue<byte[]> _queue = new();
    
    // Threading
    private Thread? _playoutThread;
    private Timer? _natKeepaliveTimer;
    private volatile bool _running;
    private volatile bool _isBuffering;
    private int _disposed;
    
    // State
    private int _queueCount;
    private int _framesSent;
    private int _consecutiveUnderruns;
    private int _currentJitterBuffer;
    private bool _wasPlaying;
    private uint _timestamp;
    private byte _lastByte; // For fade-out
    private DateTime _lastRtpSendTime = DateTime.UtcNow;
    
    // NAT state
    private IPEndPoint? _lastRemoteEndpoint;
    private volatile bool _natBindingEstablished;

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
        _lastByte = _silence;
        _currentJitterBuffer = JITTER_BUFFER_MIN;
        
        // Pre-allocate frames
        _silenceFrame = new byte[FRAME_SIZE];
        _fadeFrame = new byte[FRAME_SIZE];
        Array.Fill(_silenceFrame, _silence);
        
        // NAT traversal setup
        _mediaSession.AcceptRtpFromAny = true;
        _mediaSession.OnRtpPacketReceived += HandleSymmetricRtp;
        
        // Periodic keepalives for strict NATs (25s interval)
        _natKeepaliveTimer = new Timer(KeepaliveNAT, null, 
            TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(25));
    }

    /// <summary>
    /// Symmetric RTP: Lock to actual media source IP for NAT traversal.
    /// </summary>
    private void HandleSymmetricRtp(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || Volatile.Read(ref _disposed) != 0)
            return;

        if (_lastRemoteEndpoint == null || !_lastRemoteEndpoint.Equals(remoteEndPoint))
        {
            _lastRemoteEndpoint = remoteEndPoint;
            try
            {
                _mediaSession.SetDestination(SDPMediaTypesEnum.audio, remoteEndPoint, remoteEndPoint);
                _natBindingEstablished = true;
                OnLog?.Invoke($"[NAT] ✓ Symmetric RTP locked to {remoteEndPoint}");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[NAT] ✗ SetDestination failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Periodic keepalive for strict NATs that drop UDP bindings after 30s silence.
    /// </summary>
    private void KeepaliveNAT(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        
        // Only send keepalive if no recent RTP and NAT not yet established
        if (_natBindingEstablished || (DateTime.UtcNow - _lastRtpSendTime).TotalSeconds < 20)
            return;
        
        try
        {
            SendRtpFrame(_silenceFrame);
            OnLog?.Invoke("[NAT] Keepalive sent");
        }
        catch { /* Non-critical */ }
    }

    /// <summary>
    /// Push raw G.711 audio from OpenAI (any length).
    /// Zero DSP - frames go straight to queue.
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
            
            // Track last byte for fade-out
            _lastByte = frame[FRAME_SIZE - 1];
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _running) return;

        _framesSent = 0;
        _wasPlaying = false;
        _isBuffering = true;
        _consecutiveUnderruns = 0;
        _currentJitterBuffer = JITTER_BUFFER_MIN;
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
        
        OnLog?.Invoke($"[DirectG711RtpPlayout] Started (adaptive {_currentJitterBuffer * 20}ms buffer, ts={_timestamp})");
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
                    Thread.Sleep((int)(waitMs - 1));
                else if (waitMs > 0.1)
                    Thread.SpinWait(100);
                continue;
            }

            // Send frame
            SendNextFrame();

            // Schedule next frame (accumulate to avoid drift)
            nextFrameTimeMs += FRAME_MS;

            // Drift correction: if way behind, catch up
            now = sw.Elapsed.TotalMilliseconds;
            if (now - nextFrameTimeMs > 40)
                nextFrameTimeMs = now + FRAME_MS;
        }
    }

    private void SendNextFrame()
    {
        int currentCount = Volatile.Read(ref _queueCount);

        // Adaptive buffer: grow after consecutive underruns
        if (_consecutiveUnderruns >= UNDERRUN_THRESHOLD && _currentJitterBuffer < JITTER_BUFFER_MAX)
        {
            _currentJitterBuffer = JITTER_BUFFER_MAX;
            _consecutiveUnderruns = 0;
            OnLog?.Invoke($"[DirectG711RtpPlayout] ⚠️ Underruns detected - buffer increased to {_currentJitterBuffer * 20}ms");
        }

        // Jitter buffer: wait until we have enough frames before starting playout
        if (_isBuffering)
        {
            if (currentCount >= _currentJitterBuffer)
            {
                _isBuffering = false;
                OnLog?.Invoke($"[DirectG711RtpPlayout] Buffer ready ({currentCount} frames), starting playout");
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
            _consecutiveUnderruns = 0; // Reset on successful dequeue
            _lastByte = audioFrame[FRAME_SIZE - 1];

            if (_framesSent % 500 == 0)
                OnLog?.Invoke($"[DirectG711RtpPlayout] Sent {_framesSent} frames (queue: {Volatile.Read(ref _queueCount)})");
        }
        else
        {
            // Queue empty - create smooth fade-out frame
            _consecutiveUnderruns++;
            
            // Exponential fade toward silence (click-free transition)
            for (int i = 0; i < FRAME_SIZE; i++)
            {
                // Blend from last byte toward silence
                float blend = (float)i / FRAME_SIZE;
                _fadeFrame[i] = (byte)((_lastByte * (1 - blend)) + (_silence * blend));
            }
            _lastByte = _silence;
            frameToSend = _fadeFrame;
            
            // Fire OnQueueEmpty once when transitioning from playing to empty
            if (_wasPlaying)
            {
                _wasPlaying = false;
                _isBuffering = true;
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
            _lastRtpSendTime = DateTime.UtcNow;
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
        _isBuffering = true;
        _consecutiveUnderruns = 0;
        _currentJitterBuffer = JITTER_BUFFER_MIN; // Reset adaptive buffer
        _lastByte = _silence;
        OnLog?.Invoke("[DirectG711RtpPlayout] Queue cleared (barge-in)");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        Stop();
        _natKeepaliveTimer?.Dispose();
        
        // Unsubscribe before disposal to prevent race conditions
        _mediaSession.OnRtpPacketReceived -= HandleSymmetricRtp;
        
        // Final silence frame for graceful close
        try { SendRtpFrame(_silenceFrame); } catch { }
        
        Clear();
        GC.SuppressFinalize(this);
    }
}
