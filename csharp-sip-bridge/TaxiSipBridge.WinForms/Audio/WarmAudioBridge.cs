using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TaxiSipBridge.Audio;

/// <summary>
/// Converts OpenAI 24kHz PCM16 audio to warm 8kHz A-law RTP frames.
/// Uses a proper Butterworth anti-alias filter BEFORE decimation to eliminate
/// the "crispy" aliasing artifacts, plus a gentle low-mid presence boost.
/// </summary>
public sealed class WarmAudioBridge
{
    private const int MAX_QUEUE_FRAMES = 10; // ~200ms buffer
    private const int FRAME_SIZE = 160;      // 20ms @ 8kHz
    private const byte ALAW_SILENCE = 0xD5;

    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();

    // --- BiQuad Anti-Alias Filter (Direct Form II Transposed) ---
    // Butterworth LPF: Fc=3400Hz at Fs=24kHz
    // Removes harsh high frequencies BEFORE decimation (the key to warm audio)
    private float _z1, _z2;
    private const float B0 = 0.1311f, B1 = 0.2622f, B2 = 0.1311f;
    private const float A1 = -0.7478f, A2 = 0.2722f;

    // --- Warmth EQ: Gentle low-mid boost (Direct Form II Transposed) ---
    // Peak EQ centered ~800Hz, +2dB, Q=0.8 at Fs=24kHz
    // Adds body/warmth to the voice without muddiness
    private float _w1, _w2;
    private const float WB0 = 1.0458f, WB1 = -1.8468f, WB2 = 0.8131f;
    private const float WA1 = -1.8468f, WA2 = 0.8589f;

    /// <summary>Queue of 160-byte A-law RTP frames ready for sending.</summary>
    public ConcurrentQueue<byte[]> OutboundQueue => _outboundQueue;

    /// <summary>
    /// Process a base64-encoded audio delta from OpenAI (24kHz PCM16)
    /// and produce warm 8kHz A-law RTP frames.
    /// </summary>
    public void ProcessAudioDelta(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return;

        try
        {
            // 1. Decode base64 → raw PCM16 bytes @ 24kHz
            byte[] pcm24kBytes = Convert.FromBase64String(base64);

            // 2. Reinterpret as shorts (zero-copy)
            ReadOnlySpan<short> samples24k = MemoryMarshal.Cast<byte, short>(pcm24kBytes);

            // 3. Allocate output buffer for 8kHz (3:1 decimation)
            int outLength = samples24k.Length / 3;
            if (outLength == 0) return;

            Span<short> pcm8k = outLength <= 1024
                ? stackalloc short[outLength]
                : new short[outLength];

            // 4. Filter ALL 24kHz samples, then decimate
            // This is critical: filtering at 24kHz kills aliasing before downsampling
            int outIdx = 0;
            for (int i = 0; i < samples24k.Length; i++)
            {
                float x = samples24k[i];

                // Stage 1: Warmth EQ (low-mid boost ~800Hz)
                float warm = WB0 * x + _w1;
                _w1 = WB1 * x - WA1 * warm + _w2;
                _w2 = WB2 * x - WA2 * warm;

                // Stage 2: Anti-alias low-pass (3400Hz cutoff)
                float y = B0 * warm + _z1;
                _z1 = B1 * warm - A1 * y + _z2;
                _z2 = B2 * warm - A2 * y;

                // Only keep every 3rd sample (decimate 24k → 8k)
                if (i % 3 == 0 && outIdx < pcm8k.Length)
                {
                    // Soft clamp to prevent hard clipping
                    pcm8k[outIdx++] = (short)Math.Clamp(y, -30000f, 30000f);
                }
            }

            // 5. Encode PCM16 → G.711 A-law
            byte[] alawData = ALawEncode(pcm8k[..outIdx]);

            // 6. Frame into 20ms (160-byte) RTP chunks
            for (int i = 0; i < alawData.Length; i += FRAME_SIZE)
            {
                byte[] frame = new byte[FRAME_SIZE];
                int count = Math.Min(FRAME_SIZE, alawData.Length - i);
                Buffer.BlockCopy(alawData, i, frame, 0, count);

                // Pad with A-law silence
                if (count < FRAME_SIZE)
                    Array.Fill(frame, ALAW_SILENCE, count, FRAME_SIZE - count);

                // Drop oldest if queue full (prevents memory growth)
                while (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                    _outboundQueue.TryDequeue(out _);

                _outboundQueue.Enqueue(frame);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarmAudioBridge] Error: {ex.Message}");
        }
    }

    /// <summary>Reset filter state (call on new connection/call).</summary>
    public void Reset()
    {
        _z1 = _z2 = 0;
        _w1 = _w2 = 0;
        while (_outboundQueue.TryDequeue(out _)) { }
    }

    #region G.711 A-law Encoding

    public static byte[] ALawEncode(ReadOnlySpan<short> pcm)
    {
        byte[] encoded = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            encoded[i] = LinearToALaw(pcm[i]);
        return encoded;
    }

    private static byte LinearToALaw(short pcm)
    {
        int sign = (~pcm >> 8) & 0x80;
        if (sign == 0) pcm = (short)-pcm;
        if (pcm > 32635) pcm = 32635;

        int exponent = 7;
        for (int mask = 0x4000; (pcm & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }

        int mantissa = (pcm >> (exponent == 0 ? 4 : exponent + 3)) & 0x0F;
        return (byte)((sign | (exponent << 4) | mantissa) ^ 0x55);
    }

    #endregion
}
