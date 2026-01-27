using System;

namespace TaxiSipBridge.Audio;

/// <summary>
/// High-quality DSP pipeline optimized for OpenAI Realtime API → telephony.
/// Includes high-pass filtering, noise gating, AGC, adaptive pre-emphasis, and soft clipping.
/// Ported from Python taxi_bridge_v78.py for feature parity.
/// </summary>
public class AudioDsp
{
    // ========== INBOUND (Caller → AI) Configuration ==========
    // Aligned with Python taxi_bridge_v78.py AudioProcessor
    private const int HighPassCutoff = 80;          // Hz - removes low-frequency rumble
    private const int SampleRate = 8000;
    private const float NoiseGateThreshold = 80f;   // Soft gate threshold (v7.8: 80)
    private const float TargetRms = 300f;           // Target RMS (v7.8: 300)
    private const float AgcMaxGain = 15.0f;         // Max gain (v7.8: 15.0)
    private const float AgcMinGain = 1.0f;          // Minimum gain
    private const float AgcSmoothingFactor = 0.15f; // Gain smoothing (v7.8: 0.15)
    private const float VolumeBoostFactor = 3.0f;   // Volume boost (v7.8: 3.0)
    private const float VolumeBoostThreshold = 200f;// Only boost if RMS below this (v7.8: 200)
    private const float AgcFloorRms = 10f;          // Minimum RMS to apply AGC (v7.8: 10)
    private const float SoftClipThreshold = 32000f; // Soft clipping threshold (v7.8: 32000)
    
    // ========== OUTBOUND (AI → Caller) Configuration ==========
    // v7.8 doesn't apply volume boost to outbound, just resample + soft clip
    private const float OutboundVolumeBoost = 1.0f; // Unity gain (v7.8 style)
    
    // High-pass filter state (2nd order Butterworth)
    private readonly double[] _hpX = new double[3]; // input history
    private readonly double[] _hpY = new double[3]; // output history

    // Butterworth coefficients for 80Hz @ 8kHz
    private static readonly double[] HpB = { 0.9762421, -1.9524842, 0.9762421 };
    private static readonly double[] HpA = { 1.0, -1.9515384, 0.9534300 };

    // AGC state
    private float _lastGain = 1.0f;
    private float _preEmphasisState = 0f;
    private float _outboundPreEmphasisState = 0f;

    /// <summary>
    /// Applies full inbound DSP pipeline: high-pass → volume boost → AGC → adaptive pre-emphasis → soft clip
    /// Ported from Python taxi_bridge_v78.py AudioProcessor.process_inbound()
    /// </summary>
    public (byte[] Audio, float Gain) ApplyNoiseReduction(byte[] pcmData, bool isHighQuality = false)
    {
        if (pcmData == null || pcmData.Length == 0)
            return (pcmData ?? Array.Empty<byte>(), 1.0f);

        var floatSamples = BytesToFloat(pcmData);
        
        // Calculate initial RMS
        float rms = ComputeRmsFloat(floatSamples);
        
        // Skip aggressive DSP for already-clean high-quality signals (Opus, etc.)
        if (isHighQuality && rms > 500f / 32768f)
        {
            return (pcmData, 1.0f);
        }
        
        // Step 1: High-pass filter - removes low-frequency rumble
        ApplyHighPassFilter(floatSamples);
        
        // Step 2: Noise gate with soft transition
        ApplySoftNoiseGate(floatSamples, ref rms);
        
        // Step 3: Volume boost for very quiet audio (telephony often arrives at RMS 1-2)
        if (rms * 32768f < VolumeBoostThreshold)
        {
            for (int i = 0; i < floatSamples.Length; i++)
                floatSamples[i] *= VolumeBoostFactor;
            rms = ComputeRmsFloat(floatSamples);
        }
        
        // Step 4: AGC with smoothing
        float appliedGain = ApplySmoothedAgc(floatSamples, rms);
        
        // Step 5: Adaptive pre-emphasis (spectral tilt detection from Python)
        ApplyAdaptivePreEmphasis(floatSamples);
        
        // Step 6: Soft clipping (tanh-based, prevents hard clipping)
        ApplySoftClip(floatSamples);
        
        var processedBytes = FloatToBytes(floatSamples);
        return (processedBytes, appliedGain);
    }

