using System;
using System.Collections.Concurrent;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Direct RTP Playout for G.711 mode (8kHz passthrough).
/// Receives μ-law from OpenAI and outputs directly to SIP.
/// 
/// Key Features:
/// - Direct μ-law passthrough (no resampling when SIP uses PCMU)
/// - Direct μ-law→A-law transcode (no PCM intermediate when SIP uses PCMA)
/// - NAT punch-through via symmetric RTP
/// - Timer-driven 20ms cadence for stable delivery
/// - Grace period before OnQueueEmpty to prevent premature cutoff
/// - Volume boost and soft limiting for telephony clarity
/// </summary>
public class DirectRtpPlayoutG711 : IDisposable
{
    private const int FRAME_SIZE_BYTES = 160;  // 20ms @ 8kHz = 160 bytes G.711
    private const int FRAME_MS = 20;
    private const int MIN_FRAMES_TO_START = 5; // 100ms cushion
    private const int GRACE_PERIOD_FRAMES = 5; // 100ms grace before declaring done

    private readonly ConcurrentQueue<byte[]> _frameBuffer = new();
    private readonly RTPSession _rtpSession;
    private readonly byte[] _outputBuffer = new byte[FRAME_SIZE_BYTES];

    private System.Threading.Timer? _rtpTimer;
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

    public DirectRtpPlayoutG711(RTPSession rtpSession)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
        _rtpSession.AcceptRtpFromAny = true;
        _rtpSession.OnRtpPacketReceived += HandleSymmetricRtp;
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
                _rtpSession.SetDestination(SDPMediaTypesEnum.audio, remoteEndPoint, remoteEndPoint);
                OnLog?.Invoke($"[NAT] ✓ RTP locked to {remoteEndPoint}");
            }
        }
    }

    /// <summary>
    /// Buffer a μ-law frame from OpenAI (160 bytes = 20ms @ 8kHz).
    /// Frame is transcoded to A-law on output if carrier uses PCMA.
    /// </summary>
    public void BufferMuLawFrame(byte[] mulawFrame)
    {
        if (mulawFrame == null || mulawFrame.Length != FRAME_SIZE_BYTES) return;

        _frameBuffer.Enqueue(mulawFrame);
        _totalFramesQueued++;
    }

    /// <summary>
    /// Buffer multiple μ-law frames at once.
    /// </summary>
    public void BufferMuLawFrames(IEnumerable<byte[]> frames)
    {
        foreach (var frame in frames)
            BufferMuLawFrame(frame);
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
        _framesSent = 0;
        _rtpTimer = new System.Threading.Timer(SendFrame, null, 0, FRAME_MS);
        OnLog?.Invoke("[DirectRtpG711] Started");
    }

    public void Stop()
    {
        _rtpTimer?.Dispose();
        _rtpTimer = null;
        OnLog?.Invoke($"[DirectRtpG711] Stopped ({_framesSent} frames sent)");
    }

    private void SendFrame(object? state)
    {
        // Wait for minimum buffer before starting (absorbs OpenAI jitter)
        if (!_isCurrentlySpeaking)
        {
            if (_frameBuffer.Count < MIN_FRAMES_TO_START)
            {
                SendSilence();
                return;
            }
            _isCurrentlySpeaking = true;
            _emptyFramesCount = 0;
            OnLog?.Invoke($"[RTP] ▶️ Playing ({_frameBuffer.Count * FRAME_SIZE_BYTES} samples buffered)");
        }

        if (_frameBuffer.TryDequeue(out var mulawFrame))
        {
            _emptyFramesCount = 0;
            
            // Transcode if needed and send
            if (_useALaw)
            {
                // μ-law → A-law direct transcode (no PCM intermediate)
                var alawFrame = AudioCodecs.TranscodeMuLawToALaw(mulawFrame);
                SendRtpFrame(alawFrame);
            }
            else
            {
                // Direct μ-law passthrough
                SendRtpFrame(mulawFrame);
            }

            _framesSent++;
            if (_framesSent % 50 == 0)
                OnLog?.Invoke($"[RTP] 📤 Sent {_framesSent} frames ({_framesSent * FRAME_SIZE_BYTES} samples, queue: {_frameBuffer.Count * FRAME_SIZE_BYTES})");
        }
        else
        {
            _emptyFramesCount++;

            // Grace period: keep sending silence before declaring "done"
            if (_emptyFramesCount < GRACE_PERIOD_FRAMES)
            {
                SendSilence();
            }
            else
            {
                // Only NOW declare speaking complete
                if (_isCurrentlySpeaking)
                {
                    _isCurrentlySpeaking = false;
                    OnLog?.Invoke($"[RTP] ⏹️ Finished ({_framesSent} frames sent, {_framesSent * FRAME_SIZE_BYTES} samples total)");
                    OnQueueEmpty?.Invoke();
                }
                SendSilence();
            }
        }
    }

    private void SendRtpFrame(byte[] frame)
    {
        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, 0, _payloadType);
        _timestamp += FRAME_SIZE_BYTES;
    }

    private void SendSilence()
    {
        Array.Fill(_outputBuffer, _silenceByte);
        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, _outputBuffer, _timestamp, 0, _payloadType);
        _timestamp += FRAME_SIZE_BYTES;
    }

    public void Dispose()
    {
        _rtpSession.OnRtpPacketReceived -= HandleSymmetricRtp;
        Stop();
        Clear();
    }
}
