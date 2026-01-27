using System;

namespace TaxiSipBridge;

/// <summary>
/// Light DSP shaping to make TTS sound more natural on G.711 telephony.
/// Optimized for Balanced + Sapphire voice.
/// </summary>
public static class TelephonyVoiceShaping
{
    // === Tunables (good defaults for Sapphire on phone) ==================
    
    // Parametric EQ peaking filter
    private const float EQ_GAIN_DB = 2.5f;      // +2.5dB warmth boost
    private const float EQ_FREQ = 800f;         // 800 Hz (low-mid warmth)
    private const float EQ_Q = 1.0f;            // moderately wide Q

    // Compressor
    private const float COMP_THRESHOLD_DB = -18f;
    private const float COMP_RATIO = 2.0f;
    private const float COMP_MAKEUP_DB = 3f;

    // Output safety (avoid clipping before A-law)
    private const float OUTPUT_SAFETY_GAIN = 0.85f;
    private const float SAMPLE_RATE = 8000f; // shaping done at 8k before PCMA

    // Parametric EQ state
    private static float eq_b0, eq_b1, eq_b2, eq_a1, eq_a2;
    private static float eq_x1, eq_x2, eq_y1, eq_y2;
    private static bool eqInitialized = false;
    private static readonly object _lock = new object();

    // Compressor helper
    private static float DbToLin(float db) => (float)Math.Pow(10.0, db / 20.0);

    /// <summary>
    /// Apply EQ + compression + safety gain to 8kHz PCM16 audio.
    /// </summary>
    public static short[] Process(short[] pcm)
    {
        if (pcm == null || pcm.Length == 0)
            return pcm;

        lock (_lock)
        {
            InitEQ();

            // Convert to float -1..1
            float[] buf = new float[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
                buf[i] = pcm[i] / 32768f;

            ApplyEQ(buf);
            ApplyCompression(buf);
            ApplyOutputGain(buf);

            // Convert back to short
            short[] outShort = new short[buf.Length];
            for (int i = 0; i < buf.Length; i++)
                outShort[i] = (short)(Math.Clamp(buf[i], -1f, 1f) * 32767f);

            return outShort;
        }
    }

    /// <summary>
    /// Initialize parametric bell filter (peaking EQ).
    /// </summary>
    private static void InitEQ()
    {
        if (eqInitialized) return;
        eqInitialized = true;

        float A = DbToLin(EQ_GAIN_DB);
        float w0 = 2f * (float)Math.PI * EQ_FREQ / SAMPLE_RATE;
        float alpha = (float)Math.Sin(w0) / (2f * EQ_Q);
        float cosw0 = (float)Math.Cos(w0);

        float b0 = 1f + alpha * A;
        float b1 = -2f * cosw0;
        float b2 = 1f - alpha * A;
        float a0 = 1f + alpha / A;
        float a1 = -2f * cosw0;
        float a2 = 1f - alpha / A;

        eq_b0 = b0 / a0;
        eq_b1 = b1 / a0;
        eq_b2 = b2 / a0;
        eq_a1 = a1 / a0;
        eq_a2 = a2 / a0;
    }

    /// <summary>
    /// Apply parametric EQ to boost warmth region.
    /// </summary>
    private static void ApplyEQ(float[] buf)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            float x = buf[i];
            float y = eq_b0 * x + eq_b1 * eq_x1 + eq_b2 * eq_x2 - eq_a1 * eq_y1 - eq_a2 * eq_y2;

            eq_x2 = eq_x1;
            eq_x1 = x;
            eq_y2 = eq_y1;
            eq_y1 = y;

            buf[i] = y;
        }
    }

    /// <summary>
    /// Gentle compressor (soft knee-ish).
    /// </summary>
    private static void ApplyCompression(float[] buf)
    {
        float thresholdLin = DbToLin(COMP_THRESHOLD_DB);
        float makeupLin = DbToLin(COMP_MAKEUP_DB);

        for (int i = 0; i < buf.Length; i++)
        {
            float x = buf[i];
            float ax = Math.Abs(x);

            if (ax > thresholdLin)
            {
                float over = ax / thresholdLin;
                float dbOver = (float)(20.0 * Math.Log10(over));
                float dbComp = dbOver / COMP_RATIO;
                float gainDb = dbComp - dbOver;
                float gainLin = DbToLin(gainDb);
                x *= gainLin;
            }

            buf[i] = x * makeupLin;
        }
    }

    /// <summary>
    /// Safety gain to avoid A-law saturation.
    /// </summary>
    private static void ApplyOutputGain(float[] buf)
    {
        for (int i = 0; i < buf.Length; i++)
            buf[i] *= OUTPUT_SAFETY_GAIN;
    }

    /// <summary>
    /// Reset filter state between calls/sessions.
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            eq_x1 = eq_x2 = eq_y1 = eq_y2 = 0f;
        }
    }
}
