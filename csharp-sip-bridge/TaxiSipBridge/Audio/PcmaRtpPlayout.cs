using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using SIPSorcery.Net;

namespace TaxiSipBridge.Audio;

/// <summary>
/// 24kHz PCM16 → 8kHz → PCMA (G.711 A-law) RTP playout with a dedicated timing thread.
/// Uses SpeexDSP for high-quality resampling and wall-clock based frame pacing.
/// Expects input as 24kHz PCM16 bytes from Ada/OpenAI.
/// </summary>
public class PcmaRtpPlayout : IDisposable
{
    // 8kHz PCMA: 20ms = 160 samples
    private const int FRAME_SAMPLES_8K = 160;
    private const int FRAME_MS = 20;
    private const int JITTER_PRIME_FRAMES = 6; // 120ms jitter buffer priming

    // 24kHz input: 20ms = 480 samples
    private const int FRAME_SAMPLES_24K = 480;

    private readonly ConcurrentQueue<short[]> _frameQueue8k = new();
    private readonly byte[] _pcmaBuffer = new byte[FRAME_SAMPLES_8K];
    private readonly RTPChannel _rtpChannel;
    private readonly IPEndPoint _remoteEndPoint;

    private Thread? _playoutThread;
    private volatile bool _running;
    private bool _jitterBufferPrimed;
    private ushort _seq;
    private uint _ts;
    private readonly uint _ssrc;

    // SpeexDSP resampler handle
    private IntPtr _speexResampler = IntPtr.Zero;
    private readonly object _resamplerLock = new();
    private bool _speexAvailable;

    // Fallback: simple decimation if SpeexDSP unavailable
    private short[]? _resampleBuffer;

    // Stats
    private int _enqueuedFrames;
    private int _sentFrames;
    private int _silenceFrames;
    private int _underruns;
    private DateTime _lastStatsLog = DateTime.MinValue;

    public event Action<string>? OnDebugLog;
    public event Action? OnQueueEmpty;
    private bool _wasQueueEmpty = true;

    #region SpeexDSP P/Invoke

    private const int SPEEX_RESAMPLER_QUALITY_DEFAULT = 6;

