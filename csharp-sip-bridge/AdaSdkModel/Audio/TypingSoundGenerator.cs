using System;

namespace AdaSdkModel.Audio;

/// <summary>
/// Generates realistic keyboard tapping sounds in A-law format.
/// Used during "thinking" pauses to give the impression Ada is typing/entering info.
/// 
/// Each tap is a short impulse (3-5ms) with exponential decay, randomly spaced
/// at natural typing intervals (60-180ms between keystrokes).
/// </summary>
public sealed class TypingSoundGenerator
{
    private const int FRAME_SIZE = 160;       // 20ms @ 8kHz
    private const int SAMPLE_RATE = 8000;
    private const byte ALAW_SILENCE = 0xD5;

    // Tap characteristics
    private const int TAP_SAMPLES_MIN = 24;   // 3ms minimum tap duration
    private const int TAP_SAMPLES_MAX = 40;   // 5ms maximum tap duration
    private const double TAP_AMPLITUDE = 1800; // Subtle but audible (PCM16 range ±32767)
    private const double DECAY_RATE = 0.85;    // Exponential decay per sample

    // Timing: frames between taps (at 50fps → 60-180ms between keystrokes)
    private const int MIN_FRAMES_BETWEEN_TAPS = 3;  // 60ms
    private const int MAX_FRAMES_BETWEEN_TAPS = 9;  // 180ms

    private readonly Random _rng = new();
    private int _framesUntilNextTap;
    private int _tapSamplesRemaining;
    private double _tapCurrentAmplitude;

    // Pre-allocated frame buffer
    private readonly byte[] _frame = new byte[FRAME_SIZE];

    public TypingSoundGenerator()
    {
        _framesUntilNextTap = 0; // Immediate first tap
        _tapSamplesRemaining = TAP_SAMPLES_MIN;
        _tapCurrentAmplitude = TAP_AMPLITUDE;
    }

    /// <summary>
    /// Get the next 20ms A-law frame containing typing sounds.
    /// Call this once per frame tick during the "thinking" pause.
    /// </summary>
    public byte[] NextFrame()
    {
        // Build PCM16 samples first, then convert to A-law
        Span<short> pcm = stackalloc short[FRAME_SIZE];
        pcm.Clear();

        for (int i = 0; i < FRAME_SIZE; i++)
        {
            // Check if we need to start a new tap
            if (_tapSamplesRemaining <= 0 && i == 0)
            {
                _framesUntilNextTap--;
                if (_framesUntilNextTap <= 0)
                {
                    // Start a new keystroke tap
                    _tapSamplesRemaining = _rng.Next(TAP_SAMPLES_MIN, TAP_SAMPLES_MAX + 1);
                    // Vary amplitude ±30% for natural feel
                    _tapCurrentAmplitude = TAP_AMPLITUDE * (0.7 + _rng.NextDouble() * 0.6);
                    _framesUntilNextTap = _rng.Next(MIN_FRAMES_BETWEEN_TAPS, MAX_FRAMES_BETWEEN_TAPS + 1);
                }
            }

            if (_tapSamplesRemaining > 0)
            {
                // Generate tap: band-limited noise with exponential decay
                double noise = (_rng.NextDouble() * 2.0 - 1.0); // -1 to +1
                pcm[i] = (short)(_tapCurrentAmplitude * noise);
                _tapCurrentAmplitude *= DECAY_RATE;
                _tapSamplesRemaining--;
            }
            // else: pcm[i] stays 0 (silence)
        }

        // Convert PCM16 to A-law
        for (int i = 0; i < FRAME_SIZE; i++)
        {
            _frame[i] = LinearToALaw(pcm[i]);
        }

        var copy = new byte[FRAME_SIZE];
        Buffer.BlockCopy(_frame, 0, copy, 0, FRAME_SIZE);
        return copy;
    }

    /// <summary>
    /// Reset timing state (call when starting a new thinking pause).
    /// </summary>
    public void Reset()
    {
        _framesUntilNextTap = 0;
        _tapSamplesRemaining = TAP_SAMPLES_MIN;
        _tapCurrentAmplitude = TAP_AMPLITUDE;
    }

    /// <summary>
    /// ITU-T G.711 A-law encoder. Converts 16-bit linear PCM to 8-bit A-law.
    /// </summary>
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
        return (byte)(alaw ^ 0xD5); // Toggle even bits
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
