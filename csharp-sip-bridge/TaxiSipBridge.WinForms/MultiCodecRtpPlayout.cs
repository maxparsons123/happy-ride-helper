using System;
using System.Collections.Concurrent;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Multi-codec RTP playout engine.
/// Accepts 24kHz PCM and encodes to the negotiated codec (Opus, G.722, PCMA, PCMU).
/// Uses timer-driven 20ms RTP delivery with NAT punch-through via symmetric RTP.
/// </summary>
public class MultiCodecRtpPlayout : IDisposable
{
    private const int FRAME_MS = 20;
    private const int MIN_BUFFER_MS = 100; // Buffer before starting

    private readonly RTPSession _rtpSession;
    private readonly ConcurrentQueue<short> _sampleBuffer = new();

    // Negotiated codec
    private AudioCodecsEnum _codec = AudioCodecsEnum.PCMA;
    private int _payloadType = 8;
    private int _outputSampleRate = 8000;

    // Samples per frame based on codec
    private int _samplesPerFrame = 160; // 20ms at 8kHz

    private System.Threading.Timer? _rtpTimer;
    private uint _timestamp = 0;
    private bool _isPlaying = false;
    private short _lastSample = 0;
    private int _emptyFramesCount = 0;
    private const int GRACE_PERIOD_FRAMES = 5;

    // NAT tracking
    private IPEndPoint? _lastRemoteEndpoint;

    // Opus frame accumulator (need 960 samples at 48kHz for 20ms)
    private readonly List<short> _opusAccumulator = new();
    private const int OPUS_FRAME_SIZE = 960;

    public event Action? OnQueueEmpty;
    public event Action<string>? OnLog;

