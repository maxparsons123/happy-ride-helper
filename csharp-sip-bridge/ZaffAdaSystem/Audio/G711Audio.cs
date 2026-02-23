namespace ZaffAdaSystem.Audio;

/// <summary>G.711 A-law ↔ μ-law transcoding.</summary>
public static class G711Transcode
{
    private static readonly byte[] _muToA = BuildMuToATable();
    private static readonly byte[] _aToMu = BuildAToMuTable();

    public static byte[] MuLawToALaw(byte[] mulaw)
    {
        var result = new byte[mulaw.Length];
        for (int i = 0; i < mulaw.Length; i++) result[i] = _muToA[mulaw[i]];
        return result;
    }

    public static byte[] ALawToMuLaw(byte[] alaw)
    {
        var result = new byte[alaw.Length];
        for (int i = 0; i < alaw.Length; i++) result[i] = _aToMu[alaw[i]];
        return result;
    }

    private static byte[] BuildMuToATable()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            short pcm = MuLawDecode((byte)i);
            table[i] = ALawVolumeBoost_LinearToALaw(pcm);
        }
        return table;
    }

    private static byte[] BuildAToMuTable()
    {
        var table = new byte[256];
        var decode = CreateALawDecodeTable();
        for (int i = 0; i < 256; i++)
            table[i] = LinearToMuLaw(decode[i]);
        return table;
    }

    private static short MuLawDecode(byte mulaw)
    {
        int v = ~mulaw;
        int sign = v & 0x80;
        int exponent = (v >> 4) & 0x07;
        int mantissa = v & 0x0F;
        int sample = ((mantissa << 3) + 0x84) << exponent;
        sample -= 0x84;
        return (short)(sign != 0 ? -sample : sample);
    }

    private static byte LinearToMuLaw(short sample)
    {
        const int BIAS = 0x84;
        const int MAX = 32635;
        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        if (sample > MAX) sample = MAX;
        sample += BIAS;
        int exponent = 7;
        for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    private static byte ALawVolumeBoost_LinearToALaw(short sample)
    {
        int sign = (~sample >> 8) & 0x80;
        if (sign == 0) sample = (short)-sample;
        if (sample > 32635) sample = 32635;
        int exponent = 7;
        for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        int mantissa = (sample >> (exponent == 0 ? 4 : exponent + 3)) & 0x0F;
        return (byte)((sign | (exponent << 4) | mantissa) ^ 0x55);
    }

    private static short[] CreateALawDecodeTable()
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
