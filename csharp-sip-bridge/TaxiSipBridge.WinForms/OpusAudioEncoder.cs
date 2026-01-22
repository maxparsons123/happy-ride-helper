using System;
using System.Collections.Generic;
using System.Linq;
using SIPSorceryMedia.Abstractions;
using Concentus.Enums;
using Concentus.Structs;

namespace TaxiSipBridge
{
    /// <summary>
    /// Opus audio encoder supporting both 48kHz and 16kHz modes.
    /// Prioritizes Opus for high-quality wideband audio over G.711.
    /// </summary>
    public class OpusAudioEncoder : IAudioEncoder
    {
        private const int OPUS_CHANNELS = 1;
        private const int OPUS_BITRATE = 32000;  // 32kbps for clear voice quality

        private readonly SIPSorcery.Media.AudioEncoder _baseEncoder;
        private OpusEncoder? _opusEncoder48k;
        private OpusEncoder? _opusEncoder16k;
        private OpusDecoder? _opusDecoder48k;
        private OpusDecoder? _opusDecoder16k;
        private readonly object _encoderLock = new();
        private readonly object _decoderLock = new();

        // Opus formats - 16kHz first (matches user's SIP client), then 48kHz
        private static readonly AudioFormat OpusFormat16k = new(AudioCodecsEnum.OPUS, 111, 16000, OPUS_CHANNELS, "opus");
        private static readonly AudioFormat OpusFormat48k = new(AudioCodecsEnum.OPUS, 111, 48000, OPUS_CHANNELS, "opus");

        public OpusAudioEncoder() => _baseEncoder = new SIPSorcery.Media.AudioEncoder();

        public List<AudioFormat> SupportedFormats
        {
            get
            {
                var formats = new List<AudioFormat>
                {
                    OpusFormat16k,  // Prefer 16kHz Opus (wideband, efficient)
                    OpusFormat48k   // Also offer 48kHz Opus (fullband)
                };
                formats.AddRange(_baseEncoder.SupportedFormats); // Add PCMU, PCMA, etc.
                return formats;
            }
        }

        public byte[] EncodeAudio(short[] pcm, AudioFormat format)
        {
            if (format.Codec == AudioCodecsEnum.OPUS)
            {
                return format.ClockRate == 16000 
                    ? EncodeOpus16k(pcm) 
                    : EncodeOpus48k(pcm);
            }
            return _baseEncoder.EncodeAudio(pcm, format);
        }

        public short[] DecodeAudio(byte[] encodedSample, AudioFormat format)
        {
            if (format.Codec == AudioCodecsEnum.OPUS)
            {
                return format.ClockRate == 16000 
                    ? DecodeOpus16k(encodedSample) 
                    : DecodeOpus48k(encodedSample);
            }
            return _baseEncoder.DecodeAudio(encodedSample, format);
        }

        private byte[] EncodeOpus16k(short[] pcm)
        {
            lock (_encoderLock)
            {
                if (_opusEncoder16k == null)
                {
                    _opusEncoder16k = new OpusEncoder(16000, OPUS_CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
                    _opusEncoder16k.Bitrate = OPUS_BITRATE;
                    _opusEncoder16k.Complexity = 5;
                    _opusEncoder16k.UseVBR = true;
                }

                // 20ms frame at 16kHz = 320 samples
                int frameSize = 320;
                short[] frame = EnsureFrameSize(pcm, frameSize);

                byte[] outBuf = new byte[1275];
                int len = _opusEncoder16k.Encode(frame, 0, frameSize, outBuf, 0, outBuf.Length);
                byte[] res = new byte[len];
                Array.Copy(outBuf, res, len);
                return res;
            }
        }

        private byte[] EncodeOpus48k(short[] pcm)
        {
            lock (_encoderLock)
            {
                if (_opusEncoder48k == null)
                {
                    _opusEncoder48k = new OpusEncoder(48000, OPUS_CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
                    _opusEncoder48k.Bitrate = OPUS_BITRATE;
                    _opusEncoder48k.Complexity = 5;
                    _opusEncoder48k.UseVBR = true;
                }

                // 20ms frame at 48kHz = 960 samples
                int frameSize = 960;
                short[] frame = EnsureFrameSize(pcm, frameSize);

                byte[] outBuf = new byte[1275];
                int len = _opusEncoder48k.Encode(frame, 0, frameSize, outBuf, 0, outBuf.Length);
                byte[] res = new byte[len];
                Array.Copy(outBuf, res, len);
                return res;
            }
        }

        private short[] DecodeOpus16k(byte[] encoded)
        {
            lock (_decoderLock)
            {
                _opusDecoder16k ??= new OpusDecoder(16000, OPUS_CHANNELS);
                int frameSize = 320; // 20ms at 16kHz
                short[] outBuf = new short[frameSize];
                int len = _opusDecoder16k.Decode(encoded, 0, encoded.Length, outBuf, 0, frameSize, false);
                return len < frameSize ? outBuf.Take(len).ToArray() : outBuf;
            }
        }

        private short[] DecodeOpus48k(byte[] encoded)
        {
            lock (_decoderLock)
            {
                _opusDecoder48k ??= new OpusDecoder(48000, OPUS_CHANNELS);
                int frameSize = 960; // 20ms at 48kHz
                short[] outBuf = new short[frameSize];
                int len = _opusDecoder48k.Decode(encoded, 0, encoded.Length, outBuf, 0, frameSize, false);
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
