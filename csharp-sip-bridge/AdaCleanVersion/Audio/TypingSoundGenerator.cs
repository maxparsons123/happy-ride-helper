using System;

namespace AdaCleanVersion.Audio;

/// <summary>
/// Generates subtle, rhythmic keyboard clicking sounds in G.711 format.
/// Used during "thinking" pauses to give a discreet impression Ada is processing.
/// Supports both µ-law (PCMU) and A-law (PCMA) encoding.
/// </summary>
public sealed class TypingSoundGenerator
{
    private const int FRAME_SIZE = 160;

    private const int TAP_SAMPLES_MIN = 8;
    private const int TAP_SAMPLES_MAX = 12;
    private const double TAP_AMPLITUDE = 1200;
    private const double DECAY_RATE = 0.65;

    private const int CLICK_SPACING_MIN = 5;
    private const int CLICK_SPACING_MAX = 8;
    private const int BURST_MIN_CLICKS = 2;
    private const int BURST_MAX_CLICKS = 4;
    private const int PAUSE_MIN_FRAMES = 20;
    private const int PAUSE_MAX_FRAMES = 35;

    private enum State { InBurst, BetweenClicks, Pausing }

    private readonly Random _rng = new();
    private readonly G711CodecType _codec;
    private State _state;
    private int _framesRemaining;
    private int _clicksRemainingInBurst;
    private int _tapSamplesRemaining;
    private double _tapCurrentAmplitude;

    private readonly byte[] _frame = new byte[FRAME_SIZE];

    public TypingSoundGenerator(G711CodecType codec = G711CodecType.PCMU)
    {
        _codec = codec;
        _state = State.Pausing;
        _framesRemaining = _rng.Next(5, 12);
    }

    public byte[] NextFrame()
    {
        Span<short> pcm = stackalloc short[FRAME_SIZE];
        pcm.Clear();

        for (int i = 0; i < FRAME_SIZE; i++)
        {
            if (i == 0 && _tapSamplesRemaining <= 0)
                AdvanceState();

            if (_tapSamplesRemaining > 0)
            {
                double noise = _rng.NextDouble() * 2.0 - 1.0;
                pcm[i] = (short)(_tapCurrentAmplitude * noise);
                _tapCurrentAmplitude *= DECAY_RATE;
                _tapSamplesRemaining--;
            }
        }

        // Encode PCM16 → G.711
        for (int i = 0; i < FRAME_SIZE; i++)
            _frame[i] = G711Codec.Encode(pcm[i], _codec);

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
                    _clicksRemainingInBurst = _rng.Next(BURST_MIN_CLICKS, BURST_MAX_CLICKS + 1);
                    _state = State.InBurst;
                    FireTap();
                }
                break;

            case State.InBurst:
                if (_clicksRemainingInBurst > 0)
                {
                    _state = State.BetweenClicks;
                    _framesRemaining = _rng.Next(CLICK_SPACING_MIN, CLICK_SPACING_MAX + 1);
                }
                else
                {
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
        _tapCurrentAmplitude = TAP_AMPLITUDE * (0.80 + _rng.NextDouble() * 0.40);
        _clicksRemainingInBurst--;
    }

    public void Reset()
    {
        _state = State.Pausing;
        _framesRemaining = _rng.Next(3, 8);
        _tapSamplesRemaining = 0;
        _tapCurrentAmplitude = 0;
        _clicksRemainingInBurst = 0;
    }
}
