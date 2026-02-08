using System.Runtime.CompilerServices;

namespace AdaSipClient.Audio;

/// <summary>
/// Lightweight gain control for A-law audio frames.
/// Decodes → applies gain → re-encodes per-sample.
/// Thread-safe (stateless apart from gain property).
/// </summary>
public sealed class VolumeControl
{
    private volatile float _gain = 1.0f;

    /// <summary>Volume 0–200 (100 = unity).</summary>
    public int VolumePercent
    {
        get => (int)(_gain * 100);
        set => _gain = Math.Clamp(value, 0, 200) / 100f;
    }

    /// <summary>
    /// Apply gain to an A-law frame in-place.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyInPlace(byte[] alawFrame)
    {
        if (Math.Abs(_gain - 1.0f) < 0.01f) return; // unity = no-op

        for (int i = 0; i < alawFrame.Length; i++)
        {
            short pcm = ALawDecode(alawFrame[i]);
            int amplified = (int)(pcm * _gain);
            amplified = Math.Clamp(amplified, short.MinValue, short.MaxValue);
            alawFrame[i] = ALawEncode((short)amplified);
        }
    }

    // ── G.711 A-law codec (ITU-T G.711) ──

    private static short ALawDecode(byte alaw)
    {
        alaw ^= 0x55;
        int sign = (alaw & 0x80) != 0 ? -1 : 1;
        int seg = (alaw >> 4) & 0x07;
        int quant = alaw & 0x0F;

        int magnitude = seg == 0
            ? (quant << 4) + 8
            : ((quant << 4) + 8 + 256) << (seg - 1);

        return (short)(sign * magnitude);
    }

    private static byte ALawEncode(short pcm)
    {
        int mask = pcm < 0 ? 0xD5 : 0x55;
        int abs = Math.Abs((int)pcm);
        if (abs > 32767) abs = 32767;

        int exp = 7;
        for (int expMask = 0x4000; (abs & expMask) == 0 && exp > 0; exp--, expMask >>= 1) { }

        int mantissa = (abs >> (exp == 0 ? 4 : exp + 3)) & 0x0F;
        byte encoded = (byte)((exp << 4) | mantissa);
        return (byte)(encoded ^ mask);
    }
}
