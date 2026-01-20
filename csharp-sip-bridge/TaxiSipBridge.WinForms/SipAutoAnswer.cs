using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Net;
using System.Net.Sockets;

namespace TaxiSipBridge;

/// <summary>
/// SIP auto-answer bridge that connects incoming calls to Ada voice AI.
/// </summary>
public class SipAutoAnswer : IDisposable
{
    private readonly SipAdaBridgeConfig _config;
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regUserAgent;
    private SIPUserAgent? _userAgent;
    private AdaAudioClient? _adaClient;
    private VoIPMediaSession? _currentMediaSession;
    private CancellationTokenSource? _callCts;
    private volatile bool _isInCall;
    private volatile bool _disposed;
    private IPAddress? _localIp;

    public event Action? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;

    public bool IsRegistered { get; private set; }
    public bool IsInCall => _isInCall;

    public SipAutoAnswer(SipAdaBridgeConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SipAutoAnswer));

        Log("🚕 SIP Auto-Answer starting...");
        Log($"➡ SIP Server: {_config.SipServer}:{_config.SipPort} ({_config.Transport})");
        Log($"➡ User: {_config.SipUser}");
        Log($"➡ Ada: {_config.AdaWsUrl}");

        _localIp = GetLocalIp();
        Log($"➡ Local IP: {_localIp}");

        _sipTransport = new SIPTransport();

        switch (_config.Transport)
        {
            case SipTransportType.UDP:
                _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(_localIp, 0)));
                break;
            case SipTransportType.TCP:
                _sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(_localIp, 0)));
                break;
        }

        _regUserAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            _config.SipUser,
            _config.SipPassword,
            _config.SipServer,
            120);

        _regUserAgent.RegistrationSuccessful += OnRegistrationSuccess;
        _regUserAgent.RegistrationFailed += OnRegistrationFailure;

        _userAgent = new SIPUserAgent(_sipTransport, null);
        _userAgent.OnIncomingCall += OnIncomingCallAsync;

        _regUserAgent.Start();
        Log("🟢 Waiting for registration...");
    }

    public void Stop()
    {
        if (_disposed) return;

        Log("🛑 Stopping...");

        try { _callCts?.Cancel(); } catch { }

        if (_regUserAgent != null)
        {
            _regUserAgent.RegistrationSuccessful -= OnRegistrationSuccess;
            _regUserAgent.RegistrationFailed -= OnRegistrationFailure;
            try { _regUserAgent.Stop(); } catch { }
        }

        if (_userAgent != null)
        {
            _userAgent.OnIncomingCall -= OnIncomingCallAsync;
        }

        if (_currentMediaSession != null)
        {
            try { _currentMediaSession.Close("stopping"); } catch { }
            _currentMediaSession = null;
        }

        if (_sipTransport != null)
        {
            try { _sipTransport.Shutdown(); } catch { }
        }

        IsRegistered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        try { _adaClient?.Dispose(); _adaClient = null; } catch { }
        try { _callCts?.Dispose(); _callCts = null; } catch { }

        GC.SuppressFinalize(this);
    }

    #region SIP Registration

    private void OnRegistrationSuccess(SIPURI uri, SIPResponse resp)
    {
        Log("✅ SIP Registered - Ready for calls");
        IsRegistered = true;
        OnRegistered?.Invoke();
    }

    private void OnRegistrationFailure(SIPURI uri, SIPResponse? resp, string err)
    {
        Log($"❌ Registration failed: {err}");
        IsRegistered = false;
        OnRegistrationFailed?.Invoke(err);
    }

    #endregion

    #region Call Handling

    private async void OnIncomingCallAsync(SIPUserAgent ua, SIPRequest req)
    {
        var caller = req.Header.From.FromURI.User ?? "unknown";
        Log($"📞 Incoming call from {caller}");
        await HandleIncomingCall(ua, req, caller);
    }

    private async Task HandleIncomingCall(SIPUserAgent ua, SIPRequest req, string caller)
    {
        if (_disposed) return;

        if (_isInCall)
        {
            Log("⚠️ Already in a call, rejecting");
            await SendBusyResponse(req);
            return;
        }

        _isInCall = true;
        var callId = Guid.NewGuid().ToString("N")[..8];
        _callCts = new CancellationTokenSource();
        var cts = _callCts;

        OnCallStarted?.Invoke(callId, caller);

        VoIPMediaSession? mediaSession = null;

        try
        {
            mediaSession = await SetupMediaSession(callId, ua, req, cts.Token);
            if (mediaSession == null) return;

            _adaClient = CreateAdaClient(callId, mediaSession, cts);
            
            WireHangupHandler(ua, callId, cts);
            
            Log($"🔧 [{callId}] Connecting to Ada...");
            await _adaClient.ConnectAsync(caller, cts.Token);

            WireRtpHandler(callId, mediaSession, cts);

            Log($"✅ [{callId}] Call fully established");

            await WaitForCallEnd(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"❌ [{callId}] Error: {ex.Message}");
        }
        finally
        {
            await CleanupCall(callId, ua, mediaSession, cts);
        }
    }

    private async Task<VoIPMediaSession?> SetupMediaSession(
        string callId, SIPUserAgent ua, SIPRequest req, CancellationToken ct)
    {
        Log($"🔧 [{callId}] Creating VoIPMediaSession...");

        var mediaSession = new VoIPMediaSession();
        mediaSession.AcceptRtpFromAny = true;

        mediaSession.OnAudioFormatsNegotiated += formats =>
        {
            var fmt = formats.FirstOrDefault();
            Log($"🎵 [{callId}] Audio format: {fmt.Codec} @ {fmt.ClockRate}Hz");
        };

        _currentMediaSession = mediaSession;

        Log($"🔧 [{callId}] Accepting call...");
        var uas = ua.AcceptCall(req);

        await SendRingingResponse(callId, req);
        await Task.Delay(300, ct);

        Log($"🔧 [{callId}] Answering call...");
        bool answered = await ua.Answer(uas, mediaSession);

        if (!answered)
        {
            Log($"❌ [{callId}] ua.Answer() returned false");
            return null;
        }

        Log($"🔧 [{callId}] Starting media session...");
        await mediaSession.Start();
        Log($"✅ [{callId}] Media session started");

        LogNegotiatedCodec(callId, mediaSession);

        return mediaSession;
    }

    private AdaAudioClient CreateAdaClient(
        string callId, VoIPMediaSession mediaSession, CancellationTokenSource cts)
    {
        Log($"🔧 [{callId}] Creating AdaAudioClient...");
        
        var client = new AdaAudioClient(_config.AdaWsUrl);
        client.OnLog += msg => Log(msg);
        client.OnTranscript += t => OnTranscript?.Invoke(t);

        int chunkCount = 0;
        client.OnPcm24Audio += pcmBytes =>
        {
            if (cts.Token.IsCancellationRequested) return;

            chunkCount++;
            if (chunkCount <= 5)
                Log($"🔊 [{callId}] Ada audio #{chunkCount}: {pcmBytes.Length}b");

            SendAdaAudioToRtp(callId, mediaSession, pcmBytes, chunkCount);
        };

        Log($"🔧 [{callId}] Ada audio wired via OnPcm24Audio");
        return client;
    }

    private void SendAdaAudioToRtp(
        string callId, VoIPMediaSession mediaSession, byte[] pcmBytes, int chunkCount)
    {
        try
        {
            var pcm24k = AudioCodecs.BytesToShorts(pcmBytes);
            var pcm8k = AudioCodecs.Resample(pcm24k, 24000, 8000);
            var ulaw = AudioCodecs.MuLawEncode(pcm8k);

            uint duration = (uint)ulaw.Length;
            mediaSession.SendAudio(duration, ulaw);

            if (chunkCount <= 5)
                Log($"📤 [{callId}] Sent {ulaw.Length}b via RTP");
        }
        catch (Exception ex)
        {
            if (chunkCount <= 5)
                Log($"⚠️ [{callId}] SendAudio error: {ex.Message}");
        }
    }

    private void WireHangupHandler(SIPUserAgent ua, string callId, CancellationTokenSource cts)
    {
        ua.OnCallHungup += dialogue =>
        {
            Log($"📴 [{callId}] Caller hung up");
            try { cts.Cancel(); } catch { }
        };
    }

    private void WireRtpHandler(
        string callId, VoIPMediaSession mediaSession, CancellationTokenSource cts)
    {
        int rtpPackets = 0;
        const int FLUSH_PACKETS = 25;

        mediaSession.OnRtpPacketReceived += async (ep, mt, rtp) =>
        {
            if (mt != SDPMediaTypesEnum.audio) return;
            if (cts.Token.IsCancellationRequested) return;

            rtpPackets++;

            if (rtpPackets <= 3)
                Log($"📥 [{callId}] RTP #{rtpPackets}: {rtp.Payload?.Length ?? 0}b");

            if (rtpPackets % 100 == 0)
                Log($"📥 [{callId}] RTP total: {rtpPackets}");

            if (rtpPackets <= FLUSH_PACKETS) return;

            var payload = rtp.Payload;
            if (payload == null || payload.Length == 0) return;

            try
            {
                if (_adaClient != null && !cts.Token.IsCancellationRequested)
                    await _adaClient.SendMuLawAsync(payload);
            }
            catch (OperationCanceledException) { }
            catch { }
        };
    }

    private async Task WaitForCallEnd(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _adaClient?.IsConnected == true && !_disposed)
        {
            await Task.Delay(500, ct);
        }
    }

    private async Task CleanupCall(
        string callId, SIPUserAgent ua, VoIPMediaSession? mediaSession, CancellationTokenSource cts)
    {
        Log($"📴 [{callId}] Call ended - cleaning up");

        try { ua.Hangup(); } catch { }

        if (_adaClient != null)
        {
            try
            {
                await _adaClient.DisconnectAsync();
                _adaClient.Dispose();
            }
            catch { }
            _adaClient = null;
        }

        if (mediaSession != null)
        {
            try { mediaSession.Close("call ended"); } catch { }
        }
        _currentMediaSession = null;

        if (_callCts == cts)
        {
            try { _callCts?.Dispose(); } catch { }
            _callCts = null;
        }

        _isInCall = false;
        OnCallEnded?.Invoke(callId);
    }

    #endregion

    #region Helpers

    private IPAddress GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(_config.SipServer, _config.SipPort);
            var localEp = socket.LocalEndPoint as IPEndPoint;
            return localEp?.Address ?? IPAddress.Loopback;
        }
        catch
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip;
            }
            return IPAddress.Loopback;
        }
    }

    private async Task SendBusyResponse(SIPRequest req)
    {
        try
        {
            var busy = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null);
            await _sipTransport!.SendResponseAsync(busy);
        }
        catch { }
    }

    private async Task SendRingingResponse(string callId, SIPRequest req)
    {
        try
        {
            var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
            await _sipTransport!.SendResponseAsync(ringing);
            Log($"☎️ [{callId}] Sent 180 Ringing");
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{callId}] Failed to send ringing: {ex.Message}");
        }
    }

    private void LogNegotiatedCodec(string callId, VoIPMediaSession mediaSession)
    {
        var format = mediaSession.AudioLocalTrack?.Capabilities?.FirstOrDefault();
        if (format.HasValue && !format.Value.IsEmpty())
            Log($"🎵 [{callId}] Codec: {format.Value.Name} @ {format.Value.ClockRate}Hz");
        else
            Log($"⚠️ [{callId}] No codec negotiated!");
    }

    private void Log(string msg)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    #endregion
}