    [DllImport("libspeexdsp", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr speex_resampler_init(uint nb_channels, uint in_rate, uint out_rate, int quality, out int err);

    [DllImport("libspeexdsp", CallingConvention = CallingConvention.Cdecl)]
    private static extern void speex_resampler_destroy(IntPtr st);

    [DllImport("libspeexdsp", CallingConvention = CallingConvention.Cdecl)]
    private static extern int speex_resampler_process_int(IntPtr st, uint channel_index, short[] input, ref uint in_len, short[] output, ref uint out_len);

    #endregion

    public PcmaRtpPlayout(RTPChannel rtpChannel, IPEndPoint remoteEndPoint)
    {
        _rtpChannel = rtpChannel;
        _remoteEndPoint = remoteEndPoint;

        var rnd = new Random();
        _seq = (ushort)rnd.Next(ushort.MaxValue);
        _ts = (uint)rnd.Next(int.MaxValue);
        _ssrc = (uint)rnd.Next(int.MaxValue);

        InitSpeexResampler();
    }

    private void InitSpeexResampler()
    {
        try
        {
            _speexResampler = speex_resampler_init(1, 24000, 8000, SPEEX_RESAMPLER_QUALITY_DEFAULT, out int err);
            if (_speexResampler != IntPtr.Zero && err == 0)
            {
                _speexAvailable = true;
                OnDebugLog?.Invoke("[PcmaRtpPlayout] ✅ SpeexDSP resampler initialized (24kHz→8kHz, quality=6)");
            }
            else
            {
                _speexAvailable = false;
                OnDebugLog?.Invoke($"[PcmaRtpPlayout] ⚠️ SpeexDSP init failed (err={err}), using simple decimation");
            }
        }
        catch (DllNotFoundException)
        {
            _speexAvailable = false;
            OnDebugLog?.Invoke("[PcmaRtpPlayout] ⚠️ libspeexdsp not found, using simple decimation fallback");
        }
        catch (Exception ex)
        {
            _speexAvailable = false;
            OnDebugLog?.Invoke($"[PcmaRtpPlayout] ⚠️ SpeexDSP init error: {ex.Message}, using fallback");
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "PcmaRtpPlayout"
        };
        _playoutThread.Start();
        OnDebugLog?.Invoke("[PcmaRtpPlayout] ▶️ Started playout thread");
    }

    public void Stop()
    {
        _running = false;
        try { _playoutThread?.Join(500); } catch { }
        OnDebugLog?.Invoke($"[PcmaRtpPlayout] ⏹️ Stopped (sent={_sentFrames}, silence={_silenceFrames}, underruns={_underruns})");
    }

    /// <summary>
    /// Enqueue 24kHz PCM16 audio from Ada/OpenAI.
    /// Audio is resampled to 8kHz and split into 20ms frames.
    /// </summary>
    public void EnqueuePcm24(byte[] pcm24Bytes)
    {
        if (!_running || pcm24Bytes.Length == 0) return;

        // Convert bytes to shorts
        var pcm24 = new short[pcm24Bytes.Length / 2];
        Buffer.BlockCopy(pcm24Bytes, 0, pcm24, 0, pcm24Bytes.Length);

        // Process in 20ms frames (480 samples @ 24kHz)
        for (int offset = 0; offset < pcm24.Length; offset += FRAME_SAMPLES_24K)
        {
            int len = Math.Min(FRAME_SAMPLES_24K, pcm24.Length - offset);
            
            // Pad to exact frame size if needed
            var frame24k = new short[FRAME_SAMPLES_24K];
            Array.Copy(pcm24, offset, frame24k, 0, len);

            // Resample 24kHz → 8kHz
            var frame8k = Resample24kTo8k(frame24k);

            // Enqueue for playout
            _frameQueue8k.Enqueue(frame8k);
            _enqueuedFrames++;
            _wasQueueEmpty = false;
        }

        // Log first enqueue
        if (_enqueuedFrames == 1)
            OnDebugLog?.Invoke($"[PcmaRtpPlayout] 📥 First audio enqueued, queue={_frameQueue8k.Count}");
    }

    /// <summary>
    /// Resample 24kHz → 8kHz using SpeexDSP or simple decimation fallback.
    /// </summary>
    private short[] Resample24kTo8k(short[] pcm24k)
    {
        var output = new short[FRAME_SAMPLES_8K];

        if (_speexAvailable && _speexResampler != IntPtr.Zero)
        {
            lock (_resamplerLock)
            {
                uint inLen = (uint)pcm24k.Length;
                uint outLen = (uint)output.Length;
                speex_resampler_process_int(_speexResampler, 0, pcm24k, ref inLen, output, ref outLen);
            }
        }
        else
        {
            // Simple 3:1 decimation fallback (take every 3rd sample)
            for (int i = 0; i < FRAME_SAMPLES_8K; i++)
            {
                int srcIdx = i * 3;
                if (srcIdx < pcm24k.Length)
                    output[i] = pcm24k[srcIdx];
            }
        }

        return output;
    }

    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameMs = sw.Elapsed.TotalMilliseconds;

        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            // Wall-clock drift correction: wait until next frame time
            if (now < nextFrameMs)
            {
                double sleepMs = nextFrameMs - now;
                if (sleepMs > 1.5)
                    Thread.Sleep((int)(sleepMs - 0.5));
                else if (sleepMs > 0.1)
                    Thread.SpinWait(200);
                continue;
            }

            // Jitter buffer priming: wait for N frames before starting playback
            if (!_jitterBufferPrimed)
            {
                if (_frameQueue8k.Count < JITTER_PRIME_FRAMES)
                {
                    SendSilenceFrame();
                    nextFrameMs += FRAME_MS;
                    continue;
                }
                _jitterBufferPrimed = true;
                OnDebugLog?.Invoke($"[PcmaRtpPlayout] 🎯 Jitter buffer primed: {_frameQueue8k.Count} frames");
            }

            // Dequeue or play silence
            short[] frame;
            if (_frameQueue8k.TryDequeue(out var queuedFrame))
            {
                frame = queuedFrame;
                
                // Fire queue empty event on transition
                if (!_wasQueueEmpty && _frameQueue8k.IsEmpty)
                {
                    _wasQueueEmpty = true;
                    OnQueueEmpty?.Invoke();
                }
            }
            else
            {
                // Underrun: play silence
                frame = new short[FRAME_SAMPLES_8K];
                _underruns++;

                // Reset jitter buffer after prolonged underrun
                if (_underruns > 10 && _jitterBufferPrimed)
                {
                    _jitterBufferPrimed = false;
                    OnDebugLog?.Invoke("[PcmaRtpPlayout] ⚠️ Underrun - resetting jitter buffer");
                }
            }

            // Encode PCM16 → PCMA (G.711 A-law)
            for (int i = 0; i < FRAME_SAMPLES_8K; i++)
            {
                _pcmaBuffer[i] = G711Codec.EncodeSampleALaw(frame[i]);
            }

            // Build and send RTP packet
            SendRtpPacket(_pcmaBuffer);
            _sentFrames++;

            // Log stats every 5 seconds
            if ((DateTime.Now - _lastStatsLog).TotalSeconds >= 5)
            {
                OnDebugLog?.Invoke($"[PcmaRtpPlayout] 📊 sent={_sentFrames}, silence={_silenceFrames}, underruns={_underruns}, queue={_frameQueue8k.Count}");
                _lastStatsLog = DateTime.Now;
            }

            nextFrameMs += FRAME_MS;
        }
    }

    private void SendSilenceFrame()
    {
        // Send encoded silence (A-law silence = 0xD5)
        Array.Fill(_pcmaBuffer, (byte)0xD5);
        SendRtpPacket(_pcmaBuffer);
        _silenceFrames++;
    }

    private void SendRtpPacket(byte[] payload)
    {
        try
        {
            var rtpPacket = new RTPPacket(12 + payload.Length);
            rtpPacket.Header.SyncSource = _ssrc;
            rtpPacket.Header.SequenceNumber = _seq++;
            rtpPacket.Header.Timestamp = _ts;
            rtpPacket.Header.PayloadType = 8; // PCMA
            rtpPacket.Header.MarkerBit = 0;
            rtpPacket.Payload = payload;

            _ts += FRAME_SAMPLES_8K; // 160 samples @ 8kHz per 20ms

            byte[] rtpBytes = rtpPacket.GetBytes();
            _rtpChannel.Send(RTPChannelSocketsEnum.RTP, _remoteEndPoint, rtpBytes);
        }
        catch (Exception ex)
        {
            OnDebugLog?.Invoke($"[PcmaRtpPlayout] ❌ RTP send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear all queued audio.
    /// </summary>
    public void Clear()
    {
        while (_frameQueue8k.TryDequeue(out _)) { }
        _jitterBufferPrimed = false;
        _wasQueueEmpty = true;
    }

    public void Dispose()
    {
        Stop();

        if (_speexResampler != IntPtr.Zero)
        {
            try { speex_resampler_destroy(_speexResampler); } catch { }
            _speexResampler = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
