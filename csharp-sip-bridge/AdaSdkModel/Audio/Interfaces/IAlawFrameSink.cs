namespace AdaSdkModel.Audio.Interfaces;

/// <summary>
/// Consumes exact 160-byte A-law frames for RTP playout.
/// </summary>
public interface IAlawFrameSink
{
    void BufferALaw(byte[] alaw160);
    void Clear();
}
