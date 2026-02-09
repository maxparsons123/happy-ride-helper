using Concentus;
using Concentus.Enums;
using NAudio.Codecs;

namespace AdaMain.Audio;

/// <summary>
/// Unified telephony codec utilities for AdaMain.
///
/// Supported codecs:
/// - G.711 A-law (PCMA, PT 8) — European standard, 8kHz
/// - G.711 μ-law (PCMU, PT 0) — North American standard, 8kHz
/// - Opus (dynamic PT, typically 111) — 48kHz, mono/stereo
///
/// All G.711 methods are static/thread-safe. Opus requires a codec instance (IDisposable).
///
/// Usage:
///   var alaw = G711.ALaw.Encode(pcm16Bytes);
///   var pcm  = G711.MuLaw.Decode(ulawBytes);
///   var ulaw = G711.Transcode.ALawToMuLaw(alawBytes);
///   using var opus = new OpusCodec(48000, 1);
///   var encoded = opus.Encode(pcm16Samples);
/// </summary>
public static class G711
{
    /// <summary>G.711 A-law (PCMA, PT 8) — European standard, 8kHz.</summary>
    public static class ALaw
    {
        public const byte SilenceByte = 0xD5;
        public const int PayloadType = 8;
        public const int SampleRate = 8000;

        public static byte[] Encode(byte[] pcm16, int offset = 0, int length = -1)
        {
            if (length < 0) length = pcm16.Length - offset;
            var encoded = new byte[length / 2];
            for (int i = 0, o = 0; i < length; i += 2, o++)
                encoded[o] = ALawEncoder.LinearToALawSample(
                    (short)(pcm16[offset + i] | (pcm16[offset + i + 1] << 8)));
            return encoded;
        }

        public static byte[] Encode(short[] pcm16)
        {
            var encoded = new byte[pcm16.Length];
            for (int i = 0; i < pcm16.Length; i++)
                encoded[i] = ALawEncoder.LinearToALawSample(pcm16[i]);
            return encoded;
        }

        public static byte[] Decode(byte[] alaw, int offset = 0, int length = -1)
        {
            if (length < 0) length = alaw.Length - offset;
            var decoded = new byte[length * 2];
            for (int i = 0, o = 0; i < length; i++, o += 2)
            {
                short sample = ALawDecoder.ALawToLinearSample(alaw[offset + i]);
                decoded[o] = (byte)(sample & 0xFF);
                decoded[o + 1] = (byte)(sample >> 8);
            }
            return decoded;
        }

        public static short[] DecodeToSamples(byte[] alaw, int offset = 0, int length = -1)
        {
            if (length < 0) length = alaw.Length - offset;
            var samples = new short[length];
            for (int i = 0; i < length; i++)
                samples[i] = ALawDecoder.ALawToLinearSample(alaw[offset + i]);
            return samples;
        }
    }

    /// <summary>G.711 μ-law (PCMU, PT 0) — North American standard, 8kHz.</summary>
    public static class MuLaw
    {
        public const byte SilenceByte = 0xFF;
        public const int PayloadType = 0;
        public const int SampleRate = 8000;

        public static byte[] Encode(byte[] pcm16, int offset = 0, int length = -1)
        {
            if (length < 0) length = pcm16.Length - offset;
            var encoded = new byte[length / 2];
            for (int i = 0, o = 0; i < length; i += 2, o++)
                encoded[o] = MuLawEncoder.LinearToMuLawSample(
                    (short)(pcm16[offset + i] | (pcm16[offset + i + 1] << 8)));
            return encoded;
        }

        public static byte[] Encode(short[] pcm16)
        {
            var encoded = new byte[pcm16.Length];
            for (int i = 0; i < pcm16.Length; i++)
                encoded[i] = MuLawEncoder.LinearToMuLawSample(pcm16[i]);
            return encoded;
        }

        public static byte[] Decode(byte[] ulaw, int offset = 0, int length = -1)
        {
            if (length < 0) length = ulaw.Length - offset;
            var decoded = new byte[length * 2];
            for (int i = 0, o = 0; i < length; i++, o += 2)
            {
                short sample = MuLawDecoder.MuLawToLinearSample(ulaw[offset + i]);
                decoded[o] = (byte)(sample & 0xFF);
                decoded[o + 1] = (byte)(sample >> 8);
            }
            return decoded;
        }

