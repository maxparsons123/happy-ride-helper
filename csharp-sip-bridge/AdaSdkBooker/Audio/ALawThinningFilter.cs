namespace AdaSdkModel.Audio;

/// <summary>
/// 1st-order High-Pass Filter for G.711 A-law audio.
/// Removes low-frequency "mud" to produce a thinner, crisper voice
/// better suited to telephony playback.
///
/// Alpha controls the cutoff:
///   0.80 = very thin / tinny
///   0.88 = crisp telephony sweet-spot
///   0.95 = natural but slightly thinned
///
/// Thread-safety: each instance carries its own state —
/// create one per call session.
/// </summary>
public sealed class ALawThinningFilter
{
    private static readonly short[] _decode = CreateDecodeTable();
    private float _prevInput;
    private float _prevOutput;
    private readonly float _alpha;

    public ALawThinningFilter(float alpha = 0.88f)
    {
        _alpha = Math.Clamp(alpha, 0.5f, 0.99f);
    }

    /// <summary>
    /// Applies the high-pass thinning filter in-place on A-law encoded bytes.
    /// </summary>
    public void ApplyInPlace(byte[] alawData)
    {
        if (alawData == null || alawData.Length == 0) return;

        for (int i = 0; i < alawData.Length; i++)
        {
            // Decode A-law → linear PCM16
            short pcm = _decode[alawData[i]];
            float input = pcm;

            // 1st-order HPF: y[n] = α * (y[n-1] + x[n] - x[n-1])
            float output = _alpha * (_prevOutput + input - _prevInput);

            _prevInput = input;
            _prevOutput = output;

            // Clamp to PCM16 range
            int clamped = (int)output;
            if (clamped > 32635) clamped = 32635;
            else if (clamped < -32635) clamped = -32635;

            // Encode back to A-law
            alawData[i] = LinearToALaw((short)clamped);
        }
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

    private static short[] CreateDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int v = i ^ 0x55;
            int sign = v & 0x80;
            int exponent = (v >> 4) & 0x07;
            int mantissa = v & 0x0F;
            int sample = exponent == 0 ? (mantissa << 4) + 8 : ((mantissa << 4) + 0x108) << (exponent - 1);
            table[i] = (short)(sign != 0 ? sample : -sample);
        }
        return table;
    }
}
