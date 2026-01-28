using System;

namespace TaxiSipBridge;

/// <summary>
/// Gentle low-pass filter for OpenAI TTS output.
/// Smooths harsh high frequencies before telephony encoding.
/// Single-pole IIR for minimal phase distortion.
/// </summary>
public class TtsLowPassFilter
{
    // Cutoff frequency for 24kHz input - smooth out frequencies above 6kHz
    // (telephony only uses up to 4kHz anyway)
    private const float CUTOFF_HZ = 6000f;
    private const float SAMPLE_RATE = 24000f;
    
    // Filter coefficient (computed from cutoff)
    private readonly float _alpha;
    
    // Filter state
    private float _lastOutput;
    
    public TtsLowPassFilter()
    {
        // Single-pole IIR coefficient: alpha = 1 - e^(-2π * fc / fs)
        float omega = 2f * (float)Math.PI * CUTOFF_HZ / SAMPLE_RATE;
        _alpha = 1f - (float)Math.Exp(-omega);
    }
    
    /// <summary>
    /// Apply gentle low-pass filter to 24kHz PCM.
    /// Smooths harshness without removing clarity.
    /// </summary>
    public short[] Process(short[] input)
    {
        if (input == null || input.Length == 0)
            return input ?? Array.Empty<short>();
        
        var output = new short[input.Length];
        
        for (int i = 0; i < input.Length; i++)
        {
            float sample = input[i] / 32768f;
            
            // Single-pole low-pass: y[n] = y[n-1] + α * (x[n] - y[n-1])
            _lastOutput += _alpha * (sample - _lastOutput);
            
            output[i] = (short)Math.Clamp(_lastOutput * 32767f, -32768, 32767);
        }
        
        return output;
    }
    
    /// <summary>
    /// Reset filter state between calls.
    /// </summary>
    public void Reset()
    {
        _lastOutput = 0;
    }
}
