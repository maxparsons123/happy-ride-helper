using System;

namespace TaxiSipBridge;

/// <summary>
/// High-quality polyphase FIR resampler with Kaiser-windowed sinc filter.
/// Provides clean anti-aliased downsampling for telephony audio.
/// </summary>
public class PolyphaseFirResampler
{
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly int _decimationFactor;
    private readonly int _interpolationFactor;
    private readonly float[] _filterCoeffs;
    private readonly int _filterLength;
    private readonly int _polyphaseCount;
    private readonly float[][] _polyphaseFilters;
    
    // State buffer for continuity across frames
    private readonly float[] _historyBuffer;
    private int _historyLength;
    
    // Kaiser window parameters
    private const double KAISER_BETA = 6.0; // Good balance of stopband attenuation vs transition width
    private const int FILTER_TAPS_PER_PHASE = 16; // Taps per polyphase branch
    
    public PolyphaseFirResampler(int inputRate, int outputRate)
    {
        _inputRate = inputRate;
        _outputRate = outputRate;
        
        // Find GCD to get rational resampling ratio
        int gcd = Gcd(inputRate, outputRate);
        _interpolationFactor = outputRate / gcd;
        _decimationFactor = inputRate / gcd;
        
        // For 24000 -> 8000: interpolation=1, decimation=3
        // For 24000 -> 48000: interpolation=2, decimation=1
        
        _polyphaseCount = Math.Max(_interpolationFactor, _decimationFactor);
        _filterLength = FILTER_TAPS_PER_PHASE * _polyphaseCount;
        
        // Design the prototype lowpass filter
        _filterCoeffs = DesignKaiserLowpass(_filterLength, inputRate, outputRate);
        
        // Decompose into polyphase branches
        _polyphaseFilters = DecomposePolyphase(_filterCoeffs, _polyphaseCount);
        
        // History buffer for filter state
        _historyLength = _filterLength / _polyphaseCount + 1;
        _historyBuffer = new float[_historyLength];
    }
    
