using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Concentus.Enums;
using Concentus.Structs;

namespace TaxiSipBridge.Audio;

/// <summary>
/// Resampler mode for A/B testing audio quality.
/// </summary>
public enum ResamplerMode
{
    NAudio,      // WDL resampler - professional grade
    Custom       // Catmull-Rom interpolation with sinc filter
}

/// <summary>
/// Unified audio codec utilities for all telephony audio processing:
/// - G.711 µ-law/A-law encoding/decoding (8kHz narrowband)
/// - G.722 encoding/decoding (16kHz wideband)
/// - Opus encoding/decoding (48kHz high-quality)
/// - High-quality resampling (NAudio WDL or custom FIR)
/// - Pre/de-emphasis filters for telephony
/// </summary>
public static class AudioCodecs
{
    #region Constants
    
    private const float PRE_EMPHASIS = 0.97f;
    private const float DE_EMPHASIS = 0.97f;
    
    // Opus constants
    public const int OPUS_SAMPLE_RATE = 48000;
    public const int OPUS_CHANNELS = 1;
    public const int OPUS_BITRATE = 32000;
    public const int OPUS_FRAME_SIZE_MS = 20;
    public const int OPUS_FRAME_SIZE = OPUS_SAMPLE_RATE / 1000 * OPUS_FRAME_SIZE_MS; // 960 samples
    
    // G.722 constants
    public const int G722_SAMPLE_RATE = 16000;
    public const int G722_BITRATE = 64000;
    public const int G722_FRAME_SIZE_MS = 20;
    public const int G722_FRAME_SIZE = G722_SAMPLE_RATE / 1000 * G722_FRAME_SIZE_MS; // 320 samples
    
    #endregion
    
    #region Configuration
    
    /// <summary>
    /// Current resampler mode. Change at runtime for A/B testing.
    /// </summary>
    public static ResamplerMode CurrentResamplerMode { get; set; } = ResamplerMode.NAudio;
    
    #endregion

    #region G.711 µ-law (PCMU)
    
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
    
    #endregion

    #region G.711 A-law (PCMA)
    
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
            
            int magnitude;
            if (exponent == 0)
                magnitude = (mantissa << 4) + 8;
            else
                magnitude = ((mantissa << 4) + 0x108) << (exponent - 1);
            
