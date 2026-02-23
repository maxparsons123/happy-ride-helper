// Last updated: 2026-02-23 (v3.1 - removed ThreadStatic, stateless resampling)
namespace AdaSdkModel.Audio;

/// <summary>
/// Converts G.711 A-law (8kHz) frames to PCM16 (16kHz) for Simli avatar lip-sync.
/// Fully stateless — safe to call from any thread (ThreadPool).
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

        // Step 2: Upsample 8kHz → 16kHz (stateless — no cross-frame state)
        var samples16k = Upsample2x(samples8k);

        // Step 3: Convert to byte[]
        var result = new byte[samples16k.Length * 2];
        Buffer.BlockCopy(samples16k, 0, result, 0, result.Length);
        return result;
    }

    /// <summary>2× upsample with linear interpolation (stateless).</summary>
    private static short[] Upsample2x(short[] input)
    {
        var output = new short[input.Length * 2];

        // First sample: duplicate (no previous frame context available)
        output[0] = input[0];
        output[1] = input[0];

        for (int i = 1; i < input.Length; i++)
        {
            output[i * 2] = (short)((input[i - 1] + input[i]) / 2);
            output[i * 2 + 1] = input[i];
        }

        return output;
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
