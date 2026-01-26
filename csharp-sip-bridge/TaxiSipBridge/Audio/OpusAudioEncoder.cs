using System;
using System.Collections.Generic;
using System.Linq;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Concentus.Enums;
using Concentus.Structs;

namespace TaxiSipBridge.Audio;

/// <summary>
/// Audio encoder with Opus support for high-quality SIP audio.
/// When Opus is negotiated, we upsample 24kHz → 48kHz (preserves quality).
/// When G.711 is negotiated, we downsample 24kHz → 8kHz (loses quality).
/// </summary>
public class OpusAudioEncoder : IAudioEncoder
{
    private const int OPUS_SAMPLE_RATE = 48000;
    private const int OPUS_CHANNELS = 1;
    private const int OPUS_BITRATE = 32000; // 32kbps for good voice quality
    private const int OPUS_FRAME_SIZE_MS = 20;
    private const int OPUS_FRAME_SIZE = OPUS_SAMPLE_RATE / 1000 * OPUS_FRAME_SIZE_MS; // 960 samples

    private readonly AudioEncoder _baseEncoder;
    private OpusEncoder? _opusEncoder;
    private OpusDecoder? _opusDecoder;
    private readonly object _encoderLock = new object();
    private readonly object _decoderLock = new object();

    private static readonly AudioFormat OpusFormat = new AudioFormat(
        AudioCodecsEnum.OPUS, 111, OPUS_SAMPLE_RATE, OPUS_CHANNELS, "opus");

    public OpusAudioEncoder()
    {
        _baseEncoder = new AudioEncoder();
    }

    public List<AudioFormat> SupportedFormats
    {
        get
        {
            var formats = new List<AudioFormat>(_baseEncoder.SupportedFormats);
            formats.Insert(0, OpusFormat); // Opus has priority
            return formats;
        }
    }

    public byte[] EncodeAudio(short[] pcm, AudioFormat format)
    {
        if (format.Codec == AudioCodecsEnum.OPUS)
            return EncodeOpus(pcm);
        
        return _baseEncoder.EncodeAudio(pcm, format);
    }

    public short[] DecodeAudio(byte[] encodedSample, AudioFormat format)
    {
        if (format.Codec == AudioCodecsEnum.OPUS)
            return DecodeOpus(encodedSample);
        
        return _baseEncoder.DecodeAudio(encodedSample, format);
    }

    private byte[] EncodeOpus(short[] pcm)
    {
        lock (_encoderLock)
        {
            _opusEncoder ??= new OpusEncoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
            _opusEncoder.Bitrate = OPUS_BITRATE;

            // Ensure we have exactly 960 samples (20ms @ 48kHz)
            short[] frame;
            if (pcm.Length == OPUS_FRAME_SIZE)
            {
                frame = pcm;
            }
            else
            {
                frame = new short[OPUS_FRAME_SIZE];
                Array.Copy(pcm, frame, Math.Min(pcm.Length, OPUS_FRAME_SIZE));
            }

            byte[] outBuf = new byte[1275]; // Max Opus frame size
            int len = _opusEncoder.Encode(frame, 0, OPUS_FRAME_SIZE, outBuf, 0, outBuf.Length);
            
            byte[] result = new byte[len];
            Array.Copy(outBuf, result, len);
            return result;
        }
    }

    private short[] DecodeOpus(byte[] encoded)
    {
        lock (_decoderLock)
        {
            _opusDecoder ??= new OpusDecoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS);

            short[] outBuf = new short[OPUS_FRAME_SIZE];
            int len = _opusDecoder.Decode(encoded, 0, encoded.Length, outBuf, 0, OPUS_FRAME_SIZE, false);
            
            return len < OPUS_FRAME_SIZE ? outBuf.Take(len).ToArray() : outBuf;
        }
    }
}