            pcm[i] = (short)(sign * magnitude);
        }
        return pcm;
    }

    /// <summary>
    /// Encode PCM16 samples to A-law (G.711).
    /// </summary>
    public static byte[] ALawEncode(short[] pcm)
    {
        var alaw = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            int s = pcm[i];
            int sign = 0;
            if (s < 0) { s = -s; sign = 0x80; }
            
            int exponent = 7;
            int mask = 0x4000;
            while (exponent > 0 && (s & mask) == 0)
            {
                exponent--;
                mask >>= 1;
            }
            
            int mantissa;
            if (exponent == 0)
                mantissa = (s >> 4) & 0x0F;
            else
                mantissa = (s >> (exponent + 3)) & 0x0F;
            
            alaw[i] = (byte)((sign | (exponent << 4) | mantissa) ^ 0x55);
        }
        return alaw;
    }
    
    #endregion

    #region G.722 Wideband (16kHz)
    
    // G.722 sub-band ADPCM state
    private static int _g722LowBand = 0;
    private static int _g722HighBand = 0;
    private static int _g722LowPrev = 0;
    private static int _g722HighPrev = 0;
    private static readonly object _g722Lock = new object();

    // Quantization tables for G.722
    private static readonly int[] G722_QL = { -2048, -1024, -512, -256, 0, 256, 512, 1024 };
    private static readonly int[] G722_QH = { -256, -128, -64, 0, 64, 128, 256, 512 };

    /// <summary>
    /// Encode PCM16 samples to G.722.
    /// Input: 16kHz mono PCM16, output: 64kbps G.722 stream.
    /// </summary>
    public static byte[] G722Encode(short[] pcm)
    {
        lock (_g722Lock)
        {
            // G.722 uses sub-band ADPCM - simplified implementation
            // Each sample pair produces one byte
            var output = new byte[pcm.Length / 2];

            for (int i = 0; i < pcm.Length - 1; i += 2)
            {
                // Split into low and high sub-bands using QMF
                int low = (pcm[i] + pcm[i + 1]) / 2;
                int high = (pcm[i] - pcm[i + 1]) / 2;

                // ADPCM encode low band (6 bits)
                int diffL = low - _g722LowPrev;
                int codeL = QuantizeLow(diffL);
                _g722LowPrev = _g722LowPrev + G722_QL[codeL & 0x07];

                // ADPCM encode high band (2 bits)
                int diffH = high - _g722HighPrev;
                int codeH = QuantizeHigh(diffH);
                _g722HighPrev = _g722HighPrev + G722_QH[codeH & 0x03];

                // Pack: 6 bits low + 2 bits high
                output[i / 2] = (byte)((codeL & 0x3F) | ((codeH & 0x03) << 6));
            }

            return output;
        }
    }

    /// <summary>
    /// Decode G.722 to PCM16 samples.
    /// Input: 64kbps G.722 stream, output: 16kHz mono PCM16.
    /// </summary>
    public static short[] G722Decode(byte[] encoded)
    {
        lock (_g722Lock)
        {
            var output = new short[encoded.Length * 2];

            for (int i = 0; i < encoded.Length; i++)
            {
                // Unpack: 6 bits low + 2 bits high
                int codeL = encoded[i] & 0x3F;
                int codeH = (encoded[i] >> 6) & 0x03;

                // ADPCM decode low band
                _g722LowBand = _g722LowBand + G722_QL[codeL & 0x07];
                _g722LowBand = Math.Clamp(_g722LowBand, -32768, 32767);

                // ADPCM decode high band
                _g722HighBand = _g722HighBand + G722_QH[codeH];
                _g722HighBand = Math.Clamp(_g722HighBand, -16384, 16383);

                // Reconstruct using inverse QMF
                output[i * 2] = (short)Math.Clamp(_g722LowBand + _g722HighBand, -32768, 32767);
                output[i * 2 + 1] = (short)Math.Clamp(_g722LowBand - _g722HighBand, -32768, 32767);
            }

            return output;
        }
    }

    /// <summary>
    /// Reset G.722 encoder/decoder state (call on new call).
    /// </summary>
    public static void ResetG722()
    {
        lock (_g722Lock)
        {
            _g722LowBand = 0;
            _g722HighBand = 0;
            _g722LowPrev = 0;
            _g722HighPrev = 0;
        }
    }

    private static int QuantizeLow(int diff)
    {
        // 6-bit quantization for low sub-band
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
        // 2-bit quantization for high sub-band
        if (diff < -192) return 0;
        if (diff < -64) return 1;
        if (diff < 64) return 2;
        return 3;
    }
    
    #endregion

    #region Opus Codec
    
    private static OpusEncoder? _opusEncoder;
    private static OpusDecoder? _opusDecoder;
    private static readonly object _opusEncoderLock = new object();
    private static readonly object _opusDecoderLock = new object();
    
    /// <summary>
    /// Encode PCM16 samples to Opus.
    /// Input should be 48kHz mono, 960 samples (20ms frame).
    /// </summary>
    public static byte[] OpusEncode(short[] pcm)
    {
        lock (_opusEncoderLock)
        {
            _opusEncoder ??= new OpusEncoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
            _opusEncoder.Bitrate = OPUS_BITRATE;

            // Ensure we have exactly 960 samples (20ms @ 48kHz)
            short[] frame;
            if (pcm.Length == OPUS_FRAME_SIZE)
            {
                frame = pcm;
            }
            else
            {
                frame = new short[OPUS_FRAME_SIZE];
                Array.Copy(pcm, frame, Math.Min(pcm.Length, OPUS_FRAME_SIZE));
            }

            byte[] outBuf = new byte[1275]; // Max Opus frame size
            int len = _opusEncoder.Encode(frame, 0, OPUS_FRAME_SIZE, outBuf, 0, outBuf.Length);
            
            byte[] result = new byte[len];
            Array.Copy(outBuf, result, len);
            return result;
        }
    }

    /// <summary>
    /// Decode Opus to PCM16 samples.
    /// Output is 48kHz mono.
    /// </summary>
    public static short[] OpusDecode(byte[] encoded)
    {
        lock (_opusDecoderLock)
        {
            _opusDecoder ??= new OpusDecoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS);

            short[] outBuf = new short[OPUS_FRAME_SIZE];
            int len = _opusDecoder.Decode(encoded, 0, encoded.Length, outBuf, 0, OPUS_FRAME_SIZE, false);
            
            return len < OPUS_FRAME_SIZE ? outBuf.Take(len).ToArray() : outBuf;
        }
    }
    
    /// <summary>
    /// Reset Opus encoder/decoder state (call on new call).
    /// </summary>
    public static void ResetOpus()
    {
        lock (_opusEncoderLock)
        {
            _opusEncoder = null;
        }
        lock (_opusDecoderLock)
        {
            _opusDecoder = null;
        }
    }
    
    #endregion

    #region Codec Reset

    /// <summary>
    /// Reset all codec states (call at start of new call).
    /// </summary>
    public static void ResetAllCodecs()
    {
        ResetOpus();
        ResetG722();
    }

    #endregion

    #region Pre/De-Emphasis Filters
    
    /// <summary>
    /// Apply pre-emphasis filter to boost high frequencies (consonants).
    /// Use before upsampling for better STT accuracy.
    /// y[n] = x[n] - α * x[n-1]
    /// </summary>
    public static short[] ApplyPreEmphasis(short[] input)
    {
        if (input.Length == 0) return input;
        
        var output = new short[input.Length];
        output[0] = input[0];
        
        for (int i = 1; i < input.Length; i++)
        {
            float val = input[i] - PRE_EMPHASIS * input[i - 1];
            output[i] = SoftClip(val);
        }
        
        return output;
    }

    /// <summary>
    /// Apply de-emphasis filter to restore natural frequency balance.
    /// Use after downsampling before encoding.
    /// y[n] = x[n] + α * y[n-1]
    /// </summary>
    public static short[] ApplyDeEmphasis(short[] input)
    {
        if (input.Length == 0) return input;
        
        var output = new short[input.Length];
        output[0] = input[0];
        
        for (int i = 1; i < input.Length; i++)
        {
            float val = input[i] + DE_EMPHASIS * output[i - 1];
            output[i] = SoftClip(val);
        }
        
        return output;
    }

    /// <summary>
    /// Soft clipping to prevent harsh distortion.
    /// Uses tanh-like curve for natural limiting.
    /// </summary>
    private static short SoftClip(float sample)
    {
        const float threshold = 28000f;
        const float max = 32767f;
        
        if (sample > threshold)
        {
            float excess = (sample - threshold) / (max - threshold);
            sample = threshold + (max - threshold) * (float)Math.Tanh(excess);
        }
        else if (sample < -threshold)
        {
            float excess = (-sample - threshold) / (max - threshold);
            sample = -threshold - (max - threshold) * (float)Math.Tanh(excess);
        }
        
        return (short)Math.Clamp(sample, -32768f, 32767f);
    }
    
    #endregion

    #region Resampling
    
    /// <summary>
    /// High-quality resampling - uses CurrentResamplerMode to select algorithm.
    /// </summary>
    public static short[] Resample(short[] input, int fromRate, int toRate)
    {
        return CurrentResamplerMode switch
        {
            ResamplerMode.NAudio => ResampleNAudio(input, fromRate, toRate),
            ResamplerMode.Custom => ResampleCustom(input, fromRate, toRate),
            _ => ResampleNAudio(input, fromRate, toRate)
        };
    }
    
    /// <summary>
    /// High-quality resampling using NAudio's WDL resampler.
    /// This is a professional-grade resampler used in audio production.
    /// </summary>
    public static short[] ResampleNAudio(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;
        if (input.Length == 0) return input;

        // Convert shorts to floats for NAudio
        var floats = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
            floats[i] = input[i] / 32768f;

        // Create a raw sample provider from our float data
        var sourceProvider = new RawSourceWaveStream(
            new MemoryStream(FloatsToBytes(floats)),
            WaveFormat.CreateIeeeFloatWaveFormat(fromRate, 1));

        // Use WDL resampler (high quality)
        var resampler = new WdlResamplingSampleProvider(
            sourceProvider.ToSampleProvider(),
            toRate);

        // Read resampled data
        int expectedSamples = (int)((long)input.Length * toRate / fromRate);
        var outputFloats = new float[expectedSamples + 100]; // small buffer for rounding
        int samplesRead = resampler.Read(outputFloats, 0, outputFloats.Length);

        // Convert back to shorts
        var output = new short[samplesRead];
        for (int i = 0; i < samplesRead; i++)
            output[i] = (short)Math.Clamp(outputFloats[i] * 32767f, -32768, 32767);

        return output;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Custom resampling with Catmull-Rom interpolation (fallback if NAudio has issues).
    /// </summary>
    public static short[] ResampleCustom(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;
        if (input.Length == 0) return input;
        
        // For downsampling, apply low-pass filter first to prevent aliasing
        short[] filtered = input;
        if (fromRate > toRate)
        {
            int filterSize = (int)Math.Ceiling((double)fromRate / toRate) * 2 + 1;
            filtered = ApplyLowPassFilter(input, filterSize, (double)toRate / fromRate * 0.9);
        }
        
        double ratio = (double)fromRate / toRate;
        int outputLength = (int)(filtered.Length / ratio);
        var output = new short[outputLength];
        
        // Use cubic interpolation for better quality
        for (int i = 0; i < output.Length; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double t = srcPos - srcIndex;
            
            // Cubic Hermite interpolation (4-point)
            int i0 = Math.Max(0, srcIndex - 1);
            int i1 = Math.Min(filtered.Length - 1, srcIndex);
            int i2 = Math.Min(filtered.Length - 1, srcIndex + 1);
            int i3 = Math.Min(filtered.Length - 1, srcIndex + 2);
            
            double p0 = filtered[i0];
            double p1 = filtered[i1];
            double p2 = filtered[i2];
            double p3 = filtered[i3];
            
            // Catmull-Rom spline
            double a = -0.5 * p0 + 1.5 * p1 - 1.5 * p2 + 0.5 * p3;
            double b = p0 - 2.5 * p1 + 2.0 * p2 - 0.5 * p3;
            double c = -0.5 * p0 + 0.5 * p2;
            double d = p1;
            
            double result = a * t * t * t + b * t * t + c * t + d;
            output[i] = (short)Math.Clamp(result, -32768, 32767);
        }
        
        return output;
    }

    /// <summary>
    /// Apply windowed low-pass filter (moving average with Hann window).
    /// </summary>
    private static short[] ApplyLowPassFilter(short[] input, int windowSize, double cutoffRatio)
    {
        if (windowSize <= 1) return input;
        
        // Create windowed sinc kernel
        var kernel = new double[windowSize];
        int halfWindow = windowSize / 2;
        double sum = 0;
        
        for (int i = 0; i < windowSize; i++)
        {
            int n = i - halfWindow;
            
            // Sinc function
            double sinc = n == 0 ? 1.0 : Math.Sin(Math.PI * cutoffRatio * n) / (Math.PI * n);
            
            // Hann window
            double hann = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (windowSize - 1)));
            
            kernel[i] = sinc * hann;
            sum += kernel[i];
        }
        
        // Normalize kernel
        for (int i = 0; i < windowSize; i++)
            kernel[i] /= sum;
        
        // Apply convolution
        var output = new short[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            double acc = 0;
            for (int j = 0; j < windowSize; j++)
            {
                int idx = i + j - halfWindow;
                if (idx >= 0 && idx < input.Length)
                    acc += input[idx] * kernel[j];
            }
            output[i] = (short)Math.Clamp(acc, -32768, 32767);
        }
        
        return output;
    }

    /// <summary>
    /// High-quality resample with pre/de-emphasis for telephony.
    /// Call this for SIP → AI (inbound) path.
    /// </summary>
    public static short[] ResampleWithPreEmphasis(short[] input, int fromRate, int toRate)
    {
        var preEmphasized = ApplyPreEmphasis(input);
        return Resample(preEmphasized, fromRate, toRate);
    }

    /// <summary>
    /// High-quality resample with de-emphasis for telephony.
    /// Call this for AI → SIP (outbound) path.
    /// </summary>
    public static short[] ResampleWithDeEmphasis(short[] input, int fromRate, int toRate)
    {
        var resampled = Resample(input, fromRate, toRate);
        return ApplyDeEmphasis(resampled);
    }
    
    /// <summary>
    /// Simple 2x upsampling using linear interpolation (24kHz → 48kHz).
    /// </summary>
    public static short[] Upsample2x(short[] input)
    {
        var output = new short[input.Length * 2];
        for (int i = 0; i < input.Length - 1; i++)
        {
            output[i * 2] = input[i];
            output[i * 2 + 1] = (short)((input[i] + input[i + 1]) / 2);
        }
        if (input.Length > 0)
        {
            output[(input.Length - 1) * 2] = input[input.Length - 1];
            output[(input.Length - 1) * 2 + 1] = input[input.Length - 1];
        }
        return output;
    }

    /// <summary>
    /// 3:2 downsampling for 24kHz → 16kHz conversion (for G.722).
    /// Uses weighted linear interpolation for quality.
    /// </summary>
    public static short[] Downsample24kTo16k(short[] input)
    {
        // 24kHz → 16kHz = 3:2 ratio
        int outputLen = (input.Length * 2) / 3;
        var output = new short[outputLen];

        for (int i = 0; i < outputLen; i++)
        {
            // Source position in 24kHz stream
            double srcPos = i * 1.5;
            int srcIdx = (int)srcPos;
            double frac = srcPos - srcIdx;

            if (srcIdx + 1 < input.Length)
            {
                // Linear interpolation
                double val = input[srcIdx] * (1 - frac) + input[srcIdx + 1] * frac;
                output[i] = (short)Math.Clamp(val, -32768, 32767);
            }
            else if (srcIdx < input.Length)
            {
                output[i] = input[srcIdx];
            }
        }

        return output;
    }

    /// <summary>
    /// 2:3 upsampling for 16kHz → 24kHz conversion (from G.722).
    /// Uses linear interpolation.
    /// </summary>
    public static short[] Upsample16kTo24k(short[] input)
    {
        // 16kHz → 24kHz = 2:3 ratio
        int outputLen = (input.Length * 3) / 2;
        var output = new short[outputLen];

        for (int i = 0; i < outputLen; i++)
        {
            // Source position in 16kHz stream
            double srcPos = i * (2.0 / 3.0);
            int srcIdx = (int)srcPos;
            double frac = srcPos - srcIdx;

            if (srcIdx + 1 < input.Length)
            {
                double val = input[srcIdx] * (1 - frac) + input[srcIdx + 1] * frac;
                output[i] = (short)Math.Clamp(val, -32768, 32767);
            }
            else if (srcIdx < input.Length)
            {
                output[i] = input[srcIdx];
            }
        }

        return output;
    }
    
    #endregion

    #region Byte/Short Conversion
    
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
    
    #endregion
}

