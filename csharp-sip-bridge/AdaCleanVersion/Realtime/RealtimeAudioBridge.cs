using System.Buffers;
using System.Net;
using AdaCleanVersion.Audio;
using SIPSorcery.Net;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Bridges RTP ↔ OpenAI Realtime audio (G.711 passthrough).
/// Handles mic gate integration and jitter-buffered playout.
/// No booking logic — pure audio path.
/// </summary>
public sealed class RealtimeAudioBridge : IDisposable
{
    private readonly RTPSession _rtpSession;
    private readonly IRealtimeTransport _transport;
    private readonly G711RtpPlayout _playout;
    private readonly CancellationToken _ct;

    public MicGateController MicGate { get; }

    /// <summary>Fires with each G.711 audio frame sent to playout (for avatar feeding).</summary>
    public event Action<byte[]>? OnAudioOut;

    /// <summary>Fires when barge-in occurs.</summary>
    public event Action? OnBargeIn;

    /// <summary>Fires when mic is ungated (playout drained, caller can speak).</summary>
    public event Action? OnMicUngated;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    public RealtimeAudioBridge(
        RTPSession rtpSession,
        IRealtimeTransport transport,
        G711CodecType codec,
        MicGateController micGate,
        CancellationToken ct)
    {
        _rtpSession = rtpSession;
        _transport = transport;
        _ct = ct;
        MicGate = micGate;
        _playout = new G711RtpPlayout(rtpSession, codec);
        _playout.OnLog += msg => OnLog?.Invoke(msg);
    }

    /// <summary>Wire RTP receive and start playout engine.</summary>
    public void Start()
    {
        _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;
        _playout.Start();
    }

    /// <summary>Stop playout and unwire RTP.</summary>
    public void Stop()
    {
        _rtpSession.OnRtpPacketReceived -= OnRtpPacketReceived;
        _playout.Stop();
    }

    // ─── Inbound: RTP → OpenAI ──────────────────────────────

    private void OnRtpPacketReceived(
        IPEndPoint remoteEndPoint,
        SDPMediaTypesEnum mediaType,
        RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio) return;

        var payload = rtpPacket.Payload;

        if (MicGate.IsGated)
        {
            MicGate.Buffer(payload);
            return;
        }

        ForwardToOpenAi(payload);
    }

    /// <summary>Send raw G.711 frame to OpenAI as base64 input_audio_buffer.append.</summary>
    public void ForwardToOpenAi(byte[] g711Payload)
    {
        try
        {
            var b64 = Convert.ToBase64String(g711Payload);
            _ = _transport.SendAsync(new { type = "input_audio_buffer.append", audio = b64 }, _ct);
        }
        catch { /* non-critical — next frame will retry */ }
    }

    // ─── Outbound: OpenAI → RTP (via playout) ───────────────

    /// <summary>
    /// Handle response.audio.delta — GC-free base64 decode into playout buffer.
    /// </summary>
    public void HandleAudioDelta(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return;

        int maxBytes = (b64.Length / 4 + 1) * 3;
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            if (!Convert.TryFromBase64String(b64, rented, out int written) || written == 0)
                return;

            var g711 = new byte[written];
            Buffer.BlockCopy(rented, 0, g711, 0, written);

            _playout.BufferG711(g711);

            try { OnAudioOut?.Invoke(g711); } catch { }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // ─── Mic Gate Events ────────────────────────────────────

    /// <summary>Handle response.audio.done — mark complete and attempt ungate.</summary>
    public void HandleResponseAudioDone()
    {
        MicGate.MarkResponseCompleted();
        OnLog?.Invoke("🔊 response.audio.done");

        if (MicGate.TryRelease())
        {
            OnLog?.Invoke("🔓 Mic ungated (audio done) — buffer discarded (echo)");
            try { OnMicUngated?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// Handle barge-in (speech_started). Clears playout, flushes tail speech frames.
    /// Returns true if barge-in was processed (not debounced/already ungated).
    /// </summary>
    public bool HandleBargeIn()
    {
        if (!MicGate.TryBargeIn(out var speechFrames))
            return false;

        _playout.Clear();

        // Flush tail speech frames to OpenAI
        foreach (var f in speechFrames)
            ForwardToOpenAi(f);

        OnLog?.Invoke($"🎤 Barge-in — playout cleared, mic ungated, {speechFrames.Length} speech frames flushed");
        try { OnBargeIn?.Invoke(); } catch { }
        return true;
    }

    public void ClearPlayout() => _playout.Clear();

    public void Dispose()
    {
        Stop();
    }
}
