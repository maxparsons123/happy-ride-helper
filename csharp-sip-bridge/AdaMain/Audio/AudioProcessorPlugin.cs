using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AdaMain.Audio;

/// <summary>
/// Self-contained audio processor plugin for OpenAI → G.711 A-law
/// Features: 24-bit decode, de-essing, anti-lisp, warmth, soft clip, dither
/// </summary>
public sealed class AudioProcessorPlugin : IDisposable
{
    // Configuration
    private const float INPUT_SCALE_24BIT = 1.0f / 8388608.0f;
    private const float DEESS_THRESHOLD = 0.10f;
    private const float DEESS_RATIO = 0.30f;
    private const float PREEMPH_CORRECTION = 0.94f;
    private const float LOWPASS_COEFF = 0.12f;
    private const float SOFT_CLIP_GAIN = 0.85f;
    private const float OUTPUT_GAIN = 0.70f;  // Reduce hot output to prevent clipping/distortion
    private const float HARMONIC_AMOUNT = 0.015f;
    private const float DC_BLOCK_COEFF = 0.998f;
    private const float SLEW_MAX = 0.20f;

    // FIR lowpass coefficients for 24kHz → 8kHz
    private static readonly float[] FirLowpass24kTo8k = new[]
    {
        -0.018f, 0.012f, 0.128f, 0.244f, 0.268f, 0.244f, 0.128f, 0.012f, -0.018f
    };

    // DSP state (persistent for smoothness)
    private float _lowpassState;
    private float _dcBlockState;
    private float _preemphState;
    private float _deessEnvelope;
    private float _lastInput;
    private float _lastOutput;
    private readonly Random _dither = new();

    // Working buffers (rented from pool to avoid GC)
    private float[] _firBuffer;
    private short[] _pcm8kBuffer;
    private byte[] _alawBuffer;

    public AudioProcessorPlugin()
    {
        _firBuffer = ArrayPool<float>.Shared.Rent(16384);
        _pcm8kBuffer = ArrayPool<short>.Shared.Rent(8192);
        _alawBuffer = ArrayPool<byte>.Shared.Rent(8192);
    }

    /// <summary>
    /// Process base64 audio from OpenAI → A-law frames
    /// </summary>
    public void ProcessAudioDelta(string base64Audio, ConcurrentQueue<byte[]> outputQueue, int maxQueueFrames = 900)
    {
        if (string.IsNullOrEmpty(base64Audio)) return;

        try
        {
            var pcm24kBytes = Convert.FromBase64String(base64Audio);
            ProcessToALaw(pcm24kBytes, out var totalSamples);

            for (int i = 0; i < totalSamples; i += 160)
            {
                int count = Math.Min(160, totalSamples - i);
                var frame = new byte[160];
                Buffer.BlockCopy(_alawBuffer, i, frame, 0, count);

                if (count < 160)
                    Array.Fill(frame, (byte)0xD5, count, 160 - count);

                while (outputQueue.Count >= maxQueueFrames)
                    outputQueue.TryDequeue(out _);

                outputQueue.Enqueue(frame);
            }
        }
        catch { /* Let caller handle errors */ }
    }

    /// <summary>
    /// Process raw PCM bytes → A-law frames into the provided queue.
    /// </summary>
    public void ProcessPcmBytes(byte[] pcm24kBytes, ConcurrentQueue<byte[]> outputQueue, int maxQueueFrames = 900)
    {
        if (pcm24kBytes == null || pcm24kBytes.Length == 0) return;

        ProcessToALaw(pcm24kBytes, out var totalSamples);

        for (int i = 0; i < totalSamples; i += 160)
        {
            int count = Math.Min(160, totalSamples - i);
            var frame = new byte[160];
            Buffer.BlockCopy(_alawBuffer, i, frame, 0, count);

            if (count < 160)
                Array.Fill(frame, (byte)0xD5, count, 160 - count);

            while (outputQueue.Count >= maxQueueFrames)
                outputQueue.TryDequeue(out _);

            outputQueue.Enqueue(frame);
        }
    }

