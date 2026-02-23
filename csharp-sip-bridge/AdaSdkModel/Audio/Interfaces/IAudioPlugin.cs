using System.Collections.Concurrent;

namespace AdaSdkModel.Audio.Interfaces;

/// <summary>
/// Processes raw PCM bytes (e.g. PCM16 24kHz from OpenAI) into 160-byte A-law frames.
/// </summary>
public interface IAudioPlugin
{
    void ProcessPcmBytes(byte[] pcmBytes, ConcurrentQueue<byte[]> alawFramesOut, int maxQueueFrames = 900);
    void Reset();
}

/// <summary>
/// Accumulates raw A-law bytes into exact 160-byte frames.
/// </summary>
public interface IAlawAccumulator
{
    void Accumulate(byte[] alawBytes, ConcurrentQueue<byte[]> framesOut);
    void Clear();
}
