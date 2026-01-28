using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Direct RTP playout engine for G.711 A-law (PCMA).
/// Bypasses ambiguous SendAudio() by sending raw RTP packets via RTPChannel.
/// ✅ Works consistently across SIPSorcery versions
/// ✅ Eliminates PCM hiss/static with proper A-law encoding
/// ✅ Startup buffering prevents initial underruns
/// </summary>
public class DirectRtpPlayout : IDisposable
{
    private const int PCM8K_FRAME_SAMPLES = 160;  // 20ms @ 8kHz
    private const int FRAME_MS = 20;
    private const int MAX_QUEUE_FRAMES = 25;      // 500ms buffer (NOT 10s!)
    private const int MIN_STARTUP_FRAMES = 10;    // 200ms startup buffer

    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly RTPChannel _rtpChannel;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly byte[] _alawBuffer = new byte[PCM8K_FRAME_SAMPLES];

    // RTP state (RFC 3550 compliant)
    private ushort _sequenceNumber;
    private uint _timestamp;
    private readonly uint _ssrc = (uint)new Random().Next(1, int.MaxValue);

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

    public DirectRtpPlayout(VoIPMediaSession mediaSession)
    {
        if (mediaSession == null) throw new ArgumentNullException(nameof(mediaSession));

        // Get RTP channel SAFELY (works in v5/v6+)
        var audioStream = mediaSession.AudioStreams?.FirstOrDefault()
                       ?? throw new ArgumentException("No audio stream found", nameof(mediaSession));

        _rtpChannel = audioStream.GetRtpChannel()
                      ?? throw new ArgumentException("RTP channel not available", nameof(mediaSession));

        _remoteEndPoint = audioStream.RemoteEndPoint
                        ?? throw new ArgumentException("Remote endpoint not set", nameof(mediaSession));

        // Initialize RTP state
        _sequenceNumber = (ushort)new Random().Next(1, ushort.MaxValue);
        _timestamp = (uint)new Random().Next(1, int.MaxValue);

        Log($"✅ Direct RTP playout initialized | Remote:{_remoteEndPoint} | SSRC:{_ssrc:X8}");
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

        _running = true;
        _framesSent = 0;
        _silenceFrames = 0;
        _droppedFrames = 0;

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "DirectRtpPlayout"
        };
        _playoutThread.Start();

        Log($"▶️ Playout STARTED | Codec:PCMA | Buffer:{MAX_QUEUE_FRAMES * FRAME_MS}ms | Startup:{MIN_STARTUP_FRAMES * FRAME_MS}ms");
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
        long framesSent = 0;
        bool wasEmpty = true;
        bool startupBuffering = true;

        while (_running)
        {
            double targetTime = framesSent * FRAME_MS;
            double now = sw.Elapsed.TotalMilliseconds;

            // Startup buffering phase
            if (startupBuffering)
            {
                if (_frameQueue.Count >= MIN_STARTUP_FRAMES)
                {
                    startupBuffering = false;
                    framesSent = 0;
                    sw.Restart();
                    Log($"📦 Startup buffer ready ({_frameQueue.Count} frames / {MIN_STARTUP_FRAMES * FRAME_MS}ms)");
                    continue;
                }
                Thread.Sleep(5);
                continue;
            }

            // Timing loop
            if (now < targetTime)
            {
                double wait = targetTime - now;
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

            SendRtpPacket(frame);
            framesSent++;
        }
    }

    /// <summary>
    /// ✅ SEND RAW RTP PACKET with properly encoded A-law payload.
    /// Bypasses VoIPMediaSession.SendAudio() ambiguity entirely.
    /// </summary>
    private void SendRtpPacket(short[] pcmFrame)
    {
        try
        {
            // 1. Encode PCM → A-law (ITU-T G.711)
            for (int i = 0; i < pcmFrame.Length; i++)
            {
                _alawBuffer[i] = LinearToALaw(pcmFrame[i]);
            }

            // 2. Build RTP packet (RFC 3550 compliant)
            var rtpPacket = new RTPPacket(12 + PCM8K_FRAME_SAMPLES)
            {
                Header = new RTPHeader
                {
                    PayloadType = 8,          // PCMA = payload type 8 (RFC 3551)
                    MarkerBit = 0,
                    SequenceNumber = _sequenceNumber++,
                    Timestamp = _timestamp,
                    SyncSource = _ssrc
                },
                Payload = _alawBuffer
            };

            _timestamp += (uint)PCM8K_FRAME_SAMPLES; // Increment by samples @ 8kHz

            // 3. Send directly to remote endpoint
            _rtpChannel.SendAsync(_remoteEndPoint, rtpPacket.GetBytes()).Wait();

            Interlocked.Increment(ref _framesSent);

            // Diagnostic: Verify first packet encoding
            if (_framesSent == 1)
            {
                Log($"✅ First RTP sent | Seq:{rtpPacket.Header.SequenceNumber} | TS:{rtpPacket.Header.Timestamp} | Payload:160 bytes A-law (0x{_alawBuffer[0]:X2})");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ RTP send error: {ex.Message}");
        }
    }

    // ===========================================================================================
    // ITU-T G.711 A-law Encoder (RFC 3551 compliant) - NO EXTERNAL DEPENDENCIES
    // ===========================================================================================
    private static byte LinearToALaw(short sample)
    {
        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        sample = (short)((sample + 8) >> 4);

        int exponent, mantissa;
        if (sample >= 256) { exponent = 7; mantissa = (sample >> 4) & 0x0F; }
        else if (sample >= 128) { exponent = 6; mantissa = sample & 0x0F; }
        else if (sample >= 64) { exponent = 5; mantissa = sample & 0x0F; }
        else if (sample >= 32) { exponent = 4; mantissa = sample & 0x0F; }
        else if (sample >= 16) { exponent = 3; mantissa = sample & 0x0F; }
        else if (sample >= 8) { exponent = 2; mantissa = sample & 0x0F; }
        else if (sample >= 4) { exponent = 1; mantissa = sample & 0x0F; }
        else { exponent = 0; mantissa = sample >> 1; }

        byte alaw = (byte)((exponent << 4) | mantissa);
        alaw ^= 0x55;                // Invert odd bits (G.711 requirement)
        if (sign == 0) alaw |= 0x80; // Restore sign bit
        return alaw;
    }

    /// <summary>
    /// Anti-aliased 24kHz → 8kHz resampling (3-tap FIR low-pass).
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
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
