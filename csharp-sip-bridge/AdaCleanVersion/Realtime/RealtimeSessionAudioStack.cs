using System.Net;
using AdaCleanVersion.Audio;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Unified session audio stack — single class that wires ALL audio:
///
///   Caller RTP (G.711) → MicGate → OpenAI input_audio_buffer.append
///   OpenAI response.audio.delta → AudioOutputController → G711RtpPlayout → RTP
///
/// Clean separation:
///   - G711RtpPlayout: proven 20ms RTP cadence engine (unchanged)
///   - AudioOutputController: OpenAI→playout lifecycle, gating, barge-in
///   - MicGateController: energy-based mic gating with double-talk guard
///   - RealtimeSessionAudioStack: wires everything together
///
/// Usage:
///   var stack = new RealtimeSessionAudioStack(rtpSession, transport, micGate, cts.Token);
///   stack.OnLog += Log;
///   stack.Start();
///
///   // In WS receive loop:
///   var evt = RealtimeEventParser.Parse(json);
///   stack.HandleRealtimeEvent(evt);
/// </summary>
public sealed class RealtimeSessionAudioStack : IDisposable
{
    private readonly RTPSession _rtp;
    private readonly VoIPMediaSession _mediaSession;
    private readonly IRealtimeTransport _transport;
    private readonly MicGateController _micGate;
    private readonly CancellationToken _ct;

    private readonly G711RtpPlayout _playout;
    private readonly AudioOutputController _out;

    private volatile bool _started;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    /// <summary>Fires with each 160B output frame (for avatar, monitoring).</summary>
    public event Action<byte[]>? OnAudioOutFrame;

    /// <summary>Fires when mic is ungated after playout drains.</summary>
    public event Action? OnMicUngated;

    /// <summary>Fires on barge-in (energy or VAD).</summary>
    public event Action? OnBargeIn;

    /// <summary>Number of frames queued in playout buffer.</summary>
    public int QueuedFrames => _playout.QueuedFrames;

    /// <summary>True if AI is currently speaking.</summary>
    public bool IsAiSpeaking => _out.IsAiSpeaking;

    public RealtimeSessionAudioStack(
        RTPSession rtpSession,
        IRealtimeTransport transport,
        MicGateController micGate,
        CancellationToken ct,
        G711CodecType codec = G711CodecType.PCMA)
    {
        _rtp = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _micGate = micGate ?? throw new ArgumentNullException(nameof(micGate));
        _ct = ct;

        // Resolve VoIPMediaSession from RTPSession
        _mediaSession = (rtpSession as VoIPMediaSession)
            ?? throw new ArgumentException("A VoIPMediaSession is required for RTP playout");

        // Stable, proven 20ms cadence engine
        _playout = new G711RtpPlayout(_mediaSession, codec);

        // OpenAI → playout lifecycle controller
        _out = new AudioOutputController(_playout, _micGate, _transport, ct);

        // Wire logging/telemetry outwards
        _playout.OnLog += SafeLog;
        _out.OnLog += SafeLog;

        _out.OnAudioFrame += frame =>
        {
            try { OnAudioOutFrame?.Invoke(frame); } catch { }
        };

        _out.OnMicUngated += () =>
        {
            try { OnMicUngated?.Invoke(); } catch { }
        };

        // Caller RTP inbound → OpenAI
        _rtp.OnRtpPacketReceived += OnRtpPacketReceived;
    }

    // ─────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────

    public void Start()
    {
        if (_started) return;
        _started = true;

        _playout.Start();
        SafeLog("[AudioStack] Started");
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _playout.Stop();
        SafeLog("[AudioStack] Stopped");
    }

    public void Dispose()
    {
        Stop();
        try { _rtp.OnRtpPacketReceived -= OnRtpPacketReceived; } catch { }
        _playout.Dispose();
    }

    // ─────────────────────────────────────────────
    // Caller RTP → OpenAI
    // ─────────────────────────────────────────────

    private void OnRtpPacketReceived(
        IPEndPoint remoteEndPoint,
        SDPMediaTypesEnum mediaType,
        RTPPacket rtpPacket)
    {
        if (!_started || _ct.IsCancellationRequested) return;
        if (mediaType != SDPMediaTypesEnum.audio) return;

        var payload = rtpPacket.Payload;
        if (payload == null || payload.Length == 0) return;

        // Gate & barge-in detection
        if (!_micGate.ShouldSendToOpenAi(payload, out bool isBargeIn))
            return;

        if (isBargeIn)
        {
            _out.HandleBargeIn();
            try { OnBargeIn?.Invoke(); } catch { }
        }

        // Forward raw G.711 to OpenAI immediately
        try
        {
            var b64 = Convert.ToBase64String(payload);
            _ = _transport.SendAsync(new { type = "input_audio_buffer.append", audio = b64 }, _ct);
        }
        catch { /* non-fatal */ }
    }

    // ─────────────────────────────────────────────
    // OpenAI → RTP playout (event dispatch)
    // ─────────────────────────────────────────────

    /// <summary>
    /// Route a parsed RealtimeEvent to the appropriate handler.
    /// Call this from your WS receive loop after RealtimeEventParser.Parse().
    /// </summary>
    public void HandleRealtimeEvent(RealtimeEvent evt)
    {
        if (!_started || evt == null) return;

        switch (evt.Type)
        {
            case RealtimeEventType.AudioStarted:
                _out.HandleAudioStarted();
                break;

            case RealtimeEventType.AudioDelta:
                _out.HandleAudioDelta(evt.AudioBase64);
                break;

            case RealtimeEventType.AudioDone:
                _out.HandleAudioDone();
                break;

            // OpenAI VAD speech_started → treat as external barge-in
            case RealtimeEventType.SpeechStarted:
                _out.HandleBargeIn();
                try { OnBargeIn?.Invoke(); } catch { }
                break;
        }
    }

    // ─────────────────────────────────────────────
    // Convenience direct-call API
    // ─────────────────────────────────────────────

    public void HandleAudioStarted() => _out.HandleAudioStarted();
    public void HandleAudioDelta(string? b64) => _out.HandleAudioDelta(b64);
    public void HandleAudioDone() => _out.HandleAudioDone();

    public void HandleBargeIn()
    {
        _out.HandleBargeIn();
        try { OnBargeIn?.Invoke(); } catch { }
    }

    /// <summary>Clear playout buffer (external reset).</summary>
    public void ClearPlayout() => _playout.Clear();

    // ─────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────

    private void SafeLog(string msg)
    {
        try { OnLog?.Invoke(msg); } catch { }
    }
}
