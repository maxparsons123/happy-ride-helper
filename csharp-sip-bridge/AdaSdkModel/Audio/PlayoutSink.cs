using AdaAudioPipe.Interfaces;

namespace AdaSdkModel.Audio;

/// <summary>
/// Adapter: wraps ALawRtpPlayout as IAlawFrameSink for the audio pipe.
/// </summary>
public sealed class PlayoutSink : IAlawFrameSink
{
    private readonly ALawRtpPlayout _playout;

    public PlayoutSink(ALawRtpPlayout playout)
        => _playout = playout ?? throw new ArgumentNullException(nameof(playout));

    public void BufferALaw(byte[] alaw160) => _playout.BufferALaw(alaw160);
    public void Clear() => _playout.Clear();
}
