// Version: 6.0 - SIPSorcery-native audio bridge
using System;
using System.Collections.Concurrent;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge.Audio;

/// <summary>
/// Clean audio bridge using SIPSorcery's native audio handling.
/// 
/// Replaces complex custom playout logic with SIPSorcery's 
/// built-in RTP handling and jitter management.
/// 
/// Features:
/// - Uses SIPSorcery's SendRtpRaw for reliable RTP delivery
/// - Simple ConcurrentQueue for audio buffering
/// - NAudio pipeline for codec conversion
/// - Minimal custom code - let the libraries do the work
/// </summary>
public sealed class SipSorceryAudioBridge : IDisposable
{
    // ===========================================
    // CONSTANTS
    // ===========================================
    private const int FRAME_SIZE_8K = 160;       // 20ms @ 8kHz G.711
    private const int FRAME_MS = 20;
    private const int JITTER_BUFFER_MS = 400;    // 400ms buffer
    private const int JITTER_BUFFER_FRAMES = JITTER_BUFFER_MS / FRAME_MS;
    private const int MAX_QUEUE_FRAMES = 1500;   // ~30s safety cap
    private const byte ALAW_SILENCE = 0xD5;
    private const byte MULAW_SILENCE = 0xFF;
    
    // ===========================================
    // STATE
    // ===========================================
    private readonly VoIPMediaSession _mediaSession;
    private readonly NAudioPipeline _pipeline;
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private readonly byte _silenceByte;
    private readonly byte _payloadType;
    private readonly byte[] _silenceFrame;
    
    private Thread? _playoutThread;
    private volatile bool _running;
    private volatile bool _isBuffering = true;
    private int _queueCount;
    private int _disposed;
    private uint _rtpTimestamp;
    private int _framesSent;
    
    // ===========================================
    // EVENTS
    // ===========================================
    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;
    
    // ===========================================
    // PROPERTIES
    // ===========================================
    public int PendingFrameCount => Volatile.Read(ref _queueCount);
    public bool IsPlaying => !_isBuffering && _queueCount > 0;
    
    // ===========================================
    // CONSTRUCTOR
    // ===========================================
    public SipSorceryAudioBridge(VoIPMediaSession mediaSession, bool useALaw = true)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _pipeline = new NAudioPipeline();
        
        _silenceByte = useALaw ? ALAW_SILENCE : MULAW_SILENCE;
        _payloadType = useALaw ? (byte)8 : (byte)0;
        
        _silenceFrame = new byte[FRAME_SIZE_8K];
        Array.Fill(_silenceFrame, _silenceByte);
        
