namespace AdaMain.Audio;

/// <summary>
/// DSP processing for telephony audio.
/// Improves OpenAI ASR accuracy with volume boost, pre-emphasis, and normalization.
/// </summary>
public static class TelephonyDsp
{
    private const float PreEmphasisCoeff = 0.97f;
    private static float _prevSample;
    private static float _dcOffset;
    
    /// <summary>
    /// Process inbound audio for OpenAI:
    /// - Volume boost
    /// - Pre-emphasis (boost high frequencies for clearer consonants)
    /// - Soft clipping
    /// </summary>
    public static byte[] ProcessInbound(byte[] pcmBytes, float volumeBoost = 2.5f)
    {
        if (pcmBytes.Length < 4) return pcmBytes;
        
        var samples = AudioResampler.BytesToShorts(pcmBytes);
        var output = new float[samples.Length];
        
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];
            
            // Volume boost
            sample *= volumeBoost;
            
            // Pre-emphasis filter
            float emphasized = sample - PreEmphasisCoeff * _prevSample;
            _prevSample = sample;
            sample = emphasized;
            
            // Soft clip using tanh
            sample = MathF.Tanh(sample / 32000f) * 32000f;
            
            output[i] = sample;
        }
        
        // Convert back to shorts
        var result = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            result[i] = (short)Math.Clamp(output[i], -32768, 32767);
        }
        
        return AudioResampler.ShortsToBytes(result);
    }
    
    /// <summary>
    /// Process outbound audio for telephony:
    /// - DC removal
    /// - Low-pass filter (3.4kHz for G.711)
    /// - Normalization
    /// </summary>
    public static short[] ProcessOutbound(short[] samples, float targetLevel = 0.9f)
    {
        if (samples.Length == 0) return samples;
        
        var output = new float[samples.Length];
        
        // DC removal (high-pass filter)
        const float dcAlpha = 0.995f;
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];
            _dcOffset = dcAlpha * _dcOffset + (1 - dcAlpha) * sample;
            output[i] = sample - _dcOffset;
        }
        
        // Simple low-pass filter (moving average, 3-tap)
        for (int i = 1; i < samples.Length - 1; i++)
        {
            output[i] = (output[i - 1] + output[i] * 2 + output[i + 1]) / 4;
        }
        
        // Peak normalization
        float maxAbs = 1f;
        for (int i = 0; i < output.Length; i++)
            maxAbs = Math.Max(maxAbs, Math.Abs(output[i]));
        
        float scale = (32767f * targetLevel) / maxAbs;
        
        var result = new short[samples.Length];
        for (int i = 0; i < output.Length; i++)
        {
            result[i] = (short)Math.Clamp(output[i] * scale, -32768, 32767);
        }
        
        return result;
    }
    
    /// <summary>
    /// Reset filter state between calls.
    /// </summary>
    public static void Reset()
    {
        _prevSample = 0;
        _dcOffset = 0;
    }
}
