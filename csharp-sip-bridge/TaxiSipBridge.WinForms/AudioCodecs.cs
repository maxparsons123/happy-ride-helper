using System.Collections.Generic;
using System.Linq;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Concentus.Enums;
using Concentus.Structs;

namespace TaxiSipBridge;

/// <summary>
/// Resampler mode for A/B testing (kept for UI compatibility).
/// </summary>
public enum ResamplerMode
{
    NAudio,
    Custom
}

/// <summary>
/// Audio codec utilities for encoding/decoding and resampling.
/// Simplified for reliability - DSP effects are handled in AdaAudioSource.
/// </summary>
public static class AudioCodecs
{
    /// <summary>
    /// Current resampler mode (for UI compatibility - not actively used in simplified mode).
    /// </summary>
    public static ResamplerMode CurrentResamplerMode { get; set; } = ResamplerMode.NAudio;
    /// <summary>
    /// Decode µ-law (G.711) to PCM16 samples.
    /// </summary>
    public static short[] MuLawDecode(byte[] data)
    {
        var pcm = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            int mulaw = ~data[i];
            int sign = (mulaw & 0x80) != 0 ? -1 : 1;
            int exponent = (mulaw >> 4) & 0x07;
            int mantissa = mulaw & 0x0F;
            pcm[i] = (short)(sign * (((mantissa << 3) + 0x84) << exponent) - 0x84);
        }
        return pcm;
    }

    /// <summary>
    /// Decode A-law (G.711) to PCM16 samples.
    /// </summary>
    public static short[] ALawDecode(byte[] data)
    {
        var pcm = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            int alaw = data[i] ^ 0x55;
            int sign = (alaw & 0x80) != 0 ? -1 : 1;
            int exponent = (alaw >> 4) & 0x07;
            int mantissa = alaw & 0x0F;
            
            int magnitude = exponent == 0
                ? (mantissa << 4) + 8
                : ((mantissa << 4) + 0x108) << (exponent - 1);
            
            pcm[i] = (short)(sign * magnitude);
        }
        return pcm;
    }

    /// <summary>
    /// Encode PCM16 samples to A-law (G.711) using NAudio for high-quality encoding.
    /// Applies TelephonyVoiceShaping (EQ + compression) before encoding.
    /// </summary>
    public static byte[] ALawEncode(short[] pcm, bool applyShaping = true)
    {
        var shaped = applyShaping ? TelephonyVoiceShaping.Process(pcm) : pcm;
        var alaw = new byte[shaped.Length];
        for (int i = 0; i < shaped.Length; i++)
        {
            alaw[i] = NAudio.Codecs.ALawEncoder.LinearToALawSample(shaped[i]);
        }
        return alaw;
    }

    /// <summary>
    /// Encode PCM16 samples to µ-law (G.711).
    /// </summary>
    public static byte[] MuLawEncode(short[] pcm)
    {
        var ulaw = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            int s = pcm[i];
            int mask = 0x80, seg = 8;
            if (s < 0) { s = -s; mask = 0x00; }
            s += 0x84;
            if (s > 0x7FFF) s = 0x7FFF;
            for (int j = 0x4000; (s & j) == 0 && seg > 0; j >>= 1) seg--;
            ulaw[i] = (byte)~(mask | (seg << 4) | ((s >> (seg + 3)) & 0x0F));
        }
        return ulaw;
    }

    #region FIR Anti-Aliasing Filter and Decimation

    private const int FIR_TAPS = 101;
    private static double[]? _firCoeffs;
    private static float[]? _firHistory;
    private static readonly object _firLock = new object();

    /// <summary>
    /// Get or create FIR low-pass filter coefficients (4kHz cutoff for 24kHz sample rate).
    /// Uses Hamming window for smooth frequency response.
    /// </summary>
    private static double[] GetFirCoefficients()
    {
        if (_firCoeffs != null) return _firCoeffs;

        var coeffs = new double[FIR_TAPS];
        double cutoff = 4000.0 / 12000.0; // 4kHz / (24kHz / 2) = normalized cutoff

        for (int i = 0; i < FIR_TAPS; i++)
        {
            int m = i - FIR_TAPS / 2;
            if (m == 0)
                coeffs[i] = 2 * cutoff;
            else
                coeffs[i] = Math.Sin(2 * Math.PI * cutoff * m) / (Math.PI * m);

            // Hamming window for smooth rolloff
            coeffs[i] *= 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / FIR_TAPS);
        }

        _firCoeffs = coeffs;
        return coeffs;
    }

    /// <summary>
    /// Apply FIR low-pass filter (4kHz cutoff) to prevent aliasing before decimation.
    /// Stateful - maintains history across calls for seamless audio.
    /// </summary>
    public static float[] FirLowPass24k(float[] input)
    {
        lock (_firLock)
        {
            var coeffs = GetFirCoefficients();
            _firHistory ??= new float[FIR_TAPS];

            var output = new float[input.Length];

            for (int n = 0; n < input.Length; n++)
            {
                // Shift history and add new sample
                for (int k = FIR_TAPS - 1; k > 0; k--)
                    _firHistory[k] = _firHistory[k - 1];
                _firHistory[0] = input[n];

                // Convolve with FIR coefficients
                double sum = 0.0;
                for (int k = 0; k < FIR_TAPS; k++)
                    sum += _firHistory[k] * coeffs[k];

                output[n] = (float)sum;
            }

            return output;
        }
    }

    /// <summary>
    /// Decimate 24kHz to 8kHz (factor of 3). Must be called AFTER FirLowPass24k.
    /// </summary>
    public static short[] Decimate24kTo8k(float[] filtered)
    {
        int outputLen = filtered.Length / 3;
        var output = new short[outputLen];

        for (int i = 0, j = 0; j < outputLen; i += 3, j++)
        {
            float val = filtered[i] * 32767f;
            output[j] = (short)Math.Clamp(val, -32768, 32767);
        }

        return output;
    }

    /// <summary>
    /// High-quality 24kHz to 8kHz conversion with anti-aliasing.
    /// FIR low-pass (4kHz) → Decimate by 3 → Ready for G.711 encoding.
    /// </summary>
    public static short[] Resample24kTo8k(short[] input)
    {
        // Convert to float for filtering
        var floatInput = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
            floatInput[i] = input[i] / 32768f;

        // Anti-aliasing filter
        var filtered = FirLowPass24k(floatInput);

        // Decimate
        return Decimate24kTo8k(filtered);
    }

    /// <summary>
    /// Reset FIR filter state (call between calls/sessions).
    /// </summary>
    public static void ResetFirFilter()
    {
        lock (_firLock) { _firHistory = null; }
    }

    #endregion

    /// <summary>
    /// Simple linear interpolation resampling for upsampling (8k→24k).
    /// Anti-aliasing not needed for upsampling.
    /// </summary>
    public static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;
        if (input.Length == 0) return input;

        // For downsampling, use specialized FIR-based method
        if (fromRate == 24000 && toRate == 8000)
            return Resample24kTo8k(input);

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

    /// <summary>
    /// Convert byte array (little-endian PCM16) to short array.
    /// </summary>
    public static short[] BytesToShorts(byte[] b)
    {
        var s = new short[b.Length / 2];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = (short)(b[i * 2] | (b[i * 2 + 1] << 8));
        }
        return s;
    }

    /// <summary>
    /// Convert short array to byte array (little-endian PCM16).
    /// </summary>
    public static byte[] ShortsToBytes(short[] s)
    {
        var b = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, b, 0, b.Length);
        return b;
    }

    #region Opus Codec

    public const int OPUS_SAMPLE_RATE = 48000;
    // Decode as stereo to match SDP negotiation (ch=2), then downmix to mono in caller
    public const int OPUS_DECODE_CHANNELS = 2;
    public const int OPUS_ENCODE_CHANNELS = 1; // Encode mono (works fine with stereo SDP)
    public const int OPUS_CHANNELS = 1; // For backward compatibility
    public const int OPUS_BITRATE = 32000;
    public const int OPUS_FRAME_SIZE_MS = 20;
    public const int OPUS_FRAME_SIZE = OPUS_SAMPLE_RATE / 1000 * OPUS_FRAME_SIZE_MS; // 960 samples per channel

    private static OpusEncoder? _opusEncoder;
    private static OpusDecoder? _opusDecoder;
    private static readonly object _opusEncoderLock = new object();
    private static readonly object _opusDecoderLock = new object();

    public static byte[] OpusEncode(short[] pcm)
    {
        lock (_opusEncoderLock)
        {
            _opusEncoder ??= new OpusEncoder(OPUS_SAMPLE_RATE, OPUS_ENCODE_CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
            _opusEncoder.Bitrate = OPUS_BITRATE;

            // Expect 960 mono samples for 20ms frame at 48kHz
            short[] frame = pcm.Length == OPUS_FRAME_SIZE ? pcm : new short[OPUS_FRAME_SIZE];
            if (pcm.Length != OPUS_FRAME_SIZE)
                Array.Copy(pcm, frame, Math.Min(pcm.Length, OPUS_FRAME_SIZE));

            byte[] outBuf = new byte[1275];
            int len = _opusEncoder.Encode(frame, 0, OPUS_FRAME_SIZE, outBuf, 0, outBuf.Length);
            byte[] result = new byte[len];
            Array.Copy(outBuf, result, len);
            return result;
        }
    }

    /// <summary>
    /// Decode Opus frame. Returns STEREO interleaved samples (1920 samples for 20ms).
    /// Caller should downmix to mono if needed.
    /// </summary>
    public static short[] OpusDecode(byte[] encoded)
    {
        lock (_opusDecoderLock)
        {
            // Decode as stereo to match SDP ch=2 negotiation
            _opusDecoder ??= new OpusDecoder(OPUS_SAMPLE_RATE, OPUS_DECODE_CHANNELS);
            // Stereo output buffer: 960 samples × 2 channels = 1920 samples
            int stereoFrameSize = OPUS_FRAME_SIZE * OPUS_DECODE_CHANNELS;
            short[] outBuf = new short[stereoFrameSize];
            int len = _opusDecoder.Decode(encoded, 0, encoded.Length, outBuf, 0, OPUS_FRAME_SIZE, false);
            // len is samples per channel, so total samples = len * 2
            int totalSamples = len * OPUS_DECODE_CHANNELS;
            return totalSamples < stereoFrameSize ? outBuf.Take(totalSamples).ToArray() : outBuf;
        }
    }

    public static void ResetOpus()
    {
        lock (_opusEncoderLock) { _opusEncoder = null; }
        lock (_opusDecoderLock) { _opusDecoder = null; }
    }

    #endregion

    #region G.722 Wideband (16kHz)

    public const int G722_SAMPLE_RATE = 16000;
    public const int G722_BITRATE = 64000;

    private static int _g722LowBand = 0;
    private static int _g722HighBand = 0;
    private static int _g722LowPrev = 0;
    private static int _g722HighPrev = 0;
    private static readonly object _g722Lock = new object();

    private static readonly int[] G722_QL = { -2048, -1024, -512, -256, 0, 256, 512, 1024 };
    private static readonly int[] G722_QH = { -256, -128, -64, 0, 64, 128, 256, 512 };

    public static byte[] G722Encode(short[] pcm)
    {
        lock (_g722Lock)
        {
            var output = new byte[pcm.Length / 2];
            for (int i = 0; i < pcm.Length - 1; i += 2)
            {
                int low = (pcm[i] + pcm[i + 1]) / 2;
                int high = (pcm[i] - pcm[i + 1]) / 2;

                int diffL = low - _g722LowPrev;
                int codeL = QuantizeLow(diffL);
                _g722LowPrev = _g722LowPrev + G722_QL[codeL & 0x07];

                int diffH = high - _g722HighPrev;
                int codeH = QuantizeHigh(diffH);
                _g722HighPrev = _g722HighPrev + G722_QH[codeH & 0x03];

                output[i / 2] = (byte)((codeL & 0x3F) | ((codeH & 0x03) << 6));
            }
            return output;
        }
    }

    public static short[] G722Decode(byte[] encoded)
    {
        lock (_g722Lock)
        {
            var output = new short[encoded.Length * 2];
            for (int i = 0; i < encoded.Length; i++)
            {
                int codeL = encoded[i] & 0x3F;
                int codeH = (encoded[i] >> 6) & 0x03;

                _g722LowBand = Math.Clamp(_g722LowBand + G722_QL[codeL & 0x07], -32768, 32767);
                _g722HighBand = Math.Clamp(_g722HighBand + G722_QH[codeH], -16384, 16383);

                output[i * 2] = (short)Math.Clamp(_g722LowBand + _g722HighBand, -32768, 32767);
                output[i * 2 + 1] = (short)Math.Clamp(_g722LowBand - _g722HighBand, -32768, 32767);
            }
            return output;
        }
    }

    public static void ResetG722()
    {
        lock (_g722Lock) { _g722LowBand = _g722HighBand = _g722LowPrev = _g722HighPrev = 0; }
    }

    private static int QuantizeLow(int diff)
    {
        if (diff < -1536) return 0;
        if (diff < -768) return 1;
        if (diff < -384) return 2;
        if (diff < -128) return 3;
        if (diff < 128) return 4;
        if (diff < 384) return 5;
        if (diff < 768) return 6;
        return 7;
    }

    private static int QuantizeHigh(int diff)
    {
        if (diff < -192) return 0;
        if (diff < -64) return 1;
        if (diff < 64) return 2;
        return 3;
    }

    #endregion

    public static void ResetAllCodecs()
    {
        ResetOpus();
        ResetG722();
        ResetFirFilter();
    }
}

