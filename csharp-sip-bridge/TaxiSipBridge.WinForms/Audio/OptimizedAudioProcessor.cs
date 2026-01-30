namespace TaxiSipBridge.Audio;

/// <summary>
/// Optimized audio processor for telephony → OpenAI pipeline.
/// Applies pre-emphasis, linear interpolation resampling, and dynamic normalization.
/// </summary>
public class OptimizedAudioProcessor
{
    private const double PreEmphasisAlpha = 0.97; // Standard coefficient for speech clarity
    private double _lastSample = 0;

    /// <summary>
    /// Processes raw telephony PCM16 (8kHz) into OpenAI-ready PCM16 (24kHz).
    /// </summary>
    public byte[] PrepareForOpenAI(byte[] inputBytes, int inputRate = 8000, int targetRate = 24000)
    {
        // 1. Convert bytes to 16-bit PCM shorts
        short[] pcmData = AudioCodecs.BytesToShorts(inputBytes);

        // 2. Apply Pre-emphasis Filter (improves clarity of consonants)
        float[] emphasized = ApplyPreEmphasis(pcmData);

        // 3. Resample using linear interpolation (smoother than point replication)
        short[] resampled = Resample(emphasized, inputRate, targetRate);

        // 4. Normalize (ensures consistent volume for the model)
        Normalize(resampled);

        return AudioCodecs.ShortsToBytes(resampled);
    }

    /// <summary>
    /// Processes raw telephony PCM16 shorts into OpenAI-ready PCM16 bytes.
    /// </summary>
    public byte[] PrepareForOpenAI(short[] pcmData, int inputRate = 8000, int targetRate = 24000)
    {
        // 1. Apply Pre-emphasis Filter
        float[] emphasized = ApplyPreEmphasis(pcmData);

        // 2. Resample using linear interpolation
        short[] resampled = Resample(emphasized, inputRate, targetRate);

        // 3. Normalize
        Normalize(resampled);

        return AudioCodecs.ShortsToBytes(resampled);
    }

    /// <summary>
    /// Reset the pre-emphasis filter state (call between calls/sessions).
    /// </summary>
    public void Reset()
    {
        _lastSample = 0;
    }

    private float[] ApplyPreEmphasis(short[] samples)
    {
        float[] filtered = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double currentSample = samples[i];
            // Equation: y[n] = x[n] - alpha * x[n-1]
            filtered[i] = (float)(currentSample - (PreEmphasisAlpha * _lastSample));
            _lastSample = currentSample;
        }
        return filtered;
    }

    private short[] Resample(float[] input, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate)
        {
            // No resampling needed
            var output = new short[input.Length];
            for (int i = 0; i < input.Length; i++)
                output[i] = (short)Math.Clamp(input[i], short.MinValue, short.MaxValue);
            return output;
        }

        double ratio = (double)sourceRate / targetRate;
        int outputLength = (int)(input.Length / ratio);
        short[] result = new short[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double pos = i * ratio;
            int index = (int)pos;
            double fraction = pos - index;

            if (index + 1 < input.Length)
            {
                // Linear interpolation between two samples
                float val = input[index] * (1.0f - (float)fraction) + input[index + 1] * (float)fraction;
                result[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
            }
            else
            {
                result[i] = (short)Math.Clamp(input[index], short.MinValue, short.MaxValue);
            }
        }

        return result;
    }

    private void Normalize(short[] samples)
    {
        if (samples.Length == 0) return;

        float max = 0;
        foreach (var s in samples)
            if (Math.Abs(s) > max) max = Math.Abs(s);

        if (max > 0)
        {
            // Dynamic target: boost quiet audio, soft-limit loud audio
            float target = max < 15000 ? 28000f : Math.Min(30000f, max * 1.1f);
            float multiplier = target / max;

            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)Math.Clamp(samples[i] * multiplier, short.MinValue, short.MaxValue);
        }
    }
}
