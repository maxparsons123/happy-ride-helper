using System.Collections.Generic;
using System.Linq;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// SIPSorcery-compatible audio encoder with Opus and G.722 support.
/// Codec priority: Opus (48kHz) > G.722 (16kHz) > G.711 (8kHz)
/// </summary>
public class UnifiedAudioEncoder : IAudioEncoder
{
    private readonly AudioEncoder _baseEncoder;

    // Use mono Opus for better SIP carrier compatibility (many reject stereo)
    private const int SDP_OPUS_CHANNELS = 1;

    // Common Opus payload types - carriers may use various dynamic PTs (96-127)
    private static readonly AudioFormat[] OpusFormats = new AudioFormat[]
    {
        new AudioFormat(AudioCodecsEnum.OPUS, 96, AudioCodecs.OPUS_SAMPLE_RATE, SDP_OPUS_CHANNELS, "opus"),
        new AudioFormat(AudioCodecsEnum.OPUS, 106, AudioCodecs.OPUS_SAMPLE_RATE, SDP_OPUS_CHANNELS, "opus"),
        new AudioFormat(AudioCodecsEnum.OPUS, 111, AudioCodecs.OPUS_SAMPLE_RATE, SDP_OPUS_CHANNELS, "opus"),
        new AudioFormat(AudioCodecsEnum.OPUS, 116, AudioCodecs.OPUS_SAMPLE_RATE, SDP_OPUS_CHANNELS, "opus"),
        new AudioFormat(AudioCodecsEnum.OPUS, 117, AudioCodecs.OPUS_SAMPLE_RATE, SDP_OPUS_CHANNELS, "opus"),
        new AudioFormat(AudioCodecsEnum.OPUS, 120, AudioCodecs.OPUS_SAMPLE_RATE, SDP_OPUS_CHANNELS, "opus"),
    };

    private static readonly AudioFormat G722Format = new AudioFormat(
        AudioCodecsEnum.G722, 9, AudioCodecs.G722_SAMPLE_RATE, 1, "G722");

    public UnifiedAudioEncoder()
    {
        _baseEncoder = new AudioEncoder();
    }

    public List<AudioFormat> SupportedFormats
    {
        get
        {
            var formats = new List<AudioFormat>();
            
            // Add all Opus PT variants for maximum carrier compatibility
            formats.AddRange(OpusFormats);
            formats.Add(G722Format);

            // Add base formats but exclude Opus/G722 to avoid conflicts
            formats.AddRange(_baseEncoder.SupportedFormats.Where(f =>
                f.Codec != AudioCodecsEnum.OPUS &&
                f.Codec != AudioCodecsEnum.G722));

            return formats;
        }
    }

    public byte[] EncodeAudio(short[] pcm, AudioFormat format)
    {
        switch (format.Codec)
        {
            case AudioCodecsEnum.OPUS:
                return AudioCodecs.OpusEncode(pcm);
            case AudioCodecsEnum.G722:
                return AudioCodecs.G722Encode(pcm);
            case AudioCodecsEnum.PCMA:
                return AudioCodecs.ALawEncode(pcm);
            case AudioCodecsEnum.PCMU:
                return AudioCodecs.MuLawEncode(pcm);
            default:
                return _baseEncoder.EncodeAudio(pcm, format);
        }
    }

    public short[] DecodeAudio(byte[] encodedSample, AudioFormat format)
    {
        switch (format.Codec)
        {
            case AudioCodecsEnum.OPUS:
                return AudioCodecs.OpusDecode(encodedSample);
            case AudioCodecsEnum.G722:
                return AudioCodecs.G722Decode(encodedSample);
            case AudioCodecsEnum.PCMA:
                return AudioCodecs.ALawDecode(encodedSample);
            case AudioCodecsEnum.PCMU:
                return AudioCodecs.MuLawDecode(encodedSample);
            default:
                return _baseEncoder.DecodeAudio(encodedSample, format);
        }
    }

    public void ResetCodecState()
    {
        AudioCodecs.ResetAllCodecs();
    }
}
