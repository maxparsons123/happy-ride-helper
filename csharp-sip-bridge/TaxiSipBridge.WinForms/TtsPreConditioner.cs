using System;

namespace TaxiSipBridge;

/// <summary>
/// Streaming preprocessor for OpenAI Realtime 24kHz output → clean 8kHz PCM for PCMA encoding.
/// Pipeline: DC removal → 3.4kHz low-pass → normalize → soft-clip → downsample
/// </summary>
public class TtsPreConditioner
{
    // OpenAI Realtime outputs 24kHz, telephony needs 8kHz
    public const int InputSampleRate = 24000;
    public const int OutputSampleRate = 8000;
    public const int FrameMs = 20;

    public const int InputSamplesPerFrame = InputSampleRate * FrameMs / 1000;   // 480
    public const int OutputSamplesPerFrame = OutputSampleRate * FrameMs / 1000; // 160

    public const int InputBytesPerFrame = InputSamplesPerFrame * 2;   // 960 bytes
    public const int OutputBytesPerFrame = OutputSamplesPerFrame * 2; // 320 bytes

    // Low-pass filter state for continuity across frames
    private double _lpPrev;
    private bool _lpPrevInit;
    
    // DC removal state (stateful high-pass)
    private double _dcAccum;
    private const double DcAlpha = 0.995; // High-pass pole for DC blocking

    /// <summary>
    /// Process a single 20ms 24kHz PCM16 mono frame → 20ms 8kHz PCM16 frame.
    /// Output is ready for PCMA (G.711 A-Law) encoding.
    /// </summary>
    public short[] ProcessFrame(short[] samples24k)
    {
        if (samples24k == null || samples24k.Length == 0)
            return Array.Empty<short>();

        // Pad or truncate to exact frame size
        short[] frame = EnsureFrameSize(samples24k, InputSamplesPerFrame);

        // 1) Remove DC offset (stateful high-pass)
        RemoveDc(frame);

        // 2) Low-pass @ 3.4kHz for telephony band limiting
        LowpassInPlace(frame, 3400.0, InputSampleRate);

        // 3) Normalize to 90% and soft-clip
        NormalizeAndClip(frame);

        // 4) Downsample 24kHz → 8kHz (factor 3)
        short[] samples8k = Downsample24kTo8k(frame);

        return samples8k;
    }

    /// <summary>
    /// Process raw bytes from OpenAI Realtime → ready for PCMA encoding.
    /// </summary>
    public byte[] ProcessFrameBytes(byte[] inputBytes)
    {
        short[] samples24k = BytesToPcm(inputBytes);
        short[] samples8k = ProcessFrame(samples24k);
        return PcmToBytes(samples8k);
    }

    /// <summary>
    /// Reset filter state (call between calls/sessions).
    /// </summary>
    public void Reset()
    {
        _lpPrev = 0;
        _lpPrevInit = false;
        _dcAccum = 0;
    }

    /// <summary>
    /// Ensure frame is exactly the expected size.
    /// </summary>
    private static short[] EnsureFrameSize(short[] samples, int targetSize)
    {
        if (samples.Length == targetSize)
            return samples;

        short[] result = new short[targetSize];
        int copyLen = Math.Min(samples.Length, targetSize);
        Array.Copy(samples, result, copyLen);
        return result;
    }

    /// <summary>
    /// Stateful DC blocking filter (high-pass).
    /// </summary>
    private void RemoveDc(short[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double input = samples[i];
            double output = input - _dcAccum;
            _dcAccum = DcAlpha * _dcAccum + (1 - DcAlpha) * input;
            samples[i] = (short)Math.Clamp(output, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// One-pole low-pass filter @ 3.4kHz with state across frames.
    /// Classic telephony band limiting.
    /// </summary>
    private void LowpassInPlace(short[] samples, double cutoffHz, int sampleRate)
    {
        double rc = 1.0 / (2 * Math.PI * cutoffHz);
        double dt = 1.0 / sampleRate;
        double alpha = dt / (rc + dt);

        for (int i = 0; i < samples.Length; i++)
        {
            double cur = samples[i];

            if (!_lpPrevInit)
            {
                _lpPrev = cur;
                _lpPrevInit = true;
            }

            _lpPrev = _lpPrev + alpha * (cur - _lpPrev);
            samples[i] = (short)Math.Clamp(_lpPrev, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// Normalize to 90% of full-scale and soft-clip peaks.
    /// </summary>
    private static void NormalizeAndClip(short[] samples)
    {
        // Find peak (handle short.MinValue edge case)
        int peak = 0;
        foreach (short s in samples)
        {
            int abs = s == short.MinValue ? 32768 : Math.Abs(s);
            if (abs > peak) peak = abs;
        }

        if (peak > 0)
        {
            double scale = 0.90 * 32767.0 / peak;
            
            // Only normalize if significantly off target
            if (scale < 0.95 || scale > 1.1)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    double v = samples[i] * scale;
                    samples[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                }
            }
        }

        // Soft-clip any remaining peaks
        for (int i = 0; i < samples.Length; i++)
            samples[i] = SoftClip(samples[i]);
    }

    /// <summary>
    /// Gentle soft-clip above 90% using exponential knee.
    /// Prevents hard clipping artifacts in PCMA encoding.
    /// </summary>
    private static short SoftClip(short x)
    {
        const double threshold = 0.90;
        double v = x / 32768.0;
        double absV = Math.Abs(v);

        if (absV <= threshold)
            return x;

        double sign = v >= 0 ? 1.0 : -1.0;
        double over = absV - threshold;
        double range = 1.0 - threshold;

        // Exponential compression of the last 10%
        double y = threshold + range * (1.0 - Math.Exp(-over / range));

        return (short)(sign * y * 32767.0);
    }

    /// <summary>
    /// Downsample 24kHz → 8kHz (factor 3) with averaging.
    /// Audio is already low-passed at 3.4kHz so aliasing is minimal.
    /// </summary>
    private static short[] Downsample24kTo8k(short[] input)
    {
        int outLen = input.Length / 3;
        short[] output = new short[outLen];

        for (int i = 0; i < outLen; i++)
        {
            int idx = i * 3;
            // Average 3 samples for smoother decimation
            int sum = input[idx];
            if (idx + 1 < input.Length) sum += input[idx + 1];
            if (idx + 2 < input.Length) sum += input[idx + 2];
            output[i] = (short)(sum / 3);
        }

        return output;
    }

    /// <summary>
    /// Convert 16-bit little-endian PCM bytes to short[].
    /// </summary>
    public static short[] BytesToPcm(byte[] buf)
    {
        if (buf == null || buf.Length == 0)
            return Array.Empty<short>();
            
        short[] pcm = new short[buf.Length / 2];
        Buffer.BlockCopy(buf, 0, pcm, 0, buf.Length);
        return pcm;
    }

    /// <summary>
    /// Convert short[] samples to 16-bit little-endian PCM bytes.
    /// </summary>
    public static byte[] PcmToBytes(short[] samples)
    {
        if (samples == null || samples.Length == 0)
            return Array.Empty<byte>();
            
        byte[] buf = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, buf, 0, buf.Length);
        return buf;
    }
}
