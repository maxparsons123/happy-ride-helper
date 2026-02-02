using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Net;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Direct RTP Playout for G.711 mode (8kHz passthrough).
/// Receives G.711 from OpenAI and outputs directly to SIP.
/// 
/// Key Features:
/// - Direct passthrough (no resampling, no transcoding)
/// - OpenAI outputs matching codec format (μ-law or A-law)
/// - NAT punch-through via symmetric RTP
/// - Timer-driven 20ms cadence for stable delivery
/// - Grace period before OnQueueEmpty to prevent premature cutoff
/// </summary>
public class DirectRtpPlayoutG711 : IDisposable
{
    private const int FRAME_SIZE_BYTES = 160;  // 20ms @ 8kHz = 160 bytes G.711
    private const int FRAME_MS = 20;
    private const int MIN_FRAMES_TO_START = 12; // 240ms cushion - stable without excessive latency
    private const int GRACE_PERIOD_FRAMES = 6;  // 120ms grace period at end
    // OpenAI can deliver audio faster-than-realtime (burst). We must buffer it and pace out at 20ms.
    // Do NOT keep this too small or you'll drop speech and it will sound choppy.
    private const int MAX_QUEUE_FRAMES = 3000; // 60s max safety cap

    private readonly ConcurrentQueue<byte[]> _frameBuffer = new();
    private readonly VoIPMediaSession _mediaSession;
    private readonly byte[] _outputBuffer = new byte[FRAME_SIZE_BYTES];
    private byte[]? _lastAudioFrame = null; // Track last frame for smooth fade-out

    private Thread? _playoutThread;
    private volatile bool _running;
    private bool _isCurrentlySpeaking = false;
    private int _emptyFramesCount = 0;
    private int _framesSent = 0;

    // Codec configuration
    private bool _useALaw = false;
    private int _payloadType = 0; // 0 = PCMU, 8 = PCMA
    private byte _silenceByte = 0xFF; // μ-law silence

    // NAT tracking
    private IPEndPoint? _lastRemoteEndpoint;

    // Stats
    private int _totalFramesQueued = 0;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public int PendingFrameCount => _frameBuffer.Count;

