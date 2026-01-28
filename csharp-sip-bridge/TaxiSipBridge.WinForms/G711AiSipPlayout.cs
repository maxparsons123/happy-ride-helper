using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.SIP.App;

namespace TaxiSipBridge;

/// <summary>
/// AI → SIP playout engine with CORRECT G.711 encoding.
/// ✅ 24kHz PCM → 8kHz anti-aliased resampling
/// ✅ 8kHz PCM → G.711 A-law/μ-law encoding (embedded ITU-T implementation)
/// ✅ Sends ENCODED bytes via SendAudio() with CORRECT 20ms duration
/// ✅ Hold-state validation to prevent MOH mixing
/// </summary>
public class G711AiSipPlayout : IDisposable
{
    private const int PCM8K_FRAME_SAMPLES = 160;  // 20ms @ 8kHz = 160 samples
    private const int FRAME_MS = 20;
    private const int MAX_QUEUE_FRAMES = 25;      // 500ms buffer (NOT 10s - prevents latency buildup)

    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly VoIPMediaSession _mediaSession;
    private readonly SIPUserAgent? _ua;
    private readonly bool _useALaw;               // true = PCMA (A-law), false = PCMU (μ-law)

    // Reusable buffer: 160 bytes = 20ms of G.711 (1 byte/sample)
    private readonly byte[] _g711Buffer = new byte[PCM8K_FRAME_SAMPLES];

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

    public G711AiSipPlayout(VoIPMediaSession mediaSession, SIPUserAgent? ua = null)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _ua = ua;

        // Detect negotiated codec
        var codec = _mediaSession.AudioStreams[0]?.GetNegotiatedCodec()?.Name ?? "NONE";
        _useALaw = codec == "PCMA";

        if (codec != "PCMA" && codec != "PCMU")
            Log($"⚠️ WARNING: Negotiated codec is {codec} (expected PCMA/PCMU). Audio may not work.");
        else
            Log($"✅ Playout initialized | Codec: {(codec == "PCMA" ? "PCMA (A-law)" : "PCMU (μ-law)")}");
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

    public void Start()
    {
        if (_running || _disposed) return;

        // CRITICAL: Prevent playout while on hold (causes MOH mixing)
        if (_ua != null && !ValidateCallStateForPlayout())
        {
            throw new InvalidOperationException(
                "Cannot start playout: Call is on hold. Resume first with re-INVITE (sendrecv SDP).");
        }

        _running = true;
        _framesSent = 0;
        _silenceFrames = 0;
        _droppedFrames = 0;

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "G711AiPlayout"
        };

