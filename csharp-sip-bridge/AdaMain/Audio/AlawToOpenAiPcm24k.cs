using System.Runtime.InteropServices;

namespace AdaMain.Audio;

/// <summary>
/// Converts 8kHz A-law RTP audio to 24kHz PCM16 bytes for OpenAI Realtime API.
/// Uses native SpeexDSP resampler (quality 8) for high-fidelity upsampling.
/// </summary>
public sealed class AlawToOpenAiPcm24k : IDisposable
{
    private IntPtr _resampler;

    [DllImport("speexdsp", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr speex_resampler_init(
        uint channels, uint inRate, uint outRate, int quality, out int err);

    [DllImport("speexdsp", CallingConvention = CallingConvention.Cdecl)]
    private static extern void speex_resampler_destroy(IntPtr state);

    [DllImport("speexdsp", CallingConvention = CallingConvention.Cdecl)]
    private static extern int speex_resampler_process_int(
        IntPtr state, uint channel_index,
        short[] input, ref uint in_len,
        short[] output, ref uint out_len);

    public AlawToOpenAiPcm24k(int quality = 8)
    {
        _resampler = speex_resampler_init(
            channels: 1, inRate: 8000, outRate: 24000,
            quality: quality, out int err);

        if (err != 0 || _resampler == IntPtr.Zero)
            throw new Exception($"SpeexDSP init failed (err={err})");
    }

    /// <summary>
    /// Converts 8kHz A-law → 24kHz PCM16 mono for OpenAI Realtime STT.
    /// </summary>
    public byte[] Process(byte[] alaw8k)
    {
        if (alaw8k == null || alaw8k.Length == 0)
            return Array.Empty<byte>();

        // 1️⃣ Decode A-law → PCM16 @ 8kHz
        short[] pcm8k = DecodeALaw(alaw8k);

        // 2️⃣ DC blocker
        RemoveDcOffset(pcm8k);

        // 3️⃣ Speex resample 8k → 24k
        uint inLen = (uint)pcm8k.Length;
        uint outLen = inLen * 3;
        short[] pcm24k = new short[outLen];

        int res = speex_resampler_process_int(
            _resampler, 0, pcm8k, ref inLen, pcm24k, ref outLen);

        if (res != 0)
            throw new Exception($"SpeexDSP process failed (err={res})");

        // 4️⃣ PCM16 → byte[]
        byte[] result = new byte[outLen * sizeof(short)];
        Buffer.BlockCopy(pcm24k, 0, result, 0, result.Length);
        return result;
    }

    private static readonly IAudioCodec _alawCodec = new G711ALawCodec();

    private static short[] DecodeALaw(byte[] alaw)
    {
        return _alawCodec.Decode(alaw);
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

    public void Dispose()
    {
        if (_resampler != IntPtr.Zero)
        {
            speex_resampler_destroy(_resampler);
            _resampler = IntPtr.Zero;
        }
    }
}
