namespace AdaAudioPipe.Processing;

/// <summary>
/// PCM resampling utilities for telephony ↔ AI conversion.
/// </summary>
public static class PcmResampler
{
    /// <summary>
    /// Downsample PCM16 24kHz mono → PCM16 16kHz mono (linear interpolation).
    /// Used for Simli avatar lip-sync feed.
    /// </summary>
    public static byte[] Downsample24kTo16k(byte[] pcm24k)
    {
        if (pcm24k == null || pcm24k.Length < 4) return Array.Empty<byte>();

        int inSamples = pcm24k.Length / 2;
        var input = new short[inSamples];
        Buffer.BlockCopy(pcm24k, 0, input, 0, pcm24k.Length);

        int outSamples = inSamples * 2 / 3; // 24k → 16k
        var output = new short[outSamples];

        const double ratio = 1.5; // 24/16
        for (int i = 0; i < outSamples; i++)
        {
            double src = i * ratio;
            int idx = (int)src;
            double frac = src - idx;

            short s1 = input[Math.Min(idx, inSamples - 1)];
            short s2 = input[Math.Min(idx + 1, inSamples - 1)];
            output[i] = (short)(s1 + (s2 - s1) * frac);
        }

        var outBytes = new byte[outSamples * 2];
        Buffer.BlockCopy(output, 0, outBytes, 0, outBytes.Length);
        return outBytes;
    }

    /// <summary>
    /// Upsample PCM16 8kHz → PCM16 24kHz (linear interpolation).
    /// </summary>
    public static byte[] Upsample8kTo24k(short[] pcm8k)
    {
        if (pcm8k == null || pcm8k.Length == 0) return Array.Empty<byte>();

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

        var last = pcm8k[^1];
        var lastIdx = (pcm8k.Length - 1) * 3;
        pcm24k[lastIdx] = last;
        pcm24k[lastIdx + 1] = last;
        pcm24k[lastIdx + 2] = last;

        var bytes = new byte[pcm24k.Length * 2];
        Buffer.BlockCopy(pcm24k, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
