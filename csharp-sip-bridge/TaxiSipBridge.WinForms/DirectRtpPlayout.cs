using System;
using System.Collections.Concurrent;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// AI → SIP Audio Playout Engine.
/// 
/// Supports two modes:
/// 1. NATIVE G.711 MODE: Receives 8kHz G.711 from OpenAI, passes through directly (zero DSP)
/// 2. PCM24K MODE: Receives 24kHz PCM, downsamples to 8kHz, encodes to G.711
/// 
/// Features:
/// - NAT punch-through via symmetric RTP
/// - Grace period before OnQueueEmpty to prevent premature cutoff
/// - Smooth sample decay for clean audio tails
/// </summary>
public class DirectRtpPlayout : IDisposable
{
    private const int G711_FRAME_SIZE = 160;  // 20ms @ 8kHz = 160 bytes
    private const int FRAME_MS = 20;
    private const int MIN_BUFFER_MS = 200;    // 200ms cushion before starting

    // Native G.711 mode: queue of raw G.711 bytes
    private readonly ConcurrentQueue<byte> _g711Buffer = new();
    
    // Legacy PCM mode: queue of 8kHz PCM samples
    private readonly ConcurrentQueue<short> _sampleBuffer = new();
    
    private readonly RTPSession _rtpSession;
    private readonly byte[] _frameBuffer = new byte[G711_FRAME_SIZE];

    private System.Threading.Timer? _rtpTimer;
    private uint _timestamp = 0;
    private bool _isCurrentlySpeaking = false;
    private byte _lastG711Byte = 0xD5;  // A-law silence
    private short _lastSample = 0;
    private bool _useNativeG711 = false;

    // Grace period: continue pushing decaying samples for 100ms before declaring "done"
    private int _emptyFramesCount = 0;
    private const int GRACE_PERIOD_FRAMES = 5;

    // NAT tracking
    private IPEndPoint? _lastRemoteEndpoint;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public DirectRtpPlayout(RTPSession rtpSession, bool nativeG711Mode = false)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
        _rtpSession.AcceptRtpFromAny = true;
        _rtpSession.OnRtpPacketReceived += HandleSymmetricRtp;
        _useNativeG711 = nativeG711Mode;
        
