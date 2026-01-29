namespace TaxiSipBridge.Audio;

/// <summary>
/// Audio codec wrappers for OPUS and G.722 decoding.
/// Note: OPUS requires the Concentus NuGet package for managed decoding.
/// Add to .csproj: <PackageReference Include="Concentus" Version="2.2.1" />
/// </summary>
public static class AudioCodecs
{
    // Opus decode parameters
    public const int OPUS_SAMPLE_RATE = 48000;
    public const int OPUS_DECODE_CHANNELS = 2; // Stereo output for WhatsApp

    private static object? _opusDecoder;
    private static short[]? _opusDecodeBuffer;
    private static bool _opusInitialized = false;
    private static bool _opusAvailable = false;

    /// <summary>
    /// Decode OPUS frame to 48kHz stereo PCM16.
    /// Requires Concentus NuGet package.
    /// </summary>
    public static short[] OpusDecode(byte[] opusData)
    {
        if (opusData == null || opusData.Length == 0)
            return Array.Empty<short>();

        // Lazy init OPUS decoder
        if (!_opusInitialized)
        {
            TryInitOpus();
        }

        if (!_opusAvailable || _opusDecoder == null)
        {
            // Fallback: return silence if Concentus not available
            return new short[960 * OPUS_DECODE_CHANNELS]; // 20ms @ 48kHz stereo
        }

        try
        {
            // Use reflection to call Concentus decoder (avoids hard dependency)
            var decoderType = _opusDecoder.GetType();
            var decodeMethod = decoderType.GetMethod("Decode", new[] { typeof(byte[]), typeof(int), typeof(int), typeof(short[]), typeof(int), typeof(int) });
            
            if (decodeMethod != null && _opusDecodeBuffer != null)
            {
                lock (_opusDecoder)
                {
                    int samplesDecoded = (int)(decodeMethod.Invoke(_opusDecoder, new object[] { opusData, 0, opusData.Length, _opusDecodeBuffer, 0, 5760 }) ?? 0);
                    
                    if (samplesDecoded <= 0)
                        return Array.Empty<short>();

                    int totalSamples = samplesDecoded * OPUS_DECODE_CHANNELS;
                    var result = new short[totalSamples];
                    Array.Copy(_opusDecodeBuffer, result, totalSamples);
                    return result;
                }
            }
        }
        catch
        {
            // Decoder error - return silence
        }

        return new short[960 * OPUS_DECODE_CHANNELS];
    }

    private static void TryInitOpus()
    {
        _opusInitialized = true;
        try
        {
            // Try to load Concentus dynamically
            var concentusAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Concentus");
            
            if (concentusAssembly == null)
            {
                // Try loading it
                try
                {
                    concentusAssembly = System.Reflection.Assembly.Load("Concentus");
                }
                catch { }
            }

            if (concentusAssembly != null)
            {
                var decoderType = concentusAssembly.GetType("Concentus.Structs.OpusDecoder");
                if (decoderType != null)
                {
                    _opusDecoder = Activator.CreateInstance(decoderType, OPUS_SAMPLE_RATE, OPUS_DECODE_CHANNELS);
                    _opusDecodeBuffer = new short[5760 * OPUS_DECODE_CHANNELS]; // 120ms stereo @ 48kHz
                    _opusAvailable = true;
                }
            }
        }
        catch
        {
            _opusAvailable = false;
        }
    }

    /// <summary>
    /// Decode G.722 (16kHz) audio to 16kHz PCM16.
    /// G.722 uses ADPCM compression at 64kbps for 16kHz audio.
    /// </summary>
    public static short[] G722Decode(byte[] g722Data)
    {
        if (g722Data == null || g722Data.Length == 0)
            return Array.Empty<short>();

        // G.722 decoding: simplified passthrough
        // For WhatsApp, OPUS is the primary wideband codec
        // TODO: Implement proper G.722 ADPCM decoding if needed
        var samples = new short[g722Data.Length];
        for (int i = 0; i < g722Data.Length; i++)
        {
            samples[i] = (short)((g722Data[i] - 128) << 8);
        }
        return samples;
    }
}
