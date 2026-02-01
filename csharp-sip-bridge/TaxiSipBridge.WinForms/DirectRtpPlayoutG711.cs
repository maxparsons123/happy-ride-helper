using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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
    private const int MIN_FRAMES_TO_START = 5; // 100ms cushion
    private const int GRACE_PERIOD_FRAMES = 5; // 100ms grace before declaring done
    // OpenAI can deliver audio faster-than-realtime (burst). We must buffer it and pace out at 20ms.
    // Do NOT keep this too small or you'll drop speech and it will sound choppy.
    private const int MAX_QUEUE_FRAMES = 3000; // 60s max safety cap

    private readonly ConcurrentQueue<byte[]> _frameBuffer = new();
    private readonly VoIPMediaSession _mediaSession;
    private readonly RTPSession _rtpSession;
    private readonly byte[] _outputBuffer = new byte[FRAME_SIZE_BYTES];

    private Thread? _playoutThread;
    private volatile bool _running;
    private uint _timestamp = 0;
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

        // VoIPMediaSession does not expose RTPSession publicly in this build.
        // We extract it via reflection so we can send *raw* encoded G.711 frames via SendRtpRaw.
        _rtpSession = TryExtractRtpSession(_mediaSession)
            ?? throw new InvalidOperationException("Unable to extract RTPSession from VoIPMediaSession (required for raw G.711 playout).");

        _rtpSession.AcceptRtpFromAny = true;
        _rtpSession.OnRtpPacketReceived += HandleSymmetricRtp;
    }

    private static RTPSession? TryExtractRtpSession(VoIPMediaSession mediaSession)
    {
        // Try common field/property names first, then fallback to scanning for RTPSession-typed members.
        try
        {
            var t = mediaSession.GetType();

            // Common field names in various SIPSorcery versions
            foreach (var name in new[] { "RtpSession", "_rtpSession", "m_rtpSession", "_audioRtpSession", "AudioRtpSession" })
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f?.GetValue(mediaSession) is RTPSession rs1) return rs1;

                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (p?.GetValue(mediaSession) is RTPSession rs2) return rs2;
            }

            // Fallback: scan fields for the first RTPSession instance
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (f.FieldType == typeof(RTPSession) && f.GetValue(mediaSession) is RTPSession rs) return rs;
            }

            // Fallback: scan properties
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (p.PropertyType == typeof(RTPSession) && p.GetValue(mediaSession) is RTPSession rs) return rs;
            }
        }
        catch
        {
            // ignore, return null
        }

        return null;
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
                    _rtpSession.SetDestination(SDPMediaTypesEnum.audio, remoteEndPoint, remoteEndPoint);
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
                    SendSilence();
                }
                else
                {
                    if (_isCurrentlySpeaking)
                    {
                        _isCurrentlySpeaking = false;
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
            _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, 0, _payloadType);
            _timestamp += FRAME_SIZE_BYTES;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[RTP] ⚠️ SendRtpRaw error: {ex.Message}");
        }
    }

    private void SendSilence()
    {
        Array.Fill(_outputBuffer, _silenceByte);
        try
        {
            _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, _outputBuffer, _timestamp, 0, _payloadType);
            _timestamp += FRAME_SIZE_BYTES;
        }
        catch { }
    }

    public void Dispose()
    {
        try { _rtpSession.OnRtpPacketReceived -= HandleSymmetricRtp; } catch { }
        Stop();
        Clear();
    }
}
