namespace AdaSipClient.Audio;

/// <summary>
/// Pluggable audio pipeline — either bot (OpenAI G.711) or manual (mic/speaker).
/// The call handler creates the appropriate pipeline based on <see cref="Core.CallMode"/>.
/// </summary>
public interface IAudioPipeline : IDisposable
{
    /// <summary>Feed caller audio (8kHz A-law) into the pipeline.</summary>
    void IngestCallerAudio(byte[] alawFrame);

    /// <summary>Raised when the pipeline has audio to send back to the caller.</summary>
    event Action<byte[]>? OnOutputAudio;

    /// <summary>Start the pipeline (connect to OpenAI, open mic, etc.).</summary>
    Task StartAsync();

    /// <summary>Stop and clean up.</summary>
    void Stop();
}
