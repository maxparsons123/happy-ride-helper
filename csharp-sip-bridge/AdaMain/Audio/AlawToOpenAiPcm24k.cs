namespace AdaMain.Audio;

/// <summary>
/// Converts 8kHz A-law RTP audio to 24kHz PCM16 bytes for OpenAI Realtime API.
/// Uses G711 A-law decode → DC blocker → AudioResampler (linear interpolation 8k→24k).
/// </summary>
public sealed class AlawToOpenAiPcm24k
{
    private static readonly G711ALawCodec _alaw = new();

    public byte[] Process(byte[] alaw)
    {
        // 1️⃣ Decode A-law → PCM16 @ 8k
        short[] pcm8k = _alaw.Decode(alaw);

        // 2️⃣ DC blocker (simple & safe)
        RemoveDcOffset(pcm8k);

        // 3️⃣ Resample 8k → 24k (returns little-endian PCM16 bytes)
        return AudioResampler.Resample8kTo24k(pcm8k);
    }

    private static void RemoveDcOffset(short[] samples)
    {
        if (samples.Length == 0) return;
        long sum = 0;
        foreach (var s in samples) sum += s;
        short dc = (short)(sum / samples.Length);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(samples[i] - dc);
    }
}
