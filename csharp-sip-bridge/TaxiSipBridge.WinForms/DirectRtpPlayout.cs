using System;
using System.Collections.Concurrent;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// AI → SIP Audio Playout Engine.
/// Receives 24kHz PCM from OpenAI, resamples to 8kHz, encodes to G.711 A-law,
/// and sends via RTP at a stable 20ms cadence.
/// 
/// Features:
/// - NAT punch-through via symmetric RTP
/// - Grace period before OnQueueEmpty to prevent premature cutoff
/// - Smooth sample decay for clean audio tails
/// </summary>
public class DirectRtpPlayout : IDisposable
{
    private const int PCM_8K_SAMPLES = 160;
    private const int FRAME_MS = 20;
    private const int MIN_SAMPLES_TO_START = PCM_8K_SAMPLES * 10; // 200ms cushion

    private readonly ConcurrentQueue<short> _sampleBuffer = new();
    private readonly RTPSession _rtpSession;
    private readonly byte[] _alawBuffer = new byte[PCM_8K_SAMPLES];

    private System.Threading.Timer? _rtpTimer;
    private uint _timestamp = 0;
    private float _filterState = 0;
    private bool _isCurrentlySpeaking = false;
    private short _lastSample = 0;

    // Grace period: continue pushing decaying samples for 100ms before declaring "done"
    private int _emptyFramesCount = 0;
    private const int GRACE_PERIOD_FRAMES = 5;

    // NAT tracking
    private IPEndPoint? _lastRemoteEndpoint;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public DirectRtpPlayout(RTPSession rtpSession)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
        _rtpSession.AcceptRtpFromAny = true;
        _rtpSession.OnRtpPacketReceived += HandleSymmetricRtp;
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
    /// Buffer AI audio (PCM16 @ 24kHz) for playout.
    /// Resamples 24kHz → 8kHz with anti-aliasing filter.
    /// </summary>
    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (pcm24kBytes == null || pcm24kBytes.Length < 6) return;

        int sampleCount24k = pcm24kBytes.Length / 2;
        float alpha = 0.40f;
        float volumeBoost = 1.4f;

        for (int i = 0; i < sampleCount24k - 2; i += 3)
        {
            short s1 = BitConverter.ToInt16(pcm24kBytes, i * 2);
            short s2 = BitConverter.ToInt16(pcm24kBytes, (i + 1) * 2);
            short s3 = BitConverter.ToInt16(pcm24kBytes, (i + 2) * 2);

            // 3-tap FIR filter [0.25, 0.5, 0.25] for anti-aliasing
            float target = (s1 * 0.25f) + (s2 * 0.5f) + (s3 * 0.25f);
            _filterState = (_filterState * (1 - alpha)) + (target * alpha);

            float boosted = _filterState * volumeBoost;

            // Soft limiter to prevent clipping
            if (boosted > 28000) boosted = 28000 + (boosted - 28000) * 0.2f;
            if (boosted < -28000) boosted = -28000 + (boosted + 28000) * 0.2f;

            _sampleBuffer.Enqueue((short)Math.Clamp(boosted, short.MinValue, short.MaxValue));
        }
    }

    /// <summary>
    /// Clear buffer and reset state (call on barge-in or new response).
    /// </summary>
    public void Clear()
    {
        _sampleBuffer.Clear();
        _filterState = 0;
        _lastSample = 0;
        _isCurrentlySpeaking = false;
        _emptyFramesCount = 0;
    }

    public void Start() => _rtpTimer = new System.Threading.Timer(SendFrame, null, 0, FRAME_MS);

    private void SendFrame(object? state)
    {
        // Wait for minimum buffer before starting (absorbs OpenAI jitter)
        if (!_isCurrentlySpeaking)
        {
            if (_sampleBuffer.Count < MIN_SAMPLES_TO_START)
            {
                SendSilence();
                return;
            }
            _isCurrentlySpeaking = true;
            _emptyFramesCount = 0;
        }

        short[] frame = new short[PCM_8K_SAMPLES];
        bool hasData = false;

        for (int i = 0; i < PCM_8K_SAMPLES; i++)
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

            // Grace period: keep pushing decaying samples
            if (_emptyFramesCount < GRACE_PERIOD_FRAMES)
            {
                PushRtp(frame);
            }
            else
            {
                // Only NOW declare speaking complete
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
            _alawBuffer[i] = SIPSorcery.Media.ALawEncoder.LinearToALawSample(pcmFrame[i]);

        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, _alawBuffer, _timestamp, 0, 8);
        _timestamp += PCM_8K_SAMPLES;
    }

    private void SendSilence()
    {
        byte[] silence = new byte[PCM_8K_SAMPLES];
        Array.Fill(silence, (byte)0xD5); // G.711 A-law silence
        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, silence, _timestamp, 0, 8);
        _timestamp += PCM_8K_SAMPLES;
    }

    public void Stop() => _rtpTimer?.Dispose();

    public void Dispose()
    {
        _rtpSession.OnRtpPacketReceived -= HandleSymmetricRtp;
        Stop();
        _sampleBuffer.Clear();
    }
}
