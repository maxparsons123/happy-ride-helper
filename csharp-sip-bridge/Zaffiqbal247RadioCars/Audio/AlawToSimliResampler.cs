namespace Zaffiqbal247RadioCars.Audio;

/// <summary>
/// Converts G.711 A-law (8kHz) frames to PCM16 (16kHz) for Simli avatar lip-sync.
/// Uses linear interpolation for the 2× upsample — simple, low-latency, no dependencies.
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

        // Step 2: Upsample 8kHz → 16kHz (2× linear interpolation)
        var samples16k = new short[samples8k.Length * 2];
        for (int i = 0; i < samples8k.Length - 1; i++)
        {
            samples16k[i * 2] = samples8k[i];
            samples16k[i * 2 + 1] = (short)((samples8k[i] + samples8k[i + 1]) / 2);
        }

        // Last sample: duplicate
        samples16k[(samples8k.Length - 1) * 2] = samples8k[^1];
        samples16k[(samples8k.Length - 1) * 2 + 1] = samples8k[^1];

        // Step 3: Convert to byte[]
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
