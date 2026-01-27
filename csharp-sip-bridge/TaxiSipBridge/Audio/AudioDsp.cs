using System;

namespace TaxiSipBridge.Audio;

/// <summary>
/// High-quality DSP pipeline optimized for OpenAI Realtime API → Opus encoding.
/// Includes high-pass filtering, noise gating, AGC, and pre-emphasis for maximum clarity.
/// </summary>
public class AudioDsp
{
    // Configuration - optimized for OpenAI + Opus
    private const int HighPassCutoff = 80;          // Slightly higher to preserve voice clarity
    private const int SampleRate = 8000;
    private const float NoiseGateThreshold = 20f;   // Lower threshold for better sensitivity
    private const float NoiseGateKneeWidth = 30f;   // Soft knee range (20-50)
    private const float TargetRms = 2800f;          // Higher target for Opus compression
    private const float MaxGain = 2.5f;             // Reduced max gain to prevent clipping
    private const float MinGain = 0.9f;             // Slightly higher min gain
    private const float GainSmoothingFactor = 0.15f; // Slower smoothing to reduce pumping
    private const float PreEmphasisCoeff = 0.95f;   // High-frequency boost for Opus clarity

    // High-pass filter state (2nd order Butterworth)
    private readonly double[] _hpX = new double[3]; // input history [x[n], x[n-1], x[n-2]]
    private readonly double[] _hpY = new double[3]; // output history [y[n], y[n-1], y[n-2]]

    // Butterworth coefficients for 80Hz @ 8kHz (recalculated for better performance)
    private static readonly double[] HpB = { 0.9762421, -1.9524842, 0.9762421 };
    private static readonly double[] HpA = { 1.0, -1.9515384, 0.9534300 };

    // AGC state
    private float _lastGain = 1.0f;
    private float _preEmphasisState = 0f;

    /// <summary>
    /// Applies full DSP pipeline: high-pass → noise gate → AGC → pre-emphasis
    /// </summary>
    /// <param name="pcmData">Input PCM16 data (little-endian)</param>
    /// <returns>Processed audio bytes and applied gain</returns>
    public (byte[] Audio, float Gain) ApplyNoiseReduction(byte[] pcmData)
    {
        if (pcmData == null || pcmData.Length == 0)
            return (pcmData ?? Array.Empty<byte>(), 1.0f);

        // Convert bytes to float samples (-1.0 to 1.0)
        var floatSamples = BytesToFloat(pcmData);
        
        // Apply DSP pipeline in optimal order
        ApplyHighPassFilter(floatSamples);
        ApplySoftKneeNoiseGate(floatSamples);
        float appliedGain = ApplySmoothedAgc(floatSamples);
        ApplyPreEmphasis(floatSamples);
        
        // Convert back to bytes
        var processedBytes = FloatToBytes(floatSamples);
        
        return (processedBytes, appliedGain);
    }

