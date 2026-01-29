using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;

namespace TaxiSipBridge;

/// <summary>
/// Direct RTP playout using VoIPMediaSession.SendAudio with proper 24kHz→8kHz resampling.
/// </summary>
public class DirectRtpPlayout : IDisposable
{
    private const int SAMPLE_RATE = 8000;
    private const int FRAME_MS = 20;
    private const int PCM_8K_SAMPLES = (SAMPLE_RATE / 1000) * FRAME_MS; // 160 samples

    private readonly VoIPMediaSession _session;
    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly CancellationTokenSource _cts = new();

    private bool _running;
    private Task? _playoutTask;
    private int _debugResampleCount;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;

    public DirectRtpPlayout(VoIPMediaSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Log("✅ DirectRtpPlayout initialized (24kHz→8kHz resampling enabled)");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _debugResampleCount = 0;
        _playoutTask = Task.Run(PlayoutLoop, _cts.Token);
        Log("▶️ Playout started using VoIPMediaSession Direct Send.");
    }

    public void Stop()
    {
        _running = false;
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        try { _playoutTask?.Wait(500); } catch { }
        while (_frameQueue.TryDequeue(out _)) { }
        Log("⏹️ Playout stopped.");
    }

    public void Clear()
    {
        int cleared = 0;
        while (_frameQueue.TryDequeue(out _)) cleared++;
        if (cleared > 0) Log($"🗑️ Cleared {cleared} frames (barge-in)");
    }

    /// <summary>
    /// Buffer 24kHz PCM from OpenAI. Resamples to 8kHz and frames into 20ms chunks.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (!_running || pcm24kBytes == null || pcm24kBytes.Length < 2) return;

        try
        {
            // 1. Convert bytes → 24kHz PCM shorts
            var pcm24k = BytesToShorts(pcm24kBytes);
            if (pcm24k.Length < 3) return;

            // 2. CRITICAL: Resample 24kHz → 8kHz (3:1 decimation with anti-aliasing)
            var pcm8k = Resample24kTo8k(pcm24k);

            // 3. Diagnostic: Verify resampling ratio
            if (_debugResampleCount < 5)
            {
                double ratio = pcm24k.Length / (double)Math.Max(1, pcm8k.Length);
                Log($"🔍 Resample #{_debugResampleCount + 1}: {pcm24k.Length}→{pcm8k.Length} samples (ratio: {ratio:F2}x) {(Math.Abs(ratio - 3.0) < 0.1 ? "✅" : "❌")}");
                _debugResampleCount++;
            }

            // 4. Frame into 20ms chunks (160 samples @ 8kHz)
            for (int i = 0; i < pcm8k.Length; i += PCM_8K_SAMPLES)
            {
                var frame = new short[PCM_8K_SAMPLES];
                int len = Math.Min(PCM_8K_SAMPLES, pcm8k.Length - i);
                Array.Copy(pcm8k, i, frame, 0, len);
                _frameQueue.Enqueue(frame);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Buffer error: {ex.Message}");
        }
    }

    private void PlayoutLoop()
    {
        long totalSamplesSent = 0;
        DateTime startTime = DateTime.UtcNow;
        bool wasEmpty = false;

        while (_running && !_cts.IsCancellationRequested)
        {
            double elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
            double scheduledMs = (totalSamplesSent / (double)SAMPLE_RATE) * 1000;

            if (scheduledMs > elapsedMs)
            {
                int sleepTime = (int)(scheduledMs - elapsedMs);
                if (sleepTime > 0) Thread.Sleep(sleepTime);
            }

            if (_frameQueue.TryDequeue(out var frame))
            {
                SendRtp(frame);
                totalSamplesSent += frame.Length;
                wasEmpty = false;
            }
            else
            {
                // Send silence to keep the RTP stream alive during gaps
                SendRtp(new short[PCM_8K_SAMPLES]);
                totalSamplesSent += PCM_8K_SAMPLES;

                if (!wasEmpty)
                {
                    wasEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }
        }
    }

    private void SendRtp(short[] pcmSamples)
    {
        try
        {
            float gain = 2.5f; // Volume boost
            byte[] alawBuffer = new byte[pcmSamples.Length];

            for (int i = 0; i < pcmSamples.Length; i++)
            {
                float sample = pcmSamples[i] * gain;
                short clamped = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
                alawBuffer[i] = ALawEncoder.LinearToALawSample(clamped);
            }

            // SendAudio(duration_ms, payload) - uses session's negotiated codec
            _session.SendAudio((uint)FRAME_MS, alawBuffer);
        }
        catch (Exception ex)
        {
            Log($"⚠️ SendRtp error: {ex.Message}");
        }
    }

    /// <summary>
    /// 24kHz → 8kHz resampling with 3-tap FIR low-pass filter [0.25, 0.5, 0.25].
    /// </summary>
    private static short[] Resample24kTo8k(short[] pcm24k)
    {
        int outLen = pcm24k.Length / 3;
        var output = new short[outLen];

        for (int i = 0; i < outLen; i++)
        {
            int src = i * 3;
            int s0 = src > 0 ? pcm24k[src - 1] : pcm24k[src];
            int s1 = pcm24k[src];
            int s2 = src + 1 < pcm24k.Length ? pcm24k[src + 1] : pcm24k[src];
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

    private void Log(string msg) => OnLog?.Invoke($"[DirectRtp] {msg}");

    public void Dispose()
    {
        _running = false;
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