/// <summary>
/// SIPSorcery-compatible audio encoder with Opus and G.722 support.
/// Wraps AudioCodecs static methods for IAudioEncoder interface.
/// Codec priority: Opus (48kHz) > G.722 (16kHz) > G.711 (8kHz)
/// </summary>
public class UnifiedAudioEncoder : IAudioEncoder
{
    private readonly AudioEncoder _baseEncoder;

    // NOTE: Opus is a dynamic RTP payload. SIP endpoints commonly advertise different
    // payload types (e.g. 106, 111). We include both to maximize interoperability.
    private static readonly AudioFormat OpusFormat106 = new AudioFormat(
        AudioCodecsEnum.OPUS, 106, AudioCodecs.OPUS_SAMPLE_RATE, AudioCodecs.OPUS_CHANNELS, "opus");

    private static readonly AudioFormat OpusFormat111 = new AudioFormat(
        AudioCodecsEnum.OPUS, 111, AudioCodecs.OPUS_SAMPLE_RATE, AudioCodecs.OPUS_CHANNELS, "opus");

    // G.722 operates at 16kHz with 64kbps
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
            var formats = new List<AudioFormat>();
            // Priority order: Opus > G.722 > G.711
            // Opus 48kHz (two common dynamic payload types)
            formats.Add(OpusFormat106);
            formats.Add(OpusFormat111);
            formats.Add(G722Format);
            formats.AddRange(_baseEncoder.SupportedFormats);
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

            default:
                return _baseEncoder.DecodeAudio(encodedSample, format);
        }
    }

    /// <summary>
    /// Reset stateful codecs (call at start of new call).
    /// </summary>
    public void ResetCodecState()
    {
        AudioCodecs.ResetAllCodecs();
    }
}
