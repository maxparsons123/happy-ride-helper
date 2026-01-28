using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TaxiSipBridge.Audio;

/// <summary>
/// High-quality audio resampler using NAudio's WDL resampler.
/// Includes proper anti-aliasing for downsampling (24kHz → 8kHz).
/// Also provides G.711 A-law encoding utilities.
/// </summary>
public static class NAudioResampler
{
    /// <summary>
    /// Resample PCM16 audio using NAudio's WDL resampler (high quality with anti-aliasing).
    /// </summary>
    public static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (input.Length == 0 || fromRate == toRate)
            return input;

        // Convert shorts to bytes for NAudio
        var inputBytes = new byte[input.Length * 2];
        Buffer.BlockCopy(input, 0, inputBytes, 0, inputBytes.Length);

        // Create wave format and memory stream
        var inputFormat = new WaveFormat(fromRate, 16, 1);
        using var inputStream = new RawSourceWaveStream(new MemoryStream(inputBytes), inputFormat);
        
        // Use WDL resampler (high quality with proper anti-aliasing)
        var resampler = new WdlResamplingSampleProvider(inputStream.ToSampleProvider(), toRate);
        
        // Calculate expected output length
        double ratio = (double)toRate / fromRate;
        int expectedOutputSamples = (int)(input.Length * ratio);
        
        // Read resampled audio
        var outputBuffer = new float[expectedOutputSamples + 100]; // Small buffer for any rounding
        int samplesRead = resampler.Read(outputBuffer, 0, outputBuffer.Length);
        
        // Convert float samples back to short
        var output = new short[samplesRead];
        for (int i = 0; i < samplesRead; i++)
        {
            output[i] = (short)Math.Clamp(outputBuffer[i] * 32767f, short.MinValue, short.MaxValue);
        }
        
        return output;
    }

    /// <summary>
    /// Resample PCM16 bytes using NAudio's WDL resampler.
    /// </summary>
    public static byte[] ResampleBytes(byte[] inputBytes, int fromRate, int toRate)
    {
        if (inputBytes.Length == 0 || fromRate == toRate)
            return inputBytes;

        var inputFormat = new WaveFormat(fromRate, 16, 1);
        using var inputStream = new RawSourceWaveStream(new MemoryStream(inputBytes), inputFormat);
        
        var resampler = new WdlResamplingSampleProvider(inputStream.ToSampleProvider(), toRate);
        
        double ratio = (double)toRate / fromRate;
        int expectedOutputSamples = (int)((inputBytes.Length / 2) * ratio);
        
        var outputBuffer = new float[expectedOutputSamples + 100];
        int samplesRead = resampler.Read(outputBuffer, 0, outputBuffer.Length);
        
        var outputBytes = new byte[samplesRead * 2];
        for (int i = 0; i < samplesRead; i++)
        {
            short sample = (short)Math.Clamp(outputBuffer[i] * 32767f, short.MinValue, short.MaxValue);
            outputBytes[i * 2] = (byte)(sample & 0xFF);
            outputBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        
        return outputBytes;
    }

    /// <summary>
    /// Convert OpenAI TTS PCM (24kHz) directly to G.711 A-law bytes (8kHz).
    /// Complete pipeline: resample + encode in one call.
    /// </summary>
    public static byte[] ConvertToALaw(byte[] pcm24kBytes)
    {
        if (pcm24kBytes.Length == 0)
            return Array.Empty<byte>();

        // 1. Resample 24kHz → 8kHz with high-quality anti-aliasing
        var pcm8kBytes = ResampleBytes(pcm24kBytes, 24000, 8000);
        
        // 2. Encode to A-law
        var alawBytes = new byte[pcm8kBytes.Length / 2];
        for (int i = 0; i < alawBytes.Length; i++)
        {
            short sample = (short)(pcm8kBytes[i * 2] | (pcm8kBytes[i * 2 + 1] << 8));
            alawBytes[i] = LinearToALaw(sample);
        }
        
        return alawBytes;
    }

    /// <summary>
    /// ITU-T G.711 A-law encoder (RFC 3551 compliant).
    /// </summary>
    public static byte LinearToALaw(short sample)
    {
        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        if (sample > 32635) sample = 32635;

        int exponent, mantissa;
        if (sample >= 256)
        {
            exponent = (int)Math.Floor(Math.Log(sample, 2)) - 7;
            if (exponent > 7) exponent = 7;
            mantissa = (sample >> (exponent + 3)) & 0x0F;
        }
        else
        {
            exponent = 0;
            mantissa = sample >> 4;
        }

        byte alaw = (byte)((exponent << 4) | mantissa);
        alaw ^= 0xD5; // Invert odd bits + sign (G.711 requirement)
        if (sign == 0) alaw |= 0x80;
        return alaw;
    }

    /// <summary>
    /// ITU-T G.711 mu-law encoder (RFC 3551 compliant).
    /// </summary>
    public static byte LinearToMuLaw(short sample)
    {
        const int MULAW_MAX = 0x1FFF;
        const int MULAW_BIAS = 33;

        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        
        sample = (short)Math.Min(sample, MULAW_MAX);
        sample = (short)(sample + MULAW_BIAS);

        int exponent = 7;
        for (int expMask = 0x4000; (sample & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }

        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        byte ulaw = (byte)(~(sign | (exponent << 4) | mantissa));
        return ulaw;
    }
}
