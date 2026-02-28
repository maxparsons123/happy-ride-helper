using AdaCleanVersion.Audio;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Manages the OpenAI → RTP playout lifecycle:
///   - Decodes base64 audio deltas into 160-byte G.711 frames
///   - Feeds frames to G711RtpPlayout (the proven cadence engine)
///   - Arms/ungates MicGateController around AI speech
///   - Handles barge-in (flush playout, send response.cancel)
///
/// Does NOT touch state. Does NOT handle caller→OpenAI.
/// Pure audio output lifecycle.
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
    }

    // ─────────────────────────────────────────────
    // OpenAI audio events
    // ─────────────────────────────────────────────

    /// <summary>Handle response.audio.started — AI begins speaking.</summary>
    public void HandleAudioStarted()
    {
        _aiSpeaking = true;
        _micGate.Arm();
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
        SafeLog("🔊 response.audio.done — waiting for playout to drain");

        // Don't ungate immediately — let playout drain.
        // Poll every 20ms, but force-ungate after 4s (stuck-mic watchdog).
        _ = Task.Run(async () =>
        {
            const int maxWaitMs = 4000;
            int elapsed = 0;

            while (elapsed < maxWaitMs)
            {
                if (_ct.IsCancellationRequested) return;
                if (_playout.QueuedFrames <= 0) break;
                await Task.Delay(20, _ct).ConfigureAwait(false);
                elapsed += 20;
            }

            if (_micGate.IsGated && !_aiSpeaking)
            {
                var forced = elapsed >= maxWaitMs;
                _micGate.Ungate();

                if (forced)
                {
                    _playout.Clear();
                    SafeLog("⚠ Stuck-mic watchdog — force-ungated after 4s, playout flushed");
                }
                else
                {
                    SafeLog("🔓 Mic ungated (playout drained)");
                }

                try { OnMicUngated?.Invoke(); } catch { }
            }
        }, _ct);
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

        // Flush playout buffer
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
