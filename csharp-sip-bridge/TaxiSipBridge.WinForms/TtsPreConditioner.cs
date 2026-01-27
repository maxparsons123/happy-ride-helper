using System;

namespace TaxiSipBridge;

/// <summary>
/// Pre-conditioning DSP for OpenAI TTS output before telephony encoding.
/// Fixes digital artifacts, smooths synthetic dynamics, and prepares audio
/// for G.711 quantization.
/// 
/// Pipeline: TTS PCM 24k → De-ess → Harmonic Soften → Micro-noise → Gain Norm
/// </summary>
public static class TtsPreConditioner
{
    // ===== De-esser (Sibilant Control) =====
    // Targets 4-8kHz where "s", "sh", "ch" live
    private const float DEESS_FREQ = 5500f;
    private const float DEESS_Q = 1.5f;
    private const float DEESS_THRESHOLD = 0.15f;  // When to start reducing
    private const float DEESS_RATIO = 0.6f;       // How much to reduce (0.6 = 40% reduction)
    
    // ===== Harmonic Softener (Anti-alias blur) =====
    // Gentle 1-pole lowpass to smooth harsh digital edges
    private const float SOFTEN_FREQ = 7000f;      // Cutoff frequency
    private const float SOFTEN_AMOUNT = 0.3f;     // Blend: 0=off, 1=full filter
    
    // ===== Micro-noise Bed =====
    // Very low level noise to mask quantization artifacts
    private const float NOISE_LEVEL = 0.0008f;    // -62dB, barely perceptible
    
    // ===== Gain Normalization =====
    private const float TARGET_RMS = 0.18f;       // Target RMS level
    private const float MAX_GAIN = 2.5f;          // Limit gain boost
    private const float MIN_GAIN = 0.5f;          // Limit gain reduction
    
    // Sample rate
    private const float SR = 24000f;
    
    // Filter states
    private static float _deessX1, _deessX2, _deessY1, _deessY2;
    private static float _deessB0, _deessB1, _deessB2, _deessA1, _deessA2;
    private static float _softenState;
    private static float _softenCoeff;
    private static bool _initialized;
    private static readonly Random _rng = new Random();
    private static readonly object _lock = new object();
    
    private static float DbToLin(float db) => (float)Math.Pow(10, db / 20.0);
    
    private static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        
        // Setup de-esser bandpass filter (to detect sibilants)
        SetupBandpass(DEESS_FREQ, DEESS_Q);
        
        // Setup softener coefficient (1-pole lowpass)
        float w = 2f * (float)Math.PI * SOFTEN_FREQ / SR;
        _softenCoeff = 1f - (float)Math.Exp(-w);
    }
    
    private static void SetupBandpass(float freq, float Q)
    {
        float w0 = 2f * (float)Math.PI * freq / SR;
        float alpha = (float)Math.Sin(w0) / (2f * Q);
        float cosw = (float)Math.Cos(w0);
        
        float b0 = alpha;
        float b1 = 0f;
        float b2 = -alpha;
        float a0 = 1f + alpha;
        float a1 = -2f * cosw;
        float a2 = 1f - alpha;
        
        _deessB0 = b0 / a0;
        _deessB1 = b1 / a0;
        _deessB2 = b2 / a0;
        _deessA1 = a1 / a0;
        _deessA2 = a2 / a0;
    }
    
    /// <summary>
    /// Process 24kHz PCM audio through pre-conditioning pipeline.
    /// Call this BEFORE TelephonyVoiceShaping.
    /// </summary>
    public static short[] Process(short[] pcm)
    {
        if (pcm == null || pcm.Length == 0)
            return pcm;
        
        lock (_lock)
        {
            Initialize();
            
            // Convert to float
            float[] buf = new float[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
                buf[i] = pcm[i] / 32768f;
            
            // 1) De-ess (sibilant control)
            ApplyDeEsser(buf);
            
            // 2) Harmonic soften (gentle anti-alias blur)
            ApplyHarmonicSoften(buf);
            
            // 3) Micro-noise bed injection
            ApplyMicroNoise(buf);
            
            // 4) Gain normalization
            ApplyGainNorm(buf);
            
            // Convert back to short
            short[] output = new short[buf.Length];
            for (int i = 0; i < buf.Length; i++)
                output[i] = (short)(Math.Clamp(buf[i], -1f, 1f) * 32767f);
            
            return output;
        }
    }
    
    private static void ApplyDeEsser(float[] buf)
    {
        // Sidechain: detect sibilant energy via bandpass
        // When sibilant detected, reduce gain in that region
        
        for (int i = 0; i < buf.Length; i++)
        {
            float x = buf[i];
            
            // Bandpass filter to detect sibilants
            float bp = _deessB0 * x + _deessB1 * _deessX1 + _deessB2 * _deessX2
                     - _deessA1 * _deessY1 - _deessA2 * _deessY2;
            
            _deessX2 = _deessX1;
            _deessX1 = x;
            _deessY2 = _deessY1;
            _deessY1 = bp;
            
            // Envelope follower (detect sibilant energy)
            float env = Math.Abs(bp);
            
            // If sibilant energy exceeds threshold, reduce gain
            if (env > DEESS_THRESHOLD)
            {
                float reduction = 1f - ((env - DEESS_THRESHOLD) * (1f - DEESS_RATIO));
                reduction = Math.Clamp(reduction, DEESS_RATIO, 1f);
                buf[i] *= reduction;
            }
        }
    }
    
    private static void ApplyHarmonicSoften(float[] buf)
    {
        // Gentle 1-pole lowpass blended with original
        // Smooths harsh digital edges without losing clarity
        
        for (int i = 0; i < buf.Length; i++)
        {
            float x = buf[i];
            
            // 1-pole lowpass
            _softenState += _softenCoeff * (x - _softenState);
            
            // Blend: mix filtered with original
            buf[i] = x * (1f - SOFTEN_AMOUNT) + _softenState * SOFTEN_AMOUNT;
        }
    }
    
    private static void ApplyMicroNoise(float[] buf)
    {
        // Inject very low-level noise to mask quantization artifacts
        // This helps with the "too clean" digital silence issue
        
        for (int i = 0; i < buf.Length; i++)
        {
            // Generate white noise (-1 to 1)
            float noise = (float)(_rng.NextDouble() * 2.0 - 1.0);
            buf[i] += noise * NOISE_LEVEL;
        }
    }
    
    private static void ApplyGainNorm(float[] buf)
    {
        // Calculate RMS of input
        double sum = 0;
        for (int i = 0; i < buf.Length; i++)
            sum += buf[i] * buf[i];
        
        float rms = (float)Math.Sqrt(sum / buf.Length);
        
        if (rms < 0.001f) return; // Too quiet, skip
        
        // Calculate gain needed to reach target RMS
        float gain = TARGET_RMS / rms;
        gain = Math.Clamp(gain, MIN_GAIN, MAX_GAIN);
        
        // Apply gain
        for (int i = 0; i < buf.Length; i++)
            buf[i] *= gain;
    }
    
    /// <summary>
    /// Reset all filter states between calls/sessions.
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _deessX1 = _deessX2 = _deessY1 = _deessY2 = 0;
            _softenState = 0;
        }
    }
}
