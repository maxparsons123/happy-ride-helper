using AdaSipClient.Audio;
using AdaSipClient.Sip;

namespace AdaSipClient.Core;

/// <summary>
/// Orchestrates the audio pipeline lifecycle for each call.
/// Creates the appropriate pipeline based on CallMode,
/// wires SIP audio ↔ pipeline, and handles cleanup.
/// </summary>
public sealed class CallHandler : IDisposable
{
    private readonly AppState _state;
    private readonly ISipService _sip;
    private readonly ILogSink _log;

    private IAudioPipeline? _pipeline;
    private bool _disposed;

    public CallHandler(AppState state, ISipService sip, ILogSink log)
    {
        _state = state;
        _sip = sip;
        _log = log;

        // Wire SIP audio into the active pipeline
        _sip.OnAudioReceived += OnCallerAudio;
    }

    /// <summary>
    /// Start a pipeline for the current call based on the selected mode.
    /// </summary>
    public async Task StartPipelineAsync()
    {
        _pipeline?.Dispose();

        _pipeline = _state.Mode switch
        {
            CallMode.AutoBot => new BotAudioPipeline(_state, _log),
            CallMode.ManualListen => new ManualAudioPipeline(_state, _log),
            _ => throw new InvalidOperationException($"Unknown mode: {_state.Mode}")
        };

        // Pipeline output → SIP
        _pipeline.OnOutputAudio += frame => _sip.SendAudio(frame);

        _log.Log($"[Call] Starting {_state.Mode} pipeline...");
        await _pipeline.StartAsync();
    }

    /// <summary>
    /// Stop the current pipeline (call ended).
    /// </summary>
    public void StopPipeline()
    {
        _pipeline?.Stop();
        _pipeline?.Dispose();
        _pipeline = null;
        _log.Log("[Call] Pipeline stopped");
    }

    private void OnCallerAudio(byte[] alawFrame)
    {
        _pipeline?.IngestCallerAudio(alawFrame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sip.OnAudioReceived -= OnCallerAudio;
        StopPipeline();
    }
}
