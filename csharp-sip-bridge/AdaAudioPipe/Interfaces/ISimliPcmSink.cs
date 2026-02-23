namespace AdaAudioPipe.Interfaces;

/// <summary>
/// Optional sink for Simli avatar lip-sync.
/// Expects PCM16 16kHz mono chunks (ideally 640 bytes = 20ms).
/// </summary>
public interface ISimliPcmSink
{
    Task SendPcm16_16kAsync(byte[] pcm16_16k, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}
