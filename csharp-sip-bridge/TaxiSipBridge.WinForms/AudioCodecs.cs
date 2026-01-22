using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TaxiSipBridge;

/// <summary>
/// Resampler mode for A/B testing audio quality.
/// </summary>
public enum ResamplerMode
{
    NAudio,      // WDL resampler - professional grade
    Custom       // Catmull-Rom interpolation with sinc filter
}

/// <summary>
/// Audio codec utilities for µ-law encoding/decoding and high-quality resampling.
/// Used for converting between OpenAI Realtime API format (24kHz PCM16) and SIP telephony (8kHz µ-law).
/// </summary>
public static class AudioCodecs
{
    // Pre-emphasis coefficient - boosts high frequencies for better consonant clarity
    private const float PRE_EMPHASIS = 0.97f;
    
    // De-emphasis coefficient - restores natural frequency balance after processing
    private const float DE_EMPHASIS = 0.97f;

    /// <summary>
    /// Current resampler mode. Change at runtime for A/B testing.
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
            byte alaw = (byte)(data[i] ^ 0x55);
            int sign = (alaw & 0x80) != 0 ? -1 : 1;
            int exponent = (alaw >> 4) & 0x07;
            int mantissa = alaw & 0x0F;
            
            int sample;
            if (exponent == 0)
            {
                sample = (mantissa << 4) + 8;
            }
            else
            {
                sample = ((mantissa << 4) + 0x108) << (exponent - 1);
            }
            
            pcm[i] = (short)(sign * sample);
        }
        return pcm;

    /// <summary>
    /// Encode PCM16 samples to µ-law (G.711) using lookup table for consistent encoding.
    /// </summary>
    public static byte[] MuLawEncode(short[] pcm)
    {
        var ulaw = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            ulaw[i] = MuLawTable[pcm[i] + 32768];
        }
        return ulaw;
    }

    // Pre-computed µ-law lookup table for fast encoding
    private static readonly byte[] MuLawTable = BuildMuLawTable();
    
    private const int MULAW_BIAS = 0x84;
    private const int MULAW_CLIP = 32635;
    
    private static byte[] BuildMuLawTable()
    {
        var table = new byte[65536];
        for (int i = 0; i < 65536; i++)
        {
            short sample = (short)(i - 32768);
            table[i] = EncodeToMuLaw(sample);
        }
        return table;
    }
    
    private static byte EncodeToMuLaw(short sample)
    {
        int sign = (sample >> 8) & 0x80;
        if (sign != 0)
            sample = (short)-sample;
        
        if (sample > MULAW_CLIP)
            sample = MULAW_CLIP;
        
        sample = (short)(sample + MULAW_BIAS);
        
        int exponent = 7;
        int mask = 0x4000;
        
        while ((sample & mask) == 0 && exponent > 0)
        {
            exponent--;
            mask >>= 1;
        }
        
        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        byte ulawByte = (byte)(sign | (exponent << 4) | mantissa);
        
        return (byte)~ulawByte;
    }

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
    /// Call this for SIP → Ada (inbound) path.
    /// </summary>
    public static short[] ResampleWithPreEmphasis(short[] input, int fromRate, int toRate)
    {
        var preEmphasized = ApplyPreEmphasis(input);
        return Resample(preEmphasized, fromRate, toRate);
    }

    /// <summary>
    /// High-quality resample with de-emphasis for telephony.
    /// Call this for Ada → SIP (outbound) path.
    /// </summary>
    public static short[] ResampleWithDeEmphasis(short[] input, int fromRate, int toRate)
    {
        var resampled = Resample(input, fromRate, toRate);
        return ApplyDeEmphasis(resampled);
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
}
