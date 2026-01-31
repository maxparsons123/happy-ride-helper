using System.Runtime.InteropServices;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Unified ingress audio processor for SIP → OpenAI.
/// Handles jitter buffering, G.711/Opus/G.722 decoding, DSP, and resampling.
/// Outputs fixed 20ms PCM16 frames ready for OpenAI Realtime API.
/// 
/// Reusable by both LocalOpenAICallHandler and EdgeFunctionCallHandler.
/// </summary>
public sealed class IngressAudioProcessor : IDisposable
{
    // ===========================================
    // CONFIGURATION
    // ===========================================
    public enum TargetRate { Hz16000 = 16000, Hz24000 = 24000 }

    private readonly TargetRate _targetRate;
    private readonly int _targetSamplesPer20ms;
    private readonly int _jitterFrames;

    // ===========================================
    // DSP CONSTANTS (ASR-tuned)
    // ===========================================
    private const float DC_ALPHA = 0.995f;
    private const float PRE_EMPH = 0.97f;
    private const float GATE_OPEN_RMS = 600f;
    private const float GATE_CLOSE_RMS = 450f;
    private const int GATE_HOLD_FRAMES = 6;
    private const float RMS_ALPHA = 0.995f;
    private const float TARGET_RMS = 12000f;
    private const float AGC_MIN = 0.80f;
    private const float AGC_MAX = 2.20f;
    private const float AGC_SMOOTH = 0.02f;
    private const float LIMIT = 29000f;

    // ===========================================
    // JITTER BUFFER
    // ===========================================
    private readonly SortedDictionary<uint, RtpFrame> _rtpByTimestamp = new();
    private uint _nextTimestamp;
    private bool _sync;
    private int _missingFrames;
    private readonly object _jitterLock = new();

    private record struct RtpFrame(int PayloadType, byte[] Payload);

    // ===========================================
    // WORKING BUFFERS
    // ===========================================
    private const int RTP_8K_SAMPLES_20MS = 160;
    private short[] _lastPcmFrame = new short[RTP_8K_SAMPLES_20MS];
    private readonly short[] _resampledFrame;
    private readonly byte[] _outputBytes;

    // ===========================================
    // DSP STATE
    // ===========================================
    private float _dcState;
    private float _preEmphPrev;
    private float _rmsState;
    private float _agcGain = 1.0f;
    private bool _gateOpen = true;
    private int _gateHold;

    // ===========================================
    // CODEC STATE
    // ===========================================
    private readonly Dictionary<int, AudioCodecsEnum> _ptToCodec = new();

    private int _disposed;

    // ===========================================
    // EVENTS
    // ===========================================
    public event Action<byte[]>? OnPcmFrameReady;
    public event Action<string>? OnLog;

    // ===========================================
    // CONSTRUCTOR
    // ===========================================
    public IngressAudioProcessor(TargetRate targetRate = TargetRate.Hz16000, int jitterFrames = 6)
    {
        _targetRate = targetRate;
        _targetSamplesPer20ms = (int)targetRate / 50; // samples in 20ms
        _jitterFrames = Math.Clamp(jitterFrames, 3, 12);

        _resampledFrame = new short[_targetSamplesPer20ms];
        _outputBytes = new byte[_targetSamplesPer20ms * 2];

        Log($"[Ingress] Initialized: target={targetRate}Hz, jitter={jitterFrames} frames ({jitterFrames * 20}ms)");
    }

    // ===========================================
    // PUBLIC API
    // ===========================================