        // Random start timestamp (RFC 3550)
        _rtpTimestamp = (uint)Random.Shared.Next();
    }
    
    // ===========================================
    // AUDIO OUTPUT (OpenAI → SIP)
    // ===========================================
    
    /// <summary>
    /// Push G.711 audio from OpenAI to output queue.
    /// Used for native G.711 mode (no conversion needed).
    /// </summary>
    public void PushG711(byte[] g711)
    {
        if (Volatile.Read(ref _disposed) != 0 || g711 == null || g711.Length == 0)
            return;
        
        // Split into 20ms frames
        for (int i = 0; i < g711.Length; i += FRAME_SIZE_8K)
        {
            // Safety cap
            while (Volatile.Read(ref _queueCount) >= MAX_QUEUE_FRAMES)
            {
                if (_outboundQueue.TryDequeue(out _))
                    Interlocked.Decrement(ref _queueCount);
            }
            
            var frame = new byte[FRAME_SIZE_8K];
            int count = Math.Min(FRAME_SIZE_8K, g711.Length - i);
            Array.Copy(g711, i, frame, 0, count);
            
            // Pad incomplete frames
            if (count < FRAME_SIZE_8K)
                Array.Fill(frame, _silenceByte, count, FRAME_SIZE_8K - count);
            
            _outboundQueue.Enqueue(frame);
            Interlocked.Increment(ref _queueCount);
        }
    }
    
    /// <summary>
    /// Push PCM16 24kHz audio from OpenAI to output queue.
    /// Converts to G.711 using NAudio pipeline.
    /// </summary>
    public void PushPcm24k(byte[] pcm24k)
    {
        if (Volatile.Read(ref _disposed) != 0 || pcm24k == null || pcm24k.Length == 0)
            return;
        
        // Convert using NAudio pipeline
        var g711 = _pipeline.ConvertPcm24kToALaw(pcm24k);
        PushG711(g711);
    }
    
    /// <summary>
    /// Clear all buffered audio (for barge-in).
    /// </summary>
    public void Clear()
    {
        while (_outboundQueue.TryDequeue(out _))
            Interlocked.Decrement(ref _queueCount);
        
        _isBuffering = true;
        OnLog?.Invoke("[SipSorceryAudioBridge] Queue cleared");
    }
    
    // ===========================================
    // AUDIO INPUT (SIP → OpenAI)
    // ===========================================
    
    /// <summary>
    /// Convert incoming SIP G.711 A-law to PCM16 24kHz for OpenAI.
    /// </summary>
    public byte[] ConvertIngressToOpenAI(byte[] alaw8k)
    {
        return _pipeline.ConvertALawToPcm24k(alaw8k);
    }
    
    /// <summary>
    /// Decode A-law to PCM16 (same sample rate).
    /// For native G.711 mode, returns raw bytes.
    /// </summary>
    public byte[] ProcessIngress(byte[] alaw8k, bool nativeG711Mode)
    {
        if (nativeG711Mode)
            return alaw8k; // Passthrough for native G.711 mode
        
        return ConvertIngressToOpenAI(alaw8k);
    }
    
    // ===========================================
    // PLAYOUT THREAD
    // ===========================================
    
    public void Start()
    {
        if (_running) return;
        
        _framesSent = 0;
        _isBuffering = true;
        _running = true;
        
        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "SipSorceryAudioBridge"
        };
        _playoutThread.Start();
        
        OnLog?.Invoke($"[SipSorceryAudioBridge] Started ({JITTER_BUFFER_MS}ms buffer)");
    }
    
    public void Stop()
    {
        _running = false;
        _playoutThread?.Join(500);
        _playoutThread = null;
        
        OnLog?.Invoke($"[SipSorceryAudioBridge] Stopped ({_framesSent} frames sent)");
    }
    
    private void PlayoutLoop()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        double nextFrameTimeMs = stopwatch.Elapsed.TotalMilliseconds;
        bool wasPlaying = false;
        
        while (_running && Volatile.Read(ref _disposed) == 0)
        {
            double now = stopwatch.Elapsed.TotalMilliseconds;
            
            // Wait for next frame time
            if (now < nextFrameTimeMs)
            {
                double waitMs = nextFrameTimeMs - now;
                if (waitMs > 2)
                    Thread.Sleep((int)(waitMs - 1));
                continue;
            }
            
            // Send frame
            int currentCount = Volatile.Read(ref _queueCount);
            
            // Jitter buffer: wait until we have enough frames
            if (_isBuffering)
            {
                if (currentCount >= JITTER_BUFFER_FRAMES)
                {
                    _isBuffering = false;
                    OnLog?.Invoke($"[SipSorceryAudioBridge] Buffer ready ({currentCount} frames)");
                }
                else
                {
                    SendFrame(_silenceFrame, false);
                    nextFrameTimeMs += FRAME_MS;
                    continue;
                }
            }
            
            if (_outboundQueue.TryDequeue(out var frame))
            {
                Interlocked.Decrement(ref _queueCount);
                _framesSent++;
                wasPlaying = true;
                
                // Marker bit for first frame of talk spurt
                bool marker = !wasPlaying;
                SendFrame(frame, marker);
                
                if (_framesSent % 500 == 0)
                    OnLog?.Invoke($"[SipSorceryAudioBridge] Sent {_framesSent} frames (queue: {Volatile.Read(ref _queueCount)})");
            }
            else
            {
                // Queue empty
                if (wasPlaying)
                {
                    wasPlaying = false;
                    _isBuffering = true;
                    OnLog?.Invoke($"[SipSorceryAudioBridge] Queue empty after {_framesSent} frames");
                    OnQueueEmpty?.Invoke();
                }
                SendFrame(_silenceFrame, false);
            }
            
            // Schedule next frame
            nextFrameTimeMs += FRAME_MS;
            
            // Drift correction
            now = stopwatch.Elapsed.TotalMilliseconds;
            if (now - nextFrameTimeMs > 40)
                nextFrameTimeMs = now + FRAME_MS;
        }
    }
    
    private void SendFrame(byte[] frame, bool marker)
    {
        try
        {
            _mediaSession.SendRtpRaw(
                SDPMediaTypesEnum.audio,
                frame,
                _rtpTimestamp,
                marker ? 1 : 0,
                _payloadType
            );
            _rtpTimestamp += FRAME_SIZE_8K;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[SipSorceryAudioBridge] SendRtpRaw error: {ex.Message}");
        }
    }
    
    // ===========================================
    // DISPOSAL
    // ===========================================
    
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        
        Stop();
        Clear();
        _pipeline.Dispose();
        
        GC.SuppressFinalize(this);
    }
}
