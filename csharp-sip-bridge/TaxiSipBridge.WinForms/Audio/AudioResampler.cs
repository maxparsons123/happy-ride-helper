namespace TaxiSipBridge.Audio;

/// <summary>
/// Audio resampler using linear interpolation for telephony-grade quality
/// </summary>
public static class AudioResampler
{
    /// <summary>
    /// Resample PCM16 audio (little-endian bytes) between sample rates
    /// </summary>
    public static byte[] Resample(byte[] pcmData, int fromRate, int toRate)
    {
        if (fromRate == toRate)
            return pcmData;

        // Convert bytes to samples
        var inputSamples = new short[pcmData.Length / 2];
        for (int i = 0; i < inputSamples.Length; i++)
        {
            inputSamples[i] = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
        }

        // Calculate output length
        double ratio = (double)toRate / fromRate;
        int outputLength = (int)(inputSamples.Length * ratio);
        var outputSamples = new short[outputLength];

        // Resample using linear interpolation
        for (int i = 0; i < outputLength; i++)
        {
            double srcPos = i / ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            if (srcIndex >= inputSamples.Length - 1)
            {
                outputSamples[i] = inputSamples[inputSamples.Length - 1];
            }
            else
            {
                // Linear interpolation
                double sample = inputSamples[srcIndex] * (1 - frac) + 
                               inputSamples[srcIndex + 1] * frac;
                outputSamples[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
            }
        }

        // Convert samples back to bytes
        var outputData = new byte[outputSamples.Length * 2];
        for (int i = 0; i < outputSamples.Length; i++)
        {
            outputData[i * 2] = (byte)(outputSamples[i] & 0xFF);
            outputData[i * 2 + 1] = (byte)((outputSamples[i] >> 8) & 0xFF);
        }

        return outputData;
    }

    /// <summary>
    /// Resample short[] samples between sample rates
    /// </summary>
    public static short[] ResampleSamples(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate)
            return input;

        double ratio = (double)toRate / fromRate;
        int outputLength = (int)(input.Length * ratio);
        var output = new short[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double srcPos = i / ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            if (srcIndex >= input.Length - 1)
            {
                output[i] = input[input.Length - 1];
            }
            else
            {
                double sample = input[srcIndex] * (1 - frac) + 
                               input[srcIndex + 1] * frac;
                output[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
            }
        }

        return output;
    }

    /// <summary>
    /// Downsample by integer factor using averaging (better for 24kHz -> 8kHz)
    /// </summary>
    public static byte[] DownsampleByFactor(byte[] pcmData, int factor)
    {
        if (factor <= 1)
            return pcmData;

        int inputSamples = pcmData.Length / 2;
        int outputSamples = inputSamples / factor;
        var output = new byte[outputSamples * 2];

        for (int i = 0; i < outputSamples; i++)
        {
            int sum = 0;
            for (int j = 0; j < factor; j++)
            {
                int idx = (i * factor + j) * 2;
                short sample = (short)(pcmData[idx] | (pcmData[idx + 1] << 8));
                sum += sample;
            }
            short avg = (short)(sum / factor);
            output[i * 2] = (byte)(avg & 0xFF);
            output[i * 2 + 1] = (byte)((avg >> 8) & 0xFF);
        }

        return output;
    }

    /// <summary>
    /// Upsample by integer factor using sample repetition (simple, fast)
    /// </summary>
    public static byte[] UpsampleByFactor(byte[] pcmData, int factor)
    {
        if (factor <= 1)
            return pcmData;

        var output = new byte[pcmData.Length * factor];

        for (int i = 0; i < pcmData.Length / 2; i++)
        {
            byte lo = pcmData[i * 2];
            byte hi = pcmData[i * 2 + 1];

            for (int j = 0; j < factor; j++)
            {
                int outIdx = (i * factor + j) * 2;
                output[outIdx] = lo;
                output[outIdx + 1] = hi;
            }
        }

        return output;
    }
}
