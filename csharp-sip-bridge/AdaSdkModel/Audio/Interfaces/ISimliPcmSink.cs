namespace AdaSdkModel.Audio.Interfaces;

/// <summary>
/// Optional sink for Simli avatar lip-sync.
/// </summary>
public interface ISimliPcmSink
{
    Task SendPcm16_16kAsync(byte[] pcm16_16k, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}