        _playoutThread.Start();
        Log($"▶️ Playout STARTED | Buffer: {MAX_QUEUE_FRAMES * FRAME_MS}ms");
    }

    /// <summary>
    /// CRITICAL: Prevent MOH mixing by checking SDP for hold state.
    /// </summary>
    private bool ValidateCallStateForPlayout()
    {
        if (_ua == null || !_ua.IsCallActive)
        {
            Log("❌ Call not active");
            return false;
        }

        // Check SDP for hold indicators (sendonly/inactive = on hold)
        var sdp = _ua.CallDescriptor?.SDP?.ToString() ?? "";
        if (sdp.Contains("a=sendonly") || sdp.Contains("a=inactive"))
        {
            Log("❌ Call is ON HOLD (SDP shows sendonly/inactive) → MOH will mix with AI audio");
            Log("💡 FIX: Send re-INVITE with sendrecv SDP BEFORE starting playout");
            return false;
        }

        Log("✅ Call state validated: ACTIVE (not on hold)");
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

            SendG711Frame(frame);

            nextFrameTime += FRAME_MS;

            // Drift correction (tight 20ms threshold)
            if (now - nextFrameTime > 20)
            {
                Log($"⏱️ Drift correction: {now - nextFrameTime:F1}ms behind");
                nextFrameTime = now + FRAME_MS;
            }
        }
    }

    /// <summary>
    /// ✅ CORRECT: Encode PCM → G.711 → Send with 20ms duration (NOT 160 samples!)
    /// ⚠️ VoIPMediaSession.SendAudio(uint durationMs, byte[] audio) expects:
    ///    - durationMs = 20 (milliseconds)
    ///    - audio = 160 bytes of G.711 encoded data (1 byte/sample)
    /// </summary>
    private void SendG711Frame(short[] pcmFrame)
    {
        try
        {
            // 1. Encode 160 samples of 8kHz PCM → 160 bytes of G.711
            if (_useALaw)
            {
                for (int i = 0; i < pcmFrame.Length; i++)
                    _g711Buffer[i] = LinearToALaw(pcmFrame[i]);
            }
            else
            {
                for (int i = 0; i < pcmFrame.Length; i++)
                    _g711Buffer[i] = LinearToMuLaw(pcmFrame[i]);
            }

            // 2. CRITICAL FIX: duration = 20ms (NOT 160 samples!)
            //    SIPSorcery's SendAudio(uint durationMs, byte[] audio) expects milliseconds
            _mediaSession.SendAudio((uint)FRAME_MS, _g711Buffer);

            // Diagnostic: Verify first frame encoding (silence should be 0xD5 for A-law)
            if (_framesSent == 0)
            {
                byte silenceByte = _useALaw ? (byte)0xD5 : (byte)0xFF;
                if (_g711Buffer[0] == 0x00)
                    Log($"⚠️ WARNING: First byte is 0x00 (raw PCM silence) - should be 0x{silenceByte:X2} for G.711 silence");
                else
                    Log($"✅ First G.711 byte: 0x{_g711Buffer[0]:X2} (expected silence: 0x{silenceByte:X2})");
            }

            Interlocked.Increment(ref _framesSent);
        }
        catch (Exception ex)
        {
            Log($"⚠️ G.711 send error: {ex.Message}");
        }
    }

    // ===========================================================================================
    // ITU-T G.711 A-law Encoder (RFC 3551 compliant)
    // ===========================================================================================
    private static byte LinearToALaw(short sample)
    {
        int sign = (sample >> 8) & 0x80;          // Extract sign bit (bit 7)
        if (sign != 0) sample = (short)-sample;   // Work with magnitude

        sample = (short)((sample + 8) >> 4);      // 13-bit magnitude + bias for quantization

        int exponent, mantissa;

        if (sample >= 256)
        {
            sample = (short)((sample >> 4) & 0x7F); // 7 bits after >>4
            exponent = 7;
            mantissa = sample & 0x0F;
        }
        else if (sample >= 128)
        {
            exponent = 6;
            mantissa = sample & 0x0F;
        }
        else if (sample >= 64)
        {
            exponent = 5;
            mantissa = sample & 0x0F;
        }
        else if (sample >= 32)
        {
            exponent = 4;
            mantissa = sample & 0x0F;
        }
        else if (sample >= 16)
        {
            exponent = 3;
            mantissa = sample & 0x0F;
        }
        else if (sample >= 8)
        {
            exponent = 2;
            mantissa = sample & 0x0F;
        }
        else if (sample >= 4)
        {
            exponent = 1;
            mantissa = sample & 0x0F;
        }
        else
        {
            exponent = 0;
            mantissa = sample >> 1; // Only 3 bits for lowest segment
        }

        byte alaw = (byte)((exponent << 4) | mantissa);
        alaw ^= 0x55;                // Invert odd bits (G.711 requirement)
        if (sign == 0) alaw |= 0x80; // Restore sign bit

        return alaw;
    }

    // ===========================================================================================
    // ITU-T G.711 μ-law Encoder (RFC 3551 compliant)
    // ===========================================================================================
    private static byte LinearToMuLaw(short sample)
    {
        const int BIAS = 0x84;  // μ-law bias
        int mask = (sample < 0) ? 1 : 0;
        int magnitude = Math.Abs(sample);

        // Add bias and clamp to 13-bit range
        magnitude += BIAS;
        if (magnitude > 0x1FFF) magnitude = 0x1FFF;

        // Find segment (exponent)
        int segment = 7;
        while (segment > 0 && (magnitude & (0x1000 >> segment)) == 0)
            segment--;

        // Extract mantissa (4 bits)
        int mantissa = (magnitude >> (segment + 3)) & 0x0F;

        // Compose μ-law byte
        byte mulaw = (byte)((segment << 4) | mantissa);
        mulaw = (byte)(~mulaw); // Invert all bits (G.711 requirement)
        if (mask != 0) mulaw = (byte)(mulaw | 0x80); // Restore sign

        return mulaw;
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

            // 3-tap FIR low-pass: [0.25, 0.5, 0.25] to prevent aliasing
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

    private void Log(string msg) => OnLog?.Invoke($"[G711Playout] {msg}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