    /// <summary>
    /// High-pass filter to remove DC offset and low-frequency hum
    /// </summary>
    private void ApplyHighPassFilter(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            // Shift history buffers
            _hpX[2] = _hpX[1];
            _hpX[1] = _hpX[0];
            _hpY[2] = _hpY[1];
            _hpY[1] = _hpY[0];

            // Current input
            _hpX[0] = samples[i];

            // Direct Form II IIR filter
            double output = HpB[0] * _hpX[0] + HpB[1] * _hpX[1] + HpB[2] * _hpX[2]
                          - HpA[1] * _hpY[1] - HpA[2] * _hpY[2];

            // Clamp to prevent overflow
            _hpY[0] = Math.Clamp(output, -1.0, 1.0);
            samples[i] = (float)_hpY[0];
        }
    }

    /// <summary>
    /// Soft-knee noise gate with smooth transitions using smoothstep interpolation
    /// </summary>
    private void ApplySoftKneeNoiseGate(float[] samples)
    {
        const float kneeLow = NoiseGateThreshold / 32768f;  // Normalize threshold
        const float kneeHigh = (NoiseGateThreshold + NoiseGateKneeWidth) / 32768f;
        const float minGain = 0.1f; // Minimum gain in noise floor
        const float maxGain = 1.0f; // Full gain above knee

        for (int i = 0; i < samples.Length; i++)
        {
            float absSample = Math.Abs(samples[i]);
            
            if (absSample <= kneeLow)
            {
                // Below knee - apply minimum gain
                samples[i] *= minGain;
            }
            else if (absSample >= kneeHigh)
            {
                // Above knee - full gain (no change needed)
            }
            else
            {
                // In knee region - smooth interpolation using smoothstep
                float normalized = (absSample - kneeLow) / (kneeHigh - kneeLow);
                float smoothNormalized = normalized * normalized * (3.0f - 2.0f * normalized);
                float gain = minGain + (maxGain - minGain) * smoothNormalized;
                samples[i] *= gain;
            }
        }
    }

    /// <summary>
    /// Smoothed Automatic Gain Control with noise floor detection
    /// </summary>
    private float ApplySmoothedAgc(float[] samples)
    {
        // Calculate RMS of current frame (normalized)
        double sumSquared = 0;
        foreach (float sample in samples)
        {
            sumSquared += sample * sample;
        }
        
        float rms = (float)Math.Sqrt(sumSquared / samples.Length);
        float rmsScaled = rms * 32768f; // Scale back for threshold comparison
        
        // Only apply AGC if signal is above noise floor
        const float NoiseFloorRms = 30f;
        if (rmsScaled < NoiseFloorRms)
        {
            // Below noise floor - use minimum gain
            _lastGain = MinGain;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= _lastGain;
            }
            return _lastGain;
        }

        // Calculate target gain
        float targetGain = (TargetRms / 32768f) / rms;
        targetGain = Math.Clamp(targetGain, MinGain, MaxGain);
        
        // Smooth gain transition to prevent pumping artifacts
        _lastGain += GainSmoothingFactor * (targetGain - _lastGain);
        _lastGain = Math.Clamp(_lastGain, MinGain, MaxGain);
        
        // Apply gain
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= _lastGain;
        }
        
        return _lastGain;
    }

    /// <summary>
    /// Pre-emphasis filter to boost high frequencies for better Opus encoding
    /// H(z) = 1 - α*z^(-1) where α = 0.95
    /// </summary>
    private void ApplyPreEmphasis(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float current = samples[i];
            float emphasized = current - PreEmphasisCoeff * _preEmphasisState;
            samples[i] = Math.Clamp(emphasized, -1.0f, 1.0f);
            _preEmphasisState = current;
        }
    }

    /// <summary>
    /// Convert PCM16 bytes to float array (-1.0 to 1.0)
    /// </summary>
    private static float[] BytesToFloat(byte[] bytes)
    {
        var samples = new float[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
            samples[i] = sample / 32768.0f;
        }
        return samples;
    }

    /// <summary>
    /// Convert float array (-1.0 to 1.0) to PCM16 bytes
    /// </summary>
    private static byte[] FloatToBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>
    /// Reset all DSP state (call when starting new audio stream)
    /// </summary>
    public void Reset()
    {
        Array.Clear(_hpX, 0, _hpX.Length);
        Array.Clear(_hpY, 0, _hpY.Length);
        _lastGain = 1.0f;
        _preEmphasisState = 0f;
    }

    /// <summary>
    /// Compute RMS of PCM16 audio for diagnostics
    /// </summary>
    public static float ComputeRms(byte[] pcmData)
    {
        if (pcmData == null || pcmData.Length < 2)
            return 0;

        int sampleCount = pcmData.Length / 2;
        double sumSquares = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            sumSquares += sample * sample;
        }

        return (float)Math.Sqrt(sumSquares / sampleCount);
    }
}
