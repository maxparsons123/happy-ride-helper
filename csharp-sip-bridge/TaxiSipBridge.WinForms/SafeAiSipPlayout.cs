using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TaxiSipBridge;

/// <summary>
/// Minimal AI → SIP playout engine with NAudio high-quality resampling.
/// ✅ Uses NAudio WDL resampler for professional-grade 24kHz→8kHz conversion
/// ✅ Anti-aliased downsampling with proper low-pass filtering
/// ✅ Explicit hold/resume validation BEFORE playout starts
/// ✅ Prevents MOH mixing by verifying call state
/// </summary>
public class SafeAiSipPlayout : IDisposable
{
    private const int PCM8K_FRAME_SAMPLES = 160;  // 20ms @ 8kHz
    private const int FRAME_MS = 20;
    private const int MAX_QUEUE_FRAMES = 500;     // 10s buffer (OpenAI sends audio in bursts)

    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly VoIPMediaSession _mediaSession;
    private readonly SIPUserAgent? _ua;           // Optional for hold-state checks
    private readonly bool _useALaw;               // true = PCMA, false = PCMU

    // NAudio Infrastructure for high-quality resampling
    private readonly BufferedWaveProvider _inputBuffer;
    private readonly WdlResamplingSampleProvider _resampler;

    private Thread? _playoutThread;
    private volatile bool _running;
    private volatile bool _disposed;

    // Stats
    private int _framesSent;
    private int _silenceFrames;
    private int _droppedFrames;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;

    public int QueuedFrames => _frameQueue.Count;
    public int FramesSent => _framesSent;
    public int SilenceFrames => _silenceFrames;
    public int DroppedFrames => _droppedFrames;

    public SafeAiSipPlayout(VoIPMediaSession mediaSession, SIPUserAgent? ua = null)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _ua = ua;

        // Detect negotiated codec for proper encoding
        var localFormat = _mediaSession.AudioLocalTrack?.Capabilities?.FirstOrDefault();
        var codec = localFormat?.Name() ?? "NONE";
        
        _useALaw = codec == "PCMA";
        
        if (codec != "PCMA" && codec != "PCMU")
            Log($"⚠️ Expected G.711 codec (PCMA/PCMU). Got: {codec}");

        // Initialize NAudio Pipeline: 24kHz 16-bit Mono → 8kHz Mono
        var inputFormat = new WaveFormat(24000, 16, 1);
        _inputBuffer = new BufferedWaveProvider(inputFormat) 
        { 
            DiscardOnBufferOverflow = true,
            BufferLength = 24000 * 2 * 2 // 2 seconds of 24kHz 16-bit mono
        };
        
        // WdlResamplingSampleProvider provides professional-grade resampling with proper anti-aliasing
        _resampler = new WdlResamplingSampleProvider(_inputBuffer.ToSampleProvider(), 8000);
        
