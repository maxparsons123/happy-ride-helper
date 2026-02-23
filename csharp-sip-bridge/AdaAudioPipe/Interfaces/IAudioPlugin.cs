using System.Collections.Concurrent;

namespace AdaAudioPipe.Interfaces;

/// <summary>
/// Processes raw PCM bytes (e.g. PCM16 24kHz from OpenAI) into 160-byte A-law frames.
/// </summary>
public interface IAudioPlugin
{
    /// <summary>
    /// Convert PCM bytes to A-law frames and enqueue into the output queue.
    /// Each frame must be exactly 160 bytes.
    /// </summary>
    void ProcessPcmBytes(byte[] pcmBytes, ConcurrentQueue<byte[]> alawFramesOut, int maxQueueFrames = 900);

    /// <summary>Reset all DSP state (call boundary).</summary>
    void Reset();
}

/// <summary>
/// Accumulates raw A-law bytes (from native G.711 sources like OpenAI) 
/// into exact 160-byte frames.
/// </summary>
public interface IAlawAccumulator
{
    /// <summary>
    /// Accept raw A-law bytes (any size) and emit exact 160-byte frames.
    /// </summary>
    void Accumulate(byte[] alawBytes, ConcurrentQueue<byte[]> framesOut);

    /// <summary>Clear the accumulator buffer.</summary>
    void Clear();
}
