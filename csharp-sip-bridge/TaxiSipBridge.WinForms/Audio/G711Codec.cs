namespace TaxiSipBridge.Audio;

/// <summary>
/// G.711 μ-law and A-law codec for telephony audio conversion.
/// Uses LAZY INITIALIZATION to avoid TypeInitializationException.
/// </summary>
public static class G711Codec
{
    private const int Bias = 0x84;
    private const int Clip = 32635;

    // Lazy-initialized lookup tables (avoids static constructor issues)
    private static short[]? _ulawToLinearTable;
    private static byte[]? _linearToUlawTable;
    private static short[]? _alawToLinearTable;
    private static byte[]? _linearToAlawTable;
    private static readonly object _initLock = new object();
    private static bool _initialized = false;

    /// <summary>
    /// Ensure all lookup tables are initialized. Called lazily on first use.
    /// </summary>
    private static void EnsureInitialized()
    {
        if (_initialized) return;
        
        lock (_initLock)
        {
            if (_initialized) return;
            
            try
            {
                // Build μ-law tables
                _ulawToLinearTable = new short[256];
                _linearToUlawTable = new byte[65536];
                
                for (int i = 0; i < 256; i++)
                {
                    _ulawToLinearTable[i] = Ulaw2Linear((byte)i);
                }
                
                for (int i = 0; i < 65536; i++)
                {
                    _linearToUlawTable[i] = Linear2Ulaw((short)(i - 32768));
                }

                // Build A-law tables
                _alawToLinearTable = new short[256];
                _linearToAlawTable = new byte[65536];
                
                for (int i = 0; i < 256; i++)
                {
                    _alawToLinearTable[i] = Alaw2Linear((byte)i);
                }
                
                for (int i = 0; i < 65536; i++)
                {
                    _linearToAlawTable[i] = Linear2Alaw((short)(i - 32768));
                }

                _initialized = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[G711Codec] Init error: {ex.Message}");
                throw;
            }
        }
    }

    private static short Ulaw2Linear(byte ulawByte)
    {
        ulawByte = (byte)~ulawByte;
        int sign = ulawByte & 0x80;
        int exponent = (ulawByte >> 4) & 0x07;
        int mantissa = ulawByte & 0x0F;
        int sample = ((mantissa << 3) + Bias) << exponent;
        sample -= Bias;
        return (short)(sign != 0 ? -sample : sample);
    }

    private static byte Linear2Ulaw(short pcmSample)
    {
        int sign = (pcmSample >> 8) & 0x80;
        if (sign != 0)
            pcmSample = (short)-pcmSample;
        
        if (pcmSample > Clip)
            pcmSample = Clip;
        
        pcmSample += Bias;

        int exponent = 7;
        int mask = 0x4000;
        
        while ((pcmSample & mask) == 0 && exponent > 0)
        {
            exponent--;
            mask >>= 1;
        }

        int mantissa = (pcmSample >> (exponent + 3)) & 0x0F;
        byte ulawByte = (byte)(sign | (exponent << 4) | mantissa);
        
        return (byte)~ulawByte;
    }

    private static short Alaw2Linear(byte alawByte)
    {
        alawByte ^= 0x55;
        int sign = alawByte & 0x80;
        int exponent = (alawByte >> 4) & 0x07;
        int mantissa = alawByte & 0x0F;
        int sample;

        if (exponent == 0)
            sample = (mantissa << 4) + 8;
        else
            sample = ((mantissa << 4) + 264) << (exponent - 1);

        return (short)(sign != 0 ? -sample : sample);
    }

    private static byte Linear2Alaw(short pcmSample)
    {
        int sign = 0;
        if (pcmSample < 0)
        {
            sign = 0x80;
            pcmSample = (short)-pcmSample;
        }

        if (pcmSample > 32635) pcmSample = 32635;

        int exponent = 7;
        int mask = 0x4000;
        while ((pcmSample & mask) == 0 && exponent > 0)
        {
            exponent--;
            mask >>= 1;
        }

        int mantissa;
        if (exponent == 0)
            mantissa = pcmSample >> 4;
        else
            mantissa = (pcmSample >> (exponent + 3)) & 0x0F;

        byte alawByte = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)(alawByte ^ 0x55);
    }

    /// <summary>
    /// Convert μ-law bytes to PCM16 bytes (little-endian)
    /// </summary>
    public static byte[] UlawToPcm16(byte[] ulawData)
    {
        EnsureInitialized();
        var pcmData = new byte[ulawData.Length * 2];
        
        for (int i = 0; i < ulawData.Length; i++)
        {
            short sample = _ulawToLinearTable![ulawData[i]];
            pcmData[i * 2] = (byte)(sample & 0xFF);
            pcmData[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        
        return pcmData;
    }

    /// <summary>
    /// Convert PCM16 bytes (little-endian) to μ-law bytes
    /// </summary>
    public static byte[] Pcm16ToUlaw(byte[] pcmData)
    {
        EnsureInitialized();
        var ulawData = new byte[pcmData.Length / 2];
        
        for (int i = 0; i < ulawData.Length; i++)
        {
            short sample = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            ulawData[i] = _linearToUlawTable![sample + 32768];
        }
        
        return ulawData;
    }

    /// <summary>
    /// Decode single μ-law byte to linear PCM sample
    /// </summary>
    public static short DecodeSample(byte ulawByte)
    {
        EnsureInitialized();
        return _ulawToLinearTable![ulawByte];
    }

    /// <summary>
    /// Encode single linear PCM sample to μ-law byte
    /// </summary>
    public static byte EncodeSample(short pcmSample)
    {
        EnsureInitialized();
        return _linearToUlawTable![pcmSample + 32768];
    }

    #region A-law (PCMA) codec

    /// <summary>
    /// Convert A-law bytes to PCM16 bytes (little-endian)
    /// </summary>
    public static byte[] AlawToPcm16(byte[] alawData)
    {
        EnsureInitialized();
        var pcmData = new byte[alawData.Length * 2];
        for (int i = 0; i < alawData.Length; i++)
        {
            short sample = _alawToLinearTable![alawData[i]];
            pcmData[i * 2] = (byte)(sample & 0xFF);
            pcmData[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return pcmData;
    }

    /// <summary>
    /// Convert PCM16 bytes (little-endian) to A-law bytes
    /// </summary>
    public static byte[] Pcm16ToAlaw(byte[] pcmData)
    {
        EnsureInitialized();
        var alawData = new byte[pcmData.Length / 2];
        for (int i = 0; i < alawData.Length; i++)
        {
            short sample = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            alawData[i] = _linearToAlawTable![sample + 32768];
        }
        return alawData;
    }

    /// <summary>
    /// Decode single A-law byte to linear PCM sample
    /// </summary>
    public static short DecodeSampleALaw(byte alawByte)
    {
        EnsureInitialized();
        return _alawToLinearTable![alawByte];
    }

    /// <summary>
    /// Encode single linear PCM sample to A-law byte
    /// </summary>
    public static byte EncodeSampleALaw(short pcmSample)
    {
        EnsureInitialized();
        return _linearToAlawTable![pcmSample + 32768];
    }

    #endregion
}
