namespace AdaMain.Audio;

/// <summary>
/// Audio resampling utilities for telephony ↔ AI conversion.
/// </summary>
public static class AudioResampler
{
    /// <summary>
    /// Resample 8kHz PCM16 to 24kHz using linear interpolation.
    /// </summary>
    public static byte[] Resample8kTo24k(short[] pcm8k)
    {
        if (pcm8k.Length == 0) return Array.Empty<byte>();
        
        // 3x upsampling with linear interpolation
        var pcm24k = new short[pcm8k.Length * 3];
        
        for (int i = 0; i < pcm8k.Length - 1; i++)
        {
            var s0 = pcm8k[i];
            var s1 = pcm8k[i + 1];
            var idx = i * 3;
            
            pcm24k[idx] = s0;
            pcm24k[idx + 1] = (short)((s0 * 2 + s1) / 3);
            pcm24k[idx + 2] = (short)((s0 + s1 * 2) / 3);
        }
        
        // Handle last sample
        var last = pcm8k[^1];
        var lastIdx = (pcm8k.Length - 1) * 3;
        pcm24k[lastIdx] = last;
        pcm24k[lastIdx + 1] = last;
        pcm24k[lastIdx + 2] = last;
        
        return ShortsToBytes(pcm24k);
    }
    
    /// <summary>
    /// Resample 24kHz PCM16 to 8kHz using 3-tap FIR filter.
    /// </summary>
    public static short[] Resample24kTo8k(byte[] pcm24kBytes)
    {
        if (pcm24kBytes.Length < 2) return Array.Empty<short>();
        
        var pcm24k = BytesToShorts(pcm24kBytes);
        var len = pcm24k.Length / 3;
        var pcm8k = new short[len];
        
        // 3-tap weighted average: 0.25, 0.5, 0.25 for smoother telephony audio
        for (int i = 0; i < len; i++)
        {
            int idx = i * 3;
            if (idx + 2 < pcm24k.Length)
            {
                pcm8k[i] = (short)(
                    pcm24k[idx] * 0.25f + 
                    pcm24k[idx + 1] * 0.5f + 
                    pcm24k[idx + 2] * 0.25f);
            }
            else
            {
                pcm8k[i] = pcm24k[idx];
            }
        }
        
        return pcm8k;
    }
    
    public static byte[] ShortsToBytes(short[] shorts)
    {
        var bytes = new byte[shorts.Length * 2];
        Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
        return bytes;
    }
    
    public static short[] BytesToShorts(byte[] bytes)
    {
        var shorts = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, shorts, 0, bytes.Length);
        return shorts;
    }
}
