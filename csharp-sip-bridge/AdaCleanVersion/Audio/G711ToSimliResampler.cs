namespace AdaCleanVersion.Audio;

/// <summary>
/// Converts G.711 (8kHz) frames to PCM16 (16kHz) for Simli avatar lip-sync.
/// Uses linear interpolation for the 2× upsample — simple, low-latency, no dependencies.
/// Supports both µ-law (PCMU) and A-law (PCMA) input.
/// </summary>
public static class G711ToSimliResampler
{
    /// <summary>
    /// Decode G.711 bytes to PCM16 and upsample from 8kHz to 16kHz.
    /// Returns a byte[] containing 16-bit little-endian PCM at 16kHz.
    /// </summary>
    public static byte[] Convert(byte[] g711Frame, G711CodecType codec)
    {
        if (g711Frame == null || g711Frame.Length == 0)
            return Array.Empty<byte>();

        // Step 1: Decode G.711 → PCM16 at 8kHz
        var samples8k = new short[g711Frame.Length];
        for (int i = 0; i < g711Frame.Length; i++)
            samples8k[i] = G711Codec.Decode(g711Frame[i], codec);

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

    /// <summary>Legacy compat: assumes PCMU.</summary>
    public static byte[] Convert(byte[] mulawFrame) => Convert(mulawFrame, G711CodecType.PCMU);
}