    public MultiCodecRtpPlayout(RTPSession rtpSession)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
        _rtpSession.AcceptRtpFromAny = true;
        _rtpSession.OnRtpPacketReceived += HandleSymmetricRtp;
    }

    /// <summary>
    /// Set the output codec based on SDP negotiation.
    /// </summary>
    public void SetCodec(AudioCodecsEnum codec, int payloadType)
    {
        _codec = codec;
        _payloadType = payloadType;

        switch (codec)
        {
            case AudioCodecsEnum.OPUS:
                _outputSampleRate = 48000;
                _samplesPerFrame = 960; // 20ms at 48kHz
                break;
            case AudioCodecsEnum.G722:
                _outputSampleRate = 16000;
                _samplesPerFrame = 320; // 20ms at 16kHz
                break;
            case AudioCodecsEnum.PCMA:
            case AudioCodecsEnum.PCMU:
            default:
                _outputSampleRate = 8000;
                _samplesPerFrame = 160; // 20ms at 8kHz
                break;
        }

        OnLog?.Invoke($"[MultiCodec] Set codec: {codec} (PT{payloadType}, {_outputSampleRate}Hz, {_samplesPerFrame} samples/frame)");
    }

    /// <summary>
    /// Symmetric RTP: Lock outbound to actual caller endpoint.
    /// </summary>
    private void HandleSymmetricRtp(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType == SDPMediaTypesEnum.audio && (_lastRemoteEndpoint == null || !_lastRemoteEndpoint.Equals(remoteEndPoint)))
        {
            _lastRemoteEndpoint = remoteEndPoint;
            _rtpSession.SetDestination(SDPMediaTypesEnum.audio, remoteEndPoint, remoteEndPoint);
            OnLog?.Invoke($"[NAT] ✓ RTP locked to {remoteEndPoint}");
        }
    }

    /// <summary>
    /// Buffer audio (PCM16 @ 24kHz) for playout.
    /// Automatically resamples to the output codec's sample rate.
    /// </summary>
    public void BufferAudio(byte[] pcm24kBytes)
    {
        if (pcm24kBytes == null || pcm24kBytes.Length < 2) return;

        short[] pcm24k = AudioCodecs.BytesToShorts(pcm24kBytes);

        // Resample 24kHz → output sample rate
        short[] resampled = ResampleToOutput(pcm24k);

        foreach (var sample in resampled)
            _sampleBuffer.Enqueue(sample);
    }

    private short[] ResampleToOutput(short[] pcm24k)
    {
        switch (_outputSampleRate)
        {
            case 48000:
                // 24k → 48k (2x interpolation)
                return AudioCodecs.Resample(pcm24k, 24000, 48000);
            case 16000:
                // 24k → 16k
                return AudioCodecs.Resample(pcm24k, 24000, 16000);
            case 8000:
            default:
                // 24k → 8k
                return AudioCodecs.Resample24kTo8k(pcm24k);
        }
    }

    public void Clear()
    {
        _sampleBuffer.Clear();
        _opusAccumulator.Clear();
        _lastSample = 0;
        _isPlaying = false;
        _emptyFramesCount = 0;
    }

    public void Start()
    {
        _rtpTimer = new System.Threading.Timer(SendFrame, null, 0, FRAME_MS);
        OnLog?.Invoke($"[MultiCodec] Started ({_codec})");
    }

    private void SendFrame(object? state)
    {
        int minSamples = (_samplesPerFrame * MIN_BUFFER_MS) / FRAME_MS;

        // Wait for buffer before starting
        if (!_isPlaying)
        {
            if (_sampleBuffer.Count < minSamples)
            {
                SendSilence();
                return;
            }
            _isPlaying = true;
            _emptyFramesCount = 0;
            OnLog?.Invoke($"[RTP] ▶️ Playing ({_sampleBuffer.Count} samples buffered)");
        }

        short[] frame = new short[_samplesPerFrame];
        bool hasData = false;

        for (int i = 0; i < _samplesPerFrame; i++)
        {
            if (_sampleBuffer.TryDequeue(out short sample))
            {
                frame[i] = sample;
                _lastSample = sample;
                hasData = true;
            }
            else
            {
                // Smooth decay
                frame[i] = (short)(_lastSample * 0.8f);
                _lastSample = frame[i];
            }
        }

        if (hasData)
        {
            _emptyFramesCount = 0;
            EncodeAndSend(frame);
        }
        else
        {
            _emptyFramesCount++;
            if (_emptyFramesCount < GRACE_PERIOD_FRAMES)
            {
                EncodeAndSend(frame);
            }
            else
            {
                if (_isPlaying)
                {
                    _isPlaying = false;
                    OnQueueEmpty?.Invoke();
                }
                SendSilence();
            }
        }
    }

    private void EncodeAndSend(short[] frame)
    {
        byte[] encoded;

        switch (_codec)
        {
            case AudioCodecsEnum.OPUS:
                // Opus needs exactly 960 samples at 48kHz for 20ms
                encoded = AudioCodecs.OpusEncode(frame);
                break;

            case AudioCodecsEnum.G722:
                encoded = AudioCodecs.G722Encode(frame);
                break;

            case AudioCodecsEnum.PCMU:
                encoded = AudioCodecs.MuLawEncode(frame);
                break;

            case AudioCodecsEnum.PCMA:
            default:
                encoded = AudioCodecs.ALawEncode(frame);
                break;
        }

        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, encoded, _timestamp, 0, _payloadType);
        _timestamp += (uint)_samplesPerFrame;
    }

    private void SendSilence()
    {
        byte[] silence;

        switch (_codec)
        {
            case AudioCodecsEnum.OPUS:
                // Opus silence - encode empty frame
                silence = AudioCodecs.OpusEncode(new short[OPUS_FRAME_SIZE]);
                break;

            case AudioCodecsEnum.G722:
                silence = new byte[_samplesPerFrame / 2];
                break;

            case AudioCodecsEnum.PCMU:
                silence = new byte[_samplesPerFrame];
                Array.Fill(silence, (byte)0xFF); // µ-law silence
                break;

            case AudioCodecsEnum.PCMA:
            default:
                silence = new byte[_samplesPerFrame];
                Array.Fill(silence, (byte)0xD5); // A-law silence
                break;
        }

        _rtpSession.SendRtpRaw(SDPMediaTypesEnum.audio, silence, _timestamp, 0, _payloadType);
        _timestamp += (uint)_samplesPerFrame;
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
        _sampleBuffer.Clear();
        _opusAccumulator.Clear();
    }
}
