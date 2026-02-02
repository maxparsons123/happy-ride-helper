using System;
using System.Net;
using System.Threading;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Timer-driven RTP playout for G.711 audio from OpenAI.
/// Sends exactly one 20ms frame every 20ms - no jitter, no bursts.
/// </summary>
public sealed class SipAudioPlayout : IDisposable
{
    private const int SAMPLE_RATE = 8000;
    private const int FRAME_MS = 20;
    private const int SAMPLES_PER_FRAME = 160; // 20ms @ 8kHz

    private readonly RTPSession _rtpSession;
    private readonly Func<byte[]?> _getNextFrame;

    private readonly byte _payloadType;
    private readonly byte _silenceByte;

    private Timer? _timer;
    private uint _timestamp;

  // NAT punch-through (symmetric RTP)
  private IPEndPoint? _lastRemoteEndpoint;
  private int _aiFrames;
  private int _silenceFrames;

    private int _disposed;
    private int _framesSent;

    public event Action<string>? OnLog;
    public event Action? OnQueueEmpty;

    public SipAudioPlayout(
        RTPSession rtpSession,
        Func<byte[]?> getNextFrame,
        AudioCodecsEnum codec)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
        _getNextFrame = getNextFrame ?? throw new ArgumentNullException(nameof(getNextFrame));

        bool useALaw = codec == AudioCodecsEnum.PCMA;
        _payloadType = useALaw ? (byte)8 : (byte)0;
        _silenceByte = useALaw ? (byte)0xD5 : (byte)0xFF;

    // Symmetric RTP: lock destination to where we actually receive RTP from.
    // This is critical behind NAT / some SIP providers.
    _rtpSession.AcceptRtpFromAny = true;
    _rtpSession.OnRtpPacketReceived += HandleSymmetricRtp;
    }

  private void HandleSymmetricRtp(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
  {
    if (mediaType != SDPMediaTypesEnum.audio) return;

    if (_lastRemoteEndpoint == null || !_lastRemoteEndpoint.Equals(remoteEndPoint))
    {
      _lastRemoteEndpoint = remoteEndPoint;
      try
      {
        _rtpSession.SetDestination(SDPMediaTypesEnum.audio, remoteEndPoint, remoteEndPoint);
        OnLog?.Invoke($"[NAT] SipAudioPlayout locked RTP destination to: {remoteEndPoint}");
      }
      catch (Exception ex)
      {
        OnLog?.Invoke($"⚠️ [NAT] Failed to lock RTP destination: {ex.Message}");
      }
    }
  }

    public void Start()
    {
        _timestamp = 0;
        _framesSent = 0;
    _aiFrames = 0;
    _silenceFrames = 0;

        // High-precision timer at 20ms intervals
        _timer = new Timer(_ => SendFrame(), null, 0, FRAME_MS);
        OnLog?.Invoke($"▶️ SipAudioPlayout started (PT={_payloadType}, silence=0x{_silenceByte:X2})");
    }

    private void SendFrame()
    {
        if (_disposed != 0)
            return;

        try
        {
            // Pull exactly ONE 20ms frame from AI client
            byte[]? frame = _getNextFrame();

            if (frame == null || frame.Length != SAMPLES_PER_FRAME)
            {
                // No AI audio available → send silence to maintain RTP stream
                frame = new byte[SAMPLES_PER_FRAME];
                Array.Fill(frame, _silenceByte);
                OnQueueEmpty?.Invoke();
          _silenceFrames++;
            }
        else
        {
          _aiFrames++;
        }

            _rtpSession.SendRtpRaw(
                SDPMediaTypesEnum.audio,
                frame,
                _timestamp,
                0, // marker bit (sequence number is managed internally by SIPSorcery)
                _payloadType
            );

            _timestamp += SAMPLES_PER_FRAME;
            _framesSent++;

            if (_framesSent % 250 == 0) // Log every 5 seconds
          OnLog?.Invoke($"📤 SipAudioPlayout: {_framesSent} frames sent (ai={_aiFrames}, silence={_silenceFrames})");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"⚠️ SipAudioPlayout error: {ex.Message}");
        }
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        OnLog?.Invoke($"⏹️ SipAudioPlayout stopped ({_framesSent} frames sent)");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        Stop();

    try { _rtpSession.OnRtpPacketReceived -= HandleSymmetricRtp; } catch { }
    }
}
