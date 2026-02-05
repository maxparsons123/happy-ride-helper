 using System;
 using System.Collections.Concurrent;
 using System.Runtime.InteropServices;
 
 namespace TaxiSipBridge.Audio;
 
 /// <summary>
 /// Converts PCM16 @ 24kHz (OpenAI output)
 /// → G.711 (A-law or μ-law) @ 8kHz
 /// → 20ms RTP-ready frames (160 bytes)
 ///
 /// Designed for SIP / SIPSorcery pipelines.
 /// Drop-in replacement for jittery TtsPreConditioner path.
 /// </summary>
 public sealed class PcmToSipG711Encoder : IDisposable
 {
     public enum G711Codec
     {
         ALaw,
         MuLaw
     }
 
     private const int INPUT_RATE = 24000;
     private const int OUTPUT_RATE = 8000;
     private const int FRAME_SAMPLES = 160; // 20ms @ 8kHz
 
     private readonly G711Codec _codec;
     private readonly byte _silenceByte;
     private readonly int _speexQuality;
 
     // SpeexDSP native handle
     private IntPtr _speexResampler = IntPtr.Zero;
     private readonly object _speexLock = new();
 
     // Frame queue for RTP playout
     private readonly ConcurrentQueue<byte[]> _frameQueue = new();
     private short[] _resampleBuffer = Array.Empty<short>();
     private int _disposed;
 
     public int PendingFrames => _frameQueue.Count;
     public byte SilenceByte => _silenceByte;
 
     // P/Invoke for SpeexDSP
     [DllImport("libspeexdsp", CallingConvention = CallingConvention.Cdecl)]
     private static extern IntPtr speex_resampler_init(uint channels, uint inRate, uint outRate, int quality, out int err);
 
     [DllImport("libspeexdsp", CallingConvention = CallingConvention.Cdecl)]
     private static extern void speex_resampler_destroy(IntPtr st);
 
     [DllImport("libspeexdsp", CallingConvention = CallingConvention.Cdecl)]
     private static extern int speex_resampler_process_int(IntPtr st, uint channelIndex,
         short[] inBuf, ref uint inLen, short[] outBuf, ref uint outLen);
 
     public PcmToSipG711Encoder(G711Codec codec, int speexQuality = 6)
     {
         _codec = codec;
         _silenceByte = codec == G711Codec.ALaw ? (byte)0xD5 : (byte)0xFF;
         _speexQuality = speexQuality;
 
         InitResampler();
     }
 
     private void InitResampler()
     {
         lock (_speexLock)
         {
             if (_speexResampler != IntPtr.Zero)
             {
                 speex_resampler_destroy(_speexResampler);
             }
 
             int err;
             _speexResampler = speex_resampler_init(1, INPUT_RATE, OUTPUT_RATE, _speexQuality, out err);
             if (err != 0 || _speexResampler == IntPtr.Zero)
             {
                 throw new Exception($"SpeexDSP init failed (24k→8k): error {err}");
             }
         }
     }
 
     /// <summary>
     /// Push raw PCM16@24kHz from OpenAI.
     /// Can be any length (handles bursty OpenAI output).
     /// </summary>
     public void PushPcm16(byte[] pcm16Bytes)
     {
         if (pcm16Bytes == null || pcm16Bytes.Length < 2)
             return;
 
         if (Volatile.Read(ref _disposed) != 0)
             return;
 
         // Convert bytes → shorts
         short[] pcm24k = new short[pcm16Bytes.Length / 2];
         Buffer.BlockCopy(pcm16Bytes, 0, pcm24k, 0, pcm16Bytes.Length);
 
         // Resample 24kHz → 8kHz
         short[] pcm8k = ResampleSpeex(pcm24k);
         if (pcm8k.Length == 0)
             return;
 
         // Append to rolling buffer
         int oldLen = _resampleBuffer.Length;
         Array.Resize(ref _resampleBuffer, oldLen + pcm8k.Length);
         Array.Copy(pcm8k, 0, _resampleBuffer, oldLen, pcm8k.Length);
 
         // Emit full 20ms frames
         while (_resampleBuffer.Length >= FRAME_SAMPLES)
         {
             short[] frame = new short[FRAME_SAMPLES];
             Array.Copy(_resampleBuffer, 0, frame, 0, FRAME_SAMPLES);
 
             // Shift buffer
             short[] remaining = new short[_resampleBuffer.Length - FRAME_SAMPLES];
             Array.Copy(_resampleBuffer, FRAME_SAMPLES, remaining, 0, remaining.Length);
             _resampleBuffer = remaining;
 
             // Encode frame to G.711
             _frameQueue.Enqueue(EncodeFrame(frame));
         }
     }
 
     /// <summary>
     /// Resample using SpeexDSP (high quality, mono).
     /// </summary>
     private short[] ResampleSpeex(short[] input)
     {
         if (input.Length == 0)
             return Array.Empty<short>();
 
         lock (_speexLock)
         {
             if (_speexResampler == IntPtr.Zero)
                 return Array.Empty<short>();
 
             // Output size: 24k→8k = 1:3 ratio
             int maxOut = (input.Length / 3) + 16;
             short[] output = new short[maxOut];
 
             uint inLen = (uint)input.Length;
             uint outLen = (uint)output.Length;
 
             int result = speex_resampler_process_int(_speexResampler, 0, input, ref inLen, output, ref outLen);
             if (result != 0)
                 return Array.Empty<short>();
 
             // Trim to actual output
             if (outLen < output.Length)
             {
                 Array.Resize(ref output, (int)outLen);
             }
 
             return output;
         }
     }
 
     /// <summary>
     /// Try to get one RTP-ready G.711 frame (160 bytes = 20ms).
     /// </summary>
     public bool TryGetFrame(out byte[] g711Frame)
         => _frameQueue.TryDequeue(out g711Frame!);
 
     /// <summary>
     /// Encode PCM16@8kHz frame to G.711.
     /// Includes soft limiting to prevent crackle.
     /// </summary>
     private byte[] EncodeFrame(short[] pcm)
     {
         byte[] g711 = new byte[FRAME_SAMPLES];
 
         for (int i = 0; i < FRAME_SAMPLES; i++)
         {
             short s = pcm[i];
 
             // Soft limiter (CRITICAL for telephony - prevents clipping crackle)
             if (s > 30000) s = 30000;
             else if (s < -30000) s = -30000;
 
             g711[i] = _codec == G711Codec.ALaw
                 ? LinearToALaw(s)
                 : LinearToMuLaw(s);
         }
 
         return g711;
     }
 
     /// <summary>
     /// Clears buffered audio (use on barge-in).
     /// </summary>
     public void Clear()
     {
         while (_frameQueue.TryDequeue(out _)) { }
         _resampleBuffer = Array.Empty<short>();
     }
 
     /// <summary>
     /// Reset resampler state (use between calls).
     /// </summary>
     public void Reset()
     {
         Clear();
         InitResampler();
     }
 
     public void Dispose()
     {
         if (Interlocked.Exchange(ref _disposed, 1) == 1)
             return;
 
         Clear();
 
         lock (_speexLock)
         {
             if (_speexResampler != IntPtr.Zero)
             {
                 speex_resampler_destroy(_speexResampler);
                 _speexResampler = IntPtr.Zero;
             }
         }
     }
 
     // =====================================
     // G.711 ENCODING (inline for performance)
     // =====================================
 
     private static byte LinearToALaw(short pcm)
     {
         int sign = (~pcm >> 8) & 0x80;
         if (sign == 0) pcm = (short)-pcm;
         if (pcm > 32635) pcm = 32635;
 
         int exp = 7;
         for (int mask = 0x4000; (pcm & mask) == 0 && exp > 0; exp--, mask >>= 1) { }
 
         int mantissa = (pcm >> (exp == 0 ? 4 : exp + 3)) & 0x0F;
         return (byte)((sign | (exp << 4) | mantissa) ^ 0x55);
     }
 
     private static byte LinearToMuLaw(short sample)
     {
         const int BIAS = 0x84;
         const int MAX = 32635;
 
         int sign = (sample >> 8) & 0x80;
         if (sign != 0) sample = (short)-sample;
         if (sample > MAX) sample = MAX;
 
         sample += BIAS;
         int exp = 7;
         for (int mask = 0x4000; (sample & mask) == 0 && exp > 0; exp--, mask >>= 1) { }
 
         int mantissa = (sample >> (exp + 3)) & 0x0F;
         return (byte)~(sign | (exp << 4) | mantissa);
     }
 }