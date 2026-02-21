// Last updated: 2026-02-21 (v2.8)
using System;

namespace AdaSdkModel.Audio;

/// <summary>
/// Generates subtle, rhythmic keyboard clicking sounds in A-law format.
/// Used during "thinking" pauses to give a discreet impression Ada is processing.
///
/// v2.0 — RHYTHMIC EDITION:
///   ✅ Burst-then-pause rhythm: 2-4 clicks, then 400-700ms silence — feels like real typing
///   ✅ Much lower amplitude (1200 peak) — barely perceptible, sits under the noise floor
///   ✅ Short, crisp taps (1.0-1.5ms) with fast decay — more click, less thud
///   ✅ Only active after Ada has spoken at least once (gate set externally)
///   ✅ Rhythmically timed: clicks land on beat-like intervals, not random scatter
/// </summary>
public sealed class TypingSoundGenerator
{
    private const int FRAME_SIZE  = 160;        // 20ms @ 8kHz
    private const byte ALAW_SILENCE = 0xD5;

    // ── Tap characteristics — very subtle ──
    private const int    TAP_SAMPLES_MIN  = 8;    // ~1.0ms
    private const int    TAP_SAMPLES_MAX  = 12;   // ~1.5ms
    private const double TAP_AMPLITUDE    = 1200; // Very discreet (was 3000)
    private const double DECAY_RATE       = 0.65; // Faster decay — crisper click

    // ── Rhythm: bursts of clicks separated by longer silences ──
    // Frames = 20ms units
    private const int CLICK_SPACING_MIN   = 5;   // 100ms between clicks in a burst
    private const int CLICK_SPACING_MAX   = 8;   // 160ms between clicks in a burst
    private const int BURST_MIN_CLICKS    = 2;   // Minimum clicks per burst
    private const int BURST_MAX_CLICKS    = 4;   // Maximum clicks per burst
    private const int PAUSE_MIN_FRAMES    = 20;  // 400ms silence between bursts
    private const int PAUSE_MAX_FRAMES    = 35;  // 700ms silence between bursts

    private enum State { InBurst, BetweenClicks, Pausing }

    private readonly Random _rng = new();
    private State  _state = State.Pausing;
    private int    _framesRemaining;
    private int    _clicksRemainingInBurst;
    private int    _tapSamplesRemaining;
    private double _tapCurrentAmplitude;

    // Pre-allocated frame buffer
    private readonly byte[] _frame = new byte[FRAME_SIZE];

    public TypingSoundGenerator()
    {
        // Start with a short delay before first burst
        _state = State.Pausing;
        _framesRemaining = _rng.Next(5, 12); // 100-240ms initial delay
    }

    /// <summary>
    /// Get the next 20ms A-law frame. Returns silence if the burst is pausing.
    /// </summary>
    public byte[] NextFrame()
    {
        Span<short> pcm = stackalloc short[FRAME_SIZE];
        pcm.Clear();

        for (int i = 0; i < FRAME_SIZE; i++)
        {
            // Advance state machine at sample boundary i==0 only
            if (i == 0 && _tapSamplesRemaining <= 0)
            {
                AdvanceState();
            }

            if (_tapSamplesRemaining > 0)
            {
                double noise = _rng.NextDouble() * 2.0 - 1.0;
                pcm[i] = (short)(_tapCurrentAmplitude * noise);
                _tapCurrentAmplitude *= DECAY_RATE;
                _tapSamplesRemaining--;
            }
        }

        for (int i = 0; i < FRAME_SIZE; i++)
            _frame[i] = LinearToALaw(pcm[i]);

        var copy = new byte[FRAME_SIZE];
        Buffer.BlockCopy(_frame, 0, copy, 0, FRAME_SIZE);
        return copy;
    }

    private void AdvanceState()
    {
        switch (_state)
        {
            case State.Pausing:
                _framesRemaining--;
                if (_framesRemaining <= 0)
                {
                    // Start a new burst
                    _clicksRemainingInBurst = _rng.Next(BURST_MIN_CLICKS, BURST_MAX_CLICKS + 1);
                    _state = State.InBurst;
                    FireTap();
                }
                break;

            case State.InBurst:
                // Tap just finished — if more clicks to go, schedule next one
                if (_clicksRemainingInBurst > 0)
                {
                    _state = State.BetweenClicks;
                    _framesRemaining = _rng.Next(CLICK_SPACING_MIN, CLICK_SPACING_MAX + 1);
                }
                else
                {
                    // Burst done — enter pause
                    _state = State.Pausing;
                    _framesRemaining = _rng.Next(PAUSE_MIN_FRAMES, PAUSE_MAX_FRAMES + 1);
                }
                break;

            case State.BetweenClicks:
                _framesRemaining--;
                if (_framesRemaining <= 0)
                {
                    _state = State.InBurst;
                    FireTap();
                }
                break;
        }
    }

    private void FireTap()
    {
        _tapSamplesRemaining = _rng.Next(TAP_SAMPLES_MIN, TAP_SAMPLES_MAX + 1);
        // Vary amplitude ±20% for natural variation
        _tapCurrentAmplitude = TAP_AMPLITUDE * (0.80 + _rng.NextDouble() * 0.40);
        _clicksRemainingInBurst--;
    }

    /// <summary>Reset to initial state (call when a new thinking pause begins).</summary>
    public void Reset()
    {
        _state = State.Pausing;
        _framesRemaining = _rng.Next(3, 8); // Short delay before first burst
        _tapSamplesRemaining = 0;
        _tapCurrentAmplitude = 0;
        _clicksRemainingInBurst = 0;
    }

    private static byte LinearToALaw(short pcm)
    {
        int sign = (~pcm >> 8) & 0x80;
        if (sign == 0) pcm = (short)-pcm;
        if (pcm > 32635) pcm = 32635;

        int exp, mantissa;
        if (pcm >= 256)
        {
            exp = (int)AlawCompressTable[(pcm >> 8) & 0x7F];
            mantissa = (pcm >> (exp + 3)) & 0x0F;
        }
        else
        {
            exp = 0;
            mantissa = pcm >> 4;
        }

        byte alaw = (byte)(sign | (exp << 4) | mantissa);
        return (byte)(alaw ^ 0xD5);
    }

    private static readonly byte[] AlawCompressTable = {
        1,1,2,2,3,3,3,3,
        4,4,4,4,4,4,4,4,
        5,5,5,5,5,5,5,5,
        5,5,5,5,5,5,5,5,
        6,6,6,6,6,6,6,6,
        6,6,6,6,6,6,6,6,
        6,6,6,6,6,6,6,6,
        6,6,6,6,6,6,6,6,
        7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7
    };
}