/// <summary>
/// SIPSorcery-compatible audio encoder with Opus and G.722 support.
/// Codec priority: Opus (48kHz) > G.722 (16kHz) > G.711 (8kHz)
/// </summary>
public class UnifiedAudioEncoder : IAudioEncoder
{
    private readonly AudioEncoder _baseEncoder;

    // SDP uses 2 channels for Opus compatibility, but encoder uses mono internally
    private const int SDP_OPUS_CHANNELS = 2;
    
    private static readonly AudioFormat OpusFormat106 = new AudioFormat(
        AudioCodecsEnum.OPUS, 106, AudioCodecs.OPUS_SAMPLE_RATE, SDP_OPUS_CHANNELS, "opus");

    private static readonly AudioFormat OpusFormat111 = new AudioFormat(
        AudioCodecsEnum.OPUS, 111, AudioCodecs.OPUS_SAMPLE_RATE, SDP_OPUS_CHANNELS, "opus");

    private static readonly AudioFormat G722Format = new AudioFormat(
        AudioCodecsEnum.G722, 9, AudioCodecs.G722_SAMPLE_RATE, 1, "G722");

    public UnifiedAudioEncoder()
    {
        _baseEncoder = new AudioEncoder();
    }

    public List<AudioFormat> SupportedFormats
    {
        get
        {
            var formats = new List<AudioFormat>
            {
                OpusFormat106,
                OpusFormat111,
                G722Format
            };

            // Add base formats but exclude Opus/G722 to avoid conflicts
            formats.AddRange(_baseEncoder.SupportedFormats.Where(f =>
                f.Codec != AudioCodecsEnum.OPUS &&
                f.Codec != AudioCodecsEnum.G722));
            return formats;
        }
    }

