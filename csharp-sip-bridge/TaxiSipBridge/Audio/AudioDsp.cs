using System;

namespace TaxiSipBridge.Audio;

/// <summary>
/// Digital Signal Processing for telephony audio - ported from Python bridge v6.2
/// Includes high-pass filter, soft-knee noise gate, and smoothed AGC.
/// </summary>
public class AudioDsp
{
    // Configuration - matches Python bridge
    private const int HighPassCutoff = 60;
    private const int SampleRate = 8000;
    private const float NoiseGateThreshold = 25f;
    private const bool NoiseGateSoftKnee = true;
    private const float TargetRms = 2500f;
    private const float MaxGain = 3.0f;
    private const float MinGain = 0.8f;
    private const float GainSmoothingFactor = 0.2f;

    // High-pass filter state (2nd order Butterworth)
    private readonly double[] _hpX = new double[3]; // input history
    private readonly double[] _hpY = new double[3]; // output history

    // Butterworth coefficients for 60Hz @ 8kHz (pre-calculated)
    // Generated from: butter(2, 60, btype='high', fs=8000, output='ba')
    private static readonly double[] HpB = { 0.9780305, -1.9560610, 0.9780305 };
    private static readonly double[] HpA = { 1.0, -1.9555782, 0.9565439 };

    // AGC state
    private float _lastGain = 1.0f;

    /// <summary>
    /// Apply noise reduction pipeline: High-pass → Noise gate → AGC
    /// </summary>
    /// <param name="pcmData">16-bit PCM audio (little-endian bytes)</param>
    /// <returns>Processed audio and current gain value</returns>
    public (byte[] Audio, float Gain) ApplyNoiseReduction(byte[] pcmData)
    {
        if (pcmData == null || pcmData.Length < 4)
            return (pcmData ?? Array.Empty<byte>(), _lastGain);

        int sampleCount = pcmData.Length / 2;
        var samples = new float[sampleCount];

        // Convert bytes to float samples
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            samples[i] = sample;
        }

        // Step 1: High-pass filter (removes low-frequency hum)
        ApplyHighPassFilter(samples);

        // Step 2: Soft-knee noise gate (removes background noise)
        ApplySoftKneeNoiseGate(samples);

        // Step 3: Smoothed AGC (normalizes volume)
        ApplySmoothedAgc(samples);

        // Convert back to bytes
        var output = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)Math.Clamp(samples[i], short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(sample & 0xFF);
            output[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return (output, _lastGain);
    }

    /// <summary>
    /// Apply 2nd order Butterworth high-pass filter at 60Hz
    /// </summary>
    private void ApplyHighPassFilter(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            // Shift history
            _hpX[0] = _hpX[1];
            _hpX[1] = _hpX[2];
            _hpX[2] = samples[i];

            _hpY[0] = _hpY[1];
            _hpY[1] = _hpY[2];

            // Apply IIR filter: y[n] = b0*x[n] + b1*x[n-1] + b2*x[n-2] - a1*y[n-1] - a2*y[n-2]
            _hpY[2] = HpB[0] * _hpX[2] + HpB[1] * _hpX[1] + HpB[2] * _hpX[0]
                    - HpA[1] * _hpY[1] - HpA[2] * _hpY[0];

            samples[i] = (float)_hpY[2];
        }
    }

    /// <summary>
    /// Apply soft-knee noise gate to reduce background noise while preserving soft consonants
    /// </summary>
    private void ApplySoftKneeNoiseGate(float[] samples)
    {
        if (!NoiseGateSoftKnee)
        {
            // Hard gate fallback
            for (int i = 0; i < samples.Length; i++)
            {
                if (Math.Abs(samples[i]) < NoiseGateThreshold)
                    samples[i] *= 0.1f;
            }
            return;
        }

        // Soft-knee: gradual gain reduction in the knee region
        float kneeLow = NoiseGateThreshold;
        float kneeHigh = NoiseGateThreshold * 3f;

        for (int i = 0; i < samples.Length; i++)
        {
            float absVal = Math.Abs(samples[i]);

            // Calculate gain curve: 0.15 (below knee) to 1.0 (above knee)
            float normalized = (absVal - kneeLow) / (kneeHigh - kneeLow);
            float gainCurve = Math.Clamp(normalized, 0f, 1f);
            float gain = 0.15f + 0.85f * gainCurve;

            samples[i] *= gain;
        }
    }

    /// <summary>
    /// Apply smoothed AGC to normalize audio levels without harsh pumping
    /// </summary>
    private void ApplySmoothedAgc(float[] samples)
    {
        // Calculate RMS of the frame
        double sumSquares = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sumSquares += samples[i] * samples[i];
        }
        float rms = (float)Math.Sqrt(sumSquares / samples.Length);

        // Only apply gain if signal is above noise floor
        if (rms > 30)
        {
            float targetGain = Math.Clamp(TargetRms / rms, MinGain, MaxGain);

            // Smooth the gain transition (prevents pumping)
            _lastGain += GainSmoothingFactor * (targetGain - _lastGain);

            // Apply gain
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= _lastGain;
            }
        }
    }

    /// <summary>
    /// Reset filter state (call between calls to prevent artifacts)
    /// </summary>
    public void Reset()
    {
        Array.Clear(_hpX, 0, _hpX.Length);
        Array.Clear(_hpY, 0, _hpY.Length);
        _lastGain = 1.0f;
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
