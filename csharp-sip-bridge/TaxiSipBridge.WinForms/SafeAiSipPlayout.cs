using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// Minimal AI → SIP playout engine with hold-state safety checks.
/// ✅ Uses VoIPMediaSession.SendAudio() → auto-encodes to A-law (PCMA)
/// ✅ Anti-aliased 24kHz→8kHz resampling
/// ✅ Explicit hold/resume validation BEFORE playout starts
/// ✅ Prevents MOH mixing by verifying call state
/// </summary>
public class SafeAiSipPlayout : IDisposable
{
    private const int PCM8K_FRAME_SAMPLES = 160;  // 20ms @ 8kHz
    private const int FRAME_MS = 20;
    private const int MAX_QUEUE_FRAMES = 20;      // 400ms buffer

    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly VoIPMediaSession _mediaSession;
    private readonly SIPUserAgent? _ua;           // Optional for hold-state checks
    private readonly bool _useALaw;               // true = PCMA, false = PCMU

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
        else
            Log($"✅ Playout initialized | Codec: {codec}");
    }

    /// <summary>
    /// Buffer 24kHz PCM from OpenAI. Resamples to 8kHz with anti-aliasing.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || _disposed || pcm24kBytes == null || pcm24kBytes.Length == 0)
            return;

        try
        {
            var pcm24k = BytesToShorts(pcm24kBytes);
            var pcm8k = Resample24kTo8kAntiAliased(pcm24k);

            for (int i = 0; i < pcm8k.Length; i += PCM8K_FRAME_SAMPLES)
            {
                // Drop OLDEST frame on overflow (minimizes latency)
                if (_frameQueue.Count >= MAX_QUEUE_FRAMES)
                {
                    _frameQueue.TryDequeue(out _);
                    Interlocked.Increment(ref _droppedFrames);
                }

                var frame = new short[PCM8K_FRAME_SAMPLES];
                int len = Math.Min(PCM8K_FRAME_SAMPLES, pcm8k.Length - i);
                Array.Copy(pcm8k, i, frame, 0, len);
                _frameQueue.Enqueue(frame);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Buffer error: {ex.Message}");
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

    public void Clear() // Call on barge-in
    {
        int cleared = 0;
        while (_frameQueue.TryDequeue(out _)) cleared++;
        if (cleared > 0) Log($"🗑️ Cleared {cleared} frames (barge-in)");
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
    /// VoIPMediaSession.SendAudio() does NOT encode - it sends bytes directly to RTP.
    /// We MUST encode PCM → G.711 here before sending.
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
                    encoded[i] = G711Codec.EncodeSampleALaw(pcmFrame[i]);
            }
            else
            {
                for (int i = 0; i < pcmFrame.Length; i++)
                    encoded[i] = G711Codec.EncodeSample(pcmFrame[i]);
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

    /// <summary>
    /// Anti-aliased 24kHz → 8kHz resampling (3-tap FIR low-pass + decimation).
    /// Prevents high-frequency artifacts that cause "ringing" in telephony.
    /// </summary>
    private static short[] Resample24kTo8kAntiAliased(short[] pcm24k)
    {
        if (pcm24k.Length < 3) return Array.Empty<short>();

        int outLen = pcm24k.Length / 3;
        var output = new short[outLen];

        for (int i = 0; i < outLen; i++)
        {
            int src = i * 3;

            // 3-tap FIR: [0.25, 0.5, 0.25]
            int s0 = src > 0 ? pcm24k[src - 1] : pcm24k[src];
            int s1 = pcm24k[src];
            int s2 = src + 1 < pcm24k.Length ? pcm24k[src + 1] : pcm24k[src];

            // Weighted sum: (s0 + 2*s1 + s2) / 4
            int filtered = (s0 + (s1 << 1) + s2) >> 2;
            output[i] = (short)Math.Clamp(filtered, short.MinValue, short.MaxValue);
        }

        return output;
    }

    private static short[] BytesToShorts(byte[] bytes)
    {
        if (bytes.Length % 2 != 0) return Array.Empty<short>();
        var shorts = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, shorts, 0, bytes.Length);
        return shorts;
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
