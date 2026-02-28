using AdaCleanVersion.Audio;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Manages the OpenAI → RTP playout lifecycle:
///   - Decodes base64 audio deltas into 160-byte G.711 frames
///   - Feeds frames to G711RtpPlayout (the proven cadence engine)
///   - Arms/ungates MicGateController around AI speech
///   - Handles barge-in (flush playout, send response.cancel)
///
/// Drain detection uses G711RtpPlayout.OnDrained callback —
/// fires on the playout thread when the queue empties. No polling,
/// no Task.Delay, no thread pool pressure on the precision audio loop.
/// </summary>
public sealed class AudioOutputController
{
    private const int FrameSize = 160; // 20ms @ 8kHz

    private readonly G711RtpPlayout _playout;
    private readonly MicGateController _micGate;
    private readonly IRealtimeTransport _transport;
    private readonly CancellationToken _ct;

    // Frame accumulator for partial deltas
    private readonly byte[] _partial = new byte[FrameSize];
    private int _partialLen;

    // AI speaking state
    private volatile bool _aiSpeaking;
    private long _deltaCount;
    private long _frameCount;

    // Watchdog: if drain never fires (e.g. OpenAI sends audio.done with 0 frames),
    // force-ungate after a timeout. Much simpler than the old polling loop.
    private CancellationTokenSource? _watchdogCts;
    private readonly object _watchdogLock = new();

    /// <summary>Fires with each 160B frame queued (for avatar, monitoring).</summary>
    public event Action<byte[]>? OnAudioFrame;

    /// <summary>Fires when mic is ungated after playout drains.</summary>
    public event Action? OnMicUngated;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    public bool IsAiSpeaking => _aiSpeaking;

    public AudioOutputController(
        G711RtpPlayout playout,
        MicGateController micGate,
        IRealtimeTransport transport,
        CancellationToken ct)
    {
        _playout = playout ?? throw new ArgumentNullException(nameof(playout));
        _micGate = micGate ?? throw new ArgumentNullException(nameof(micGate));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ct = ct;

        // Wire drain callback — fires on playout thread, zero polling
        _playout.OnDrained += HandlePlayoutDrained;
    }

    // ─────────────────────────────────────────────
    // OpenAI audio events
    // ─────────────────────────────────────────────

    /// <summary>Handle response.audio.started — AI begins speaking.</summary>
    public void HandleAudioStarted()
    {
        _aiSpeaking = true;
        _micGate.Arm();
        CancelWatchdog();
        _playout.DisarmDrain(); // cancel any pending drain from previous response
        SafeLog("🔇 Mic gated (audio started)");
    }

    /// <summary>Handle response.audio.delta — decode base64, split into 160B frames, feed playout.</summary>
    public void HandleAudioDelta(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return;

        var dc = Interlocked.Increment(ref _deltaCount);

        // Arm on first delta if AudioStarted wasn't sent (model-version compat)
        if (!_aiSpeaking)
        {
            _aiSpeaking = true;
            _micGate.Arm();
            CancelWatchdog();
            _playout.DisarmDrain();
            SafeLog("🔇 Mic gated (first audio delta)");
        }

        byte[] data;
        try { data = Convert.FromBase64String(b64); }
        catch { return; }

        if (data.Length == 0) return;

        int offset = 0;

        // Fill partial accumulator from previous delta
        if (_partialLen > 0)
        {
            int needed = FrameSize - _partialLen;
            int copy = Math.Min(needed, data.Length);
            Buffer.BlockCopy(data, 0, _partial, _partialLen, copy);
            _partialLen += copy;
            offset = copy;

            if (_partialLen >= FrameSize)
            {
                EmitFrame(_partial, 0);
                _partialLen = 0;
            }
        }

        // Enqueue complete 160-byte frames
        while (offset + FrameSize <= data.Length)
        {
            EmitFrame(data, offset);
            offset += FrameSize;
        }

        // Save trailing partial
        int remaining = data.Length - offset;
        if (remaining > 0)
        {
            Buffer.BlockCopy(data, offset, _partial, _partialLen, remaining);
            _partialLen += remaining;
        }
    }