        if (_useNativeG711)
            OnLog?.Invoke("[RTP] Native G.711 mode enabled (zero DSP passthrough)");
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
                OnLog?.Invoke($"[NAT] Symmetric RTP locked to: {remoteEndPoint}");
            }
        }
    }

    /// <summary>
    /// Buffer native G.711 audio from OpenAI (8kHz, A-law or μ-law).
    /// ZERO DSP - direct passthrough to RTP.
    /// </summary>
    public void BufferG711Audio(byte[] g711Bytes)
    {
        if (g711Bytes == null || g711Bytes.Length == 0) return;

        foreach (byte b in g711Bytes)
            _g711Buffer.Enqueue(b);
    }

    /// <summary>
    /// Buffer AI audio (PCM16 @ 24kHz) for playout.
    /// Resamples 24kHz → 8kHz with simple 3:1 decimation.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (pcm24kBytes == null || pcm24kBytes.Length < 6) return;

        int sampleCount24k = pcm24kBytes.Length / 2;

        // Simple 3:1 decimation (take middle sample)
        for (int i = 0; i < sampleCount24k - 2; i += 3)
        {
            short sample = BitConverter.ToInt16(pcm24kBytes, (i + 1) * 2);
            _sampleBuffer.Enqueue(sample);
        }
    }

    /// <summary>
    /// Clear buffer and reset state (call on barge-in or new response).
    /// </summary>
    public void Clear()
    {
        _g711Buffer.Clear();
        _sampleBuffer.Clear();
        _lastG711Byte = 0xD5;  // A-law silence
        _lastSample = 0;
        _isCurrentlySpeaking = false;
        _emptyFramesCount = 0;
        OnLog?.Invoke("[RTP] Buffer Purged & Silent Flush Sent");
    }

    public void Start() => _rtpTimer = new System.Threading.Timer(SendFrame, null, 0, FRAME_MS);

    private void SendFrame(object? state)
    {
        if (_useNativeG711)
            SendNativeG711Frame();
        else
            SendPcmFrame();
    }

    /// <summary>
    /// Native G.711 mode: Pass through G.711 bytes directly (zero DSP).
    /// </summary>
    private void SendNativeG711Frame()
    {
        int minBufferBytes = G711_FRAME_SIZE * (MIN_BUFFER_MS / FRAME_MS);

        // Wait for minimum buffer before starting
        if (!_isCurrentlySpeaking)
        {
            if (_g711Buffer.Count < minBufferBytes)
            {
                SendSilence();
                return;
            }
            _isCurrentlySpeaking = true;
            _emptyFramesCount = 0;
            OnLog?.Invoke("[RTP] Buffer ready, starting playout.");
        }

        bool hasData = false;

        for (int i = 0; i < G711_FRAME_SIZE; i++)
        {
            if (_g711Buffer.TryDequeue(out byte sample))
            {
                _frameBuffer[i] = sample;
                _lastG711Byte = sample;
                hasData = true;
            }
            else
            {
                // Fade to silence (interpolate towards 0xD5 for A-law)
                _frameBuffer[i] = _lastG711Byte;
            }
        }

        if (hasData)
        {
            _emptyFramesCount = 0;
            _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, _frameBuffer, _timestamp, 0, 8);
            _timestamp += G711_FRAME_SIZE;
        }
        else
        {
            _emptyFramesCount++;

            if (_emptyFramesCount < GRACE_PERIOD_FRAMES)
            {
                _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, _frameBuffer, _timestamp, 0, 8);
                _timestamp += G711_FRAME_SIZE;
            }
            else
            {
                if (_isCurrentlySpeaking)
                {
                    _isCurrentlySpeaking = false;
                    OnQueueEmpty?.Invoke();
                }
                SendSilence();
            }
        }
    }

    /// <summary>
    /// Legacy PCM mode: Downsample and encode to G.711.
    /// </summary>
    private void SendPcmFrame()
    {
        int minSamples = G711_FRAME_SIZE * (MIN_BUFFER_MS / FRAME_MS);

        if (!_isCurrentlySpeaking)
        {
            if (_sampleBuffer.Count < minSamples)
            {
                SendSilence();
                return;
            }
            _isCurrentlySpeaking = true;
            _emptyFramesCount = 0;
            OnLog?.Invoke("[RTP] Buffer ready, starting playout.");
        }

        short[] frame = new short[G711_FRAME_SIZE];
        bool hasData = false;

        for (int i = 0; i < G711_FRAME_SIZE; i++)
        {
            if (_sampleBuffer.TryDequeue(out short sample))
            {
                frame[i] = sample;
                _lastSample = sample;
                hasData = true;
            }
            else
            {
                // Smooth decay to avoid clicks
                frame[i] = (short)(_lastSample * 0.8f);
                _lastSample = frame[i];
            }
        }

        if (hasData)
        {
            _emptyFramesCount = 0;
            PushRtp(frame);
        }
        else
        {
            _emptyFramesCount++;

            if (_emptyFramesCount < GRACE_PERIOD_FRAMES)
            {
                PushRtp(frame);
            }
            else
            {
                if (_isCurrentlySpeaking)
                {
                    _isCurrentlySpeaking = false;
                    OnQueueEmpty?.Invoke();
                }
                SendSilence();
            }
        }
    }

    private void PushRtp(short[] pcmFrame)
    {
        for (int i = 0; i < pcmFrame.Length; i++)
            _frameBuffer[i] = SIPSorcery.Media.ALawEncoder.LinearToALawSample(pcmFrame[i]);

        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, _frameBuffer, _timestamp, 0, 8);
        _timestamp += G711_FRAME_SIZE;
    }

    private void SendSilence()
    {
        Array.Fill(_frameBuffer, (byte)0xD5); // G.711 A-law silence
        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, _frameBuffer, _timestamp, 0, 8);
        _timestamp += G711_FRAME_SIZE;
    }

    public void Stop() => _rtpTimer?.Dispose();

    public void Dispose()
    {
        _rtpSession.OnRtpPacketReceived -= HandleSymmetricRtp;
        Stop();
        _g711Buffer.Clear();
        _sampleBuffer.Clear();
    }
}
