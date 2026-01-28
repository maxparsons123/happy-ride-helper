using System;

namespace TaxiSipBridge;

/// <summary>
/// 2-pole Butterworth low-pass filter for OpenAI TTS output.
/// Removes harsh high frequencies before telephony encoding.
/// 4kHz cutoff (telephony bandwidth is 300Hz-3.4kHz).
/// Includes soft limiter to prevent clipping.
/// </summary>
public class TtsLowPassFilter
{
    // Cutoff frequency - telephony only uses up to 3.4kHz
    // Using 4kHz to preserve clarity while removing harshness
    private const float CUTOFF_HZ = 4000f;
    private const float SAMPLE_RATE = 24000f;
    
    // Soft limiter threshold (leaves headroom for resampling)
    private const float LIMITER_THRESHOLD = 0.85f;  // ~27850 in short terms
    private const float LIMITER_KNEE = 0.1f;        // Soft knee width
    
    // Butterworth 2-pole coefficients
    private readonly float _a0, _a1, _a2;
    private readonly float _b1, _b2;
    
    // Filter state (2 samples of history)
    private float _x1, _x2;  // input history
    private float _y1, _y2;  // output history
    
    public TtsLowPassFilter()
    {
        // Pre-warp the cutoff frequency for bilinear transform
        float omega = (float)Math.Tan(Math.PI * CUTOFF_HZ / SAMPLE_RATE);
        float omega2 = omega * omega;
        
        // Butterworth Q = 1/sqrt(2) for maximally flat response
        float q = (float)Math.Sqrt(2);
        
        // Bilinear transform coefficients for 2-pole Butterworth
        float n = 1f / (1f + omega / q + omega2);
        
        _a0 = omega2 * n;
        _a1 = 2f * _a0;
        _a2 = _a0;
        _b1 = 2f * (omega2 - 1f) * n;
        _b2 = (1f - omega / q + omega2) * n;
    }
    
    /// <summary>
    /// Apply Butterworth low-pass filter with soft limiting.
    /// Removes harshness and prevents clipping before resampling.
    /// </summary>
    public short[] Process(short[] input)
    {
        if (input == null || input.Length == 0)
            return input ?? Array.Empty<short>();
        
        var output = new short[input.Length];
        
        for (int i = 0; i < input.Length; i++)
        {
            float x0 = input[i] / 32768f;
            
            // 2-pole IIR: y[n] = a0*x[n] + a1*x[n-1] + a2*x[n-2] - b1*y[n-1] - b2*y[n-2]
            float y0 = _a0 * x0 + _a1 * _x1 + _a2 * _x2 - _b1 * _y1 - _b2 * _y2;
            
            // Update filter history
            _x2 = _x1;
            _x1 = x0;
            _y2 = _y1;
            _y1 = y0;
            
            // Apply soft limiter to prevent clipping
            y0 = SoftLimit(y0);
            
            output[i] = (short)Math.Clamp(y0 * 32767f, -32768, 32767);
        }
        
        return output;
    }
    
    /// <summary>
    /// Soft knee limiter using tanh-style compression.
    /// Prevents hard clipping while preserving dynamics.
    /// </summary>
    private float SoftLimit(float sample)
    {
        float absVal = Math.Abs(sample);
        
        if (absVal <= LIMITER_THRESHOLD - LIMITER_KNEE)
        {
            // Below knee - pass through
            return sample;
        }
        else if (absVal >= LIMITER_THRESHOLD + LIMITER_KNEE)
        {
            // Above knee - full compression (tanh)
            float sign = sample >= 0 ? 1f : -1f;
            return sign * (LIMITER_THRESHOLD + (1f - LIMITER_THRESHOLD) * (float)Math.Tanh((absVal - LIMITER_THRESHOLD) * 3f));
        }
        else
        {
            // In the knee - smooth transition
            float t = (absVal - (LIMITER_THRESHOLD - LIMITER_KNEE)) / (2f * LIMITER_KNEE);
            float gain = 1f - t * 0.15f;  // Gentle gain reduction in knee
            return sample * gain;
        }
    }
    
    /// <summary>
    /// Reset filter state between calls.
    /// </summary>
    public void Reset()
    {
        _x1 = _x2 = 0;
        _y1 = _y2 = 0;
    }
}
