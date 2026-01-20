using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Custom audio encoder that adds Opus support via Concentus (pure C#).
/// Falls back to base SIPSorcery AudioEncoder for G.711 codecs.
/// </summary>
public class OpusAudioEncoder : IAudioEncoder
{
    private const int OPUS_SAMPLE_RATE = 48000;
    private const int OPUS_CHANNELS = 1;
    private const int OPUS_BITRATE = 24000;
    private const int OPUS_FRAME_SIZE_MS = 20;
    private const int OPUS_FRAME_SIZE = OPUS_SAMPLE_RATE / 1000 * OPUS_FRAME_SIZE_MS; // 960 samples

    private readonly SIPSorcery.Media.AudioEncoder _baseEncoder;
    private global::Concentus.Structs.OpusEncoder? _opusEncoder;
    private global::Concentus.Structs.OpusDecoder? _opusDecoder;
    private readonly object _encoderLock = new();
    private readonly object _decoderLock = new();

    // Opus format definition (dynamic payload type 111, 48kHz mono)
    private static readonly AudioFormat OpusFormat = new AudioFormat(
        AudioCodecsEnum.OPUS,
        111,
        OPUS_SAMPLE_RATE,
        OPUS_CHANNELS,
        "opus"
    );

    public OpusAudioEncoder()
    {
        _baseEncoder = new SIPSorcery.Media.AudioEncoder();
    }

    public List<AudioFormat> SupportedFormats
    {
        get
        {
            var formats = new List<AudioFormat>(_baseEncoder.SupportedFormats);
            
            // Add Opus at the beginning (higher priority for Zoiper)
            formats.Insert(0, OpusFormat);
            
            return formats;
        }
    }

    public byte[] EncodeAudio(short[] pcm, AudioFormat format)
    {
        if (format.Codec == AudioCodecsEnum.OPUS)
        {
            return EncodeOpus(pcm);
        }
        
        return _baseEncoder.EncodeAudio(pcm, format);
    }

    public short[] DecodeAudio(byte[] encodedSample, AudioFormat format)
    {
        if (format.Codec == AudioCodecsEnum.OPUS)
        {
            return DecodeOpus(encodedSample);
        }
        
        return _baseEncoder.DecodeAudio(encodedSample, format);
    }

    private byte[] EncodeOpus(short[] pcm)
    {
        lock (_encoderLock)
        {
            if (_opusEncoder == null)
            {
                _opusEncoder = global::Concentus.Structs.OpusEncoder.Create(
                    OPUS_SAMPLE_RATE,
                    OPUS_CHANNELS,
                    global::Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
                _opusEncoder.Bitrate = OPUS_BITRATE;
                _opusEncoder.SignalType = global::Concentus.Enums.OpusSignal.OPUS_SIGNAL_VOICE;
                _opusEncoder.Complexity = 5;
            }

            // Opus expects exactly 960 samples for 20ms at 48kHz
            short[] frame;
            if (pcm.Length == OPUS_FRAME_SIZE)
            {
                frame = pcm;
            }
            else if (pcm.Length < OPUS_FRAME_SIZE)
            {
                frame = new short[OPUS_FRAME_SIZE];
                Array.Copy(pcm, frame, pcm.Length);
            }
            else
            {
                frame = new short[OPUS_FRAME_SIZE];
                Array.Copy(pcm, frame, OPUS_FRAME_SIZE);
            }

            var outputBuffer = new byte[4000]; // Max Opus frame

#pragma warning disable CS0618 // Concentus: prefer Span overloads when available
            int encodedLength = _opusEncoder.Encode(frame, 0, OPUS_FRAME_SIZE, outputBuffer, 0, outputBuffer.Length);
#pragma warning restore CS0618
            
            var result = new byte[encodedLength];
            Array.Copy(outputBuffer, result, encodedLength);
            return result;
        }
    }

    private short[] DecodeOpus(byte[] encodedSample)
    {
        lock (_decoderLock)
        {
            if (_opusDecoder == null)
            {
                _opusDecoder = global::Concentus.Structs.OpusDecoder.Create(OPUS_SAMPLE_RATE, OPUS_CHANNELS);
            }

            var outputBuffer = new short[OPUS_FRAME_SIZE];

#pragma warning disable CS0618 // Concentus: prefer Span overloads when available
            int decodedSamples = _opusDecoder.Decode(encodedSample, 0, encodedSample.Length, outputBuffer, 0, OPUS_FRAME_SIZE, false);
#pragma warning restore CS0618
            
            if (decodedSamples < OPUS_FRAME_SIZE)
            {
                var result = new short[decodedSamples];
                Array.Copy(outputBuffer, result, decodedSamples);
                return result;
            }
            
            return outputBuffer;
        }
    }

    /// <summary>
    /// Check if the format is Opus.
    /// </summary>
    public static bool IsOpus(AudioFormat format) => format.Codec == AudioCodecsEnum.OPUS;

    /// <summary>
    /// Get the Opus audio format for SDP negotiation.
    /// </summary>
    public static AudioFormat GetOpusFormat() => OpusFormat;
}
