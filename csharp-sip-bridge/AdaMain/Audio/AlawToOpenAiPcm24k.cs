using AdaMain.Audio;

namespace AdaMain.Audio;

/// <summary>
/// Converts 8kHz A-law RTP audio to 24kHz PCM16 for OpenAI Realtime API.
/// Uses Speex resampler (quality 8) with DC offset removal.
/// </summary>
public sealed class AlawToOpenAiPcm24k
{
    private readonly SpeexResampler _resampler;

    public AlawToOpenAiPcm24k()
    {
        // 8k → 24k, mono, quality 8 (best for voice)
        _resampler = new SpeexResampler(
            channels: 1,
            inRate: 8000,
            outRate: 24000,
            quality: 8
        );
    }

    public byte[] Process(byte[] alaw)
    {
        // 1️⃣ Decode A-law → PCM16 @ 8k
        short[] pcm8k = AudioCodecs.ALawDecode(alaw);

        // 2️⃣ DC blocker (simple & safe)
        RemoveDcOffset(pcm8k);

        // 3️⃣ Resample → 24k
        short[] pcm24k = _resampler.Process(pcm8k);

        // 4️⃣ Convert to bytes (little-endian PCM16)
        return AudioCodecs.ShortsToBytes(pcm24k);
    }

    private static void RemoveDcOffset(short[] samples)
    {
        long sum = 0;
        foreach (var s in samples) sum += s;
        short dc = (short)(sum / samples.Length);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(samples[i] - dc);
    }
}
