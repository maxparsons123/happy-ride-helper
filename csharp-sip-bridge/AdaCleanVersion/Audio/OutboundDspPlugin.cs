using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace AdaCleanVersion.Audio;

/// <summary>
/// Outbound DSP processor for AI → caller audio.
/// Ported from AdaSdkModel AudioProcessorPlugin.
/// Features: de-essing, anti-lisp, warmth (2nd harmonic), soft clip, slew limiting, dither.
/// 
/// NOT wired in yet — call ProcessAlawFrame() to apply DSP to raw A-law frames
/// when ready to integrate with the G711RtpPlayout pipeline.
/// </summary>
public sealed class OutboundDspPlugin : IDisposable
{
    // ── Configuration ──
    private const float DEESS_THRESHOLD = 0.10f;
    private const float DEESS_RATIO = 0.30f;
    private const float PREEMPH_CORRECTION = 0.94f;
    private const float LOWPASS_COEFF = 0.12f;
    private const float SOFT_CLIP_GAIN = 1.05f;
    private const float HARMONIC_AMOUNT = 0.015f;
    private const float DC_BLOCK_COEFF = 0.998f;
    private const float SLEW_MAX = 0.20f;

    // ── DSP state (persistent across frames for smoothness) ──
    private float _lowpassState;
    private float _dcBlockState;
    private float _preemphState;
    private float _deessEnvelope;
    private float _lastInput;
    private float _lastOutput;
    private readonly Random _dither = new();

    private bool _disposed;

    /// <summary>
    /// Process an A-law frame in-place: decode → DSP → re-encode.
    /// Each frame is expected to be 160 bytes (20ms at 8kHz).
    /// </summary>
    public void ProcessAlawFrameInPlace(byte[] alawFrame)
    {
        if (alawFrame == null || alawFrame.Length == 0) return;

        for (int i = 0; i < alawFrame.Length; i++)
        {
            // Decode A-law → PCM16 → float [-1..1]
            short pcm = G711Codec.ALawDecode(alawFrame[i]);
            float sample = pcm / 32768f;

            // Apply DSP chain
            float processed = ProcessSample(sample);

            // Float → PCM16 → A-law with dither
            short outPcm = (short)Math.Clamp((int)(processed * 32767f), short.MinValue, short.MaxValue);
            alawFrame[i] = DitheredALawEncode(outPcm);
        }
    }

    /// <summary>
    /// Process a batch of A-law bytes (any length) and return a new processed array.
    /// </summary>
    public byte[] ProcessAlawBytes(byte[] alawBytes)
    {
        if (alawBytes == null || alawBytes.Length == 0) return alawBytes;

        var result = new byte[alawBytes.Length];
        Buffer.BlockCopy(alawBytes, 0, result, 0, alawBytes.Length);
        ProcessAlawFrameInPlace(result);
        return result;
    }

    /// <summary>Reset all DSP state (e.g. on call boundary).</summary>
    public void Reset()
    {
        _lowpassState = 0;
        _dcBlockState = 0;
        _preemphState = 0;
        _deessEnvelope = 0;
        _lastInput = 0;
        _lastOutput = 0;
    }

    // ── DSP Chain ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ProcessSample(float input)
    {
        // DC blocking
        float dcBlocked = input - _dcBlockState;
        _dcBlockState = input * (1 - DC_BLOCK_COEFF) + _dcBlockState * DC_BLOCK_COEFF;

        // Pre-emphasis correction (undo telephone pre-emphasis)
        float deemph = dcBlocked + PREEMPH_CORRECTION * _preemphState;
        _preemphState = deemph;

        // Lowpass smoothing
        _lowpassState += LOWPASS_COEFF * (deemph - _lowpassState);
        float filtered = _lowpassState;

        // De-essing (reduce sibilance)
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

        // Warmth (subtle 2nd harmonic)
        float harmonic = deessed * deessed * Math.Sign(deessed);
        float warmed = deessed + HARMONIC_AMOUNT * harmonic;

        // Soft clipping
        float boosted = warmed * SOFT_CLIP_GAIN;
        float clipped = FastTanh(boosted);

        // Slew rate limiting (anti-click)
        float delta = clipped - _lastOutput;
        if (delta > SLEW_MAX) delta = SLEW_MAX + (delta - SLEW_MAX) * 0.1f;
        if (delta < -SLEW_MAX) delta = -SLEW_MAX + (delta + SLEW_MAX) * 0.1f;

        _lastOutput += delta;
        return _lastOutput;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte DitheredALawEncode(short pcm)
    {
        float d1 = _dither.NextSingle();
        float d2 = _dither.NextSingle();
        float dithered = pcm + (d1 - d2) * 0.5f;
        int sample = (int)Math.Clamp(dithered, -32768f, 32767f);
        return G711Codec.ALawEncode((short)sample);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FastTanh(float x)
    {
        float x2 = x * x;
        return x * (27 + x2) / (27 + 9 * x2);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
