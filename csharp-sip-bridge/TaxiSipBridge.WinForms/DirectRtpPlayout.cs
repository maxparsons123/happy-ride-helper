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

    // 160ms (8 frames) cushion - absorbs OpenAI burst jitter without feeling laggy
    private const int MIN_SAMPLES_TO_START = PCM_8K_SAMPLES * 8;

    private readonly ConcurrentQueue<short> _sampleBuffer = new();
    private readonly RTPSession _rtpSession;
    private readonly byte[] _alawBuffer = new byte[PCM_8K_SAMPLES];

    private System.Threading.Timer? _rtpTimer;
    private uint _timestamp = 0;
    private float _filterState = 0;
    private bool _isCurrentlySpeaking = false;
    private short _lastSample = 0;
    private IPEndPoint? _lastRemoteEndpoint;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public DirectRtpPlayout(RTPSession rtpSession)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));

        // Enable symmetric RTP: accept audio from any address (critical for NAT)
        _rtpSession.AcceptRtpFromAny = true;

        // Subscribe to inbound RTP to implement symmetric NAT traversal
        _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;

        Log("✅ DirectRtpPlayout initialized (symmetric RTP + NAT keepalive)");
    }

    /// <summary>
    /// Symmetric RTP handler: dynamically update remote endpoint based on where packets arrive from.
    /// This punches through NAT by sending audio back to the actual source address.
    /// </summary>
    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio) return;

        // Check if remote endpoint changed (NAT rebinding or initial discovery)
        if (_lastRemoteEndpoint == null || !_lastRemoteEndpoint.Equals(remoteEndPoint))
        {
            bool isFirst = _lastRemoteEndpoint == null;
            _lastRemoteEndpoint = remoteEndPoint;

            // Update session's destination to match actual source (symmetric RTP)
            try
            {
                _rtpSession.SetDestination(SDPMediaTypesEnum.audio, remoteEndPoint, remoteEndPoint);

                if (isFirst)
                    Log($"🔄 NAT: Symmetric RTP locked → {remoteEndPoint}");
                else
                    Log($"🔄 NAT: Endpoint rebind → {remoteEndPoint}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ NAT endpoint update failed: {ex.Message}");
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

            // 3-tap FIR weighted average for anti-aliasing
            float target = (s1 * 0.2f) + (s2 * 0.6f) + (s3 * 0.2f);

            // IIR smoothing filter
            _filterState = (_filterState * (1 - alpha)) + (target * alpha);

            // Volume boost with soft-knee limiting
            float boosted = _filterState * volumeBoost;
            if (boosted > 27000) boosted = 27000 + (boosted - 27000) * 0.4f;
            if (boosted < -27000) boosted = -27000 + (boosted + 27000) * 0.4f;

            _sampleBuffer.Enqueue((short)Math.Clamp(boosted, short.MinValue, short.MaxValue));
        }
    }

    public void Start()
    {
        _rtpTimer = new System.Threading.Timer(SendFrame, null, 0, FRAME_MS);
        Log("▶️ Playout started (160ms startup buffer)");
    }

    private void SendFrame(object? state)
    {
        // Only start "Speaking" state once buffer is deep enough
        // This absorbs the initial OpenAI burst jitter
        if (!_isCurrentlySpeaking)
        {
            if (_sampleBuffer.Count < MIN_SAMPLES_TO_START)
            {
                SendSilence();
                return;
            }
            _isCurrentlySpeaking = true;
            Log($"📦 Startup buffer ready ({_sampleBuffer.Count} samples)");
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
                // Fade-out on underrun to avoid clicks
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
            // Buffer ran completely dry - reset to wait for new cushion
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

    /// <summary>
    /// Send G.711 A-law silence (0xD5) for NAT keepalive.
    /// Keeps the NAT port mapping "hot" during AI silence.
    /// </summary>
    private void SendSilence()
    {
        byte[] silence = new byte[PCM_8K_SAMPLES];
        for (int i = 0; i < silence.Length; i++) silence[i] = 0xD5;

        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, silence, _timestamp, 0, 8);
        _timestamp += PCM_8K_SAMPLES;
    }

    public void Clear()
    {
        while (_sampleBuffer.TryDequeue(out _)) { }
        _isCurrentlySpeaking = false;
        _filterState = 0;
        _lastSample = 0;
        Log("🗑️ Buffer cleared (barge-in)");
    }

    public void Stop()
    {
        _rtpTimer?.Dispose();
        _rtpSession.OnRtpPacketReceived -= OnRtpPacketReceived;
        Log("⏹️ Playout stopped");
    }

    private void Log(string msg) => OnLog?.Invoke($"[DirectRtp] {msg}");

    public void Dispose() => Stop();
}
