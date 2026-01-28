using System;
using System.Runtime.InteropServices;

namespace TaxiSipBridge;

/// <summary>
/// High-quality audio resampler using libspeexdsp.
/// SpeexDSP is known for excellent audio quality, used by many audio applications.
/// Quality levels: 0 (fastest) to 10 (best quality).
/// </summary>
public class SpeexDspResampler : IDisposable
{
    private IntPtr _resampler;
    private readonly uint _inRate;
    private readonly uint _outRate;
    private readonly int _quality;
    private bool _disposed;

    // libspeexdsp P/Invoke declarations
    private const string SPEEXDSP_LIB = "libspeexdsp";

    [DllImport(SPEEXDSP_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr speex_resampler_init(uint nb_channels, uint in_rate, uint out_rate, int quality, out int err);

    [DllImport(SPEEXDSP_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern int speex_resampler_process_int(IntPtr st, uint channel_index, short[] input, ref uint in_len, short[] output, ref uint out_len);

    [DllImport(SPEEXDSP_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern void speex_resampler_destroy(IntPtr st);

    [DllImport(SPEEXDSP_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern int speex_resampler_reset_mem(IntPtr st);

    [DllImport(SPEEXDSP_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern int speex_resampler_set_quality(IntPtr st, int quality);

    [DllImport(SPEEXDSP_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern int speex_resampler_get_quality(IntPtr st, out int quality);

    /// <summary>
    /// Create a new SpeexDSP resampler.
    /// </summary>
    /// <param name="inRate">Input sample rate (e.g., 24000)</param>
    /// <param name="outRate">Output sample rate (e.g., 8000)</param>
    /// <param name="quality">Quality level 0-10 (default 8 = high quality, good for telephony)</param>
    public SpeexDspResampler(uint inRate, uint outRate, int quality = 8)
    {
        _inRate = inRate;
        _outRate = outRate;
        _quality = Math.Clamp(quality, 0, 10);

        _resampler = speex_resampler_init(1, inRate, outRate, _quality, out int err);
        if (_resampler == IntPtr.Zero || err != 0)
        {
            throw new InvalidOperationException($"Failed to initialize SpeexDSP resampler: error {err}");
        }
    }

    /// <summary>
    /// Resample audio samples.
    /// </summary>
    /// <param name="input">Input samples at source sample rate</param>
    /// <returns>Output samples at target sample rate</returns>
    public short[] Resample(short[] input)
    {
        if (_disposed || _resampler == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(SpeexDspResampler));

        // Calculate expected output length
        int expectedOutLen = (int)((long)input.Length * _outRate / _inRate) + 16; // +16 for safety
        var output = new short[expectedOutLen];

        uint inLen = (uint)input.Length;
        uint outLen = (uint)output.Length;

        int err = speex_resampler_process_int(_resampler, 0, input, ref inLen, output, ref outLen);
        if (err != 0)
        {
            throw new InvalidOperationException($"SpeexDSP resampling failed: error {err}");
        }

        // Trim to actual output length
        if (outLen < output.Length)
        {
            var trimmed = new short[outLen];
            Array.Copy(output, trimmed, outLen);
            return trimmed;
        }

        return output;
    }

    /// <summary>
    /// Resample to a specific output length (pads or trims as needed).
    /// </summary>
    public short[] Resample(short[] input, int outputLen)
    {
        var resampled = Resample(input);
        
        if (resampled.Length == outputLen)
            return resampled;

        var result = new short[outputLen];
        Array.Copy(resampled, result, Math.Min(resampled.Length, outputLen));
        return result;
    }

    /// <summary>
    /// Reset resampler state (call between utterances or calls).
    /// </summary>
    public void Reset()
    {
        if (_resampler != IntPtr.Zero)
        {
            speex_resampler_reset_mem(_resampler);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_resampler != IntPtr.Zero)
            {
                speex_resampler_destroy(_resampler);
                _resampler = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }

    ~SpeexDspResampler()
    {
        Dispose();
    }
}

/// <summary>
/// Static helper for SpeexDSP resampling with automatic instance management.
/// </summary>
public static class SpeexDspResamplerHelper
{
    private static SpeexDspResampler? _resampler24kTo8k;
    private static SpeexDspResampler? _resampler8kTo24k;
    private static SpeexDspResampler? _resampler24kTo16k;
    private static SpeexDspResampler? _resampler16kTo24k;
    private static readonly object _lock = new();
    private static bool _available = true;

    /// <summary>
    /// Check if libspeexdsp is available.
    /// </summary>
    public static bool IsAvailable => _available;

    /// <summary>
    /// Resample 24kHz to 8kHz using SpeexDSP.
    /// </summary>
    public static short[] Resample24kTo8k(short[] input)
    {
        lock (_lock)
        {
            try
            {
                _resampler24kTo8k ??= new SpeexDspResampler(24000, 8000, 8);
                return _resampler24kTo8k.Resample(input);
            }
            catch (DllNotFoundException)
            {
                _available = false;
                throw;
            }
        }
    }

    /// <summary>
    /// Resample 8kHz to 24kHz using SpeexDSP.
    /// </summary>
    public static short[] Resample8kTo24k(short[] input)
    {
        lock (_lock)
        {
            try
            {
                _resampler8kTo24k ??= new SpeexDspResampler(8000, 24000, 8);
                return _resampler8kTo24k.Resample(input);
            }
            catch (DllNotFoundException)
            {
                _available = false;
                throw;
            }
        }
    }

    /// <summary>
    /// Resample 24kHz to 16kHz using SpeexDSP.
    /// </summary>
    public static short[] Resample24kTo16k(short[] input)
    {
        lock (_lock)
        {
            try
            {
                _resampler24kTo16k ??= new SpeexDspResampler(24000, 16000, 8);
                return _resampler24kTo16k.Resample(input);
            }
            catch (DllNotFoundException)
            {
                _available = false;
                throw;
            }
        }
    }

    /// <summary>
    /// Resample 16kHz to 24kHz using SpeexDSP.
    /// </summary>
    public static short[] Resample16kTo24k(short[] input)
    {
        lock (_lock)
        {
            try
            {
                _resampler16kTo24k ??= new SpeexDspResampler(16000, 24000, 8);
                return _resampler16kTo24k.Resample(input);
            }
            catch (DllNotFoundException)
            {
                _available = false;
                throw;
            }
        }
    }

    /// <summary>
    /// Reset all resampler states.
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _resampler24kTo8k?.Reset();
            _resampler8kTo24k?.Reset();
            _resampler24kTo16k?.Reset();
            _resampler16kTo24k?.Reset();
        }
    }

    /// <summary>
    /// Dispose all resamplers.
    /// </summary>
    public static void Dispose()
    {
        lock (_lock)
        {
            _resampler24kTo8k?.Dispose();
            _resampler8kTo24k?.Dispose();
            _resampler24kTo16k?.Dispose();
            _resampler16kTo24k?.Dispose();
            _resampler24kTo8k = null;
            _resampler8kTo24k = null;
            _resampler24kTo16k = null;
            _resampler16kTo24k = null;
        }
    }
}
