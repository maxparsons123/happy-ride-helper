namespace AdaAudioPipe.Interfaces;

/// <summary>
/// Consumes exact 160-byte A-law frames for RTP playout.
/// </summary>
public interface IAlawFrameSink
{
    /// <summary>Buffer a single 160-byte A-law frame for playout.</summary>
    void BufferALaw(byte[] alaw160);

    /// <summary>Clear all buffered frames (barge-in / cancellation).</summary>
    void Clear();
}