    /// <summary>Handle response.audio.done — AI finished speaking.</summary>
    public void HandleAudioDone()
    {
        _aiSpeaking = false;

        // Flush any remaining partial frame (pad with silence to prevent
        // stale bytes contaminating the next response)
        if (_partialLen > 0)
        {
            // Pad remainder with silence (0xD5 for PCMA)
            Array.Fill(_partial, (byte)0xD5, _partialLen, FrameSize - _partialLen);
            EmitFrame(_partial, 0);
            _partialLen = 0;
        }

        SafeLog("🔊 response.audio.done — drain armed");

        // Arm drain detector — playout thread will fire OnDrained when queue empties
        _playout.ArmDrain();

        // Safety watchdog: if drain never fires (empty response, or playout stopped),
        // force-ungate after a generous timeout based on queued frames
        StartWatchdog();
    }

    /// <summary>
    /// Called by G711RtpPlayout.OnDrained on the playout thread.
    /// Queue just went empty — ungate mic immediately, zero latency.
    /// </summary>
    private void HandlePlayoutDrained()
    {
        // CRITICAL: This runs on the high-priority RTP playout thread.
        // Do minimal work here — offload everything to the thread pool
        // so the next Tick() isn't delayed (which causes the stutter).
        Task.Run(() =>
        {
            CancelWatchdog();

            if (_micGate.IsGated && !_aiSpeaking)
            {
                _micGate.Ungate();
                SafeLog("🔓 Mic ungated (playout drained)");
                try { OnMicUngated?.Invoke(); } catch { }
            }
        });
    }

    // ─────────────────────────────────────────────
    // Barge-in
    // ─────────────────────────────────────────────

    /// <summary>Execute barge-in: flush playout, cancel response, ungate mic.</summary>
    public void HandleBargeIn()
    {
        if (!_aiSpeaking) return;

        _aiSpeaking = false;
        _partialLen = 0;
        CancelWatchdog();

        // Flush playout buffer (also disarms drain)
        _playout.Clear();

        // Cancel AI response
        try
        {
            _ = _transport.SendAsync(new { type = "response.cancel" }, _ct);
        }
        catch { }

        // Ungate mic
        _micGate.Ungate();

        SafeLog("🎤 Barge-in — playout flushed, response.cancel sent, mic ungated");
    }

    // ─────────────────────────────────────────────
    // Watchdog (safety net only — drain callback is primary)
    // ─────────────────────────────────────────────

    private void StartWatchdog()
    {
        lock (_watchdogLock)
        {
            _watchdogCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            _watchdogCts = cts;

            int queuedMs = _playout.QueuedFrames * 20;
            int timeoutMs = Math.Clamp(queuedMs + 2000, 4000, 15000);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(timeoutMs, cts.Token);
                    if (cts.IsCancellationRequested) return;

                    // Drain callback didn't fire — force ungate
                    _playout.DisarmDrain();
                    if (_micGate.IsGated && !_aiSpeaking)
                    {
                        _micGate.Ungate();
                        _playout.Clear();
                        SafeLog($"⚠ Stuck-mic watchdog — force-ungated after {timeoutMs}ms");
                        try { OnMicUngated?.Invoke(); } catch { }
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }
    }

    private void CancelWatchdog()
    {
        lock (_watchdogLock)
        {
            _watchdogCts?.Cancel();
            _watchdogCts = null;
        }
    }

    // ─────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────

    private void EmitFrame(byte[] source, int offset)
    {
        var frame = new byte[FrameSize];
        Buffer.BlockCopy(source, offset, frame, 0, FrameSize);

        _playout.BufferG711(frame);

        var fc = Interlocked.Increment(ref _frameCount);
        if (fc == 1 || fc % 50 == 0)
            SafeLog($"🔈 Frame #{fc} buffered");

        try { OnAudioFrame?.Invoke(frame); } catch { }
    }

    private void SafeLog(string msg)
    {
        try { OnLog?.Invoke(msg); } catch { }
    }
}
