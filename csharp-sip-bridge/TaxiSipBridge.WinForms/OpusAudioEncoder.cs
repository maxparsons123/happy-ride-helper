using System;
using System.Collections.Generic;
using System.Linq;
using SIPSorceryMedia.Abstractions;
using Concentus.Enums;
using Concentus.Structs;

namespace TaxiSipBridge
{
    /// <summary>
    /// Opus audio encoder supporting full range of sample rates: 8k, 12k, 16k, 24k, 48kHz.
    /// Opus internally always works at 48kHz but can encode/decode at any supported rate.
    /// </summary>
    public class OpusAudioEncoder : IAudioEncoder
    {
        private const int OPUS_CHANNELS = 1;
        private const int OPUS_BITRATE = 32000;  // 32kbps for clear voice quality

        private readonly SIPSorcery.Media.AudioEncoder _baseEncoder;
        
        // Opus encoders/decoders for each sample rate
        private readonly Dictionary<int, OpusEncoder> _encoders = new();
        private readonly Dictionary<int, OpusDecoder> _decoders = new();
        private readonly object _encoderLock = new();
        private readonly object _decoderLock = new();

        // Supported Opus sample rates (Opus spec: 8000, 12000, 16000, 24000, 48000)
        private static readonly int[] OpusSampleRates = { 8000, 12000, 16000, 24000, 48000 };

        public OpusAudioEncoder() => _baseEncoder = new SIPSorcery.Media.AudioEncoder();

        public List<AudioFormat> SupportedFormats
        {
            get
            {
                var formats = new List<AudioFormat>();
                
                // Add Opus formats in priority order (16k first for wideband SIP, then others)
                formats.Add(new AudioFormat(AudioCodecsEnum.OPUS, 111, 16000, OPUS_CHANNELS, "opus"));
                formats.Add(new AudioFormat(AudioCodecsEnum.OPUS, 111, 24000, OPUS_CHANNELS, "opus"));
                formats.Add(new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, OPUS_CHANNELS, "opus"));
                formats.Add(new AudioFormat(AudioCodecsEnum.OPUS, 111, 12000, OPUS_CHANNELS, "opus"));
                formats.Add(new AudioFormat(AudioCodecsEnum.OPUS, 111, 8000, OPUS_CHANNELS, "opus"));
                
                // Add base encoder formats (PCMU, PCMA, etc.) as fallback
                formats.AddRange(_baseEncoder.SupportedFormats);
                
                return formats;
            }
        }

        public byte[] EncodeAudio(short[] pcm, AudioFormat format)
        {
            if (format.Codec == AudioCodecsEnum.OPUS)
            {
                return EncodeOpus(pcm, format.ClockRate);
            }
            return _baseEncoder.EncodeAudio(pcm, format);
        }

        public short[] DecodeAudio(byte[] encodedSample, AudioFormat format)
        {
            if (format.Codec == AudioCodecsEnum.OPUS)
            {
                return DecodeOpus(encodedSample, format.ClockRate);
            }
            return _baseEncoder.DecodeAudio(encodedSample, format);
        }

        private byte[] EncodeOpus(short[] pcm, int sampleRate)
        {
            lock (_encoderLock)
            {
                // Get or create encoder for this sample rate
                if (!_encoders.TryGetValue(sampleRate, out var encoder))
                {
                    encoder = new OpusEncoder(sampleRate, OPUS_CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
                    encoder.Bitrate = OPUS_BITRATE;
                    encoder.Complexity = 5;
                    encoder.UseVBR = true;
                    _encoders[sampleRate] = encoder;
                }

                // Frame size for 20ms at this sample rate
                int frameSize = sampleRate / 1000 * 20;
                short[] frame = EnsureFrameSize(pcm, frameSize);

                byte[] outBuf = new byte[1275];
                int len = encoder.Encode(frame, 0, frameSize, outBuf, 0, outBuf.Length);
                byte[] res = new byte[len];
                Array.Copy(outBuf, res, len);
                return res;
            }
        }

        private short[] DecodeOpus(byte[] encoded, int sampleRate)
        {
            lock (_decoderLock)
            {
                // Get or create decoder for this sample rate
                if (!_decoders.TryGetValue(sampleRate, out var decoder))
                {
                    decoder = new OpusDecoder(sampleRate, OPUS_CHANNELS);
                    _decoders[sampleRate] = decoder;
                }

                // Frame size for 20ms at this sample rate
                int frameSize = sampleRate / 1000 * 20;
                short[] outBuf = new short[frameSize];
                int len = decoder.Decode(encoded, 0, encoded.Length, outBuf, 0, frameSize, false);
                return len < frameSize ? outBuf.Take(len).ToArray() : outBuf;
            }
        }

        private static short[] EnsureFrameSize(short[] pcm, int targetSize)
        {
            if (pcm.Length == targetSize) return pcm;
            
            var frame = new short[targetSize];
            if (pcm.Length > targetSize)
            {
                Array.Copy(pcm, frame, targetSize);
            }
            else
            {
                // Pad with last sample to avoid clicks
                Array.Copy(pcm, frame, pcm.Length);
                if (pcm.Length > 0)
                {
                    short last = pcm[^1];
                    for (int i = pcm.Length; i < targetSize; i++)
                        frame[i] = last;
                }
            }
            return frame;
        }
    }
}
