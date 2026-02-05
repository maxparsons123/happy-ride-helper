 using System;
 using System.Collections.Concurrent;
 using System.Runtime.InteropServices;
 
 namespace AdaMain.Audio;
 
 /// <summary>
 /// Converts PCM16 @ 24kHz (OpenAI pcm16 output)
 /// → G.711 (A-law or μ-law) @ 8kHz
 /// → 20ms RTP-ready frames (160 bytes)
 ///
 /// Designed for SIP / SIPSorcery pipelines.
 /// Drop-in replacement for jittery audio paths.
 /// </summary>
 public sealed class PcmToSipG711Encoder : IDisposable
 {
     private const int INPUT_RATE = 24000;
     private const int OUTPUT_RATE = 8000;
     private const int FRAME_SAMPLES = 160; // 20ms @ 8kHz
 
     private readonly IAudioCodec _codec;
     private readonly int _speexQuality;
 
     // SpeexDSP native handle
     private IntPtr _speexResampler = IntPtr.Zero;
     private readonly object _speexLock = new();
 
     // Frame queue for RTP playout
     private readonly ConcurrentQueue<byte[]> _frameQueue = new();
     private short[] _resampleBuffer = Array.Empty<short>();
     private int _disposed;
 
     public int PendingFrames => _frameQueue.Count;
     public byte SilenceByte => _codec.SilenceByte;
     public string CodecName => _codec.Name;
 
     // P/Invoke for SpeexDSP
     [DllImport("libspeexdsp", CallingConvention = CallingConvention.Cdecl)]
     private static extern IntPtr speex_resampler_init(uint channels, uint inRate, uint outRate, int quality, out int err);
 
     [DllImport("libspeexdsp", CallingConvention = CallingConvention.Cdecl)]
     private static extern void speex_resampler_destroy(IntPtr st);
 
     [DllImport("libspeexdsp", CallingConvention = CallingConvention.Cdecl)]
     private static extern int speex_resampler_process_int(IntPtr st, uint channelIndex,
         short[] inBuf, ref uint inLen, short[] outBuf, ref uint outLen);
 
     /// <summary>
     /// Create encoder with specified codec.
     /// </summary>
     /// <param name="codec">G.711 codec (use G711CodecFactory.Create)</param>
     /// <param name="speexQuality">Resampler quality 0-10 (6 recommended for telephony)</param>
     public PcmToSipG711Encoder(IAudioCodec codec, int speexQuality = 6)
     {
         _codec = codec ?? throw new ArgumentNullException(nameof(codec));
         _speexQuality = speexQuality;
         InitResampler();
     }
 
     /// <summary>
     /// Create encoder for A-law or μ-law by name.
     /// </summary>
     public PcmToSipG711Encoder(string codecName, int speexQuality = 6)
         : this(G711CodecFactory.Create(codecName), speexQuality)
     {
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
 
             // Soft limit + encode frame to G.711
             _frameQueue.Enqueue(EncodeFrame(frame));
         }
     }
 
     /// <summary>
     /// Push raw PCM16 shorts directly.
     /// </summary>
     public void PushPcm16(short[] pcm24k)
     {
         if (pcm24k == null || pcm24k.Length == 0)
             return;
 
         if (Volatile.Read(ref _disposed) != 0)
             return;
 
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
 
             short[] remaining = new short[_resampleBuffer.Length - FRAME_SAMPLES];
             Array.Copy(_resampleBuffer, FRAME_SAMPLES, remaining, 0, remaining.Length);
             _resampleBuffer = remaining;
 
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
     /// Get all available frames (non-blocking).
     /// </summary>
     public byte[][] GetAllFrames()
     {
         var frames = new System.Collections.Generic.List<byte[]>();
         while (_frameQueue.TryDequeue(out var frame))
             frames.Add(frame);
         return frames.ToArray();
     }
 
     /// <summary>
     /// Encode PCM16@8kHz frame to G.711.
     /// Includes soft limiting to prevent crackle.
     /// </summary>
     private byte[] EncodeFrame(short[] pcm)
     {
         // Soft limit before encoding (CRITICAL for telephony)
         for (int i = 0; i < pcm.Length; i++)
         {
             if (pcm[i] > 30000) pcm[i] = 30000;
             else if (pcm[i] < -30000) pcm[i] = -30000;
         }
 
         return _codec.Encode(pcm);
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
 }