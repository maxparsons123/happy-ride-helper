using System;
using System.Collections.Generic;
using System.Linq;
using SIPSorceryMedia.Abstractions;
using Concentus.Enums;
using Concentus.Structs;

namespace TaxiSipBridge
{
    public class OpusAudioEncoder : IAudioEncoder
    {
        private const int OPUS_SAMPLE_RATE = 48000;
        private const int OPUS_CHANNELS = 1;
        private const int OPUS_BITRATE = 32000;  // Increased from 24kbps for better voice quality
        private const int OPUS_FRAME_SIZE_MS = 20;
        private const int OPUS_FRAME_SIZE = OPUS_SAMPLE_RATE / 1000 * OPUS_FRAME_SIZE_MS; // 960 samples

        private readonly SIPSorcery.Media.AudioEncoder _baseEncoder;
        private OpusEncoder _opusEncoder;
        private OpusDecoder _opusDecoder;
        private readonly object _encoderLock = new object();
        private readonly object _decoderLock = new object();

        private static readonly AudioFormat OpusFormat = new AudioFormat(AudioCodecsEnum.OPUS, 111, OPUS_SAMPLE_RATE, OPUS_CHANNELS, "opus");

        public OpusAudioEncoder() => _baseEncoder = new SIPSorcery.Media.AudioEncoder();

        public List<AudioFormat> SupportedFormats
        {
            get
            {
                var formats = new List<AudioFormat>(_baseEncoder.SupportedFormats);
                formats.Insert(0, OpusFormat); // Priority for Opus
                return formats;
            }
        }

        public byte[] EncodeAudio(short[] pcm, AudioFormat format) =>
            format.Codec == AudioCodecsEnum.OPUS ? EncodeOpus(pcm) : _baseEncoder.EncodeAudio(pcm, format);

        public short[] DecodeAudio(byte[] encodedSample, AudioFormat format) =>
            format.Codec == AudioCodecsEnum.OPUS ? DecodeOpus(encodedSample) : _baseEncoder.DecodeAudio(encodedSample, format);

        private byte[] EncodeOpus(short[] pcm)
        {
            lock (_encoderLock)
            {
                // Use constructor instead of .Create() to avoid FileSystemAclExtensions conflict
                if (_opusEncoder == null)
                {
                    _opusEncoder = new OpusEncoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
                    _opusEncoder.Bitrate = OPUS_BITRATE;
                    _opusEncoder.Complexity = 5; // Balance between quality and CPU (0-10)
                    _opusEncoder.UseVBR = true;  // Variable bitrate for better quality
                }

                // Ensure we have exactly 960 samples for 20ms frame
                short[] frame;
                if (pcm.Length == OPUS_FRAME_SIZE)
                {
                    frame = pcm;
                }
                else if (pcm.Length > OPUS_FRAME_SIZE)
                {
                    frame = new short[OPUS_FRAME_SIZE];
                    Array.Copy(pcm, frame, OPUS_FRAME_SIZE);
                }
                else
                {
                    // Pad with zeros if too short
                    frame = new short[OPUS_FRAME_SIZE];
                    Array.Copy(pcm, frame, pcm.Length);
                }

                byte[] outBuf = new byte[1275];
                int len = _opusEncoder.Encode(frame, 0, OPUS_FRAME_SIZE, outBuf, 0, outBuf.Length);
                byte[] res = new byte[len];
                Array.Copy(outBuf, res, len);
                return res;
            }
        }

        private short[] DecodeOpus(byte[] encoded)
        {
            lock (_decoderLock)
            {
                // Use constructor instead of .Create() to avoid FileSystemAclExtensions conflict
                _opusDecoder ??= new OpusDecoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS);

                short[] outBuf = new short[OPUS_FRAME_SIZE];
                int len = _opusDecoder.Decode(encoded, 0, encoded.Length, outBuf, 0, OPUS_FRAME_SIZE, false);
                return len < OPUS_FRAME_SIZE ? outBuf.Take(len).ToArray() : outBuf;
            }
        }
    }
}