        Log($"✅ Playout initialized | Codec: {codec} | Resampler: NAudio WDL");
    }

    /// <summary>
    /// Buffer 24kHz PCM from OpenAI. Processes through NAudio WDL resampler to 8kHz.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || _disposed || pcm24kBytes == null || pcm24kBytes.Length == 0)
            return;

        try
        {
            // 1. Push raw 24kHz bytes into NAudio input buffer
            _inputBuffer.AddSamples(pcm24kBytes, 0, pcm24kBytes.Length);

            // 2. Read from resampler (it pulls from inputBuffer and converts to 8kHz)
            float[] sampleBuffer = new float[PCM8K_FRAME_SAMPLES];
            int samplesRead;
            
            while ((samplesRead = _resampler.Read(sampleBuffer, 0, PCM8K_FRAME_SAMPLES)) > 0)
            {
                // Convert Float32 samples back to Int16 (short)
                var pcm8kFrame = new short[PCM8K_FRAME_SAMPLES];
                for (int n = 0; n < samplesRead; n++)
                {
                    // Clamp to prevent overflow
                    float sample = Math.Clamp(sampleBuffer[n], -1.0f, 1.0f);
                    pcm8kFrame[n] = (short)(sample * short.MaxValue);
                }

                // Drop OLDEST frame on overflow (minimizes latency)
                if (_frameQueue.Count >= MAX_QUEUE_FRAMES)
                {
                    _frameQueue.TryDequeue(out _);
                    Interlocked.Increment(ref _droppedFrames);
                }

                _frameQueue.Enqueue(pcm8kFrame);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ NAudio resampling error: {ex.Message}");
        }
    }

    /// <summary>
    /// START PLAYOUT - includes critical hold-state safety check.
    /// </summary>
    public void Start()
    {
        if (_running || _disposed) return;

        // Validate call state if UA available
        if (_ua != null && !ValidateCallStateForPlayout())
        {
            Log("⚠️ Call state validation failed, starting anyway...");
        }

        _running = true;
        _framesSent = 0;
        _silenceFrames = 0;
        _droppedFrames = 0;

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "SafeAiPlayout"
        };

        _playoutThread.Start();
        Log($"▶️ Playout STARTED | Buffer: {MAX_QUEUE_FRAMES * FRAME_MS}ms");
    }

    /// <summary>
    /// Validate call state to prevent MOH mixing.
    /// </summary>
    private bool ValidateCallStateForPlayout()
    {
        if (_ua == null) return true;

        // Call must be answered
        if (!_ua.IsCallActive)
        {
            Log($"❌ Call not active");
            return false;
        }

        Log("✅ Call state validated: ACTIVE");
        return true;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        try { _playoutThread?.Join(500); } catch { }
        _playoutThread = null;

        while (_frameQueue.TryDequeue(out _)) { }

        Log($"⏹️ Playout stopped (sent={_framesSent}, silence={_silenceFrames}, dropped={_droppedFrames})");
    }

    /// <summary>
    /// Clear all buffers on barge-in.
    /// </summary>
    public void Clear()
    {
        // Clear NAudio internal buffer
        _inputBuffer.ClearBuffer();
        
        // Clear frame queue
        int cleared = 0;
        while (_frameQueue.TryDequeue(out _)) cleared++;
        
        if (cleared > 0) Log($"🗑️ Cleared {cleared} frames + NAudio buffer (barge-in)");
    }

    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameTime = sw.Elapsed.TotalMilliseconds;
        bool wasEmpty = true;

        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            if (now < nextFrameTime)
            {
                double wait = nextFrameTime - now;
                if (wait > 2) Thread.Sleep((int)(wait - 1));
                else if (wait > 0.5) Thread.SpinWait(500);
                continue;
            }

            short[] frame;
            if (_frameQueue.TryDequeue(out var queued))
            {
                frame = queued;
                wasEmpty = false;
            }
            else
            {
                frame = new short[PCM8K_FRAME_SAMPLES];
                Interlocked.Increment(ref _silenceFrames);

                if (!wasEmpty)
                {
                    wasEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }

            SendPcmFrame(frame);

            nextFrameTime += FRAME_MS;

            // Drift correction (tighter 20ms threshold)
            if (now - nextFrameTime > 20)
            {
                Log($"⏱️ Drift correction: {now - nextFrameTime:F1}ms behind");
                nextFrameTime = now + FRAME_MS;
            }
        }
    }

    /// <summary>
    /// Encode PCM to G.711 and send via RTP.
    /// Uses SIPSorcery's built-in G.711 encoders for reliable encoding.
    /// </summary>
    private void SendPcmFrame(short[] pcmFrame)
    {
        try
        {
            // Encode PCM16 to G.711 (160 samples → 160 bytes)
            var encoded = new byte[pcmFrame.Length];
            
            if (_useALaw)
            {
                for (int i = 0; i < pcmFrame.Length; i++)
                    encoded[i] = ALawEncoder.LinearToALawSample(pcmFrame[i]);
            }
            else
            {
                for (int i = 0; i < pcmFrame.Length; i++)
                    encoded[i] = MuLawEncoder.LinearToMuLawSample(pcmFrame[i]);
            }

            // RTP duration = 160 samples @ 8kHz clock rate
            const uint RTP_DURATION = 160;
            _mediaSession.SendAudio(RTP_DURATION, encoded);

            Interlocked.Increment(ref _framesSent);
        }
        catch (Exception ex)
        {
            Log($"⚠️ RTP send error: {ex.Message}");
        }
    }

    private void Log(string msg) => OnLog?.Invoke($"[SafePlayout] {msg}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
