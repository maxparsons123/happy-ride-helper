using System.Collections.Concurrent;
using System.Threading.Channels;
using AdaSdkModel.Audio.Interfaces;
using AdaSdkModel.Audio.Processing;

namespace AdaSdkModel.Audio;

/// <summary>
/// OpenAI Realtime audio output pipe — supports BOTH modes:
///   1. Native A-law mode: OpenAI sends G.711 A-law → accumulate → frame → playout
///   2. PCM mode: OpenAI sends PCM16 24kHz → plugin (DSP) → A-law frames → playout
/// </summary>
public sealed class OpenAiToSipPipe : IDisposable
{
    private const int ALAW_FRAME_BYTES = 160;
    private const int DEFAULT_MAX_FRAMES = 240;
    private const int DEFAULT_DROP_BATCH = 20;

    private readonly IAudioPlugin? _plugin;
    private readonly AlawFrameAccumulator _accumulator;
    private readonly IAlawFrameSink _sink;

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

    public event Action<byte[]>? OnFrameOut;
    public event Action<string>? OnLog;

    public int BufferedFrames => _frames.Reader.Count;

    public OpenAiToSipPipe(
        IAlawFrameSink sink,
        IAudioPlugin? plugin = null,
        int maxFrames = DEFAULT_MAX_FRAMES,
        int dropBatch = DEFAULT_DROP_BATCH,
        float alawGain = 1.0f)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _plugin = plugin;
        _accumulator = new AlawFrameAccumulator();

        _maxFrames = Math.Max(60, maxFrames);
        _dropBatch = Math.Clamp(dropBatch, 5, 120);

        _alawGain = alawGain;
        _applyGain = Math.Abs(alawGain - 1.0f) > 0.001f;

        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_maxFrames)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _plugin?.Reset();
            _accumulator.Clear();
            _cts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpLoop(_cts.Token));
            Log($"[PIPE] Started (max={_maxFrames}, drop={_dropBatch}, gain={_alawGain:F2}, mode={(_plugin != null ? "PCM" : "A-law")})");
        }
    }

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
            _accumulator.Clear();
            DrainChannel();
            _plugin?.Reset();
            Log("[PIPE] Stopped + cleared");
        }
    }

    public void PushAlaw(byte[] alawBytes)
    {
        if (alawBytes == null || alawBytes.Length == 0) return;
        if (Volatile.Read(ref _started) == 0) return;
        _accumulator.Accumulate(alawBytes, _pluginOut);
        DrainPluginOutToChannel();
    }

    public void PushPcm(byte[] pcmBytes)
    {
        if (pcmBytes == null || pcmBytes.Length == 0) return;
        if (Volatile.Read(ref _started) == 0) return;
        if (_plugin == null) { Log("[PIPE] PushPcm called but no plugin — dropping"); return; }

        try { _plugin.ProcessPcmBytes(pcmBytes, _pluginOut); }
        catch (Exception ex) { Log($"[PIPE] Plugin error: {ex.Message}"); return; }

        DrainPluginOutToChannel();
    }

    public void Clear()
    {
        try { _sink.Clear(); } catch { }
        _accumulator.Clear();
        DrainChannel();
        Log("[PIPE] Cleared (barge-in)");
    }

    public void Flush() { }

    private void DrainPluginOutToChannel()
    {
        while (_pluginOut.TryDequeue(out var frame))
        {
            if (frame == null || frame.Length != ALAW_FRAME_BYTES) continue;
            _frames.Writer.TryWrite(frame);
        }

        if (_frames.Reader.Count > _maxFrames - 5)
        {
            DropOldestBatch(_dropBatch);
            Log($"[PIPE] ⚠ Overrun: dropped {_dropBatch} frames");
        }
    }

    private async Task PumpLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _frames.Reader.ReadAsync(ct);
                if (_applyGain) ALawGain.ApplyInPlace(frame, _alawGain);
                try { OnFrameOut?.Invoke(frame); } catch { }
                _sink.BufferALaw(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"[PIPE] PumpLoop crashed: {ex.Message}"); }
    }

    private void DropOldestBatch(int count)
    {
        for (int i = 0; i < count; i++)
            if (!_frames.Reader.TryRead(out _)) break;
    }

    private void DrainChannel()
    {
        while (_frames.Reader.TryRead(out _)) { }
    }

    private void Log(string msg) { try { OnLog?.Invoke(msg); } catch { } }

    public void Dispose() { Stop(); _frames.Writer.TryComplete(); }
}
