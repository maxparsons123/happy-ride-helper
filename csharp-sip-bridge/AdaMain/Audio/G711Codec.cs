namespace AdaMain.Audio;

/// <summary>
/// G.711 μ-law (PCMU) and A-law (PCMA) codec implementation.
/// </summary>
public sealed class G711MuLawCodec : IAudioCodec
{
    public string Name => "PCMU";
    public int PayloadType => 0;
    public byte SilenceByte => 0xFF;
    
    private static readonly short[] _muLawToLinear = CreateMuLawDecodeTable();
    private static readonly byte[] _linearToMuLaw = CreateMuLawEncodeTable();
    
    public byte[] Encode(short[] pcm)
    {
        var result = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            result[i] = LinearToMuLaw(pcm[i]);
        return result;
    }
    
    public short[] Decode(byte[] g711)
    {
        var result = new short[g711.Length];
        for (int i = 0; i < g711.Length; i++)
            result[i] = _muLawToLinear[g711[i]];
        return result;
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
    
    private static short[] CreateMuLawDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int v = ~i;
            int sign = v & 0x80;
            int exponent = (v >> 4) & 0x07;
            int mantissa = v & 0x0F;
            int sample = ((mantissa << 3) + 0x84) << exponent;
            sample -= 0x84;
            table[i] = (short)(sign != 0 ? -sample : sample);
        }
        return table;
    }
    
    private static byte[] CreateMuLawEncodeTable()
    {
        var table = new byte[65536];
        for (int i = 0; i < 65536; i++)
            table[i] = LinearToMuLaw((short)(i - 32768));
        return table;
    }
}

public sealed class G711ALawCodec : IAudioCodec
{
    public string Name => "PCMA";
    public int PayloadType => 8;
    public byte SilenceByte => 0xD5;
    
    private static readonly short[] _aLawToLinear = CreateALawDecodeTable();
    
    public byte[] Encode(short[] pcm)
    {
        var result = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            result[i] = LinearToALaw(pcm[i]);
        return result;
    }
    
    public short[] Decode(byte[] g711)
    {
        var result = new short[g711.Length];
        for (int i = 0; i < g711.Length; i++)
            result[i] = _aLawToLinear[g711[i]];
        return result;
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
    
    private static short[] CreateALawDecodeTable()
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

/// <summary>
/// Factory for creating codec instances.
/// </summary>
public static class G711CodecFactory
{
    public static IAudioCodec Create(string codecName) => codecName.ToUpperInvariant() switch
    {
        "PCMU" or "ULAW" or "G711U" => new G711MuLawCodec(),
        "PCMA" or "ALAW" or "G711A" => new G711ALawCodec(),
        _ => new G711ALawCodec() // Default to A-law
    };
}
