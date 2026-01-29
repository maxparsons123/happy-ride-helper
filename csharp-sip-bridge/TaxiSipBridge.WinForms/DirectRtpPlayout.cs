using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

public class DirectRtpPlayout : IDisposable
{
    private const int PCM_8K_SAMPLES = 160;
    private const int FRAME_MS = 20;

    // 160ms cushion to absorb OpenAI's initial burst and NAT discovery time.
    private const int MIN_SAMPLES_TO_START = PCM_8K_SAMPLES * 8;

    private readonly ConcurrentQueue<short> _sampleBuffer = new();
    private readonly RTPSession _rtpSession;
    private readonly byte[] _alawBuffer = new byte[PCM_8K_SAMPLES];

    private System.Threading.Timer? _rtpTimer;
    private uint _timestamp = 0;
    private float _filterState = 0;
    private bool _isCurrentlySpeaking = false;
    private short _lastSample = 0;

    // NAT Tracking
    private IPEndPoint? _lastRemoteEndpoint;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public DirectRtpPlayout(RTPSession rtpSession)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));

        // --- NAT PUNCH-THROUGH SETUP ---
        // AcceptRtpFromAny is the primary flag for NAT traversal in SIPSorcery
        _rtpSession.AcceptRtpFromAny = true;

        // Wire up Symmetric RTP: Listen for where the caller is sending audio from
        _rtpSession.OnRtpPacketReceived += HandleSymmetricRtp;
    }

    /// <summary>
    /// SYMMETRIC RTP: This ensures audio is sent back to the actual network source,
    /// bypassing NAT firewalls that block the IP specified in the SIP Invite.
    /// </summary>
    private void HandleSymmetricRtp(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType == SDPMediaTypesEnum.audio)
        {
            if (_lastRemoteEndpoint == null || !_lastRemoteEndpoint.Equals(remoteEndPoint))
            {
                _lastRemoteEndpoint = remoteEndPoint;
                // Dynamically pivot our outgoing audio stream to this discovered endpoint
                _rtpSession.SetDestination(SDPMediaTypesEnum.audio, remoteEndPoint, remoteEndPoint);
                OnLog?.Invoke($"[NAT] Symmetric RTP locked to: {remoteEndPoint}");
            }
        }
    }

    public void BufferAiAudio(byte[] pcm24kBytes)
    {
        if (pcm24kBytes == null || pcm24kBytes.Length < 6) return;

        int sampleCount24k = pcm24kBytes.Length / 2;
        float alpha = 0.40f;
        float volumeBoost = 1.5f;

        for (int i = 0; i < sampleCount24k - 2; i += 3)
        {
            short s1 = BitConverter.ToInt16(pcm24kBytes, i * 2);
            short s2 = BitConverter.ToInt16(pcm24kBytes, (i + 1) * 2);
            short s3 = BitConverter.ToInt16(pcm24kBytes, (i + 2) * 2);

            // Gaussian weighting for natural smoothness
            float target = (s1 * 0.2f) + (s2 * 0.6f) + (s3 * 0.2f);

            _filterState = (_filterState * (1 - alpha)) + (target * alpha);

            float boosted = _filterState * volumeBoost;

            // Soft-Limiter to prevent clipping harshness
            if (boosted > 27000) boosted = 27000 + (boosted - 27000) * 0.4f;
            if (boosted < -27000) boosted = -27000 + (boosted + 27000) * 0.4f;

            _sampleBuffer.Enqueue((short)Math.Clamp(boosted, short.MinValue, short.MaxValue));
        }
    }

    public void Start()
    {
        _rtpTimer = new System.Threading.Timer(SendFrame, null, 0, FRAME_MS);
    }

    private void SendFrame(object? state)
    {
        if (!_isCurrentlySpeaking)
        {
            if (_sampleBuffer.Count < MIN_SAMPLES_TO_START)
            {
                SendSilence(); // Keeps NAT mapping active while waiting for cushion
                return;
            }
            _isCurrentlySpeaking = true;
        }

        short[] frame = new short[PCM_8K_SAMPLES];
        int samplesRead = 0;

        for (int i = 0; i < PCM_8K_SAMPLES; i++)
        {
            if (_sampleBuffer.TryDequeue(out short sample))
            {
                frame[i] = sample;
                _lastSample = sample;
                samplesRead++;
            }
            else
            {
                // Quick decay to avoid clicks/pops
                frame[i] = (short)(_lastSample * 0.65f);
                _lastSample = frame[i];
            }
        }

        if (samplesRead > 0)
        {
            PushRtp(frame);
        }
        else
        {
            _isCurrentlySpeaking = false;
            OnQueueEmpty?.Invoke();
            SendSilence();
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
        // 0xD5 is the A-Law silence byte; sends 20ms of silence to keep the NAT port open
        byte[] silence = new byte[PCM_8K_SAMPLES];
        for (int i = 0; i < silence.Length; i++) silence[i] = 0xD5;

        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, silence, _timestamp, 0, 8);
        _timestamp += PCM_8K_SAMPLES;
    }

    /// <summary>
    /// Clear the audio buffer. Call this on response.created to flush stale data
    /// and ensure the new AI response starts cleanly without collision.
    /// </summary>
    public void Clear()
    {
        while (_sampleBuffer.TryDequeue(out _)) { }
        _isCurrentlySpeaking = false;
        _filterState = 0;
        _lastSample = 0;
    }

    public void Stop()
    {
        _rtpTimer?.Dispose();
        _rtpTimer = null;
    }

    public void Dispose()
    {
        _rtpSession.OnRtpPacketReceived -= HandleSymmetricRtp;
        Stop();
    }
}
