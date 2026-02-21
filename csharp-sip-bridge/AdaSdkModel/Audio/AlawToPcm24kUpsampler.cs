// Last updated: 2026-02-21 (v2.8)
namespace AdaSdkModel.Audio;

/// <summary>
/// Converts G.711 A-law 8kHz audio from SIP to PCM16 24kHz for OpenAI.
/// 
/// Pipeline: A-law decode → PCM16 8kHz → Upsample 3× with linear interpolation → PCM16 24kHz
/// 
/// Optional ingress gain is applied in the PCM domain (clean linear math)
/// before upsampling, replacing the old A-law byte manipulation approach.
/// 
/// Thread-safety: stateless — safe to share, but gain is mutable.
/// </summary>
public sealed class AlawToPcm24kUpsampler
{
    private static readonly short[] _decodeTable = CreateDecodeTable();

    private float _ingressGain = 4.0f;

    public float IngressGain
    {
        get => _ingressGain;
        set => _ingressGain = Math.Clamp(value, 0.0f, 10.0f);
    }

    /// <summary>
    /// Convert G.711 A-law bytes (8kHz) to PCM16 24kHz little-endian byte array.
    /// </summary>
    public byte[] Convert(byte[] alawData)
    {
        if (alawData == null || alawData.Length == 0)
            return Array.Empty<byte>();

        // Step 1: Decode A-law → PCM16 at 8kHz + apply gain in PCM domain
        var samples8k = new short[alawData.Length];
        for (int i = 0; i < alawData.Length; i++)
        {
            int pcm = _decodeTable[alawData[i]];
            
            // Apply gain in linear PCM domain (mathematically correct)
            if (Math.Abs(_ingressGain - 1.0f) > 0.01f)
            {
                pcm = (int)(pcm * _ingressGain);
                if (pcm > 32767) pcm = 32767;
                else if (pcm < -32767) pcm = -32767;
            }

            samples8k[i] = (short)pcm;
        }

        // Step 2: Upsample 8kHz → 24kHz (3× with linear interpolation)
        int outLen = samples8k.Length * 3;
        var samples24k = new short[outLen];

        for (int i = 0; i < samples8k.Length; i++)
        {
            short current = samples8k[i];
            short next = (i < samples8k.Length - 1) ? samples8k[i + 1] : current;

            int baseIdx = i * 3;
            samples24k[baseIdx] = current;
            samples24k[baseIdx + 1] = (short)(current + (next - current) / 3);
            samples24k[baseIdx + 2] = (short)(current + 2 * (next - current) / 3);
        }

        // Step 3: Convert to byte[] (little-endian PCM16)
        var result = new byte[samples24k.Length * 2];
        Buffer.BlockCopy(samples24k, 0, result, 0, result.Length);
        return result;
    }

    private static short[] CreateDecodeTable()
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