    private void ProcessToALaw(byte[] pcm24kBytes, out int totalSamples)
    {
        totalSamples = 0;

        float[] pcm24k;
        if (pcm24kBytes.Length % 3 == 0 && pcm24kBytes.Length % 4 != 0)
            pcm24k = Decode24BitPacked(pcm24kBytes);
        else if (pcm24kBytes.Length % 4 == 0)
            pcm24k = Decode32Bit(pcm24kBytes);
        else
            pcm24k = Decode16Bit(pcm24kBytes);

        int firRadius = FirLowpass24kTo8k.Length / 2;
        int outIndex = 0;

        for (int i = firRadius; i < pcm24k.Length - firRadius; i++)
        {
            float acc = 0f;
            for (int t = -firRadius; t <= firRadius; t++)
                acc += pcm24k[i + t] * FirLowpass24kTo8k[t + firRadius];

            if ((i - firRadius) % 3 != 0) continue;
            if (outIndex >= _pcm8kBuffer.Length) break;

            float processed = ProcessSampleRounded(acc);
            _pcm8kBuffer[outIndex] = (short)Math.Clamp((int)processed, short.MinValue, short.MaxValue);
            outIndex++;
        }

        for (int i = 0; i < outIndex; i++)
            _alawBuffer[i] = QuantizeToALaw(_pcm8kBuffer[i]);

        totalSamples = outIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ProcessSampleRounded(float input)
    {
        float dcBlocked = input - _dcBlockState;
        _dcBlockState = input * (1 - DC_BLOCK_COEFF) + _dcBlockState * DC_BLOCK_COEFF;

        float deemph = dcBlocked + PREEMPH_CORRECTION * _preemphState;
        _preemphState = deemph;

        _lowpassState += LOWPASS_COEFF * (deemph - _lowpassState);
        float filtered = _lowpassState;

        float derivative = Math.Abs(filtered - _lastInput);
        _lastInput = filtered;
        _deessEnvelope = 0.92f * _deessEnvelope + 0.08f * derivative;

        float deessed = filtered;
        if (_deessEnvelope > DEESS_THRESHOLD)
        {
            float excess = _deessEnvelope - DEESS_THRESHOLD;
            float gain = 1.0f - (excess / _deessEnvelope) * (1.0f - DEESS_RATIO);
            deessed *= Math.Max(0.6f, gain);
        }

        float harmonic = deessed * deessed * Math.Sign(deessed);
        float warmed = deessed + HARMONIC_AMOUNT * harmonic;

        float boosted = warmed * SOFT_CLIP_GAIN;
        float clipped = FastTanh(boosted);

        float delta = clipped - _lastOutput;
        if (delta > SLEW_MAX) delta = SLEW_MAX + (delta - SLEW_MAX) * 0.1f;
        if (delta < -SLEW_MAX) delta = -SLEW_MAX + (delta + SLEW_MAX) * 0.1f;

        _lastOutput += delta;
        return _lastOutput * OUTPUT_GAIN * 32767f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte QuantizeToALaw(short pcm)
    {
        float d1 = _dither.NextSingle();
        float d2 = _dither.NextSingle();
        float dithered = pcm + (d1 - d2) * 0.5f;
        int sample = (int)Math.Clamp(dithered, -32768f, 32767f);
        return ALawEncode((short)sample);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ALawEncode(short pcm)
    {
        int mask = pcm < 0 ? 0xD5 : 0x55;
        int abs = Math.Abs(pcm);
        abs = (abs + 8) >> 4;
        int seg = 0;
        for (int i = 0; i < 8 && abs > 0; i++, abs >>= 1) seg++;
        int aval = seg >= 8 ? 0x7F : (seg << 4) | (abs & 0x0F);
        return (byte)(aval ^ mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FastTanh(float x)
    {
        float x2 = x * x;
        return x * (27 + x2) / (27 + 9 * x2);
    }

    private float[] Decode24BitPacked(byte[] data)
    {
        int samples = data.Length / 3;
        var result = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            int off = i * 3;
            int s = data[off] | (data[off + 1] << 8) | (data[off + 2] << 16);
            if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
            result[i] = s * INPUT_SCALE_24BIT;
        }
        return result;
    }

    private float[] Decode32Bit(byte[] data)
    {
        int samples = data.Length / 4;
        var result = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float f = BitConverter.ToSingle(data, i * 4);
            if (Math.Abs(f) <= 1.0f && Math.Abs(f) >= 0.0001f)
                result[i] = f;
            else
            {
                int s = BitConverter.ToInt32(data, i * 4);
                result[i] = s / 2147483648f;
            }
        }
        return result;
    }

    private float[] Decode16Bit(byte[] data)
    {
        int samples = data.Length / 2;
        var result = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
            result[i] = s / 32768f;
        }
        return result;
    }

    public void Reset()
    {
        _lowpassState = 0;
        _dcBlockState = 0;
        _preemphState = 0;
        _deessEnvelope = 0;
        _lastInput = 0;
        _lastOutput = 0;
    }

    public void Dispose()
    {
        ArrayPool<float>.Shared.Return(_firBuffer);
        ArrayPool<short>.Shared.Return(_pcm8kBuffer);
        ArrayPool<byte>.Shared.Return(_alawBuffer);
    }
}
