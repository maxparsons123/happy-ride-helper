using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AdaSdkModel.Ai;
using AdaSdkModel.Audio;
using AdaSdkModel.Config;
using AdaSdkModel.Core;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace AdaSdkModel.Sip;

/// <summary>
/// Multi-call SIP server with G.711 A-law passthrough + OpenAI SDK.
/// Each incoming call gets its own SIPUserAgent, VoIPMediaSession,
/// ALawRtpPlayout, and CallSession — fully isolated.
/// </summary>
public sealed class SipServer : IAsyncDisposable
{
    private readonly ILogger<SipServer> _logger;
    private readonly SipSettings _settings;
    private readonly AudioSettings _audioSettings;
    private readonly SessionManager _sessionManager;

    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _listenerAgent;
    private IPAddress? _localIp;

    private volatile bool _isRunning;
    private volatile bool _disposed;

    private readonly ConcurrentDictionary<string, ActiveCall> _activeCalls = new();

    public bool IsRegistered { get; private set; }
    public int ActiveCallCount => _activeCalls.Count;

    public event Action<string>? OnLog;

    public SipServer(
        ILogger<SipServer> logger,
        SipSettings settings,
        AudioSettings audioSettings,
        SessionManager sessionManager)
    {
        _logger = logger;
        _settings = settings;
        _audioSettings = audioSettings;
        _sessionManager = sessionManager;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return;

        Log("🚀 SipServer starting (AdaSdkModel)...");
        Log($"➡ SIP: {_settings.Server}:{_settings.Port} ({_settings.Transport})");

        _localIp = GetLocalIp();
        Log($"➡ Local IP: {_localIp}");

        InitializeSipTransport();
        InitializeRegistration();

        _listenerAgent = new SIPUserAgent(_transport, null);
        _listenerAgent.OnIncomingCall += (ua, req) =>
        {
            _ = HandleIncomingCallAsync(req).ContinueWith(t =>
            {
                if (t.IsFaulted) Log($"💥 Call error: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        };

        _regAgent!.Start();
        _isRunning = true;
        Log("🟢 Waiting for SIP registration...");
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        Log("🛑 Stopping...");

        _regAgent?.Stop();
        _listenerAgent = null;

        foreach (var sid in _activeCalls.Keys.ToList())
        {
            if (_activeCalls.TryRemove(sid, out var call))
                await CleanupCallAsync(call, "shutdown");
        }

        try { _transport?.Shutdown(); } catch { }
        _transport = null;
        IsRegistered = false;
        _isRunning = false;
    }

    private void InitializeSipTransport()
    {
        _transport = new SIPTransport();
        _transport.AddSIPChannel(_settings.Transport.ToUpperInvariant() switch
        {
            "TCP" => (SIPChannel)new SIPTCPChannel(new IPEndPoint(_localIp!, 0)),
            _ => new SIPUDPChannel(new IPEndPoint(_localIp!, 0))
        });
    }

    private void InitializeRegistration()
    {
        var authUser = _settings.EffectiveAuthUser;
        var resolvedHost = ResolveDns(_settings.Server);
        var registrar = _settings.Port == 5060 ? resolvedHost : $"{resolvedHost}:{_settings.Port}";

        _regAgent = new SIPRegistrationUserAgent(_transport, _settings.Username, _settings.Password, registrar, 120);
        _regAgent.RegistrationSuccessful += (uri, resp) =>
        {
            Log($"✅ SIP Registered as {_settings.Username}@{_settings.Server}");
            IsRegistered = true;
        };
        _regAgent.RegistrationFailed += (uri, resp, err) =>
        {
            Log($"❌ SIP Registration failed: {err}");
            IsRegistered = false;
        };
    }

    private async Task HandleIncomingCallAsync(SIPRequest req)
    {
        var caller = CallerIdExtractor.Extract(req);
        Log($"📞 Incoming call from {caller} (active: {_activeCalls.Count})");

        var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
        await _transport!.SendResponseAsync(ringing);

        if (!_settings.AutoAnswer) return;

        var callAgent = new SIPUserAgent(_transport, null);
        var audioEncoder = new AudioEncoder();
        var audioSource = new AudioExtrasSource(audioEncoder, new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        audioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMA || fmt.Codec == AudioCodecsEnum.PCMU);

        var rtpSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = audioSource });
        rtpSession.AcceptRtpFromAny = true;

        AudioCodecsEnum negotiatedCodec = AudioCodecsEnum.PCMA;
        rtpSession.OnAudioFormatsNegotiated += formats => negotiatedCodec = formats.FirstOrDefault().Codec;

        var serverUa = callAgent.AcceptCall(req);
        await Task.Delay(200);

        if (!await callAgent.Answer(serverUa, rtpSession))
        {
            Log("❌ Failed to answer"); return;
        }

        await rtpSession.Start();

        ICallSession session;
        try { session = await _sessionManager.CreateSessionAsync(caller); }
        catch (Exception ex) { Log($"❌ AI init failed: {ex.Message}"); callAgent.Hangup(); rtpSession.Close("ai_fail"); return; }

        var activeCall = new ActiveCall { SessionId = session.SessionId, Session = session, RtpSession = rtpSession, CallAgent = callAgent };

        if (!_activeCalls.TryAdd(session.SessionId, activeCall))
        {
            await session.EndAsync("duplicate"); callAgent.Hangup(); rtpSession.Close("dup"); return;
        }

        // Create playout
        var playout = new ALawRtpPlayout(rtpSession);
        playout.OnLog += msg => Log($"[{session.SessionId}] {msg}");
        activeCall.Playout = playout;

        if (session.AiClient is OpenAiSdkClient sdk)
            sdk.GetQueuedFrames = () => playout.QueuedFrames;

        // Wire audio pipeline
        WireAudioPipeline(activeCall, negotiatedCodec);
        playout.Start();

        callAgent.OnCallHungup += async (d) => await RemoveAndCleanupAsync(session.SessionId, "remote_hangup");
        session.OnEnded += async (s, r) => await RemoveAndCleanupAsync(s.SessionId, r);

        Log($"📞 Call {session.SessionId} active for {caller} (total: {_activeCalls.Count})");
    }

    private void WireAudioPipeline(ActiveCall call, AudioCodecsEnum negotiatedCodec)
    {
        var session = call.Session;
        var playout = call.Playout!;
        var rtpSession = call.RtpSession;
        var sid = call.SessionId;

        int isBotSpeaking = 0, adaHasStartedSpeaking = 0, watchdogPending = 0;
        DateTime botStoppedSpeakingAt = DateTime.MinValue;
        DateTime lastBargeInLogAt = DateTime.MinValue;
        var callStartedAt = DateTime.UtcNow;
        bool inboundFlushComplete = false;
        int inboundPacketCount = 0;

        session.OnAudioOut += frame =>
        {
            Interlocked.Exchange(ref isBotSpeaking, 1);
            Interlocked.Exchange(ref adaHasStartedSpeaking, 1);
            playout.BufferALaw(frame);
        };

        session.OnBargeIn += () =>
        {
            if (Volatile.Read(ref isBotSpeaking) == 0 && playout.QueuedFrames == 0) return;
            playout.Clear();
            Interlocked.Exchange(ref isBotSpeaking, 0);
        };

        if (session.AiClient is OpenAiSdkClient sdkClient)
        {
            sdkClient.OnResponseCompleted += () =>
            {
                Interlocked.Exchange(ref isBotSpeaking, 0);
                botStoppedSpeakingAt = DateTime.UtcNow;
                if (playout.QueuedFrames == 0) sdkClient.NotifyPlayoutComplete();
                else Interlocked.Exchange(ref watchdogPending, 1);
            };
        }

        playout.OnQueueEmpty += () =>
        {
            if (Volatile.Read(ref adaHasStartedSpeaking) == 1 && Volatile.Read(ref isBotSpeaking) == 1)
            {
                Interlocked.Exchange(ref isBotSpeaking, 0);
                botStoppedSpeakingAt = DateTime.UtcNow;
                session.NotifyPlayoutComplete();
            }
            else if (Volatile.Read(ref watchdogPending) == 1)
            {
                Interlocked.Exchange(ref watchdogPending, 0);
                session.NotifyPlayoutComplete();
            }
        };

        rtpSession.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;
            var payload = rtpPacket.Payload;
            if (payload == null || payload.Length == 0) return;

            if (!inboundFlushComplete) { inboundPacketCount++; if (inboundPacketCount < 20) return; inboundFlushComplete = true; }
            if ((DateTime.UtcNow - callStartedAt).TotalMilliseconds < 500) return;

            byte[] g711ToSend = negotiatedCodec == AudioCodecsEnum.PCMU ? G711Transcode.MuLawToALaw(payload) : payload;

            bool applySoftGate = Volatile.Read(ref isBotSpeaking) == 1;
            if (!applySoftGate && Volatile.Read(ref adaHasStartedSpeaking) == 1 && botStoppedSpeakingAt != DateTime.MinValue)
            {
                if ((DateTime.UtcNow - botStoppedSpeakingAt).TotalMilliseconds < 300) applySoftGate = true;
            }

            if (applySoftGate)
            {
                double sumSq = 0;
                for (int i = 0; i < g711ToSend.Length; i++) { short pcm = ALawDecode(g711ToSend[i]); sumSq += (double)pcm * pcm; }
                float rms = (float)Math.Sqrt(sumSq / g711ToSend.Length);
                if (rms < (_audioSettings.BargeInRmsThreshold > 0 ? _audioSettings.BargeInRmsThreshold : 1200))
                {
                    g711ToSend = new byte[payload.Length];
                    Array.Fill(g711ToSend, (byte)0xD5);
                }
            }

            var ingressGain = (float)_audioSettings.IngressVolumeBoost;
            if (ingressGain > 1.01f)
            {
                var boosted = new byte[g711ToSend.Length];
                Buffer.BlockCopy(g711ToSend, 0, boosted, 0, g711ToSend.Length);
                ALawVolumeBoost.ApplyInPlace(boosted, ingressGain);
                session.ProcessInboundAudio(boosted);
            }
            else
            {
                session.ProcessInboundAudio(g711ToSend);
            }
        };

        session.OnEnded += (s, reason) => { try { playout.Dispose(); } catch { } };
    }

    private async Task RemoveAndCleanupAsync(string sessionId, string reason)
    {
        if (_activeCalls.TryRemove(sessionId, out var call))
            await CleanupCallAsync(call, reason);
    }

    private async Task CleanupCallAsync(ActiveCall call, string reason)
    {
        if (Interlocked.Exchange(ref call.CleanupDone, 1) == 1) return;
        try { await call.Session.EndAsync(reason); } catch { }
        try { call.CallAgent.Hangup(); } catch { }
        try { call.RtpSession.Close(reason); } catch { }
        try { call.Playout?.Dispose(); } catch { }
        Log($"[{call.SessionId}] 📴 Cleaned up: {reason}");
    }

    // ── Helpers ──

    private static readonly short[] ALawDecodeTable = BuildALawTable();
    private static short[] BuildALawTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int a = i ^ 0x55; int sign = (a & 0x80) != 0 ? -1 : 1;
            int segment = (a >> 4) & 0x07; int value = (a & 0x0F) << 4 | 0x08;
            if (segment > 0) value = (value + 0x100) << (segment - 1);
            table[i] = (short)(sign * value);
        }
        return table;
    }
    private static short ALawDecode(byte alaw) => ALawDecodeTable[alaw];

    private string ResolveDns(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        try { return Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork).ToString(); }
        catch { return host; }
    }

    private IPAddress GetLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(_settings.Server, _settings.Port);
            return (s.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        }
        catch { return IPAddress.Loopback; }
    }

    private void Log(string msg)
    {
        _logger.LogInformation("{Msg}", msg);
        OnLog?.Invoke(msg);
    }

    public async ValueTask DisposeAsync() { if (!_disposed) { _disposed = true; await StopAsync(); } }

    private sealed class ActiveCall
    {
        public required string SessionId { get; init; }
        public required ICallSession Session { get; init; }
        public required VoIPMediaSession RtpSession { get; init; }
        public required SIPUserAgent CallAgent { get; init; }
        public ALawRtpPlayout? Playout { get; set; }
        public int CleanupDone;
    }
}