    public byte[] EncodeAudio(short[] pcm, AudioFormat format)
    {
        switch (format.Codec)
        {
            case AudioCodecsEnum.OPUS:
                return AudioCodecs.OpusEncode(pcm);
            case AudioCodecsEnum.G722:
                return AudioCodecs.G722Encode(pcm);
            case AudioCodecsEnum.PCMA:
                return AudioCodecs.ALawEncode(pcm);
            case AudioCodecsEnum.PCMU:
                return AudioCodecs.MuLawEncode(pcm);
            default:
                return _baseEncoder.EncodeAudio(pcm, format);
        }
    }

    public short[] DecodeAudio(byte[] encodedSample, AudioFormat format)
    {
        switch (format.Codec)
        {
            case AudioCodecsEnum.OPUS:
                return AudioCodecs.OpusDecode(encodedSample);
            case AudioCodecsEnum.G722:
                return AudioCodecs.G722Decode(encodedSample);
            case AudioCodecsEnum.PCMA:
                return AudioCodecs.ALawDecode(encodedSample);
            case AudioCodecsEnum.PCMU:
                return AudioCodecs.MuLawDecode(encodedSample);
            default:
                return _baseEncoder.DecodeAudio(encodedSample, format);
        }
    }

    public void ResetCodecState()
    {
        AudioCodecs.ResetAllCodecs();
    }
}
