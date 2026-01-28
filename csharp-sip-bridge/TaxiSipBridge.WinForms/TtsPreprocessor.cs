using System;

namespace TaxiSipBridge;

/// <summary>
/// Preprocessing stage for OpenAI Realtime audio output.
/// Cleans PCM16 frames before PCMA encoding to prevent distortion.
/// </summary>
public static class TtsPreprocessor
{
    private const double NORMALIZE_TARGET = 0.90;  // 90% of max range
    private const double SOFT_CLIP_THRESHOLD = 0.90;
    
    /// <summary>
    /// Full preprocessing pipeline: normalize → soft-clip → ready for PCMA.
    /// </summary>
    public static short[] PreprocessPcm16(short[] samples)
    {
        if (samples.Length == 0) return samples;
        
        // 1. Normalize to ~90% to ensure consistent levels
        Normalize(samples);
        
        // 2. Soft-clip to prevent hard clipping artifacts
        SoftClipArray(samples);
        
        return samples;
    }
    
    /// <summary>
    /// Normalize samples to target level (90% of max range).
    /// </summary>
    public static void Normalize(short[] samples)
    {
        // Find peak
        int peak = 0;
        foreach (short s in samples)
        {
            int abs = s == short.MinValue ? 32768 : Math.Abs(s);
            if (abs > peak) peak = abs;
        }
        
        if (peak == 0) return;
        
        // Only normalize if needed (peak > target or significantly below)
        double currentLevel = peak / 32768.0;
        if (currentLevel >= NORMALIZE_TARGET * 0.95 && currentLevel <= 1.0)
            return; // Already in good range
        
        double gain = NORMALIZE_TARGET * 32767.0 / peak;
        
        // Apply gain with clamping
        for (int i = 0; i < samples.Length; i++)
        {
            double scaled = samples[i] * gain;
            samples[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
        }
    }
    
    /// <summary>
    /// Soft-clip a single sample using exponential knee.
    /// Prevents hard clipping while preserving dynamics.
    /// </summary>
    public static short SoftClip(short x)
    {
        double v = x / 32768.0;
        double absV = Math.Abs(v);
        
        if (absV <= SOFT_CLIP_THRESHOLD)
            return x;
        
        // Exponential soft-knee compression above threshold
        double sign = v >= 0 ? 1.0 : -1.0;
        double excess = absV - SOFT_CLIP_THRESHOLD;
        double range = 1.0 - SOFT_CLIP_THRESHOLD;
        double compressed = SOFT_CLIP_THRESHOLD + range * (1.0 - Math.Exp(-excess / range));
        
        return (short)(sign * compressed * 32767.0);
    }
    
    /// <summary>
    /// Apply soft-clip to entire array.
    /// </summary>
    public static void SoftClipArray(short[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
            samples[i] = SoftClip(samples[i]);
    }
    
    /// <summary>
    /// Convert raw bytes to PCM16 samples (little-endian).
    /// </summary>
    public static short[] BytesToPcm(byte[] buf)
    {
        if (buf.Length == 0) return Array.Empty<short>();
        
        short[] pcm = new short[buf.Length / 2];
        Buffer.BlockCopy(buf, 0, pcm, 0, buf.Length);
        return pcm;
    }
    
    /// <summary>
    /// Convert PCM16 samples back to raw bytes.
    /// </summary>
    public static byte[] PcmToBytes(short[] samples)
    {
        byte[] buf = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, buf, 0, buf.Length);
        return buf;
    }
}