        public static short[] DecodeToSamples(byte[] ulaw, int offset = 0, int length = -1)
        {
            if (length < 0) length = ulaw.Length - offset;
            var samples = new short[length];
            for (int i = 0; i < length; i++)
                samples[i] = MuLawDecoder.MuLawToLinearSample(ulaw[offset + i]);
            return samples;
        }
    }

    /// <summary>Cross-transcode between A-law and μ-law (single-pass).</summary>
    public static class Transcode
    {
        public static byte[] ALawToMuLaw(byte[] alaw)
        {
            var result = new byte[alaw.Length];
            for (int i = 0; i < alaw.Length; i++)
                result[i] = MuLawEncoder.LinearToMuLawSample(ALawDecoder.ALawToLinearSample(alaw[i]));
            return result;
        }

        public static byte[] MuLawToALaw(byte[] ulaw)
        {
            var result = new byte[ulaw.Length];
            for (int i = 0; i < ulaw.Length; i++)
                result[i] = ALawEncoder.LinearToALawSample(MuLawDecoder.MuLawToLinearSample(ulaw[i]));
            return result;
        }

        public static void ALawToMuLawInPlace(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = MuLawEncoder.LinearToMuLawSample(ALawDecoder.ALawToLinearSample(buffer[i]));
        }

        public static void MuLawToALawInPlace(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = ALawEncoder.LinearToALawSample(MuLawDecoder.MuLawToLinearSample(buffer[i]));
        }
    }
}

/// <summary>
/// Opus codec wrapper using Concentus (pure managed, no native deps).
/// 48kHz default, mono recommended for SIP telephony.
///
/// Usage:
///   using var opus = new OpusCodec(48000, 1);
///   byte[] encoded = opus.Encode(pcm16Samples);
///   short[] decoded = opus.Decode(encoded);
///
/// For SIP integration with SIPSorcery:
///   new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 1, "useinbandfec=1")
/// </summary>
public sealed class OpusCodec : IDisposable
{
    public const int MaxSamplesPerChannel = 2880;
    public const int MaxEncodedFrameSize = 1275;

    public int SampleRate { get; }
    public int Channels { get; }

    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;
    private bool _disposed;

    public OpusCodec(
        int sampleRate = 48000,
        int channels = 1,
        OpusApplication application = OpusApplication.OPUS_APPLICATION_VOIP)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, application);
        _decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
    }

    /// <summary>Encode PCM16 samples to Opus.</summary>
    public byte[] Encode(short[] pcm16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm16.Length > Channels * MaxSamplesPerChannel)
            throw new ArgumentException($"Input ({pcm16.Length}) exceeds Opus max ({Channels * MaxSamplesPerChannel})");

        Span<byte> output = stackalloc byte[MaxEncodedFrameSize];
        int len = _encoder.Encode(pcm16, pcm16.Length / Channels, output, output.Length);
        return output[..len].ToArray();
    }

    /// <summary>Encode PCM16 bytes (little-endian) to Opus.</summary>
    public byte[] Encode(byte[] pcm16Bytes, int offset = 0, int length = -1)
    {
        if (length < 0) length = pcm16Bytes.Length - offset;
        var samples = new short[length / 2];
        Buffer.BlockCopy(pcm16Bytes, offset, samples, 0, length);
        return Encode(samples);
    }

    /// <summary>Decode Opus to PCM16 samples.</summary>
    public short[] Decode(byte[] opusData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int maxSamples = MaxSamplesPerChannel * Channels;
        var floatBuf = new float[maxSamples];
        int samplesPerChannel = _decoder.Decode(opusData, floatBuf, floatBuf.Length, false);
        int total = samplesPerChannel * Channels;

        var pcm16 = new short[total];
        for (int i = 0; i < total; i++)
            pcm16[i] = (short)(Math.Clamp(floatBuf[i], -1.0f, 1.0f) * 32767);
        return pcm16;
    }

    /// <summary>Decode Opus to PCM16 bytes (little-endian).</summary>
    public byte[] DecodeToBytes(byte[] opusData)
    {
        var samples = Decode(opusData);
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_encoder as IDisposable)?.Dispose();
        (_decoder as IDisposable)?.Dispose();
    }
}
