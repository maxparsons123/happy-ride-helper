using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// AI → SIP Audio Playout Engine.
/// Receives 24kHz PCM from OpenAI, resamples to 8kHz, encodes to G.711 (A-law/μ-law),
/// and sends via VoIPMediaSession at a stable 20ms cadence.
/// ⚠️ IMPORTANT: VoIPMediaSession.SendAudio() expects PRE-ENCODED G.711 bytes, not raw PCM!
/// </summary>
public class AiSipAudioPlayout : IDisposable
{
    private const int PCM_FRAME_SAMPLES = 160;  // 20ms @ 8kHz = 160 samples
    private const int FRAME_MS = 20;            // 20ms per frame
    private const int MAX_QUEUE_FRAMES = 500;   // 10 seconds max buffer (500 × 20ms)

    // Outbound loudness tuning (telephony tends to sound quiet otherwise)
    private const float OUTPUT_GAIN = 2.2f;     // gentle boost (soft-clipped below)
    private const int LIMIT_THRESHOLD = 28000;  // start compressing above this

    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly VoIPMediaSession _mediaSession;
    private readonly string _negotiatedCodec;

    // Stateful FIR fallback to avoid “distant/muffled” audio from naive decimation
    private readonly PolyphaseFirResampler _fir24kTo8k = new(24000, 8000);

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

