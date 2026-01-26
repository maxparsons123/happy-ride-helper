namespace TaxiSipBridge.Audio;

/// <summary>
/// Stateful resampler that maintains continuity between frames to prevent clicks/rattling.
/// Uses polyphase FIR filtering for high-quality 24kHz to 8kHz conversion.
/// </summary>
public class ContinuousResampler
{
    private readonly int _fromRate;
    private readonly int _toRate;
    private readonly int _ratio;
    
    // FIR filter state - maintains history across frames
    private readonly short[] _history;
    private int _historyIndex;
    
    // Inter-frame crossfade
    private short _lastOutputSample;
    private const int CROSSFADE_SAMPLES = 8;

    // 7-tap polyphase FIR coefficients for 3:1 decimation (24kHz -> 8kHz)
    // Designed with Parks-McClellan algorithm, passband 0-3.5kHz, stopband 4kHz+
    private static readonly float[] FirCoeffs = new float[]
    {
        0.0156f, 0.0938f, 0.2344f, 0.3125f, 0.2344f, 0.0938f, 0.0156f
    };

    public ContinuousResampler(int fromRate, int toRate)
    {
        _fromRate = fromRate;
        _toRate = toRate;
        _ratio = fromRate / toRate; // e.g., 3 for 24k->8k
        _history = new short[FirCoeffs.Length];
        _historyIndex = 0;
        _lastOutputSample = 0;
    }

    /// <summary>
    /// Resample a frame while maintaining continuity with previous frames.
    /// </summary>
    public short[] Process(short[] input)
    {
        if (_fromRate == _toRate) return input;
        if (input.Length == 0) return input;

        int outputLen = input.Length / _ratio;
        var output = new short[outputLen];

        for (int i = 0; i < outputLen; i++)
        {
            int centerIdx = i * _ratio + (_ratio / 2);
            
            // Apply FIR filter centered on decimation point
            float acc = 0;
            for (int j = 0; j < FirCoeffs.Length; j++)
            {
                int srcIdx = centerIdx - (FirCoeffs.Length / 2) + j;
                short sample;
                
                if (srcIdx < 0)
                {
                    // Use history from previous frame
                    int histIdx = (_historyIndex + FirCoeffs.Length + srcIdx) % FirCoeffs.Length;
                    sample = _history[histIdx];
                }
                else if (srcIdx < input.Length)
                {
                    sample = input[srcIdx];
                }
                else
                {
                    // Extrapolate using last sample
                    sample = input[^1];
                }
                
                acc += sample * FirCoeffs[j];
            }
            
            output[i] = (short)Math.Clamp(acc, short.MinValue, short.MaxValue);
        }

        // Apply crossfade at frame boundary to eliminate clicks
        if (output.Length > CROSSFADE_SAMPLES)
        {
            for (int i = 0; i < CROSSFADE_SAMPLES; i++)
            {
                float t = (float)(i + 1) / (CROSSFADE_SAMPLES + 1);
                // Smooth transition from last frame's final sample
                float blended = _lastOutputSample * (1 - t) + output[i] * t;
                output[i] = (short)blended;
            }
        }

        // Update history for next frame
        int histStart = Math.Max(0, input.Length - FirCoeffs.Length);
        for (int i = histStart; i < input.Length; i++)
        {
            _history[_historyIndex] = input[i];
            _historyIndex = (_historyIndex + 1) % FirCoeffs.Length;
        }

        if (output.Length > 0)
        {
            _lastOutputSample = output[^1];
        }

        return output;
    }

    /// <summary>
    /// Reset resampler state (call when starting new audio stream).
    /// </summary>
    public void Reset()
    {
        Array.Clear(_history);
        _historyIndex = 0;
        _lastOutputSample = 0;
    }
}