    /// <summary>
    /// Set known payload type → codec mappings (from SDP parsing).
    /// </summary>
    public void SetCodecMap(IReadOnlyDictionary<int, AudioCodecsEnum> ptToCodec)
    {
        _ptToCodec.Clear();
        foreach (var kv in ptToCodec)
            _ptToCodec[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Feed inbound RTP packets. Call from OnRtpPacketReceived.
    /// </summary>
    public void PushRtpAudio(SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || Volatile.Read(ref _disposed) != 0)
            return;

        var ts = rtpPacket.Header.Timestamp;
        var pt = rtpPacket.Header.PayloadType;
        var payload = rtpPacket.Payload;

        if (payload == null || payload.Length == 0) return;

        lock (_jitterLock)
        {
            // Store by timestamp (reorder)
            if (!_rtpByTimestamp.ContainsKey(ts))
                _rtpByTimestamp[ts] = new RtpFrame(pt, payload);

            if (!_sync)
            {
                // Wait for enough buffered frames
                if (_rtpByTimestamp.Count >= _jitterFrames)
                {
                    _nextTimestamp = MinKey();
                    _sync = true;
                    Log($"[Ingress] Jitter sync: start ts={_nextTimestamp}, buffer={_rtpByTimestamp.Count}");
                }
                return;
            }

            // Drain frames in order
            DrainFrames();
        }
    }

    /// <summary>
    /// Reset all state (call between sessions).
    /// </summary>
    public void Reset()
    {
        lock (_jitterLock)
        {
            _rtpByTimestamp.Clear();
            _sync = false;
            _nextTimestamp = 0;
            _missingFrames = 0;
        }

        Array.Clear(_lastPcmFrame);
        _dcState = 0;
        _preEmphPrev = 0;
        _rmsState = 0;
        _agcGain = 1.0f;
        _gateOpen = true;
        _gateHold = 0;
        _ptToCodec.Clear();

        Log("[Ingress] State reset");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Log("[Ingress] Disposed");
    }

    // ===========================================
    // JITTER BUFFER DRAIN
    // ===========================================
    private void DrainFrames()
    {
        while (_rtpByTimestamp.Count > 0)
        {
            if (_rtpByTimestamp.TryGetValue(_nextTimestamp, out var frame))
            {
                _rtpByTimestamp.Remove(_nextTimestamp);
                _missingFrames = 0;

                ProcessOneFrame(frame.PayloadType, frame.Payload);
                _nextTimestamp += GetTimestampIncrement(frame.PayloadType);
            }
            else
            {
                _missingFrames++;
                if (_missingFrames <= 2)
                {
                    // PLC: repeat last frame with attenuation
                    PlcFill();
                    _nextTimestamp += RTP_8K_SAMPLES_20MS; // assume 8k for PLC
                    continue;
                }

                // Resync if too far behind
                if (_rtpByTimestamp.Count >= _jitterFrames)
                {
                    var newTs = MinKey();
                    Log($"[Ingress] Resync: {_nextTimestamp} → {newTs}");
                    _nextTimestamp = newTs;
                    _missingFrames = 0;
                    continue;
                }

                break; // wait for more packets
            }
        }
    }

    private uint GetTimestampIncrement(int pt)
    {
        if (!_ptToCodec.TryGetValue(pt, out var codec))
            codec = pt == 8 ? AudioCodecsEnum.PCMA : AudioCodecsEnum.PCMU;

        return codec switch
        {
            AudioCodecsEnum.OPUS => 960,    // 20ms @ 48kHz
            AudioCodecsEnum.G722 => 320,    // 20ms @ 16kHz
            _ => 160                         // 20ms @ 8kHz (G.711)
        };
    }

    private uint MinKey()
    {
        foreach (var kv in _rtpByTimestamp) return kv.Key;
        return 0;
    }

    // ===========================================
    // FRAME PROCESSING
    // ===========================================
    private void ProcessOneFrame(int pt, byte[] payload)
    {
        if (!_ptToCodec.TryGetValue(pt, out var codec))
            codec = pt == 8 ? AudioCodecsEnum.PCMA : AudioCodecsEnum.PCMU;

        short[] pcm;
        int sampleRate;

        switch (codec)
        {
            case AudioCodecsEnum.PCMU:
                pcm = AudioCodecs.MuLawDecode(payload);
                sampleRate = 8000;
                break;

            case AudioCodecsEnum.PCMA:
                pcm = AudioCodecs.ALawDecode(payload);
                sampleRate = 8000;
                break;

            case AudioCodecsEnum.OPUS:
                var pcm48 = AudioCodecs.OpusDecode(payload);
                // Downmix stereo to mono if needed
                if (AudioCodecs.OPUS_DECODE_CHANNELS == 2 && pcm48.Length % 2 == 0)
                {
                    pcm = new short[pcm48.Length / 2];
                    for (int i = 0; i < pcm.Length; i++)
                        pcm[i] = (short)((pcm48[i * 2] + pcm48[i * 2 + 1]) / 2);
                }
                else
                {
                    pcm = pcm48;
                }
                // Decimate 48k → 24k (2:1)
                pcm = Decimate(pcm, 2);
                sampleRate = 24000;
                break;

            case AudioCodecsEnum.G722:
                pcm = AudioCodecs.G722Decode(payload);
                sampleRate = 16000;
                break;

            default:
                pcm = AudioCodecs.ALawDecode(payload);
                sampleRate = 8000;
                break;
        }

        ProcessPcmFrame(pcm, sampleRate);
    }

    private void ProcessPcmFrame(short[] pcm, int sampleRate)
    {
        // Store for PLC
        if (sampleRate == 8000 && pcm.Length <= _lastPcmFrame.Length)
            Array.Copy(pcm, _lastPcmFrame, pcm.Length);

        // Apply DSP
        ApplyAsrDsp(pcm);

        // Resample to target rate
        short[] resampled = Resample(pcm, sampleRate, (int)_targetRate);

        // Ensure exactly 20ms output
        int needed = _targetSamplesPer20ms;
        if (resampled.Length >= needed)
        {
            Array.Copy(resampled, _resampledFrame, needed);
        }
        else
        {
            Array.Copy(resampled, _resampledFrame, resampled.Length);
            Array.Clear(_resampledFrame, resampled.Length, needed - resampled.Length);
        }

        // Convert to bytes (PCM16 LE)
        MemoryMarshal.AsBytes(_resampledFrame.AsSpan()).CopyTo(_outputBytes);

        // Emit
        OnPcmFrameReady?.Invoke(_outputBytes.ToArray());
    }

    private void PlcFill()
    {
        // Attenuate to avoid robotic repetition
        for (int i = 0; i < _lastPcmFrame.Length; i++)
            _lastPcmFrame[i] = (short)(_lastPcmFrame[i] * 0.92f);

        ProcessPcmFrame(_lastPcmFrame, 8000);
    }

    // ===========================================
    // DSP (ASR-tuned)
    // ===========================================
    private void ApplyAsrDsp(short[] frame)
    {
        double sumSq = 0;

        for (int i = 0; i < frame.Length; i++)
        {
            float x = frame[i];

            // DC blocker
            _dcState = (DC_ALPHA * _dcState) + ((1f - DC_ALPHA) * x);
            x -= _dcState;

            // Pre-emphasis
            float y = x - (PRE_EMPH * _preEmphPrev);
            _preEmphPrev = x;
            x = y;

            frame[i] = (short)Math.Clamp(x, short.MinValue, short.MaxValue);
            sumSq += x * x;
        }

        float rms = (float)Math.Sqrt(sumSq / frame.Length);

        // Noise gate
        if (_gateOpen)
        {
            if (rms < GATE_CLOSE_RMS)
            {
                _gateHold++;
                if (_gateHold >= GATE_HOLD_FRAMES)
                    _gateOpen = false;
            }
            else
            {
                _gateHold = 0;
            }
        }
        else
        {
            if (rms > GATE_OPEN_RMS)
            {
                _gateOpen = true;
                _gateHold = 0;
            }
        }

        if (!_gateOpen)
        {
            Array.Clear(frame, 0, frame.Length);
            _rmsState *= RMS_ALPHA;
            _agcGain = 1.0f;
            return;
        }

        // AGC
        _rmsState = (RMS_ALPHA * _rmsState) + ((1f - RMS_ALPHA) * rms * rms);
        float curRms = (float)Math.Sqrt(Math.Max(_rmsState, 1f));
        float desired = TARGET_RMS / Math.Max(curRms, 1f);
        desired = Math.Clamp(desired, AGC_MIN, AGC_MAX);
        _agcGain = (_agcGain * (1f - AGC_SMOOTH)) + (desired * AGC_SMOOTH);

        // Apply gain + limiter
        for (int i = 0; i < frame.Length; i++)
        {
            float val = frame[i] * _agcGain;
            if (val > LIMIT) val = LIMIT;
            else if (val < -LIMIT) val = -LIMIT;
            frame[i] = (short)val;
        }
    }

    // ===========================================
    // RESAMPLING
    // ===========================================
    private static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;
        if (input.Length == 0) return input;

        double ratio = (double)fromRate / toRate;
        int outputLength = (int)(input.Length / ratio);
        var output = new short[outputLength];

        for (int i = 0; i < output.Length; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            if (srcIndex + 1 < input.Length)
            {
                double val = input[srcIndex] * (1 - frac) + input[srcIndex + 1] * frac;
                output[i] = (short)Math.Clamp(val, -32768, 32767);
            }
            else if (srcIndex < input.Length)
            {
                output[i] = input[srcIndex];
            }
        }
        return output;
    }

    private static short[] Decimate(short[] input, int factor)
    {
        int outLen = input.Length / factor;
        var output = new short[outLen];
        for (int i = 0, j = 0; j < outLen && i < input.Length; i += factor, j++)
            output[j] = input[i];
        return output;
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
