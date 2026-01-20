using System.Collections.Concurrent;
using Concentus.Structs;
using Concentus.Enums;
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
    private OpusEncoder? _opusEncoder;
    private OpusDecoder? _opusDecoder;
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
                _opusEncoder = OpusEncoder.Create(OPUS_SAMPLE_RATE, OPUS_CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
                _opusEncoder.Bitrate = OPUS_BITRATE;
                _opusEncoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
                _opusEncoder.Complexity = 5;
            }

            // Opus expects exactly 960 samples for 20ms at 48kHz
            // If input is different, pad or truncate
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
            int encodedLength = _opusEncoder.Encode(frame, 0, OPUS_FRAME_SIZE, outputBuffer, 0, outputBuffer.Length);
            
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
                _opusDecoder = OpusDecoder.Create(OPUS_SAMPLE_RATE, OPUS_CHANNELS);
            }

            var outputBuffer = new short[OPUS_FRAME_SIZE];
            int decodedSamples = _opusDecoder.Decode(encodedSample, 0, encodedSample.Length, outputBuffer, 0, OPUS_FRAME_SIZE, false);
            
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
