namespace AdaSdkModel.Audio.Processing;

/// <summary>
/// Applies volume boost to G.711 A-law audio in-place.
/// </summary>
public static class ALawGain
{
    private static readonly short[] DecodeTable = CreateDecodeTable();

    public static void ApplyInPlace(byte[] alawData, float gain)
    {
        if (alawData == null || alawData.Length == 0) return;
        if (Math.Abs(gain - 1.0f) < 0.01f) return;

        for (int i = 0; i < alawData.Length; i++)
        {
            short pcm = DecodeTable[alawData[i]];
            int amplified = (int)(pcm * gain);
            if (amplified > 32635) amplified = 32635;
            else if (amplified < -32635) amplified = -32635;
            alawData[i] = LinearToALaw((short)amplified);
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
            int sample = exponent == 0
                ? (mantissa << 4) + 8
                : ((mantissa << 4) + 0x108) << (exponent - 1);
            table[i] = (short)(sign != 0 ? sample : -sample);
        }
        return table;
    }
}
