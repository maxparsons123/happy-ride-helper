using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TaxiSipBridge.Audio;

/// <summary>
/// High-quality audio resampler using NAudio's WDL resampler.
/// </summary>
public static class NAudioResampler
{
    /// <summary>
    /// Resamples raw PCM bytes using NAudio's WDL resampler (gold standard anti-aliasing).
    /// </summary>
    public static byte[] ResampleBytes(byte[] inputPcm, int inputRate, int outputRate)
    {
        if (inputPcm.Length == 0 || inputRate == outputRate)
            return inputPcm;

        // 1. Setup the input format (16-bit Mono PCM from OpenAI)
        var inputFormat = new WaveFormat(inputRate, 16, 1);
        
        using var msInput = new MemoryStream(inputPcm);
        using var rawProvider = new RawSourceWaveStream(msInput, inputFormat);
        
        // 2. Convert to Float32 for high-quality resampling
        var sampleProvider = rawProvider.ToSampleProvider();
        
        // 3. WDL resampler - gold standard for anti-aliasing
        var resampler = new WdlResamplingSampleProvider(sampleProvider, outputRate);
        
        using var msOutput = new MemoryStream();
        
        // Drain the resampler
        float[] buffer = new float[outputRate];
        int samplesRead;
        
        while ((samplesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < samplesRead; i++)
            {
                short sample = (short)Math.Clamp(buffer[i] * short.MaxValue, short.MinValue, short.MaxValue);
                byte[] bytes = BitConverter.GetBytes(sample);
                msOutput.Write(bytes, 0, 2);
            }
        }

        return msOutput.ToArray();
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
