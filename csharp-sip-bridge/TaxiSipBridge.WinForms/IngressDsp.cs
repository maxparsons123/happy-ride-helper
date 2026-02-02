namespace TaxiSipBridge;

/// <summary>
/// Simple ingress DSP for improving STT quality.
/// Applies minimal processing: DC removal, pre-emphasis, normalization, and soft gate.
/// NO hard blocking - uses soft gate to allow loud barge-ins to break through.
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
    
    // Soft gate settings
    private const float SOFT_GATE_ATTENUATION = 0.10f;  // 90% reduction while bot speaking
    private const float BARGE_IN_RMS_THRESHOLD = 2000f; // RMS threshold to bypass soft gate
    
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
    /// <param name="pcm">Audio samples to process</param>
    /// <param name="isBotSpeaking">If true, applies soft gate attenuation</param>
    /// <returns>True if audio is loud enough to be a barge-in, false otherwise</returns>
    public static bool ApplyForStt(short[] pcm, bool isBotSpeaking = false)
    {
        if (pcm == null || pcm.Length == 0) return false;
        
        float rawRms;
        
        lock (_lock)
        {
            // Calculate raw RMS before any processing (for barge-in detection)
            double rawSumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                rawSumSq += (double)pcm[i] * pcm[i];
            }
            rawRms = (float)Math.Sqrt(rawSumSq / pcm.Length);
            
            // Check if this is a potential barge-in (loud enough to break through)
            bool isBargeIn = rawRms >= BARGE_IN_RMS_THRESHOLD;
            
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
            
            // Pass 2: Calculate processed RMS and normalize
            double sumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                sumSq += (double)pcm[i] * pcm[i];
            }
            float rms = (float)Math.Sqrt(sumSq / pcm.Length);
            
            if (rms > 10) // Only normalize if there's meaningful audio
            {
                float gain = Math.Clamp(TARGET_RMS / rms, MIN_GAIN, MAX_GAIN);
                
                // Apply soft gate attenuation if bot is speaking and NOT a barge-in
                if (isBotSpeaking && !isBargeIn)
                {
                    gain *= SOFT_GATE_ATTENUATION;
                }
                
                for (int i = 0; i < pcm.Length; i++)
                {
                    float val = pcm[i] * gain;
                    // Soft limit to prevent clipping
                    if (val > 28000) val = 28000 + (val - 28000) * 0.1f;
                    else if (val < -28000) val = -28000 + (val + 28000) * 0.1f;
                    pcm[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
                }
            }
            
            return isBargeIn;
        }
    }
    
    /// <summary>
    /// Apply STT-optimized DSP to PCM16 audio (legacy overload, no soft gate).
    /// Modifies the input array in-place.
    /// </summary>
    public static void ApplyForStt(short[] pcm)
    {
        ApplyForStt(pcm, isBotSpeaking: false);
    }
    
    /// <summary>
    /// Apply STT-optimized DSP and return the processed audio.
    /// Does not modify the input array.
    /// </summary>
    public static short[] ProcessForStt(short[] pcm, bool isBotSpeaking = false)
    {
        if (pcm == null || pcm.Length == 0) return pcm;
        
        var output = new short[pcm.Length];
        Array.Copy(pcm, output, pcm.Length);
        ApplyForStt(output, isBotSpeaking);
        return output;
    }
}