    public DirectRtpPlayoutG711(VoIPMediaSession mediaSession)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));

        // Use VoIPMediaSession APIs only (no reflection) for maximum compatibility.
        _mediaSession.AcceptRtpFromAny = true;
        _mediaSession.OnRtpPacketReceived += HandleSymmetricRtp;
    }

    /// <summary>
    /// Set the output codec (PCMU or PCMA).
    /// </summary>
    public void SetCodec(AudioCodecsEnum codec, int payloadType)
    {
        _useALaw = (codec == AudioCodecsEnum.PCMA);
        _payloadType = payloadType;
        _silenceByte = _useALaw ? (byte)0xD5 : (byte)0xFF;
        OnLog?.Invoke($"[DirectRtpG711] Codec set to {codec} (PT{payloadType})");
    }

    /// <summary>
    /// Symmetric RTP: Lock outbound audio to the actual caller endpoint (bypasses NAT issues).
    /// </summary>
    private void HandleSymmetricRtp(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType == SDPMediaTypesEnum.audio)
        {
            if (_lastRemoteEndpoint == null || !_lastRemoteEndpoint.Equals(remoteEndPoint))
            {
                _lastRemoteEndpoint = remoteEndPoint;
                try
                {
                    _mediaSession.SetDestination(SDPMediaTypesEnum.audio, remoteEndPoint, remoteEndPoint);
                    OnLog?.Invoke($"[NAT] ✓ RTP locked to {remoteEndPoint}");
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[NAT] ⚠️ SetDestination failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Buffer a G.711 frame from OpenAI (160 bytes = 20ms @ 8kHz).
    /// Frame is passed through directly - OpenAI outputs matching codec format.
    /// Drops oldest frames when buffer exceeds MAX_QUEUE_FRAMES to prevent latency buildup.
    /// </summary>
    public void BufferG711Frame(byte[] g711Frame)
    {
        if (g711Frame == null || g711Frame.Length != FRAME_SIZE_BYTES) return;

        // Safety cap only (should not trigger in normal operation).
        // If this triggers, we'd rather drop *oldest* audio than grow indefinitely.
        while (_frameBuffer.Count >= MAX_QUEUE_FRAMES)
        {
            _frameBuffer.TryDequeue(out _);
            if (_frameBuffer.Count % 200 == 0)
                OnLog?.Invoke($"[RTP] ⚠️ Buffer overflow: dropping old audio (queue={_frameBuffer.Count})");
        }

        _frameBuffer.Enqueue(g711Frame);
        _totalFramesQueued++;
    }

    /// <summary>
    /// Alias for BufferG711Frame for backwards compatibility.
    /// </summary>
    public void BufferMuLawFrame(byte[] frame) => BufferG711Frame(frame);

    /// <summary>
    /// Buffer multiple G.711 frames at once.
    /// </summary>
    public void BufferG711Frames(IEnumerable<byte[]> frames)
    {
        foreach (var frame in frames)
            BufferG711Frame(frame);
    }

    /// <summary>
    /// Clear buffer and reset state (call on barge-in or new response).
    /// </summary>
    public void Clear()
    {
        while (_frameBuffer.TryDequeue(out _)) { }
        _isCurrentlySpeaking = false;
        _emptyFramesCount = 0;
        _lastAudioFrame = null; // Clear fade-out reference
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _framesSent = 0;

        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "DirectRtpPlayoutG711"
        };
        _playoutThread.Start();

        OnLog?.Invoke("[DirectRtpG711] Started");
    }

    public void Stop()
    {
        _running = false;
        try { _playoutThread?.Join(500); } catch { }
        _playoutThread = null;
        OnLog?.Invoke($"[DirectRtpG711] Stopped ({_framesSent} frames sent)");
    }

    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextFrameTimeMs = sw.Elapsed.TotalMilliseconds;

        while (_running)
        {
            var now = sw.Elapsed.TotalMilliseconds;

            if (now < nextFrameTimeMs)
            {
                var wait = nextFrameTimeMs - now;
                if (wait > 2) Thread.Sleep((int)(wait - 1));
                else if (wait > 0.5) Thread.SpinWait(500);
                continue;
            }

            // Wait for minimum buffer before starting (absorbs OpenAI jitter)
            if (!_isCurrentlySpeaking)
            {
                if (_frameBuffer.Count < MIN_FRAMES_TO_START)
                {
                    SendSilence();
                    nextFrameTimeMs += FRAME_MS;
                    continue;
                }
                _isCurrentlySpeaking = true;
                _emptyFramesCount = 0;
                OnLog?.Invoke($"[RTP] ▶️ Playing ({_frameBuffer.Count * FRAME_SIZE_BYTES} samples buffered)");
            }

            if (_frameBuffer.TryDequeue(out var g711Frame))
            {
                _emptyFramesCount = 0;
                _lastAudioFrame = g711Frame; // Store for potential fade-out
                SendRtpFrame(g711Frame);

                _framesSent++;
                if (_framesSent % 50 == 0)
                    OnLog?.Invoke($"[RTP] 📤 Sent {_framesSent} frames ({_framesSent * FRAME_SIZE_BYTES} samples, queue: {_frameBuffer.Count * FRAME_SIZE_BYTES})");
            }
            else
            {
                _emptyFramesCount++;

                if (_emptyFramesCount < GRACE_PERIOD_FRAMES)
                {
                    // Fade out gradually during grace period to avoid abrupt silence (gruuuf noise)
                    SendFadeOutFrame(_emptyFramesCount);
                }
                else
                {
                    if (_isCurrentlySpeaking)
                    {
                        _isCurrentlySpeaking = false;
                        _lastAudioFrame = null;
                        OnLog?.Invoke($"[RTP] ⏹️ Finished ({_framesSent} frames sent, {_framesSent * FRAME_SIZE_BYTES} samples total)");
                        OnQueueEmpty?.Invoke();
                    }
                    SendSilence();
                }
            }

            nextFrameTimeMs += FRAME_MS;

            // Drift correction
            if (now - nextFrameTimeMs > 20)
                nextFrameTimeMs = now + FRAME_MS;
        }
    }

    private void SendRtpFrame(byte[] frame)
    {
        try
        {
            // Match other playout engines in this repo: duration is RTP timestamp units.
            // For 8kHz audio: 20ms = 160 samples.
            const uint RTP_DURATION = FRAME_SIZE_BYTES;
            _mediaSession.SendAudio(RTP_DURATION, frame);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[RTP] ⚠️ SendAudio error: {ex.Message}");
        }
    }

    private void SendSilence()
    {
        Array.Fill(_outputBuffer, _silenceByte);
        try
        {
            const uint RTP_DURATION = FRAME_SIZE_BYTES;
            _mediaSession.SendAudio(RTP_DURATION, _outputBuffer);
        }
        catch { }
    }

    /// <summary>
    /// Send a fade-out frame during grace period to smooth the transition to silence.
    /// IMPORTANT: G.711 is logarithmic - we must decode→fade→re-encode in linear PCM domain.
    /// </summary>
    private void SendFadeOutFrame(int frameIndex)
    {
        if (_lastAudioFrame == null)
        {
            SendSilence();
            return;
        }

        // Calculate fade multiplier (1.0 → 0.0 over grace period)
        float fadeMultiplier = 1.0f - ((float)(frameIndex + 1) / GRACE_PERIOD_FRAMES);
        
        for (int i = 0; i < FRAME_SIZE_BYTES; i++)
        {
            // Decode G.711 to linear PCM, apply fade, re-encode
            short pcmSample = _useALaw ? ALawDecode(_lastAudioFrame[i]) : MuLawDecode(_lastAudioFrame[i]);
            short fadedSample = (short)(pcmSample * fadeMultiplier);
            _outputBuffer[i] = _useALaw ? ALawEncode(fadedSample) : MuLawEncode(fadedSample);
        }

        try
        {
            const uint RTP_DURATION = FRAME_SIZE_BYTES;
            _mediaSession.SendAudio(RTP_DURATION, _outputBuffer);
        }
        catch { }
    }

    // G.711 A-Law decode (ITU-T G.711)
    private static short ALawDecode(byte alaw)
    {
        alaw ^= 0x55;
        int sign = (alaw & 0x80) != 0 ? -1 : 1;
        int exponent = (alaw >> 4) & 0x07;
        int mantissa = alaw & 0x0F;
        int magnitude = exponent == 0 
            ? (mantissa << 4) + 8 
            : ((mantissa << 4) + 264) << (exponent - 1);
        return (short)(sign * magnitude);
    }

    // G.711 A-Law encode (ITU-T G.711)
    private static byte ALawEncode(short pcm)
    {
        int sign = (pcm >> 8) & 0x80;
        if (sign != 0) pcm = (short)-pcm;
        if (pcm > 32635) pcm = 32635;
        
        byte encoded;
        if (pcm >= 256)
        {
            int exponent = 7;
            for (int expMask = 0x4000; (pcm & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }
            int mantissa = (pcm >> (exponent + 3)) & 0x0F;
            encoded = (byte)((exponent << 4) | mantissa);
        }
        else
        {
            encoded = (byte)(pcm >> 4);
        }
        return (byte)((encoded ^ 0x55) | sign);
    }

    // G.711 Mu-Law decode (ITU-T G.711)
    private static short MuLawDecode(byte ulaw)
    {
        ulaw = (byte)~ulaw;
        int sign = (ulaw & 0x80) != 0 ? -1 : 1;
        int exponent = (ulaw >> 4) & 0x07;
        int mantissa = ulaw & 0x0F;
        int magnitude = ((mantissa << 3) + 132) << exponent - 132;
        return (short)(sign * magnitude);
    }

    // G.711 Mu-Law encode (ITU-T G.711)
    private static byte MuLawEncode(short pcm)
    {
        const int BIAS = 132;
        int sign = (pcm >> 8) & 0x80;
        if (sign != 0) pcm = (short)-pcm;
        pcm = (short)Math.Min(pcm + BIAS, 32767);
        
        int exponent = 7;
        for (int expMask = 0x4000; (pcm & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }
        int mantissa = (pcm >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public void Dispose()
    {
        try { _mediaSession.OnRtpPacketReceived -= HandleSymmetricRtp; } catch { }
        Stop();
        Clear();
    }
}