    public AiSipAudioPlayout(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));

        // Get negotiated codec from AudioLocalTrack (correct SIPSorcery API)
        var localFormat = _mediaSession.AudioLocalTrack?.Capabilities?.FirstOrDefault();
        _negotiatedCodec = localFormat?.Name() ?? "NONE";

        // Log codec info (don't throw - the session may still work)
        if (_negotiatedCodec == "PCMA" || _negotiatedCodec == "PCMU")
        {
            Log($"✅ Codec negotiated: {_negotiatedCodec} (A-law={_negotiatedCodec == "PCMA"})");
        }
        else
        {
            Log($"⚠️ Unexpected codec: {_negotiatedCodec} - expected PCMA/PCMU for G.711");
        }
    }

    /// <summary>
    /// Buffer AI audio (PCM16 @ 24kHz) for playout.
    /// Resamples to 8kHz PCM with anti-aliasing filter.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || _disposed || pcm24kBytes == null || pcm24kBytes.Length == 0)
            return;

        try
        {
            // Convert bytes to shorts (little-endian)
            var pcm24k = BytesToShorts(pcm24kBytes);

            // Resample 24kHz → 8kHz (high-quality)
            var pcm8k = Resample24kTo8kHighQuality(pcm24k);

            // Make Ada louder / less distant on G.711 by applying a gentle boost + limiter
            ApplyGainAndLimiterInPlace(pcm8k);

            // Split into 20ms frames (160 samples each) and queue
            for (int i = 0; i < pcm8k.Length; i += PCM_FRAME_SAMPLES)
            {
                // Drop OLDEST frame on overflow to minimize latency (not newest)
                if (_frameQueue.Count >= MAX_QUEUE_FRAMES)
                {
                    _frameQueue.TryDequeue(out _);
                    Interlocked.Increment(ref _droppedFrames);
                }

                var frame = new short[PCM_FRAME_SAMPLES];
                int len = Math.Min(PCM_FRAME_SAMPLES, pcm8k.Length - i);
                Array.Copy(pcm8k, i, frame, 0, len);
                // Remaining samples stay 0 (silence)

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
            Name = "AiSipPlayoutThread"
        };

        _playoutThread.Start();
        Log($"▶️ Playout started (codec={_negotiatedCodec}, buffer={MAX_QUEUE_FRAMES * FRAME_MS}ms)");
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
        int count = 0;
        while (_frameQueue.TryDequeue(out _)) count++;
        if (count > 0)
            Log($"🗑️ Cleared {count} frames from queue");
    }

    /// <summary>
    /// High-precision 20ms playout loop.
    /// </summary>
    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameTimeMs = sw.Elapsed.TotalMilliseconds;
        bool wasEmpty = false;

        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            // Wait for next frame time
            if (now < nextFrameTimeMs)
            {
                double waitMs = nextFrameTimeMs - now;
                if (waitMs > 2.0)
                    Thread.Sleep((int)(waitMs - 1));
                else if (waitMs > 0.5)
                    Thread.SpinWait(500);
                continue;
            }

            // Get next frame or generate silence on underrun
            short[] frame;
            if (_frameQueue.TryDequeue(out var queuedFrame))
            {
                frame = queuedFrame;
                wasEmpty = false;
            }
            else
            {
                frame = new short[PCM_FRAME_SAMPLES];
                Interlocked.Increment(ref _silenceFrames);

                // Notify once when queue empties (useful for barge-in detection)
                if (!wasEmpty)
                {
                    wasEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }

            // Send encoded G.711 payload (160 samples -> 160 bytes)
            SendPcmFrame(frame);

            // Schedule next frame
            nextFrameTimeMs += FRAME_MS;

            // Drift correction: reset if >40ms behind (prevents audible glitches)
            if (now - nextFrameTimeMs > 40)
            {
                Log($"⏱️ Drift correction: {now - nextFrameTimeMs:F1}ms behind, resetting timer");
                nextFrameTimeMs = now + FRAME_MS;
            }
        }
    }

    /// <summary>
    /// Encode PCM to A-law/μ-law and send via VoIPMediaSession.
    /// VoIPMediaSession.SendAudio expects PRE-ENCODED G.711 bytes, not raw PCM!
    /// </summary>
    private void SendPcmFrame(short[] pcmFrame)
    {
        try
        {
            // Encode PCM to G.711 (160 samples → 160 bytes)
            var encodedBytes = new byte[pcmFrame.Length];
            
            if (_negotiatedCodec == "PCMA")
            {
                // A-law encoding
                for (int i = 0; i < pcmFrame.Length; i++)
                    encodedBytes[i] = G711Codec.EncodeSampleALaw(pcmFrame[i]);
            }
            else
            {
                // μ-law encoding (PCMU)
                for (int i = 0; i < pcmFrame.Length; i++)
                    encodedBytes[i] = G711Codec.EncodeSample(pcmFrame[i]);
            }

            // SendAudio expects: duration in RTP units (160 for 20ms @ 8kHz), encoded bytes
            _mediaSession.SendAudio((uint)encodedBytes.Length, encodedBytes);

            Interlocked.Increment(ref _framesSent);
        }
        catch (Exception ex)
        {
            Log($"⚠️ RTP send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resample 24kHz → 8kHz using the best available path.
    /// Prefer SpeexDSP (very high quality). Fallback: stateful 21-tap FIR.
    /// </summary>
    private short[] Resample24kTo8kHighQuality(short[] pcm24k)
    {
        if (pcm24k.Length < 3) return Array.Empty<short>();

        // Best path: SpeexDSP (if native library present)
        if (SpeexDspResamplerHelper.IsAvailable)
        {
            try
            {
                return SpeexDspResamplerHelper.Resample24kTo8k(pcm24k);
            }
            catch (DllNotFoundException)
            {
                // Helper flips IsAvailable=false internally; fall through to FIR
            }
            catch
            {
                // If Speex fails for any reason, don't break audio; fall back
            }
        }

        // Fallback: stateful FIR polyphase decimator
        int outLen = pcm24k.Length / 3;
        return _fir24kTo8k.Resample(pcm24k, outLen);
    }

    /// <summary>
    /// Gentle gain + limiter to improve perceived loudness on narrowband telephony.
    /// (Keeps it simple: no heavy DSP, just avoid clipping.)
    /// </summary>
    private static void ApplyGainAndLimiterInPlace(short[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            int v = (int)(samples[i] * OUTPUT_GAIN);
            int a = Math.Abs(v);

            // Soft limiter above LIMIT_THRESHOLD
            if (a > LIMIT_THRESHOLD)
            {
                int excess = a - LIMIT_THRESHOLD;
                // Compress excess 4:1 (simple and cheap)
                a = LIMIT_THRESHOLD + (excess / 4);
                if (a > short.MaxValue) a = short.MaxValue;
                v = v < 0 ? -a : a;
            }

            samples[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// Convert byte array to short array (little-endian).
    /// </summary>
    private static short[] BytesToShorts(byte[] bytes)
    {
        if (bytes.Length % 2 != 0)
            throw new ArgumentException("Byte array length must be even", nameof(bytes));

        var shorts = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, shorts, 0, bytes.Length);
        return shorts;
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
