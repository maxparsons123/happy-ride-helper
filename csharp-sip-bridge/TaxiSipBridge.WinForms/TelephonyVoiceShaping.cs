using System;

namespace TaxiSipBridge;

/// <summary>
/// Light DSP shaping to make TTS sound more natural on G.711 telephony.
/// Tuned for Sapphire voice, SIP G.711, Balanced mode.
/// </summary>
public static class TelephonyVoiceShaping
{
    // ===== Tuned for Sapphire, SIP G.711, Balanced =====
    
    // EQ1 - Warmth
    private const float EQ1_GAIN_DB = 1.5f;
    private const float EQ1_FREQ = 800f;
    private const float EQ1_Q = 1.3f;

    // EQ2 - Presence (anti-lisp)
    private const float EQ2_GAIN_DB = 1.0f;
    private const float EQ2_FREQ = 2200f;
    private const float EQ2_Q = 1.2f;

    // Compressor
    private const float COMP_THRESH_DB = -18f;
    private const float COMP_RATIO = 2.0f;
    private const float COMP_MAKEUP_DB = 1.5f;

    // Output safety
    private const float OUTPUT_SAFETY = 0.88f;
    private const float SR = 8000f;

    // Biquad states (two filters, peaking)
    private static float[] x1 = new float[2], x2 = new float[2], y1 = new float[2], y2 = new float[2];
    private static float[] b0 = new float[2], b1 = new float[2], b2 = new float[2], a1 = new float[2], a2 = new float[2];
    private static bool coeffsInit = false;
    private static readonly object _lock = new object();

    private static float DbToLin(float db) => (float)Math.Pow(10, db / 20.0);

    private static void Init()
    {
        if (coeffsInit) return;
        coeffsInit = true;
        SetupPeak(0, EQ1_GAIN_DB, EQ1_FREQ, EQ1_Q);
        SetupPeak(1, EQ2_GAIN_DB, EQ2_FREQ, EQ2_Q);
    }

    private static void SetupPeak(int i, float gainDb, float freq, float Q)
    {
        float A = DbToLin(gainDb);
        float w0 = 2f * (float)Math.PI * freq / SR;
        float alpha = (float)Math.Sin(w0) / (2f * Q);
        float cosw = (float)Math.Cos(w0);

        float bb0 = 1f + alpha * A;
        float bb1 = -2f * cosw;
        float bb2 = 1f - alpha * A;
        float aa0 = 1f + alpha / A;
        float aa1 = -2f * cosw;
        float aa2 = 1f - alpha / A;

        b0[i] = bb0 / aa0;
        b1[i] = bb1 / aa0;
        b2[i] = bb2 / aa0;
        a1[i] = aa1 / aa0;
        a2[i] = aa2 / aa0;
    }

    /// <summary>
    /// Apply EQ + compression + safety gain to 8kHz PCM16 audio.
    /// </summary>
    public static short[] Process(short[] pcm)
    {
        if (pcm == null || pcm.Length == 0)
            return pcm;

        lock (_lock)
        {
            Init();

            float[] buf = new float[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
                buf[i] = pcm[i] / 32768f;

            // EQ stages
            ApplyEQStage(buf, 0);  // Warmth at 800Hz
            ApplyEQStage(buf, 1);  // Presence at 2.2kHz

            // Compression
            float tLin = DbToLin(COMP_THRESH_DB);
            float makeup = DbToLin(COMP_MAKEUP_DB);
            for (int i = 0; i < buf.Length; i++)
            {
                float x = buf[i];
                float ax = Math.Abs(x);
                if (ax > tLin)
                {
                    float over = ax / tLin;
                    float dOver = (float)(20f * Math.Log10(over));
                    float dComp = dOver / COMP_RATIO;
                    float gDb = dComp - dOver;
                    float gain = DbToLin(gDb);
                    x *= gain;
                }
                buf[i] = x * makeup;
            }

            // Output safety
            for (int i = 0; i < buf.Length; i++)
                buf[i] *= OUTPUT_SAFETY;

            // Back to short
            short[] outPcm = new short[buf.Length];
            for (int i = 0; i < buf.Length; i++)
                outPcm[i] = (short)(Math.Clamp(buf[i], -1f, 1f) * 32767f);

            return outPcm;
        }
    }

    private static void ApplyEQStage(float[] buf, int i)
    {
        for (int n = 0; n < buf.Length; n++)
        {
            float x = buf[n];
            float y = b0[i] * x + b1[i] * x1[i] + b2[i] * x2[i] - a1[i] * y1[i] - a2[i] * y2[i];
            x2[i] = x1[i]; x1[i] = x;
            y2[i] = y1[i]; y1[i] = y;
            buf[n] = y;
        }
    }

    /// <summary>
    /// Reset filter state between calls/sessions.
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            Array.Clear(x1);
            Array.Clear(x2);
            Array.Clear(y1);
            Array.Clear(y2);
        }
    }
}
