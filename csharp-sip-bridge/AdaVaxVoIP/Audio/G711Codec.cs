namespace AdaVaxVoIP.Audio;

/// <summary>
/// G.711 A-law codec with lookup tables for zero-allocation encode/decode.
/// </summary>
public static class G711Codec
{
    public const byte ALawSilence = 0xD5;

    private static readonly byte[] LinearToALawTable;
    private static readonly short[] ALawToLinearTable;

    static G711Codec()
    {
        LinearToALawTable = new byte[65536];
        for (int i = 0; i < 65536; i++)
            LinearToALawTable[i] = EncodeALaw((short)i);

        ALawToLinearTable = new short[256];
        for (int i = 0; i < 256; i++)
            ALawToLinearTable[i] = DecodeALaw((byte)i);
    }

    public static byte[] PcmToALaw(byte[] pcmData)
    {
        int samples = pcmData.Length / 2;
        byte[] alaw = new byte[samples];
        for (int i = 0; i < samples; i++)
        {
            ushort idx = (ushort)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            alaw[i] = LinearToALawTable[idx];
        }
        return alaw;
    }

    public static byte[] ALawToPcm(byte[] alawData)
    {
        byte[] pcm = new byte[alawData.Length * 2];
        for (int i = 0; i < alawData.Length; i++)
        {
            short sample = ALawToLinearTable[alawData[i]];
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return pcm;
    }

    private static byte EncodeALaw(short pcm)
    {
        const int cClip = 32635;
        int mask;
        if (pcm < 0)
        {
            pcm = (short)(-pcm - 1);
            mask = 0x7F;
        }
        else
        {
            mask = 0xFF;
        }

        pcm = (short)Math.Min(pcm, cClip);
        pcm += 0x84;

        int seg;
        if (pcm >= 0x4000) seg = 7;
        else if (pcm >= 0x2000) seg = 6;
        else if (pcm >= 0x1000) seg = 5;
        else if (pcm >= 0x0800) seg = 4;
        else if (pcm >= 0x0400) seg = 3;
        else if (pcm >= 0x0200) seg = 2;
        else if (pcm >= 0x0100) seg = 1;
        else seg = 0;

        int aval = (seg << 4) | ((pcm >> (seg + 3)) & 0x0F);
        return (byte)(aval ^ mask);
    }

    private static short DecodeALaw(byte alaw)
    {
        alaw ^= 0x55;
        int sign = (alaw & 0x80) != 0 ? -1 : 1;
        int segment = (alaw >> 4) & 0x07;
        int value = (alaw & 0x0F) << 4 | 0x08;
        if (segment > 0)
            value = (value + 0x100) << (segment - 1);
        return (short)(sign * value);
    }
}
