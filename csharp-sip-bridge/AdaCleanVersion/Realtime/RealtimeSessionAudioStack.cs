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

    // ── Audio monitor stats (logged every 50 frames = 1s) ──
    private int _monFrameCount;
    private int _monSentCount;
    private int _monGatedCount;
    private double _monEnergySum;
    private double _monEnergyMin = double.MaxValue;
    private double _monEnergyMax;
    private int _monSilenceFrames; // frames below threshold 5
    private const int MonitorInterval = 50; // frames (1 second)

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
        G711CodecType codec = G711CodecType.PCMU,
        VoIPMediaSession? mediaSession = null)
    {
        _rtp = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _micGate = micGate ?? throw new ArgumentNullException(nameof(micGate));
        _ct = ct;

        // Resolve VoIPMediaSession — explicit param or cast from RTPSession
        _mediaSession = mediaSession
            ?? (rtpSession as VoIPMediaSession)
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

        // ── Audio monitor: compute energy for every frame ──
        double energy = ComputeEnergy(payload);
        _monFrameCount++;
        _monEnergySum += energy;
        if (energy < _monEnergyMin) _monEnergyMin = energy;
        if (energy > _monEnergyMax) _monEnergyMax = energy;
        if (energy < 5) _monSilenceFrames++;

        // Gate & barge-in detection
        if (!_micGate.ShouldSendToOpenAi(payload, out bool isBargeIn))
        {
            _monGatedCount++;
            LogMonitorIfDue();
            return;
        }

        _monSentCount++;

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

        LogMonitorIfDue();
    }

    private void LogMonitorIfDue()
    {
        if (_monFrameCount < MonitorInterval) return;

        double avg = _monEnergySum / _monFrameCount;
        double min = _monEnergyMin == double.MaxValue ? 0 : _monEnergyMin;
        Log($"🎤 Audio monitor: sent={_monSentCount} gated={_monGatedCount} " +
            $"energy avg={avg:F1} min={min:F1} max={_monEnergyMax:F1} " +
            $"silence={_monSilenceFrames}/{_monFrameCount} " +
            $"micGated={_micGate.IsGated}");

        // Reset
        _monFrameCount = 0;
        _monSentCount = 0;
        _monGatedCount = 0;
        _monEnergySum = 0;
        _monEnergyMin = double.MaxValue;
        _monEnergyMax = 0;
        _monSilenceFrames = 0;
    }

    /// <summary>Average absolute deviation from codec silence center (same as MicGateController).</summary>
    private double ComputeEnergy(byte[] frame)
    {
        if (frame.Length == 0) return 0;
        // Use PCMA silence center 0xD5 (213) — matches MicGateController
        const int silenceCenter = 0xD5;
        double sum = 0;
        for (int i = 0; i < frame.Length; i++)
            sum += Math.Abs(frame[i] - silenceCenter);
        return sum / frame.Length;
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
