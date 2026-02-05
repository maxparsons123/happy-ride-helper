// Version: 5.2 - Pure A-law end-to-end passthrough (no PCM conversion, no DSP)
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Windows multimedia timer for precise 20ms scheduling.
/// Thread.Sleep has 15.6ms granularity which causes jitter.
/// </summary>
internal static class WinMmTimer
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uPeriod);
    
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uPeriod);
}

/// <summary>
/// Production-grade RTP playout for native G.711 mode.
/// 
/// ZERO DSP: No resampling, no filtering, no volume processing.
/// Raw G.711 bytes from OpenAI → jitter buffer → RTP at 20ms intervals.
/// 
/// v5.2 FIXES:
/// ✅ Increased jitter buffer to 25 frames (500ms) for smoother audio
/// ✅ Improved timing precision in playout loop
/// ✅ Better drift correction
/// 
/// Features:
/// ✅ Windows multimedia timer for 1ms precision (vs 15.6ms default)
/// ✅ Large persistent jitter buffer (survives barge-ins)
/// ✅ NAT keepalives for strict NATs (25s interval)
/// ✅ Symmetric RTP locking (dynamic endpoint detection)
/// ✅ Thread-safe disposal
/// </summary>
public sealed class DirectG711RtpPlayout : IDisposable
{
    private const int FRAME_MS = 20;
    private const int FRAME_SIZE = 160; // 20ms @ 8kHz G.711
    private const int MAX_QUEUE_FRAMES = 1500; // ~30s max buffer (safety cap)
    
    // v5.2: Increased jitter buffer for smoother audio during OpenAI bursts
    private const int JITTER_BUFFER_FRAMES = 25;  // 500ms buffer (handles bursty delivery)

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
    private bool _wasPlaying;
    private uint _timestamp;
    private byte _lastByte; // For fade-out
    private DateTime _lastRtpSendTime = DateTime.UtcNow;
    private volatile bool _isNewTalkSpurt; // Marker bit for first frame of new audio burst
    
    // NAT state
    private IPEndPoint? _lastRemoteEndpoint;
    private volatile bool _natBindingEstablished;

    // Multimedia timer state
    private bool _mmTimerActive;

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

        // New audio burst from AI - set marker bit for first frame
        _isNewTalkSpurt = true;

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
        _running = true;
        
        // Random start timestamp (RFC 3550 compliant)
        _timestamp = (uint)Random.Shared.Next();

        // Enable Windows multimedia timer for 1ms precision
        try
        {
            WinMmTimer.TimeBeginPeriod(1);
            _mmTimerActive = true;
        }
        catch { /* Non-Windows or no permission */ }

        // High-precision playout thread
        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "G711RtpPlayout"
        };
        _playoutThread.Start();
        
        OnLog?.Invoke($"[DirectG711RtpPlayout] Started (Native G.711, {JITTER_BUFFER_FRAMES * 20}ms buffer, mmTimer={_mmTimerActive})");
    }

    public void Stop()
    {
        _running = false;
        _playoutThread?.Join(500);
        _playoutThread = null;
        
        // Release multimedia timer
        if (_mmTimerActive)
        {
            try { WinMmTimer.TimeEndPeriod(1); } catch { }
            _mmTimerActive = false;
        }
        
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

        // Jitter buffer: wait until we have enough frames before starting playout
        if (_isBuffering)
        {
            if (currentCount >= JITTER_BUFFER_FRAMES)
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
            _framesSent++;
            _wasPlaying = true;
            _consecutiveUnderruns = 0; // Reset on successful dequeue
            _lastByte = audioFrame[FRAME_SIZE - 1];

            // Send with marker bit if this is the first frame of a new talk spurt
            bool marker = _isNewTalkSpurt;
            _isNewTalkSpurt = false;
            
            SendRtpFrame(audioFrame, marker);

            if (_framesSent % 500 == 0)
                OnLog?.Invoke($"[DirectG711RtpPlayout] Sent {_framesSent} frames (queue: {Volatile.Read(ref _queueCount)})");
            
            return;
        }
        else
        {
            // Queue empty - send silence immediately (no fade on G.711)
            // IMPORTANT: G.711 is logarithmic encoding, linear fade produces distorted audio!
            // Fading between G.711 values causes warbly/distorted sound.
            _consecutiveUnderruns++;
            _lastByte = _silence;
            frameToSend = _silenceFrame;
            
            // Fire OnQueueEmpty once when transitioning from playing to empty
            if (_wasPlaying)
            {
                _wasPlaying = false;
                _isBuffering = true;
                _isNewTalkSpurt = false; // Reset for next audio burst
                OnLog?.Invoke($"[DirectG711RtpPlayout] Queue empty after {_framesSent} frames");
                OnQueueEmpty?.Invoke();
            }
        }

        SendRtpFrame(frameToSend, false);
    }

    private void SendRtpFrame(byte[] frame, bool marker)
    {
        try
        {
            _mediaSession.SendRtpRaw(
                SDPMediaTypesEnum.audio,
                frame,
                _timestamp,
                marker ? 1 : 0,  // Marker bit for jitter buffer sync at talk spurt start
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

    private void SendRtpFrame(byte[] frame)
    {
        SendRtpFrame(frame, false);
    }

    /// <summary>
    /// Clear all buffered audio (call on barge-in).
    /// </summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out _))
            Interlocked.Decrement(ref _queueCount);

        _wasPlaying = false;
        _isBuffering = true;  // Will rebuffer to 300ms before resuming
        _consecutiveUnderruns = 0;
        _lastByte = _silence;
        _isNewTalkSpurt = false;
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
        try { SendRtpFrame(_silenceFrame, false); } catch { }
        
        Clear();
        GC.SuppressFinalize(this);
    }
}
