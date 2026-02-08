using NAudio.Codecs;

namespace AdaMain.Audio;

/// <summary>
/// Lightweight G.711 codec utilities using NAudio's built-in encoders.
/// Supports A-law (PCMA) and μ-law (PCMU) with zero-allocation encode/decode
/// and cross-transcoding (A-law ↔ μ-law).
///
/// Usage:
///   var alaw = G711.ALaw.Encode(pcm16Bytes);          // PCM16 → A-law
///   var pcm  = G711.ALaw.Decode(alawBytes);            // A-law → PCM16
///   var ulaw = G711.Transcode.ALawToMuLaw(alawBytes);  // A-law → μ-law
/// </summary>
public static class G711
{
    /// <summary>G.711 A-law (PCMA, PT 8) — European standard.</summary>
    public static class ALaw
    {
        public const byte SilenceByte = 0xD5;
        public const int PayloadType = 8;

        /// <summary>Encode PCM16 bytes (little-endian) to A-law.</summary>
        public static byte[] Encode(byte[] pcm16, int offset = 0, int length = -1)
        {
            if (length < 0) length = pcm16.Length - offset;
            var encoded = new byte[length / 2];
            for (int i = 0, o = 0; i < length; i += 2, o++)
                encoded[o] = ALawEncoder.LinearToALawSample(
                    (short)(pcm16[offset + i] | (pcm16[offset + i + 1] << 8)));
            return encoded;
        }

        /// <summary>Encode PCM16 samples to A-law.</summary>
        public static byte[] Encode(short[] pcm16)
        {
            var encoded = new byte[pcm16.Length];
            for (int i = 0; i < pcm16.Length; i++)
                encoded[i] = ALawEncoder.LinearToALawSample(pcm16[i]);
            return encoded;
        }

        /// <summary>Decode A-law to PCM16 bytes (little-endian).</summary>
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

        /// <summary>Decode A-law to PCM16 samples.</summary>
        public static short[] DecodeToSamples(byte[] alaw, int offset = 0, int length = -1)
        {
            if (length < 0) length = alaw.Length - offset;
            var samples = new short[length];
            for (int i = 0; i < length; i++)
                samples[i] = ALawDecoder.ALawToLinearSample(alaw[offset + i]);
            return samples;
        }
    }

    /// <summary>G.711 μ-law (PCMU, PT 0) — North American standard.</summary>
    public static class MuLaw
    {
        public const byte SilenceByte = 0xFF;
        public const int PayloadType = 0;

        /// <summary>Encode PCM16 bytes (little-endian) to μ-law.</summary>
        public static byte[] Encode(byte[] pcm16, int offset = 0, int length = -1)
        {
            if (length < 0) length = pcm16.Length - offset;
            var encoded = new byte[length / 2];
            for (int i = 0, o = 0; i < length; i += 2, o++)
                encoded[o] = MuLawEncoder.LinearToMuLawSample(
                    (short)(pcm16[offset + i] | (pcm16[offset + i + 1] << 8)));
            return encoded;
        }

        /// <summary>Encode PCM16 samples to μ-law.</summary>
        public static byte[] Encode(short[] pcm16)
        {
            var encoded = new byte[pcm16.Length];
            for (int i = 0; i < pcm16.Length; i++)
                encoded[i] = MuLawEncoder.LinearToMuLawSample(pcm16[i]);
            return encoded;
        }

        /// <summary>Decode μ-law to PCM16 bytes (little-endian).</summary>
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

        /// <summary>Decode μ-law to PCM16 samples.</summary>
        public static short[] DecodeToSamples(byte[] ulaw, int offset = 0, int length = -1)
        {
            if (length < 0) length = ulaw.Length - offset;
            var samples = new short[length];
            for (int i = 0; i < length; i++)
                samples[i] = MuLawDecoder.MuLawToLinearSample(ulaw[offset + i]);
            return samples;
        }
    }

    /// <summary>Cross-transcode between A-law and μ-law (single-pass, no PCM intermediate allocation).</summary>
    public static class Transcode
    {
        /// <summary>Convert A-law bytes to μ-law bytes.</summary>
        public static byte[] ALawToMuLaw(byte[] alaw)
        {
            var result = new byte[alaw.Length];
            for (int i = 0; i < alaw.Length; i++)
                result[i] = MuLawEncoder.LinearToMuLawSample(
                    ALawDecoder.ALawToLinearSample(alaw[i]));
            return result;
        }

        /// <summary>Convert μ-law bytes to A-law bytes.</summary>
        public static byte[] MuLawToALaw(byte[] ulaw)
        {
            var result = new byte[ulaw.Length];
            for (int i = 0; i < ulaw.Length; i++)
                result[i] = ALawEncoder.LinearToALawSample(
                    MuLawDecoder.MuLawToLinearSample(ulaw[i]));
            return result;
        }

        /// <summary>Convert A-law bytes to μ-law bytes in-place.</summary>
        public static void ALawToMuLawInPlace(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = MuLawEncoder.LinearToMuLawSample(
                    ALawDecoder.ALawToLinearSample(buffer[i]));
        }

        /// <summary>Convert μ-law bytes to A-law bytes in-place.</summary>
        public static void MuLawToALawInPlace(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = ALawEncoder.LinearToALawSample(
                    MuLawDecoder.MuLawToLinearSample(buffer[i]));
        }
    }
}
