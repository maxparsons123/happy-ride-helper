// Last updated: 2026-02-21 (v2.8)
namespace AdaSdkModel.Audio;

/// <summary>
/// Converts PCM16 24kHz audio from OpenAI to G.711 A-law 8kHz for SIP RTP.
/// 
/// Pipeline: PCM16 24kHz → Low-pass filter (3.4kHz Butterworth) → Decimate 3:1 → A-law encode
/// 
/// The anti-aliasing filter prevents the "lispy" artifacts caused by naive decimation.
/// Without it, frequencies above 4kHz fold back into the audible range.
/// 
/// Thread-safety: each instance carries filter state — create one per call session.
/// </summary>
public sealed class Pcm24kToAlawResampler
{
    // 2nd-order Butterworth low-pass at 3400Hz / 24000Hz sample rate
    // Designed with bilinear transform: fc=3400, fs=24000
    // This gives -3dB at 3.4kHz and steep rolloff above — matches G.711 telephone bandwidth
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    // Optional: volume gain applied in PCM domain (clean linear math)
    private float _outputGain = 1.0f;

    public float OutputGain
    {
        get => _outputGain;
        set => _outputGain = Math.Clamp(value, 0.0f, 10.0f);
    }

    public Pcm24kToAlawResampler()
    {
        // 2nd-order Butterworth LPF coefficients
        // fc = 3400Hz, fs = 24000Hz
        // Pre-warped: ω = tan(π * fc / fs) = tan(π * 3400 / 24000)
        double wc = Math.Tan(Math.PI * 3400.0 / 24000.0);
        double wc2 = wc * wc;
        double sqrt2 = Math.Sqrt(2.0);
        double norm = 1.0 / (1.0 + sqrt2 * wc + wc2);

        _b0 = wc2 * norm;
        _b1 = 2.0 * _b0;
        _b2 = _b0;
        _a1 = 2.0 * (wc2 - 1.0) * norm;
        _a2 = (1.0 - sqrt2 * wc + wc2) * norm;
    }

    /// <summary>
    /// Convert PCM16 24kHz (little-endian byte array) to G.711 A-law 8kHz bytes.
    /// </summary>
    public byte[] Convert(byte[] pcm24kBytes)
    {
        if (pcm24kBytes == null || pcm24kBytes.Length < 2)
            return Array.Empty<byte>();

        int sampleCount = pcm24kBytes.Length / 2;
        
        // Apply LPF to all samples first (in-place conceptually)
        // Then decimate by picking every 3rd sample
        int outputSamples = sampleCount / 3;
        var alawOut = new byte[outputSamples];

        int outIdx = 0;
        for (int i = 0; i < sampleCount && outIdx < outputSamples; i++)
        {
            // Read PCM16 little-endian
            short sample = (short)(pcm24kBytes[i * 2] | (pcm24kBytes[i * 2 + 1] << 8));
            double x = sample;

            // Apply 2nd-order Butterworth LPF
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;

            // Decimate: only take every 3rd sample (24kHz → 8kHz)
            if (i % 3 == 0)
            {
                // Apply output gain in PCM domain
                double gained = y * _outputGain;

                // Clamp to PCM16 range
                int clamped = (int)gained;
                if (clamped > 32635) clamped = 32635;
                else if (clamped < -32635) clamped = -32635;

                alawOut[outIdx++] = LinearToALaw((short)clamped);
            }
        }

        // Handle case where output is smaller than expected
        if (outIdx < outputSamples)
        {
            var trimmed = new byte[outIdx];
            Buffer.BlockCopy(alawOut, 0, trimmed, 0, outIdx);
            return trimmed;
        }

        return alawOut;
    }

    /// <summary>Reset filter state (call between sessions).</summary>
    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0;
    }

    private static byte LinearToALaw(short sample)
    {
        int sign = (~sample >> 8) & 0x80;
        if (sign == 0) sample = (short)-sample;
        if (sample > 32635) sample = 32635;
        int exponent = 7;
        for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        int mantissa = (sample >> (exponent == 0 ? 4 : exponent + 3)) & 0x0F;
        return (byte)((sign | (exponent << 4) | mantissa) ^ 0x55);
    }
}
