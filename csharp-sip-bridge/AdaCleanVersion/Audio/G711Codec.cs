namespace AdaCleanVersion.Audio;

/// <summary>
/// Shared G.711 codec with both µ-law (PCMU) and A-law (PCMA) encode/decode.
/// Uses lookup tables for zero-allocation hot-path performance.
/// </summary>
public enum G711CodecType { PCMU, PCMA }

public static class G711Codec
{
    // ─── Silence bytes ──────────────────────────────────────
    public const byte MuLawSilence = 0xFF;
    public const byte ALawSilence  = 0xD5;

    // ─── RTP payload types ──────────────────────────────────
    public const int MuLawPayloadType = 0;
    public const int ALawPayloadType  = 8;

    // ─── Lookup tables ──────────────────────────────────────
    private static readonly short[] MuLawDecodeTable = BuildMuLawDecodeTable();
    private static readonly short[] ALawDecodeTable  = BuildALawDecodeTable();

    // ─── Codec-type helpers ─────────────────────────────────

    public static byte SilenceByte(G711CodecType codec) =>
        codec == G711CodecType.PCMA ? ALawSilence : MuLawSilence;

    public static int PayloadType(G711CodecType codec) =>
        codec == G711CodecType.PCMA ? ALawPayloadType : MuLawPayloadType;

    public static G711CodecType Parse(string? name) =>
        string.Equals(name, "PCMA", StringComparison.OrdinalIgnoreCase) ? G711CodecType.PCMA : G711CodecType.PCMU;

    // ─── Unified Encode / Decode ────────────────────────────

    public static byte Encode(short sample, G711CodecType codec) =>
        codec == G711CodecType.PCMA ? ALawEncode(sample) : MuLawEncode(sample);

    public static short Decode(byte encoded, G711CodecType codec) =>
        codec == G711CodecType.PCMA ? ALawDecodeTable[encoded] : MuLawDecodeTable[encoded];

    // ─── Bulk helpers ───────────────────────────────────────

    /// <summary>Encode PCM16 samples → G.711 bytes.</summary>
    public static byte[] EncodeBlock(short[] pcm, G711CodecType codec)
    {
        var result = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            result[i] = Encode(pcm[i], codec);
        return result;
    }

    /// <summary>Decode G.711 bytes → PCM16 samples.</summary>
    public static short[] DecodeBlock(byte[] g711, G711CodecType codec)
    {
        var result = new short[g711.Length];
        for (int i = 0; i < g711.Length; i++)
            result[i] = Decode(g711[i], codec);
        return result;
    }

    // ─── µ-law ──────────────────────────────────────────────

    public static short MuLawDecode(byte mulaw) => MuLawDecodeTable[mulaw];

    public static byte MuLawEncode(short sample)
    {
        const int BIAS = 0x84;
        const int MAX = 32635;
        var sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        if (sample > MAX) sample = MAX;
        sample = (short)(sample + BIAS);
        var exponent = 7;
        for (var mask = 0x4000; (sample & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        var mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)(~(sign | (exponent << 4) | mantissa));
    }

    // ─── A-law ──────────────────────────────────────────────

    public static short ALawDecode(byte alaw) => ALawDecodeTable[alaw];

    public static byte ALawEncode(short sample)
    {
        int mask;
        if (sample < 0)
        {
            sample = (short)Math.Min(-sample, 32767);
            mask = 0x55;
        }
        else
        {
            mask = 0xD5;
        }

        if (sample > 32635) sample = 32635;

        int exponent, mantissa;
        if (sample < 256)
        {
            exponent = 0;
            mantissa = (sample >> 4) & 0x0F;
        }
        else
        {
            exponent = 1;
            for (int tmp = sample >> 5; tmp > 1; tmp >>= 1)
                exponent++;
            mantissa = (sample >> (exponent + 3)) & 0x0F;
        }

        return (byte)((exponent << 4 | mantissa) ^ mask);
    }

    // ─── Table builders ─────────────────────────────────────

    private static short[] BuildMuLawDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            var v = (byte)~i;
            int sign = (v & 0x80) != 0 ? -1 : 1;
            int exp = (v >> 4) & 0x07;
            int mant = v & 0x0F;
            int mag = ((mant << 4) + 0x08) << exp;
            mag -= 0x84;
            table[i] = (short)(sign * mag);
        }
        return table;
    }

    private static short[] BuildALawDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int v = i ^ 0x55;
            int sign = (v & 0x80) != 0 ? 1 : -1;
            int exp = (v >> 4) & 0x07;
            int mant = v & 0x0F;
            int sample;
            if (exp == 0)
                sample = (mant << 4) + 8;
            else
                sample = ((mant << 4) + 0x108) << (exp - 1);
            table[i] = (short)(sign * sample);
        }
        return table;
    }
}
