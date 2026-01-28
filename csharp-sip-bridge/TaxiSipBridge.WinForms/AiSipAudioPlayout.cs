using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// AI → SIP Audio Playout Engine.
/// Implements proper SIP telephony with:
/// ✔ 20ms frame packetization (160B)
/// ✔ Timer-driven RTP pacing
/// ✔ Underrun silence fill
/// ✔ Timestamp + sequence management
/// ✔ Multi-call readiness
/// ✔ Clean stop/start
/// </summary>
public class AiSipAudioPlayout : IDisposable
{
    private const int FRAME_SIZE = 160;        // 20ms @ 8kHz PCMA = 160 samples = 160 bytes
    private const int FRAME_MS = 20;           // 20ms per frame
    private const byte ALAW_SILENCE = 0xD5;    // A-law silence value
    private const int MAX_QUEUE_FRAMES = 50;   // 1 second max buffer (50 × 20ms)
    
    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private readonly VoIPMediaSession _mediaSession;
    
    private Thread? _playoutThread;
    private volatile bool _running;
    private volatile bool _disposed;
    
    // RTP state
    private ushort _seq;
    private uint _timestamp;
    
    // Resampler (24kHz → 8kHz) - using NAudio's WDL resampler
    private readonly object _resamplerLock = new();
    
    // Stats
    private int _framesSent;
    private int _silenceFrames;
    private int _droppedFrames;
    
    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;
    
    public int QueuedFrames => _frameQueue.Count;
    public int FramesSent => _framesSent;
    public int SilenceFrames => _silenceFrames;
    
    public AiSipAudioPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        
        // Initialize RTP state with random values
        var rnd = new Random();
        _seq = (ushort)rnd.Next(ushort.MaxValue);
        _timestamp = (uint)rnd.Next(int.MaxValue);
    }
    
    /// <summary>
    /// Buffer AI audio (PCM16 @ 24kHz) for playout.
    /// Resamples to 8kHz, encodes to A-law, and queues 20ms frames.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || _disposed || pcm24kBytes == null || pcm24kBytes.Length == 0)
            return;
        
        try
        {
            // Convert bytes to shorts
            var pcm24k = BytesToShorts(pcm24kBytes);
            
            // Resample 24kHz → 8kHz
            var pcm8k = Resample24kTo8k(pcm24k);
            
            // Encode to A-law
            var alaw = EncodeAlaw(pcm8k);
            
            // Split into 20ms frames and queue
            for (int i = 0; i < alaw.Length; i += FRAME_SIZE)
            {
                // Check for overflow
                if (_frameQueue.Count >= MAX_QUEUE_FRAMES)
                {
                    Interlocked.Increment(ref _droppedFrames);
                    continue; // Drop oldest-ish frames by not enqueueing
                }
                
                var frame = new byte[FRAME_SIZE];
                int len = Math.Min(FRAME_SIZE, alaw.Length - i);
                Buffer.BlockCopy(alaw, i, frame, 0, len);
                
                // Pad with silence if needed
                if (len < FRAME_SIZE)
                    Array.Fill(frame, ALAW_SILENCE, len, FRAME_SIZE - len);
                
                _frameQueue.Enqueue(frame);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ BufferAiAudio error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Start the playout thread.
    /// </summary>
    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;
        
        _framesSent = 0;
        _silenceFrames = 0;
        _droppedFrames = 0;
        
        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = $"AiSipPlayout-{Environment.CurrentManagedThreadId}"
        };
        
        _playoutThread.Start();
        Log("▶️ Playout started");
    }
    
    /// <summary>
    /// Stop the playout thread and clear queue.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        
        try { _playoutThread?.Join(500); } catch { }
        _playoutThread = null;
        
        // Clear queue
        while (_frameQueue.TryDequeue(out _)) { }
        
        Log($"⏹️ Playout stopped (sent={_framesSent}, silence={_silenceFrames}, dropped={_droppedFrames})");
    }
    
    /// <summary>
    /// Clear all queued frames (e.g., on barge-in).
    /// </summary>
    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _)) { }
        Log("🗑️ Queue cleared");
    }
    
    /// <summary>
    /// High-precision 20ms playout loop.
    /// </summary>
    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameMs = sw.Elapsed.TotalMilliseconds;
        bool wasEmpty = false;
        
        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            
            // Wait for next frame time
            if (now < nextFrameMs)
            {
                double waitMs = nextFrameMs - now;
                if (waitMs > 2)
                    Thread.Sleep((int)(waitMs - 1));
                else if (waitMs > 0.5)
                    Thread.SpinWait(500);
                continue;
            }
            
            // Get next frame or silence
            byte[] frame;
            if (_frameQueue.TryDequeue(out var queuedFrame))
            {
                frame = queuedFrame;
                wasEmpty = false;
            }
            else
            {
                // Underrun - send silence
                frame = new byte[FRAME_SIZE];
                Array.Fill(frame, ALAW_SILENCE);
                Interlocked.Increment(ref _silenceFrames);
                
                // Notify once when queue empties
                if (!wasEmpty)
                {
                    wasEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }
            
            // Send RTP
            SendRtpFrame(frame);
            
            // Schedule next frame
            nextFrameMs += FRAME_MS;
            
            // Drift correction: if we're way behind, reset
            if (now - nextFrameMs > 100)
            {
                Log($"⚠️ Drift correction: {now - nextFrameMs:F1}ms behind");
                nextFrameMs = now + FRAME_MS;
            }
        }
    }
    
    /// <summary>
    /// Send a single RTP frame.
    /// </summary>
    private void SendRtpFrame(byte[] frame)
    {
        try
        {
            // Use SIPSorcery's built-in audio sending
            // PT=8 is PCMA (A-law)
            _mediaSession.SendAudio((uint)FRAME_MS, frame);
            
            Interlocked.Increment(ref _framesSent);
            _seq++;
            _timestamp += FRAME_SIZE;
        }
        catch (Exception ex)
        {
            Log($"⚠️ RTP send error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Resample 24kHz PCM to 8kHz using NAudio's WDL resampler.
    /// </summary>
    private short[] Resample24kTo8k(short[] pcm24k)
    {
        lock (_resamplerLock)
        {
            return NAudioResampler.Resample(pcm24k, 24000, 8000);
        }
    }
    
    /// <summary>
    /// Convert byte array to short array (little-endian).
    /// </summary>
    private static short[] BytesToShorts(byte[] bytes)
    {
        var shorts = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, shorts, 0, bytes.Length);
        return shorts;
    }
    
    /// <summary>
    /// Encode PCM16 to A-law.
    /// </summary>
    private static byte[] EncodeAlaw(short[] pcm)
    {
        var alaw = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            alaw[i] = LinearToAlaw(pcm[i]);
        }
        return alaw;
    }
    
    /// <summary>
    /// ITU-T G.711 A-law encoding.
    /// </summary>
    private static byte LinearToAlaw(short linear)
    {
        int sign = (linear >> 8) & 0x80;
        if (sign != 0) linear = (short)-linear;
        if (linear > 32635) linear = 32635;
        
        int exponent = 7;
        int expMask = 0x4000;
        
        for (; exponent > 0; exponent--)
        {
            if ((linear & expMask) != 0) break;
            expMask >>= 1;
        }
        
        int mantissa = (linear >> (exponent == 0 ? 4 : exponent + 3)) & 0x0F;
        byte alaw = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)(alaw ^ 0xD5); // Toggle even bits
    }
    
    private void Log(string msg) => OnLog?.Invoke($"[AiPlayout] {msg}");
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Stop();
        
        GC.SuppressFinalize(this);
    }
}
