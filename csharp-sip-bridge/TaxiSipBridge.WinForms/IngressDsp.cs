namespace TaxiSipBridge;

/// <summary>
/// Ingress DSP optimized for OpenAI Whisper STT quality.
/// Minimal processing to preserve speech clarity.
/// NO pre-emphasis (OpenAI handles this internally).
/// </summary>
public static class IngressDsp
{
    // ===========================================
    // DSP STATE (per-call, reset between calls)
    // ===========================================
    private static float _dcState;
    private static readonly object _lock = new();
    
    // ===========================================
    // CONSTANTS (tuned for OpenAI Whisper STT)
    // ===========================================
    private const float DC_ALPHA = 0.995f;          // DC blocker time constant
    private const float TARGET_RMS = 4000f;         // Reduced from 6000 - less aggressive normalization
    private const float MIN_GAIN = 0.8f;            // Less compression (was 0.5)
    private const float MAX_GAIN = 4.0f;            // Reduced from 8.0 - less noise amplification
    private const float NOISE_FLOOR_RMS = 100f;     // Below this RMS, audio is likely noise
    
    // Soft gate settings - less aggressive to preserve speech quality
    private const float SOFT_GATE_ATTENUATION = 0.15f;  // 85% reduction (was 90%)
    private const float BARGE_IN_RMS_THRESHOLD = 1500f; // Lowered for easier barge-in detection
    
    // Stats for diagnostics
    private static int _framesProcessed;
    private static float _avgRms;
    
    /// <summary>
    /// Reset DSP state between calls.
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _dcState = 0;
            _framesProcessed = 0;
            _avgRms = 0;
        }
    }
    
    /// <summary>
    /// Apply STT-optimized DSP to PCM16 audio.
    /// Modifies the input array in-place.
    /// SIMPLIFIED: DC removal + gentle normalization only (no pre-emphasis).
    /// </summary>
    /// <param name="pcm">Audio samples to process</param>
    /// <param name="isBotSpeaking">If true, applies soft gate attenuation</param>
    /// <returns>True if audio is loud enough to be a barge-in, false otherwise</returns>
    public static bool ApplyForStt(short[] pcm, bool isBotSpeaking = false)
    {
        if (pcm == null || pcm.Length == 0) return false;
        
        lock (_lock)
        {
            // Calculate raw RMS before any processing (for barge-in detection)
            double rawSumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                rawSumSq += (double)pcm[i] * pcm[i];
            }
            float rawRms = (float)Math.Sqrt(rawSumSq / pcm.Length);
            
            // Check if this is a potential barge-in (loud enough to break through)
            bool isBargeIn = rawRms >= BARGE_IN_RMS_THRESHOLD;
            
            // If audio is below noise floor and bot is speaking, zero it out
            if (rawRms < NOISE_FLOOR_RMS && isBotSpeaking)
            {
                Array.Clear(pcm, 0, pcm.Length);
                return false;
            }
            
            // Pass 1: DC removal only (NO pre-emphasis - OpenAI handles this)
            for (int i = 0; i < pcm.Length; i++)
            {
                float x = pcm[i];
                
                // DC blocker (high-pass filter to remove DC offset)
                _dcState = (DC_ALPHA * _dcState) + ((1f - DC_ALPHA) * x);
                x -= _dcState;
                
                pcm[i] = (short)Math.Clamp(x, short.MinValue, short.MaxValue);
            }
            
            // Pass 2: Calculate processed RMS and apply gentle normalization
            double sumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                sumSq += (double)pcm[i] * pcm[i];
            }
            float rms = (float)Math.Sqrt(sumSq / pcm.Length);
            
            // Update running average for diagnostics
            _framesProcessed++;
            _avgRms = _avgRms + (rms - _avgRms) / Math.Min(_framesProcessed, 100);
            
            // Only normalize if there's meaningful audio above noise floor
            if (rms > NOISE_FLOOR_RMS)
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
                    // Gentle soft limit to prevent clipping (smoother curve)
                    if (val > 24000) val = 24000 + (val - 24000) * 0.05f;
                    else if (val < -24000) val = -24000 + (val + 24000) * 0.05f;
                    pcm[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
                }
            }
            
            return isBargeIn;
        }
    }
    
    /// <summary>
    /// Get current average RMS for diagnostics.
    /// </summary>
    public static float GetAverageRms()
    {
        lock (_lock) { return _avgRms; }
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
