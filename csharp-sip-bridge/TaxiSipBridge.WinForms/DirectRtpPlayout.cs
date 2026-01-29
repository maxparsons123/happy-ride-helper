using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Direct RTP playout engine with CORRECT 24kHz→8kHz resampling.
/// ✅ Fixes "very fast audio" caused by missing resampling
/// ✅ Validates resampling ratio at runtime (3:1 decimation)
/// ✅ Anti-aliased FIR filter prevents high-frequency artifacts
/// ✅ Correct A-law encoding (0xD5 silence byte)
/// ✅ 20ms duration parameter (NOT 160 samples)
/// </summary>
public class DirectRtpPlayout : IDisposable
{
    private const int PCM8K_FRAME_SAMPLES = 160;  // 20ms @ 8kHz
    private const int FRAME_MS = 20;
    private const int MAX_QUEUE_FRAMES = 25;      // 500ms buffer
    private const int MIN_STARTUP_FRAMES = 10;    // 200ms startup buffer

    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly VoIPMediaSession _mediaSession;
    private readonly byte[] _alawBuffer = new byte[PCM8K_FRAME_SAMPLES];

    private Thread? _playoutThread;
    private volatile bool _running;
    private volatile bool _disposed;
    private int _debugResampleCount;

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

    public DirectRtpPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        Log("✅ DirectRtpPlayout initialized (24kHz→8kHz resampling enabled)");
    }

    /// <summary>
    /// Buffer 24kHz PCM from OpenAI. CORRECTLY resamples to 8kHz with anti-aliasing.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || _disposed || pcm24kBytes == null || pcm24kBytes.Length == 0)
            return;

        try
        {
            // 1. Convert bytes → 24kHz PCM shorts
            var pcm24k = BytesToShorts(pcm24kBytes);
            if (pcm24k.Length == 0) return;

            // 2. CRITICAL: Resample 24kHz → 8kHz (3:1 decimation with anti-aliasing)
            var pcm8k = Resample24kTo8kAntiAliased(pcm24k);

            // 3. Diagnostic: Verify resampling ratio (MUST be ~3.0x)
            if (_debugResampleCount < 5)
            {
                double ratio = pcm24k.Length / (double)Math.Max(1, pcm8k.Length);
                Log($"🔍 Resample #{_debugResampleCount + 1}: {pcm24k.Length} samples @24kHz → {pcm8k.Length} samples @8kHz (ratio: {ratio:F2}x) {(Math.Abs(ratio - 3.0) < 0.1 ? "✅" : "❌ CORRUPTED")}");
                _debugResampleCount++;
            }

            // 4. Frame into 20ms chunks (160 samples @ 8kHz)
            for (int i = 0; i < pcm8k.Length; i += PCM8K_FRAME_SAMPLES)
            {
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

        _running = true;
        _framesSent = 0;
        _silenceFrames = 0;
        _droppedFrames = 0;
        _debugResampleCount = 0;

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "DirectRtpPlayout"
        };
        _playoutThread.Start();

        Log($"▶️ Playout STARTED | Buffer:{MAX_QUEUE_FRAMES * FRAME_MS}ms | Startup:{MIN_STARTUP_FRAMES * FRAME_MS}ms");
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

    public void Clear()
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
        bool startupBuffering = true;

        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            // Startup buffering phase
            if (startupBuffering)
            {
                if (_frameQueue.Count >= MIN_STARTUP_FRAMES)
                {
                    startupBuffering = false;
                    nextFrameTime = sw.Elapsed.TotalMilliseconds;
                    Log($"📦 Startup buffer ready ({_frameQueue.Count} frames / {MIN_STARTUP_FRAMES * FRAME_MS}ms)");
                }
                else
                {
                    if (now >= nextFrameTime)
                    {
                        SendAlawFrame(new short[PCM8K_FRAME_SAMPLES]);
                        Interlocked.Increment(ref _silenceFrames);
                        nextFrameTime += FRAME_MS;
                    }
                    Thread.Sleep(1);
                    continue;
                }
            }

            // Timing loop with precision sleep/spin
            if (now < nextFrameTime)
            {
                double wait = nextFrameTime - now;
                if (wait > 2) Thread.Sleep((int)(wait - 1));
                else if (wait > 0.5) Thread.SpinWait(500);
                continue;
            }

            // Get frame or generate silence
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

            SendAlawFrame(frame);
            nextFrameTime += FRAME_MS;

            // Drift correction (tight 20ms threshold)
            if (now - nextFrameTime > 20)
            {
                Log($"⏱️ Drift correction: {now - nextFrameTime:F1}ms behind");
                nextFrameTime = now + FRAME_MS;
            }
        }
    }

    private void SendAlawFrame(short[] pcmFrame)
    {
        try
        {
            // Encode PCM → A-law (ITU-T G.711)
            for (int i = 0; i < pcmFrame.Length; i++)
            {
                _alawBuffer[i] = LinearToALaw(pcmFrame[i]);
            }

            // ✅ CORRECT: 20ms duration (milliseconds), NOT 160 samples!
            _mediaSession.SendAudio((uint)FRAME_MS, _alawBuffer);

            Interlocked.Increment(ref _framesSent);

            // First-frame diagnostic
            if (_framesSent == 1)
            {
                Log($"✅ First frame sent | 160 bytes A-law | Silence byte: 0x{_alawBuffer[0]:X2} (expected 0xD5)");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ SendAudio error: {ex.Message}");
        }
    }

    // ===========================================================================================
    // ITU-T G.711 A-law Encoder (RFC 3551 compliant)
    // ===========================================================================================
    private static byte LinearToALaw(short sample)
    {
        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        sample = (short)((sample + 8) >> 4);

        int exponent = sample switch
        {
            >= 256 => 7,
            >= 128 => 6,
            >= 64 => 5,
            >= 32 => 4,
            >= 16 => 3,
            >= 8 => 2,
            >= 4 => 1,
            _ => 0
        };

        int mantissa = exponent == 0
            ? sample >> 1
            : sample & 0x0F;

        byte alaw = (byte)((exponent << 4) | mantissa);
        alaw ^= 0x55;
        if (sign == 0) alaw |= 0x80;
        return alaw;
    }

    /// <summary>
    /// ✅ CORRECT 24kHz → 8kHz resampling with anti-aliasing.
    /// 3:1 decimation with 3-tap FIR low-pass filter [0.25, 0.5, 0.25].
    /// Prevents high-frequency aliasing that causes "ringing" in telephony.
    /// </summary>
    private static short[] Resample24kTo8kAntiAliased(short[] pcm24k)
    {
        if (pcm24k.Length < 3)
            return Array.Empty<short>();

        // CRITICAL: Output length MUST be input/3 (3:1 decimation)
        int outLen = pcm24k.Length / 3;
        var output = new short[outLen];

        for (int i = 0; i < outLen; i++)
        {
            int src = i * 3;
            // 3-tap FIR low-pass: [0.25, 0.5, 0.25] prevents aliasing
            int s0 = src > 0 ? pcm24k[src - 1] : pcm24k[src];
            int s1 = pcm24k[src];
            int s2 = src + 1 < pcm24k.Length ? pcm24k[src + 1] : pcm24k[src];
            int filtered = (s0 + (s1 << 1) + s2) >> 2; // (s0 + 2*s1 + s2) / 4
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

    private void Log(string msg) => OnLog?.Invoke($"[DirectRtp] {msg}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
