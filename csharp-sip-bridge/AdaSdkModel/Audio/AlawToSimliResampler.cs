namespace AdaSdkModel.Audio;

/// <summary>
/// Converts G.711 A-law (8kHz) frames to PCM16 (16kHz) for Simli avatar lip-sync.
/// Uses cubic interpolation for the 2× upsample with a simple low-pass filter
/// to reduce aliasing artifacts (crackling).
/// </summary>
public static class AlawToSimliResampler
{
    /// <summary>
    /// Decode A-law bytes to PCM16 and upsample from 8kHz to 16kHz.
    /// Returns a byte[] containing 16-bit little-endian PCM at 16kHz.
    /// </summary>
    public static byte[] Convert(byte[] alawFrame)
    {
        if (alawFrame == null || alawFrame.Length == 0)
            return Array.Empty<byte>();

        // Step 1: Decode A-law → PCM16 at 8kHz
        var samples8k = new short[alawFrame.Length];
        for (int i = 0; i < alawFrame.Length; i++)
            samples8k[i] = ALawDecode(alawFrame[i]);

        // Step 2: Upsample 8kHz → 16kHz (2× with cubic interpolation)
        var samples16k = new short[samples8k.Length * 2];
        for (int i = 0; i < samples8k.Length; i++)
        {
            // Original sample stays
            samples16k[i * 2] = samples8k[i];

            // Interpolated midpoint using cubic (Catmull-Rom) for smoother result
            int im1 = Math.Max(i - 1, 0);
            int ip1 = Math.Min(i + 1, samples8k.Length - 1);
            int ip2 = Math.Min(i + 2, samples8k.Length - 1);

            float p0 = samples8k[im1];
            float p1 = samples8k[i];
            float p2 = samples8k[ip1];
            float p3 = samples8k[ip2];

            // Catmull-Rom at t=0.5
            float mid = 0.5f * (
                2f * p1 +
                (-p0 + p2) * 0.5f +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * 0.25f +
                (-p0 + 3f * p1 - 3f * p2 + p3) * 0.125f
            );

            samples16k[i * 2 + 1] = (short)Math.Clamp((int)mid, short.MinValue, short.MaxValue);
        }

        // Step 3: Simple 3-tap low-pass to smooth any remaining artifacts
        // [0.25, 0.5, 0.25] kernel - very light, preserves timing
        for (int i = 1; i < samples16k.Length - 1; i++)
        {
            // Only filter the interpolated (odd) samples to avoid smearing originals
            if ((i & 1) == 1)
            {
                int smoothed = (samples16k[i - 1] + 2 * samples16k[i] + samples16k[i + 1]) / 4;
                samples16k[i] = (short)smoothed;
            }
        }

        // Step 4: Convert to byte[]
        var result = new byte[samples16k.Length * 2];
        Buffer.BlockCopy(samples16k, 0, result, 0, result.Length);
        return result;
    }

    /// <summary>ITU-T G.711 A-law decode.</summary>
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
}
