// Version: 6.0 - NAudio-based audio pipeline (replaces custom DSP)
using System;
using System.IO;
using NAudio.Codecs;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TaxiSipBridge.Audio;

/// <summary>
/// Clean audio pipeline using NAudio's proven algorithms.
/// 
/// Replaces custom DSP (IngressDsp, TtsPreConditioner, etc.) with 
/// NAudio's WDL resampler and built-in codecs for reliable audio.
/// 
/// Features:
/// - WDL resampler (studio-grade anti-aliasing)
/// - NAudio's G.711 codecs (A-law/μ-law)
/// - Minimal processing - let the AI handle voice quality
/// - Thread-safe streaming support
/// </summary>
public sealed class NAudioPipeline : IDisposable
{
    // ===========================================
    // CONSTANTS
    // ===========================================
    private const int SIP_RATE = 8000;
    private const int OPENAI_PCM_RATE = 24000;
    private const int FRAME_SIZE_8K = 160;  // 20ms @ 8kHz
    private const int FRAME_SIZE_24K = 480; // 20ms @ 24kHz
    
    // ===========================================
    // STATE
    // ===========================================
    private bool _disposed;
    
    /// <summary>
    /// Convert SIP G.711 A-law (8kHz) to PCM16 (24kHz) for OpenAI.
    /// Used for ingress path: SIP caller → OpenAI.
    /// </summary>
    public byte[] ConvertALawToPcm24k(byte[] alaw8k)
    {
        if (alaw8k == null || alaw8k.Length == 0)
            return Array.Empty<byte>();
        
        // Step 1: Decode A-law to PCM16 @ 8kHz
        var pcm8k = new short[alaw8k.Length];
        for (int i = 0; i < alaw8k.Length; i++)
        {
            pcm8k[i] = ALawDecoder.ALawToLinearSample(alaw8k[i]);
        }
        
        // Step 2: Resample 8kHz → 24kHz using NAudio WDL
        var pcm24k = ResamplePcm(pcm8k, SIP_RATE, OPENAI_PCM_RATE);
        
        // Step 3: Convert to bytes
        return ShortsToBytes(pcm24k);
    }
    
    /// <summary>
    /// Convert OpenAI PCM16 (24kHz) to G.711 A-law (8kHz) for SIP.
    /// Used for egress path: OpenAI → SIP caller.
    /// </summary>
    public byte[] ConvertPcm24kToALaw(byte[] pcm24kBytes)
    {
        if (pcm24kBytes == null || pcm24kBytes.Length == 0)
            return Array.Empty<byte>();
        
        // Step 1: Convert bytes to shorts
        var pcm24k = BytesToShorts(pcm24kBytes);
        
        // Step 2: Resample 24kHz → 8kHz using NAudio WDL
        var pcm8k = ResamplePcm(pcm24k, OPENAI_PCM_RATE, SIP_RATE);
        
        // Step 3: Encode to A-law
        var alaw = new byte[pcm8k.Length];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            alaw[i] = ALawEncoder.LinearToALawSample(pcm8k[i]);
        }
        
        return alaw;
    }
    
    /// <summary>
    /// Decode G.711 A-law to PCM16 (same sample rate).
    /// </summary>
    public short[] DecodeALaw(byte[] alaw)
    {
        var pcm = new short[alaw.Length];
        for (int i = 0; i < alaw.Length; i++)
        {
            pcm[i] = ALawDecoder.ALawToLinearSample(alaw[i]);
        }
        return pcm;
    }
    
    /// <summary>
    /// Encode PCM16 to G.711 A-law.
    /// </summary>
    public byte[] EncodeALaw(short[] pcm)
    {
        var alaw = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            alaw[i] = ALawEncoder.LinearToALawSample(pcm[i]);
        }
        return alaw;
    }
    
    /// <summary>
    /// Decode G.711 μ-law to PCM16 (same sample rate).
    /// </summary>
    public short[] DecodeMuLaw(byte[] ulaw)
    {
        var pcm = new short[ulaw.Length];
        for (int i = 0; i < ulaw.Length; i++)
        {
            pcm[i] = MuLawDecoder.MuLawToLinearSample(ulaw[i]);
        }
        return pcm;
    }
    
    /// <summary>
    /// Encode PCM16 to G.711 μ-law.
    /// </summary>
    public byte[] EncodeMuLaw(short[] pcm)
    {
        var ulaw = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            ulaw[i] = MuLawEncoder.LinearToMuLawSample(pcm[i]);
        }
        return ulaw;
    }
    
    /// <summary>
    /// High-quality resample using NAudio's WDL resampler.
    /// This is studio-grade quality with proper anti-aliasing.
    /// </summary>
    public short[] ResamplePcm(short[] input, int fromRate, int toRate)
    {
        if (input.Length == 0 || fromRate == toRate)
            return input;
        
        // Convert shorts to bytes for NAudio
        var inputBytes = ShortsToBytes(input);
        
        // Setup input format (16-bit Mono PCM)
        var inputFormat = new WaveFormat(fromRate, 16, 1);
        
        using var msInput = new MemoryStream(inputBytes);
        using var rawProvider = new RawSourceWaveStream(msInput, inputFormat);
        
        // Convert to Float32 for high-quality resampling
        var sampleProvider = rawProvider.ToSampleProvider();
        
        // WDL resampler - gold standard for anti-aliasing
        var resampler = new WdlResamplingSampleProvider(sampleProvider, toRate);
        
        // Read resampled audio
        double ratio = (double)toRate / fromRate;
        int estimatedSamples = (int)Math.Ceiling(input.Length * ratio) + 64;
        var buffer = new float[estimatedSamples];
        
        int samplesRead = resampler.Read(buffer, 0, buffer.Length);
        
        // Convert back to shorts
        var output = new short[samplesRead];
        for (int i = 0; i < samplesRead; i++)
        {
            output[i] = (short)Math.Clamp(buffer[i] * short.MaxValue, short.MinValue, short.MaxValue);
        }
        
        return output;
    }
    
    /// <summary>
    /// Resample byte array (PCM16).
    /// </summary>
    public byte[] ResampleBytes(byte[] inputPcm, int fromRate, int toRate)
    {
        if (inputPcm.Length == 0 || fromRate == toRate)
            return inputPcm;
        
        var inputShorts = BytesToShorts(inputPcm);
        var outputShorts = ResamplePcm(inputShorts, fromRate, toRate);
        return ShortsToBytes(outputShorts);
    }
    
    // ===========================================
    // UTILITIES
    // ===========================================
    
    private static byte[] ShortsToBytes(short[] shorts)
    {
        var bytes = new byte[shorts.Length * 2];
        Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
        return bytes;
    }
    
    private static short[] BytesToShorts(byte[] bytes)
    {
        var shorts = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, shorts, 0, bytes.Length);
        return shorts;
    }
    
    public void Dispose()
    {
        _disposed = true;
    }
}