    /// <summary>
    /// Resample audio from input rate to output rate with high-quality anti-aliasing.
    /// </summary>
    public short[] Resample(short[] input, int outputLength)
    {
        if (input.Length == 0) return new short[outputLength];
        
        // Special case: same rate
        if (_inputRate == _outputRate)
        {
            var copy = new short[outputLength];
            Array.Copy(input, copy, Math.Min(input.Length, outputLength));
            return copy;
        }
        
        // Convert to float for processing
        var inputFloat = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
            inputFloat[i] = input[i] / 32768f;
        
        float[] output;
        
        if (_decimationFactor > _interpolationFactor)
        {
            // Downsampling path (e.g., 24kHz -> 8kHz)
            output = PolyphaseDecimate(inputFloat, outputLength);
        }
        else
        {
            // Upsampling path (e.g., 24kHz -> 48kHz)
            output = PolyphaseInterpolate(inputFloat, outputLength);
        }
        
        // Convert back to short with soft limiting
        var result = new short[outputLength];
        for (int i = 0; i < outputLength && i < output.Length; i++)
        {
            float sample = output[i] * 32768f;
            // Soft clip to prevent harsh distortion
            if (sample > 30000f)
                sample = 30000f + (sample - 30000f) * 0.3f;
            else if (sample < -30000f)
                sample = -30000f + (sample + 30000f) * 0.3f;
            
            result[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        }
        
        return result;
    }
    
    /// <summary>
    /// Polyphase decimation for downsampling.
    /// </summary>
    private float[] PolyphaseDecimate(float[] input, int outputLength)
    {
        var output = new float[outputLength];
        int filterHalfLen = _polyphaseFilters[0].Length / 2;
        
        // Create extended input with history
        var extended = new float[_historyLength + input.Length];
        Array.Copy(_historyBuffer, 0, extended, 0, _historyLength);
        Array.Copy(input, 0, extended, _historyLength, input.Length);
        
        // Polyphase decimation
        for (int outIdx = 0; outIdx < outputLength; outIdx++)
        {
            // Calculate input position for this output sample
            double inputPos = (double)outIdx * _decimationFactor / _interpolationFactor;
            int baseIdx = (int)inputPos;
            int phase = (int)((inputPos - baseIdx) * _polyphaseCount) % _polyphaseCount;
            
            float[] phaseFilter = _polyphaseFilters[phase];
            float sum = 0;
            
            // Apply polyphase filter
            int startIdx = baseIdx + _historyLength - filterHalfLen;
            for (int k = 0; k < phaseFilter.Length; k++)
            {
                int idx = startIdx + k;
                if (idx >= 0 && idx < extended.Length)
                    sum += extended[idx] * phaseFilter[k];
            }
            
            output[outIdx] = sum;
        }
        
        // Update history buffer for next frame
        int historyStart = Math.Max(0, input.Length - _historyLength);
        int copyLen = Math.Min(_historyLength, input.Length);
        Array.Clear(_historyBuffer, 0, _historyLength);
        for (int i = 0; i < copyLen; i++)
            _historyBuffer[_historyLength - copyLen + i] = input[historyStart + i];
        
        return output;
    }
    
    /// <summary>
    /// Polyphase interpolation for upsampling.
    /// </summary>
    private float[] PolyphaseInterpolate(float[] input, int outputLength)
    {
        var output = new float[outputLength];
        int filterHalfLen = _polyphaseFilters[0].Length / 2;
        
        // Create extended input with history
        var extended = new float[_historyLength + input.Length];
        Array.Copy(_historyBuffer, 0, extended, 0, _historyLength);
        Array.Copy(input, 0, extended, _historyLength, input.Length);
        
        // Polyphase interpolation
        for (int outIdx = 0; outIdx < outputLength; outIdx++)
        {
            // Calculate input position for this output sample
            double inputPos = (double)outIdx * _decimationFactor / _interpolationFactor;
            int baseIdx = (int)inputPos;
            int phase = (int)((inputPos - baseIdx) * _interpolationFactor) % _interpolationFactor;
            
            float[] phaseFilter = _polyphaseFilters[phase];
            float sum = 0;
            
            // Apply polyphase filter
            int startIdx = baseIdx + _historyLength - filterHalfLen;
            for (int k = 0; k < phaseFilter.Length; k++)
            {
                int idx = startIdx + k;
                if (idx >= 0 && idx < extended.Length)
                    sum += extended[idx] * phaseFilter[k];
            }
            
            output[outIdx] = sum * _interpolationFactor; // Gain compensation for interpolation
        }
        
        // Update history buffer for next frame
        int historyStart = Math.Max(0, input.Length - _historyLength);
        int copyLen = Math.Min(_historyLength, input.Length);
        Array.Clear(_historyBuffer, 0, _historyLength);
        for (int i = 0; i < copyLen; i++)
            _historyBuffer[_historyLength - copyLen + i] = input[historyStart + i];
        
        return output;
    }
    
    /// <summary>
    /// Design Kaiser-windowed sinc lowpass filter.
    /// </summary>
    private static float[] DesignKaiserLowpass(int length, int inputRate, int outputRate)
    {
        var coeffs = new float[length];
        
        // Cutoff frequency: slightly below Nyquist of lower rate
        double cutoffHz = Math.Min(inputRate, outputRate) / 2.0 * 0.9; // 90% of Nyquist
        double normalizedCutoff = cutoffHz / Math.Max(inputRate, outputRate);
        
        int center = length / 2;
        double sum = 0;
        
        for (int i = 0; i < length; i++)
        {
            double n = i - center;
            
            // Sinc function
            double sinc;
            if (Math.Abs(n) < 1e-10)
                sinc = 2.0 * normalizedCutoff;
            else
                sinc = Math.Sin(2.0 * Math.PI * normalizedCutoff * n) / (Math.PI * n);
            
            // Kaiser window
            double x = 2.0 * i / (length - 1) - 1.0;
            double kaiser = BesselI0(KAISER_BETA * Math.Sqrt(1.0 - x * x)) / BesselI0(KAISER_BETA);
            
            coeffs[i] = (float)(sinc * kaiser);
            sum += coeffs[i];
        }
        
        // Normalize to unity gain at DC
        for (int i = 0; i < length; i++)
            coeffs[i] /= (float)sum;
        
        return coeffs;
    }
    
    /// <summary>
    /// Decompose prototype filter into polyphase branches.
    /// </summary>
    private static float[][] DecomposePolyphase(float[] prototype, int phases)
    {
        int tapsPerPhase = (prototype.Length + phases - 1) / phases;
        var polyphase = new float[phases][];
        
        for (int p = 0; p < phases; p++)
        {
            polyphase[p] = new float[tapsPerPhase];
            for (int t = 0; t < tapsPerPhase; t++)
            {
                int protoIdx = t * phases + p;
                if (protoIdx < prototype.Length)
                    polyphase[p][t] = prototype[protoIdx];
            }
        }
        
        return polyphase;
    }
    
    /// <summary>
    /// Modified Bessel function of the first kind, order 0.
    /// Used for Kaiser window calculation.
    /// </summary>
    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        double x2 = x * x / 4.0;
        
        for (int k = 1; k <= 25; k++)
        {
            term *= x2 / (k * k);
            sum += term;
            if (term < 1e-12 * sum)
                break;
        }
        
        return sum;
    }
    
    /// <summary>
    /// Greatest common divisor for rational resampling ratio.
    /// </summary>
    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            int temp = b;
            b = a % b;
            a = temp;
        }
        return a;
    }
    
    /// <summary>
    /// Reset state for new audio stream.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_historyBuffer, 0, _historyBuffer.Length);
    }
}
