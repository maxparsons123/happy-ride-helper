namespace TaxiSipBridge;

/// <summary>
/// Audio codec utilities for µ-law encoding/decoding and resampling.
/// Used for converting between OpenAI Realtime API format (24kHz PCM16) and SIP telephony (8kHz µ-law).
/// </summary>
public static class AudioCodecs
{
    /// <summary>
    /// Decode µ-law (G.711) to PCM16 samples.
    /// </summary>
    public static short[] MuLawDecode(byte[] data)
    {
        var pcm = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            int mulaw = ~data[i];
            int sign = (mulaw & 0x80) != 0 ? -1 : 1;
            int exponent = (mulaw >> 4) & 0x07;
            int mantissa = mulaw & 0x0F;
            pcm[i] = (short)(sign * (((mantissa << 3) + 0x84) << exponent) - 0x84);
        }
        return pcm;
    }

    /// <summary>
    /// Encode PCM16 samples to µ-law (G.711).
    /// </summary>
    public static byte[] MuLawEncode(short[] pcm)
    {
        var ulaw = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            int s = pcm[i];
            int mask = 0x80, seg = 8;
            if (s < 0) { s = -s; mask = 0x00; }
            s += 0x84;
            if (s > 0x7FFF) s = 0x7FFF;
            for (int j = 0x4000; (s & j) == 0 && seg > 0; j >>= 1) seg--;
            ulaw[i] = (byte)~(mask | (seg << 4) | ((s >> (seg + 3)) & 0x0F));
        }
        return ulaw;
    }

    /// <summary>
    /// Simple linear resampling between sample rates.
    /// </summary>
    public static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;
        
        double ratio = (double)fromRate / toRate;
        int outputLength = (int)(input.Length / ratio);
        var output = new short[outputLength];
        
        for (int i = 0; i < output.Length; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;
            
            // Linear interpolation for better quality
            if (srcIndex + 1 < input.Length)
            {
                output[i] = (short)(input[srcIndex] * (1 - frac) + input[srcIndex + 1] * frac);
            }
            else if (srcIndex < input.Length)
            {
                output[i] = input[srcIndex];
            }
        }
        
        return output;
    }

    /// <summary>
    /// Convert byte array (little-endian PCM16) to short array.
    /// </summary>
    public static short[] BytesToShorts(byte[] b)
    {
        var s = new short[b.Length / 2];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = (short)(b[i * 2] | (b[i * 2 + 1] << 8));
        }
        return s;
    }

    /// <summary>
    /// Convert short array to byte array (little-endian PCM16).
    /// </summary>
    public static byte[] ShortsToBytes(short[] s)
    {
        var b = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, b, 0, b.Length);
        return b;
    }
}
