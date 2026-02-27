using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using AdaCleanVersion.Audio;
using SIPSorcery.Media;
using SIPSorcery.Net;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Production-grade telephony audio bridge: SIP RTP ↔ OpenAI Realtime.
///
/// Design principles:
///   - G.711 passthrough only (zero format conversion)
///   - Deterministic 20ms RTP clock (you are the clock)
///   - Immediate upstream audio (each 20ms frame sent individually)
///   - Jitter buffer (FIFO, silence fill)
///   - Simple energy-based mic gate for barge-in
///   - response.cancel on barge-in (let model recover naturally)
///   - Let SIPSorcery manage RTP headers (SSRC/seq/timestamp)
///
/// AudioBridge never touches state.
/// ToolRouter never touches audio.
/// Separation = stability.
/// </summary>
public sealed class RealtimeAudioBridge : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    private const int FrameSize = 160;          // 20ms @ 8kHz
    private const int MaxJitterFrames = 500;    // ~10s buffer — OpenAI sends full responses in <1s bursts

    private readonly VoIPMediaSession _mediaSession;
    private readonly IRealtimeTransport _transport;
    private readonly MicGateController _micGate;
    private readonly CancellationToken _ct;
    private readonly byte _silenceByte;

    // Jitter buffer: ConcurrentQueue of exactly 160-byte frames
    private readonly ConcurrentQueue<byte[]> _jitterBuffer = new();
    private readonly byte[] _partialAccum = new byte[FrameSize];
    private int _partialLen;

    // RTP send loop state
    private Thread? _sendThread;
    private volatile bool _running;

    // AI speaking state (for barge-in)
    private volatile bool _aiSpeaking;

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private static readonly long TicksPerFrame = (long)(20_000_000.0 / NsPerTick);

    /// <summary>Fires with each G.711 audio frame queued for playout (for avatar feeding).</summary>
    public event Action<byte[]>? OnAudioOut;

    /// <summary>Fires when barge-in occurs.</summary>
    public event Action? OnBargeIn;

    /// <summary>Fires when mic is ungated (AI finished speaking).</summary>
    public event Action? OnMicUngated;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    public MicGateController MicGate => _micGate;

    public RealtimeAudioBridge(
        RTPSession rtpSession,
        IRealtimeTransport transport,
        G711CodecType codec,
        MicGateController micGate,
        CancellationToken ct,
        VoIPMediaSession? mediaSession = null)
    {
        _transport = transport;
        _micGate = micGate;
        _ct = ct;
        _silenceByte = G711Codec.SilenceByte(codec);

        _mediaSession = mediaSession ?? (rtpSession as VoIPMediaSession)
            ?? throw new ArgumentException("A VoIPMediaSession is required for RTP playout");

        // CRITICAL: Tell SIPSorcery which codec we're sending so it manages
        // SSRC, sequence numbers, timestamps, and payload type correctly.
        var sdpFormat = codec == G711CodecType.PCMA
            ? SDPMediaFormatsEnum.PCMA
            : SDPMediaFormatsEnum.PCMU;
        _mediaSession.SetSendCodecFormat(new SDPAudioVideoMediaFormat(sdpFormat));

        // Wire RTP inbound
        rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;
    }

    // ─────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────

    public void Start()
    {
        if (_running) return;
        _running = true;

        if (IsWindows)
        {
            try { timeBeginPeriod(1); } catch { }
        }

        _sendThread = new Thread(RtpSendLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _sendThread.Start();

        OnLog?.Invoke("RTP send loop started (20ms deterministic pacing)");
    }

    public void Dispose()
    {
        _running = false;
        try { _sendThread?.Join(500); } catch { }

        if (IsWindows)
        {
            try { timeEndPeriod(1); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────
    // RTP INBOUND: Caller → OpenAI
    // Each 20ms frame sent immediately. No batching.
    // ─────────────────────────────────────────────────────────

    private void OnRtpPacketReceived(
        IPEndPoint remoteEndPoint,
        SDPMediaTypesEnum mediaType,
        RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || _ct.IsCancellationRequested)
            return;

        var payload = rtpPacket.Payload;
        if (payload == null || payload.Length == 0)
            return;

        // Mic gate check with barge-in detection
        if (!_micGate.ShouldSendToOpenAi(payload, out bool isBargeIn))
            return;

        if (isBargeIn)
        {
            ExecuteBargeIn();
        }

        // Forward raw G.711 to OpenAI immediately
        ForwardToOpenAi(payload);
    }

    /// <summary>Send raw G.711 frame to OpenAI as base64 input_audio_buffer.append.</summary>
    public void ForwardToOpenAi(byte[] g711Payload)
    {
        try
        {
            var b64 = Convert.ToBase64String(g711Payload);
            _ = _transport.SendAsync(
                new { type = "input_audio_buffer.append", audio = b64 }, _ct);
        }
        catch { /* non-critical — next frame will retry */ }
    }

    // ─────────────────────────────────────────────────────────
    // OPENAI → RTP: Audio delta handling
    // Decode base64, split into 160-byte frames, enqueue to jitter buffer
    // ─────────────────────────────────────────────────────────

    /// <summary>Handle response.audio.delta — decode and enqueue frames.</summary>
    private long _deltaCount;
    private long _frameCount;

    public void HandleAudioDelta(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return;

        var dc = Interlocked.Increment(ref _deltaCount);
        if (dc == 1 || dc % 50 == 0)
            OnLog?.Invoke($"🔈 AudioDelta #{dc} ({b64.Length} b64 chars, jitter={_jitterBuffer.Count})");

        // Arm mic gate on first delta if not already speaking
        // (AudioStarted event is not sent by all model versions)
        if (!_aiSpeaking)
        {
            _micGate.Arm();
            OnLog?.Invoke("🔇 Mic gated (first audio delta)");
        }
        _aiSpeaking = true;

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
            Buffer.BlockCopy(data, 0, _partialAccum, _partialLen, copy);
            _partialLen += copy;
            offset = copy;

            if (_partialLen >= FrameSize)
            {
                EnqueueFrame(_partialAccum, 0);
                _partialLen = 0;
            }
        }

        // Enqueue complete 160-byte frames
        while (offset + FrameSize <= data.Length)
        {
            EnqueueFrame(data, offset);
            offset += FrameSize;
        }

        // Save trailing partial
        int remaining = data.Length - offset;
        if (remaining > 0)
        {
            Buffer.BlockCopy(data, offset, _partialAccum, _partialLen, remaining);
            _partialLen += remaining;
        }
    }

    private void EnqueueFrame(byte[] source, int offset)
    {
        var frame = new byte[FrameSize];
        Buffer.BlockCopy(source, offset, frame, 0, FrameSize);
        var fc = Interlocked.Increment(ref _frameCount);
        _jitterBuffer.Enqueue(frame);

        if (fc == 1 || fc % 50 == 0)
            OnLog?.Invoke($"🔈 Frame #{fc} enqueued (jitter={_jitterBuffer.Count})");

        // Cap jitter buffer to prevent drift accumulation
        while (_jitterBuffer.Count > MaxJitterFrames)
            _jitterBuffer.TryDequeue(out _);

        // Feed avatar
        try { OnAudioOut?.Invoke(frame); } catch { }
    }

    /// <summary>Handle response.audio.done — AI finished speaking.</summary>
    public void HandleResponseAudioDone()
    {
        _aiSpeaking = false;
        // Don't ungate immediately — let jitter buffer drain first.
        // The send loop will detect empty buffer + !_aiSpeaking and ungate.
        OnLog?.Invoke("🔊 response.audio.done");
    }

    // ─────────────────────────────────────────────────────────
    // RTP SEND LOOP: Fixed 20ms deterministic clock
    // You are the clock. Not the network. Not OpenAI.
    // ─────────────────────────────────────────────────────────

    private void RtpSendLoop()
    {
        var silenceFrame = new byte[FrameSize];
        Array.Fill(silenceFrame, _silenceByte);

        long nextTick = Stopwatch.GetTimestamp();
        bool wasPlaying = false;

        while (_running)
        {
            long now = Stopwatch.GetTimestamp();
            long wait = nextTick - now;

            if (wait > 0)
            {
                long waitNs = (long)(wait * NsPerTick);
                if (waitNs > 2_000_000)
                    Thread.Sleep((int)(waitNs / 1_000_000) - 1);
                else
                    Thread.SpinWait(50);
                continue;
            }

            // Pop frame or silence
            byte[] frame;
            if (_jitterBuffer.TryDequeue(out var queued))
            {
                frame = queued;
                wasPlaying = true;
            }
            else
            {
                frame = silenceFrame;

                // Ungate mic when buffer drained and AI finished
                if (wasPlaying && !_aiSpeaking && _micGate.IsGated)
                {
                    _micGate.Ungate();
                    OnLog?.Invoke("🔓 Mic ungated (playout drained)");
                    try { OnMicUngated?.Invoke(); } catch { }
                }
                wasPlaying = false;
            }

            // Send RTP — let SIPSorcery manage SSRC/seq/timestamp/PT
            try
            {
                _mediaSession.SendAudioFrame((uint)FrameSize, frame);
            }
            catch { /* RTP send failure — non-fatal */ }

            nextTick += TicksPerFrame;

            // Drift correction: if we fell behind by >5 frames, reset
            long drift = Stopwatch.GetTimestamp() - nextTick;
            if (drift > TicksPerFrame * 5)
                nextTick = Stopwatch.GetTimestamp() + TicksPerFrame;
        }
    }

    // ─────────────────────────────────────────────────────────
    // BARGE-IN: Stop AI, flush buffer, send response.cancel
    // That's it. No state changes. No instruction resends.
    // Let the model recover naturally.
    // ─────────────────────────────────────────────────────────

    private void ExecuteBargeIn()
    {
        // Only honor barge-in while AI is actively speaking.
        // After response.audio.done, let queued playout drain naturally.
        if (!_aiSpeaking)
            return;

        _aiSpeaking = false;

        // Flush jitter buffer
        while (_jitterBuffer.TryDequeue(out _)) { }
        _partialLen = 0;

        // Send response.cancel to OpenAI
        try
        {
            _ = _transport.SendAsync(new { type = "response.cancel" }, _ct);
        }
        catch { }

        // Ungate mic
        _micGate.Ungate();

        OnLog?.Invoke("🎤 Barge-in — playout flushed, response.cancel sent, mic ungated");
        try { OnBargeIn?.Invoke(); } catch { }
    }

    /// <summary>External barge-in trigger (from speech_started event).</summary>
    public bool HandleBargeIn()
    {
        if (!_aiSpeaking)
            return false;

        ExecuteBargeIn();
        return true;
    }

    /// <summary>Clear playout buffer (external reset).</summary>
    public void ClearPlayout()
    {
        while (_jitterBuffer.TryDequeue(out _)) { }
        _partialLen = 0;
    }
}
