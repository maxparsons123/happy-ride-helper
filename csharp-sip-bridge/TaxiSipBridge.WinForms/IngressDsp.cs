namespace TaxiSipBridge;

/// <summary>
/// Simple ingress DSP for improving STT quality.
/// Applies minimal processing: DC removal, pre-emphasis, and normalization.
/// NO noise gate - let OpenAI's VAD handle speech detection.
/// </summary>
public static class IngressDsp
{
    // ===========================================
    // DSP STATE (per-call, reset between calls)
    // ===========================================
    private static float _dcState;
    private static float _preEmphPrev;
    private static readonly object _lock = new();
    
    // ===========================================
    // CONSTANTS
    // ===========================================
    private const float DC_ALPHA = 0.995f;      // DC blocker time constant
    private const float PRE_EMPH = 0.97f;       // Standard pre-emphasis for speech
    private const float TARGET_RMS = 6000f;     // Target RMS for normalization
    private const float MIN_GAIN = 0.5f;        // Minimum AGC gain
    private const float MAX_GAIN = 8.0f;        // Maximum AGC gain (boost quiet callers)
    
    /// <summary>
    /// Reset DSP state between calls.
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _dcState = 0;
            _preEmphPrev = 0;
        }
    }
    
    /// <summary>
    /// Apply STT-optimized DSP to PCM16 audio.
    /// Modifies the input array in-place.
    /// </summary>
    public static void ApplyForStt(short[] pcm)
    {
        if (pcm == null || pcm.Length == 0) return;
        
        lock (_lock)
        {
            // Pass 1: DC removal + pre-emphasis
            for (int i = 0; i < pcm.Length; i++)
            {
                float x = pcm[i];
                
                // DC blocker (high-pass)
                _dcState = (DC_ALPHA * _dcState) + ((1f - DC_ALPHA) * x);
                x -= _dcState;
                
                // Pre-emphasis (boost high frequencies)
                float y = x - (PRE_EMPH * _preEmphPrev);
                _preEmphPrev = x;
                x = y;
                
                pcm[i] = (short)Math.Clamp(x, short.MinValue, short.MaxValue);
            }
            
            // Pass 2: Calculate RMS and normalize
            double sumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                sumSq += (double)pcm[i] * pcm[i];
            }
            float rms = (float)Math.Sqrt(sumSq / pcm.Length);
            
            if (rms > 10) // Only normalize if there's meaningful audio
            {
                float gain = Math.Clamp(TARGET_RMS / rms, MIN_GAIN, MAX_GAIN);
                
                for (int i = 0; i < pcm.Length; i++)
                {
                    float val = pcm[i] * gain;
                    // Soft limit to prevent clipping
                    if (val > 28000) val = 28000 + (val - 28000) * 0.1f;
                    else if (val < -28000) val = -28000 + (val + 28000) * 0.1f;
                    pcm[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
                }
            }
        }
    }
    
    /// <summary>
    /// Apply STT-optimized DSP and return the processed audio.
    /// Does not modify the input array.
    /// </summary>
    public static short[] ProcessForStt(short[] pcm)
    {
        if (pcm == null || pcm.Length == 0) return pcm;
        
        var output = new short[pcm.Length];
        Array.Copy(pcm, output, pcm.Length);
        ApplyForStt(output);
        return output;
    }
}
