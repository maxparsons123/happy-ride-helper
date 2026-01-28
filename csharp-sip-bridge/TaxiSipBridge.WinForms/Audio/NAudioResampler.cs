using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TaxiSipBridge.Audio;

/// <summary>
/// High-quality audio resampler using NAudio's WDL resampler.
/// Includes proper anti-aliasing for downsampling (24kHz → 8kHz).
/// </summary>
public class NAudioResampler
{
    /// <summary>
    /// Resample PCM16 audio using NAudio's WDL resampler (high quality with anti-aliasing).
    /// </summary>
    public static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (input.Length == 0 || fromRate == toRate)
            return input;

        // Convert shorts to bytes for NAudio
        var inputBytes = new byte[input.Length * 2];
        Buffer.BlockCopy(input, 0, inputBytes, 0, inputBytes.Length);

        // Create wave format and memory stream
        var inputFormat = new WaveFormat(fromRate, 16, 1);
        using var inputStream = new RawSourceWaveStream(new MemoryStream(inputBytes), inputFormat);
        
        // Use WDL resampler (high quality with proper anti-aliasing)
        var resampler = new WdlResamplingSampleProvider(inputStream.ToSampleProvider(), toRate);
        
        // Calculate expected output length
        double ratio = (double)toRate / fromRate;
        int expectedOutputSamples = (int)(input.Length * ratio);
        
        // Read resampled audio
        var outputBuffer = new float[expectedOutputSamples + 100]; // Small buffer for any rounding
        int samplesRead = resampler.Read(outputBuffer, 0, outputBuffer.Length);
        
        // Convert float samples back to short
        var output = new short[samplesRead];
        for (int i = 0; i < samplesRead; i++)
        {
            output[i] = (short)Math.Clamp(outputBuffer[i] * 32767f, short.MinValue, short.MaxValue);
        }
        
        return output;
    }

    /// <summary>
    /// Resample PCM16 bytes using NAudio's WDL resampler.
    /// </summary>
    public static byte[] ResampleBytes(byte[] inputBytes, int fromRate, int toRate)
    {
        if (inputBytes.Length == 0 || fromRate == toRate)
            return inputBytes;

        var inputFormat = new WaveFormat(fromRate, 16, 1);
        using var inputStream = new RawSourceWaveStream(new MemoryStream(inputBytes), inputFormat);
        
        var resampler = new WdlResamplingSampleProvider(inputStream.ToSampleProvider(), toRate);
        
        double ratio = (double)toRate / fromRate;
        int expectedOutputSamples = (int)((inputBytes.Length / 2) * ratio);
        
        var outputBuffer = new float[expectedOutputSamples + 100];
        int samplesRead = resampler.Read(outputBuffer, 0, outputBuffer.Length);
        
        var outputBytes = new byte[samplesRead * 2];
        for (int i = 0; i < samplesRead; i++)
        {
            short sample = (short)Math.Clamp(outputBuffer[i] * 32767f, short.MinValue, short.MaxValue);
            outputBytes[i * 2] = (byte)(sample & 0xFF);
            outputBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        
        return outputBytes;
    }
}

/// <summary>
/// Continuous NAudio resampler that maintains state across frames.
/// Prevents glitches at frame boundaries for streaming audio.
/// </summary>
public class ContinuousNAudioResampler : IDisposable
{
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly double _ratio;
    private readonly List<short> _inputBuffer = new();
    private readonly int _minInputSamples;
    private bool _disposed;

    public ContinuousNAudioResampler(int inputRate, int outputRate)
    {
        _inputRate = inputRate;
        _outputRate = outputRate;
        _ratio = (double)outputRate / inputRate;
        // Buffer at least 2 frames worth for smooth resampling
        _minInputSamples = inputRate / 1000 * 40; // 40ms
    }

    /// <summary>
    /// Process a frame of audio, returning resampled output.
    /// Maintains state across calls for glitch-free streaming.
    /// </summary>
    public short[] Process(short[] input)
    {
        if (input.Length == 0)
            return Array.Empty<short>();

        if (_inputRate == _outputRate)
            return input;

        // Add new samples to buffer
        _inputBuffer.AddRange(input);

        // Need minimum samples for quality resampling
        if (_inputBuffer.Count < _minInputSamples)
            return Array.Empty<short>();

        // Resample accumulated buffer
        var inputArray = _inputBuffer.ToArray();
        var resampled = NAudioResampler.Resample(inputArray, _inputRate, _outputRate);

        // Keep last few samples for continuity
        int keepSamples = _inputRate / 1000 * 10; // Keep 10ms
        if (_inputBuffer.Count > keepSamples)
        {
            _inputBuffer.RemoveRange(0, _inputBuffer.Count - keepSamples);
        }

        return resampled;
    }

    /// <summary>
    /// Flush any remaining audio in the buffer.
    /// </summary>
    public short[] Flush()
    {
        if (_inputBuffer.Count == 0)
            return Array.Empty<short>();

        var remaining = _inputBuffer.ToArray();
        _inputBuffer.Clear();
        
        if (_inputRate == _outputRate)
            return remaining;

        return NAudioResampler.Resample(remaining, _inputRate, _outputRate);
    }

    public void Reset()
    {
        _inputBuffer.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inputBuffer.Clear();
        GC.SuppressFinalize(this);
    }
}
