using System;
using System.Runtime.InteropServices;

namespace TaxiSipBridge;

/// <summary>
/// High-quality audio resampler using libsoxr (SoX Resampler Library).
/// Provides professional-grade sample rate conversion with minimal aliasing.
/// 
/// Requires libsoxr native library:
/// - Windows: soxr.dll in application directory
/// - Linux: libsoxr.so (apt install libsoxr0)
/// - macOS: libsoxr.dylib (brew install libsoxr)
/// </summary>
public class SoxrResampler : IDisposable
{
    private IntPtr _soxr = IntPtr.Zero;
    private bool _disposed = false;
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly int _channels;

    // Quality presets
    public enum Quality
    {
        Quick = 0,      // Low quality, fast
        Low = 1,        // 
        Medium = 2,     // Good for speech
        High = 4,       // High quality (default)
        VeryHigh = 6    // Maximum quality
    }

    #region P/Invoke

    private const string SOXR_LIB = "soxr";

    [StructLayout(LayoutKind.Sequential)]
    private struct soxr_io_spec_t
    {
        public uint itype;
        public uint otype;
        public double scale;
        public IntPtr e;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct soxr_quality_spec_t
    {
        public double precision;
        public double phase_response;
        public double passband_end;
        public double stopband_begin;
        public IntPtr e;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct soxr_runtime_spec_t
    {
        public uint log2_min_dft_size;
        public uint log2_large_dft_size;
        public uint coef_size_kbytes;
        public uint num_threads;
        public IntPtr e;
        public uint flags;
    }

    // Data types
    private const uint SOXR_FLOAT32_I = 0;  // float interleaved
    private const uint SOXR_FLOAT64_I = 1;  // double interleaved
    private const uint SOXR_INT32_I = 2;    // int32 interleaved
    private const uint SOXR_INT16_I = 3;    // int16 interleaved

    [DllImport(SOXR_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr soxr_create(
        double input_rate,
        double output_rate,
        uint num_channels,
        out IntPtr error,
        ref soxr_io_spec_t io_spec,
        ref soxr_quality_spec_t quality_spec,
        IntPtr runtime_spec);

    [DllImport(SOXR_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr soxr_process(
        IntPtr resampler,
        IntPtr ibuf,
        UIntPtr ilen,
        out UIntPtr idone,
        IntPtr obuf,
        UIntPtr olen,
        out UIntPtr odone);

    [DllImport(SOXR_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern void soxr_delete(IntPtr resampler);

    [DllImport(SOXR_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr soxr_strerror(IntPtr error);

    [DllImport(SOXR_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern soxr_quality_spec_t soxr_quality_spec(uint recipe, uint flags);

    [DllImport(SOXR_LIB, CallingConvention = CallingConvention.Cdecl)]
    private static extern soxr_io_spec_t soxr_io_spec(uint itype, uint otype);

    #endregion

    /// <summary>
    /// Create a new SoX resampler.
    /// </summary>
    /// <param name="inputRate">Input sample rate (e.g., 24000)</param>
    /// <param name="outputRate">Output sample rate (e.g., 8000)</param>
    /// <param name="channels">Number of channels (1 for mono)</param>
    /// <param name="quality">Resampling quality preset</param>
    public SoxrResampler(int inputRate, int outputRate, int channels = 1, Quality quality = Quality.High)
    {
        _inputRate = inputRate;
        _outputRate = outputRate;
        _channels = channels;

        var ioSpec = soxr_io_spec(SOXR_INT16_I, SOXR_INT16_I);
        var qualitySpec = soxr_quality_spec((uint)quality, 0);

        _soxr = soxr_create(
            inputRate,
            outputRate,
            (uint)channels,
            out IntPtr error,
            ref ioSpec,
            ref qualitySpec,
            IntPtr.Zero);

        if (error != IntPtr.Zero || _soxr == IntPtr.Zero)
        {
            string? errorMsg = error != IntPtr.Zero ? Marshal.PtrToStringAnsi(soxr_strerror(error)) : "Unknown error";
            throw new InvalidOperationException($"Failed to create soxr resampler: {errorMsg}");
        }

        System.Diagnostics.Debug.WriteLine($"[SoxrResampler] ✓ Initialized: {inputRate}Hz → {outputRate}Hz, quality={quality}");
    }

    /// <summary>
    /// Resample audio data.
    /// </summary>
    /// <param name="input">Input PCM16 samples</param>
    /// <returns>Resampled PCM16 samples</returns>
    public short[] Process(short[] input)
    {
        if (_disposed || _soxr == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(SoxrResampler));

        if (input == null || input.Length == 0)
            return input ?? Array.Empty<short>();

        // Calculate output size with some headroom
        int inputFrames = input.Length / _channels;
        int outputFrames = (int)Math.Ceiling(inputFrames * (double)_outputRate / _inputRate) + 16;
        var output = new short[outputFrames * _channels];

        GCHandle inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        GCHandle outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);

        try
        {
            IntPtr error = soxr_process(
                _soxr,
                inputHandle.AddrOfPinnedObject(),
                (UIntPtr)inputFrames,
                out UIntPtr idone,
                outputHandle.AddrOfPinnedObject(),
                (UIntPtr)outputFrames,
                out UIntPtr odone);

            if (error != IntPtr.Zero)
            {
                string? errorMsg = Marshal.PtrToStringAnsi(soxr_strerror(error));
                throw new InvalidOperationException($"Resampling failed: {errorMsg}");
            }

            int actualOutput = (int)odone * _channels;
            if (actualOutput < output.Length)
            {
                Array.Resize(ref output, actualOutput);
            }

            return output;
        }
        finally
        {
            inputHandle.Free();
            outputHandle.Free();
        }
    }

    /// <summary>
    /// Flush any remaining samples from the resampler.
    /// Call this at the end of a stream to get final samples.
    /// </summary>
    public short[] Flush()
    {
        if (_disposed || _soxr == IntPtr.Zero)
            return Array.Empty<short>();

        // Allocate output buffer for flushing
        var output = new short[256 * _channels];
        GCHandle outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);

        try
        {
            IntPtr error = soxr_process(
                _soxr,
                IntPtr.Zero,  // null input signals end of stream
                UIntPtr.Zero,
                out _,
                outputHandle.AddrOfPinnedObject(),
                (UIntPtr)256,
                out UIntPtr odone);

            if (error != IntPtr.Zero || (int)odone == 0)
                return Array.Empty<short>();

            int actualOutput = (int)odone * _channels;
            Array.Resize(ref output, actualOutput);
            return output;
        }
        finally
        {
            outputHandle.Free();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (_soxr != IntPtr.Zero)
            {
                soxr_delete(_soxr);
                _soxr = IntPtr.Zero;
            }
            _disposed = true;
        }
    }

    ~SoxrResampler()
    {
        Dispose(false);
    }
}

/// <summary>
/// Static helper methods for common resampling operations using libsoxr.
/// </summary>
public static class SoxrResamplerHelper
{
    private static SoxrResampler? _24kTo8k;
    private static SoxrResampler? _8kTo24k;
    private static readonly object _lock = new object();

    /// <summary>
    /// High-quality 24kHz to 8kHz resampling using libsoxr.
    /// </summary>
    public static short[] Resample24kTo8k(short[] input)
    {
        lock (_lock)
        {
            _24kTo8k ??= new SoxrResampler(24000, 8000, 1, SoxrResampler.Quality.High);
            return _24kTo8k.Process(input);
        }
    }

    /// <summary>
    /// High-quality 8kHz to 24kHz resampling using libsoxr.
    /// </summary>
    public static short[] Resample8kTo24k(short[] input)
    {
        lock (_lock)
        {
            _8kTo24k ??= new SoxrResampler(8000, 24000, 1, SoxrResampler.Quality.High);
            return _8kTo24k.Process(input);
        }
    }

    /// <summary>
    /// Reset resampler state (call between calls/sessions).
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _24kTo8k?.Dispose();
            _24kTo8k = null;
            _8kTo24k?.Dispose();
            _8kTo24k = null;
        }
    }
}