    /// <summary>
    /// Applies outbound DSP: volume boost + soft clip for Ada → caller path
    /// Ported from Python taxi_bridge_v78.py AudioProcessor.process_outbound()
    /// </summary>
    public byte[] ProcessOutbound(byte[] pcmData)
    {
        if (pcmData == null || pcmData.Length == 0)
            return pcmData ?? Array.Empty<byte>();

        var floatSamples = BytesToFloat(pcmData);
        
        // Apply volume boost to Ada's voice
        for (int i = 0; i < floatSamples.Length; i++)
            floatSamples[i] *= OutboundVolumeBoost;
        
        // Soft clip to prevent distortion
        ApplySoftClip(floatSamples);
        
        return FloatToBytes(floatSamples);
    }

    /// <summary>
    /// High-pass filter to remove DC offset and low-frequency rumble
    /// </summary>
    private void ApplyHighPassFilter(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            _hpX[2] = _hpX[1];
            _hpX[1] = _hpX[0];
            _hpY[2] = _hpY[1];
            _hpY[1] = _hpY[0];

            _hpX[0] = samples[i];

            double output = HpB[0] * _hpX[0] + HpB[1] * _hpX[1] + HpB[2] * _hpX[2]
                          - HpA[1] * _hpY[1] - HpA[2] * _hpY[2];

            _hpY[0] = Math.Clamp(output, -1.0, 1.0);
            samples[i] = (float)_hpY[0];
        }
    }

    /// <summary>
    /// Soft noise gate with fade transition (prevents clicks)
    /// From Python: if rms < threshold, apply soft fade instead of hard cut
    /// </summary>
    private void ApplySoftNoiseGate(float[] samples, ref float rms)
    {
        float thresholdNorm = NoiseGateThreshold / 32768f;
        float rmsScaled = rms;
        
        if (rmsScaled < thresholdNorm)
        {
            // Soft fade: gate_gain = rms / threshold
            float gateGain = rmsScaled / thresholdNorm;
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= gateGain;
            rms *= gateGain;
        }
    }

    /// <summary>
    /// Smoothed AGC with high max gain for quiet telephony audio
    /// </summary>
    private float ApplySmoothedAgc(float[] samples, float rms)
    {
        float targetRmsNorm = TargetRms / 32768f;
        float floorRmsNorm = AgcFloorRms / 32768f;
        
        if (rms < floorRmsNorm)
        {
            _lastGain = AgcMinGain;
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= _lastGain;
            return _lastGain;
        }

        float targetGain = targetRmsNorm / rms;
        targetGain = Math.Clamp(targetGain, AgcMinGain, AgcMaxGain);
        
        // Smooth gain transition
        _lastGain += AgcSmoothingFactor * (targetGain - _lastGain);
        _lastGain = Math.Clamp(_lastGain, AgcMinGain, AgcMaxGain);
        
        for (int i = 0; i < samples.Length; i++)
            samples[i] *= _lastGain;
        
        return _lastGain;
    }

    /// <summary>
    /// Adaptive pre-emphasis based on spectral tilt
    /// From Python: analyze spectral tilt and adjust coefficient (0.92-0.97)
    /// </summary>
    private void ApplyAdaptivePreEmphasis(float[] samples)
    {
        if (samples.Length < 100)
        {
            ApplyPreEmphasis(samples, 0.95f);
            return;
        }
        
        // Calculate spectral tilt from sample differences (simplified from Python)
        float sumDiff = 0;
        int checkLen = Math.Min(1000, samples.Length - 1);
        for (int i = 0; i < checkLen; i++)
            sumDiff += samples[i + 1] - samples[i];
        float spectralTilt = sumDiff / checkLen;
        
        // Adaptive coefficient: more pre-emphasis for low-tilt signals
        float preEmphCoeff = spectralTilt < 0 ? 0.97f : 0.92f;
        ApplyPreEmphasis(samples, preEmphCoeff);
    }

    /// <summary>
    /// Pre-emphasis filter: H(z) = 1 - α*z^(-1)
    /// </summary>
    private void ApplyPreEmphasis(float[] samples, float coeff)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float current = samples[i];
            float emphasized = current - coeff * _preEmphasisState;
            samples[i] = emphasized;
            _preEmphasisState = current;
        }
    }

    /// <summary>
    /// Soft clipping using tanh (prevents hard clipping distortion)
    /// From Python: np.tanh(samples / threshold) * threshold
    /// </summary>
    private void ApplySoftClip(float[] samples)
    {
        float thresholdNorm = SoftClipThreshold / 32768f;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(Math.Tanh(samples[i] / thresholdNorm) * thresholdNorm);
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
    /// Compute RMS of float samples
    /// </summary>
    private static float ComputeRmsFloat(float[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (float s in samples)
            sum += s * s;
        return (float)Math.Sqrt(sum / samples.Length);
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
        _outboundPreEmphasisState = 0f;
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
