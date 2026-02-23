using System.Collections.Concurrent;
using System.Threading.Channels;
using AdaAudioPipe.Interfaces;
using AdaAudioPipe.Processing;

namespace AdaAudioPipe;

/// <summary>
/// OpenAI Realtime audio output pipe:
/// response.audio.delta (PCM) → plugin → 160-byte A-law frames → playout sink.
///
/// Key guarantees:
/// - Single consumer (no double speaking)
/// - Bounded memory + latency clamp (drop oldest when overloaded)
/// - Handles OpenAI burst delivery without garbling
/// - Clean start/stop lifecycle per call
/// </summary>
public sealed class OpenAiToSipPipe : IDisposable
{
    private const int ALAW_FRAME_BYTES = 160;       // 20ms @ 8kHz PCMA
    private const int DEFAULT_MAX_FRAMES = 240;     // 240 × 20ms = 4.8s max buffered
    private const int DEFAULT_DROP_BATCH = 20;      // drop 0.4s at a time when overloaded

    private readonly IAudioPlugin _plugin;
    private readonly IAlawFrameSink _sink;
    private readonly ISimliPcmSink? _simli;
    private readonly bool _enableSimliFork;

    private readonly ConcurrentQueue<byte[]> _pluginOut = new();
    private readonly Channel<byte[]> _frames;
    private readonly int _maxFrames;
    private readonly int _dropBatch;

    private readonly float _alawGain;
    private readonly bool _applyGain;

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private int _started;

    public event Action<string>? OnLog;

    /// <summary>Number of frames currently buffered in the channel.</summary>
    public int BufferedFrames => _frames.Reader.Count;

    public OpenAiToSipPipe(
        IAudioPlugin plugin,
        IAlawFrameSink sink,
        int maxFrames = DEFAULT_MAX_FRAMES,
        int dropBatch = DEFAULT_DROP_BATCH,
        float alawGain = 1.0f,
        ISimliPcmSink? simli = null,
        bool enableSimliFork = false)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

        _maxFrames = Math.Max(60, maxFrames);
        _dropBatch = Math.Clamp(dropBatch, 5, 120);

        _alawGain = alawGain;
        _applyGain = Math.Abs(alawGain - 1.0f) > 0.001f;

        _simli = simli;
        _enableSimliFork = enableSimliFork && simli != null;

        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_maxFrames)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>Start the pump. Idempotent — safe to call multiple times.</summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;

            _plugin.Reset();
            _cts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpLoop(_cts.Token));
            Log($"[PIPE] Started (max={_maxFrames}, drop={_dropBatch}, gain={_alawGain:F2}, simli={_enableSimliFork})");
        }
    }

    /// <summary>Stop the pump and clear all buffers. Idempotent.</summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _started, 0) == 0) return;

            try { _cts?.Cancel(); } catch { }
            try { _pumpTask?.Wait(500); } catch { }

            _cts?.Dispose();
            _cts = null;
            _pumpTask = null;

            try { _sink.Clear(); } catch { }
            DrainChannel();
            _plugin.Reset();

            Log("[PIPE] Stopped + cleared");
        }
    }

    /// <summary>
    /// Feed raw PCM bytes from OpenAI response.audio.delta.
    /// Thread-safe. Non-blocking.
    /// </summary>
    public void PushPcm(byte[] pcmBytes)
    {
        if (pcmBytes == null || pcmBytes.Length == 0) return;
        if (Volatile.Read(ref _started) == 0) return;

        // 1) Optional Simli fork — feed BEFORE plugin modifies anything
        if (_enableSimliFork)
            _ = TrySendToSimliAsync(pcmBytes);

        // 2) Plugin: PCM → A-law frames
        try
        {
            _plugin.ProcessPcmBytes(pcmBytes, _pluginOut);
        }
        catch (Exception ex)
        {
            Log($"[PIPE] Plugin error: {ex.Message}");
            return;
        }

        // 3) Move frames into bounded channel
        while (_pluginOut.TryDequeue(out var frame))
        {
            if (frame == null || frame.Length != ALAW_FRAME_BYTES) continue;
            _frames.Writer.TryWrite(frame);
        }

        // 4) Extra latency clamp if way over capacity
        if (_frames.Reader.Count > _maxFrames - 5)
        {
            DropOldestBatch(_dropBatch);
            Log($"[PIPE] ⚠ Overrun: dropped {_dropBatch} frames");
        }
    }

    /// <summary>
    /// Clear all queued audio immediately (barge-in / cancellation).
    /// </summary>
    public void Clear()
    {
        try { _sink.Clear(); } catch { }
        DrainChannel();

        if (_enableSimliFork)
            _ = SafeSimliClearAsync();

        Log("[PIPE] Cleared (barge-in)");
    }

    // ── Pump loop ───────────────────────────────────────────────

    private async Task PumpLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _frames.Reader.ReadAsync(ct);

                if (_applyGain)
                    ALawGain.ApplyInPlace(frame, _alawGain);

                _sink.BufferALaw(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"[PIPE] PumpLoop crashed: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void DropOldestBatch(int count)
    {
        for (int i = 0; i < count; i++)
            if (!_frames.Reader.TryRead(out _)) break;
    }

    private void DrainChannel()
    {
        while (_frames.Reader.TryRead(out _)) { }
    }

    private void Log(string msg)
    {
        try { OnLog?.Invoke(msg); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _frames.Writer.TryComplete();
    }

    // ── Simli fork ──────────────────────────────────────────────

    private async Task TrySendToSimliAsync(byte[] openAiPcm)
    {
        if (_simli == null || _cts == null) return;
        try
        {
            var pcm16k = PcmResampler.Downsample24kTo16k(openAiPcm);
            await _simli.SendPcm16_16kAsync(pcm16k, _cts.Token);
        }
        catch { /* never block SIP audio */ }
    }

    private async Task SafeSimliClearAsync()
    {
        if (_simli == null || _cts == null) return;
        try { await _simli.ClearAsync(_cts.Token); } catch { }
    }
}
