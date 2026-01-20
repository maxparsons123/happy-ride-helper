using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TaxiSipBridge;

public class SipAutoAnswer : IDisposable
{
    private readonly SipAdaBridgeConfig _config;
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regUserAgent;
    private SIPUserAgent? _userAgent;
    private AdaAudioClient? _adaClient;
    private AdaAudioSource? _adaSource;
    private VoIPMediaSession? _currentMediaSession;
    private CancellationTokenSource? _callCts;
    private volatile bool _isInCall = false;
    private volatile bool _disposed = false;
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

    /// <summary>
    /// Get the local IP address that can reach the SIP server.
    /// </summary>
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

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SipAutoAnswer));

        Log($"🚕 SIP Auto-Answer starting...");
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
            try
            {
                var busy = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null);
                await _sipTransport!.SendResponseAsync(busy);
            }
            catch { }
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
            // Create AdaAudioSource - implements IAudioSource for proper codec negotiation
            _adaSource = new AdaAudioSource();

            // Create VoIPMediaSession with our custom audio source
            var mediaEndPoints = new MediaEndPoints 
            { 
                AudioSource = _adaSource, 
                AudioSink = null  // We handle inbound audio via AdaAudioClient
            };
            mediaSession = new VoIPMediaSession(mediaEndPoints);
            mediaSession.AcceptRtpFromAny = true;
            
            _currentMediaSession = mediaSession;

            var uas = ua.AcceptCall(req);

            try
            {
                var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
                await _sipTransport!.SendResponseAsync(ringing);
                Log($"☎️ [{callId}] Ringing...");
            }
            catch { }

            await Task.Delay(300, cts.Token);

            bool answered = await ua.Answer(uas, mediaSession);

            if (!answered)
            {
                Log($"❌ [{callId}] Failed to answer");
                return;
            }

            await mediaSession.Start();
            Log($"✅ [{callId}] Call answered, RTP started");

            // Log the negotiated codec
            var selectedFormat = mediaSession.AudioLocalTrack?.Capabilities?.FirstOrDefault();
            if (selectedFormat != null)
                Log($"🎵 [{callId}] Negotiated codec: {selectedFormat.FormatName} @ {selectedFormat.ClockRate}Hz");

            _adaClient = new AdaAudioClient(_config.AdaWsUrl);
            _adaClient.OnLog += msg => Log(msg);
            _adaClient.OnTranscript += t => OnTranscript?.Invoke(t);
            
            // Wire up AdaAudioClient to feed AdaAudioSource
            _adaClient.OnPcm24Audio += pcmBytes =>
            {
                _adaSource?.EnqueuePcm24(pcmBytes);
            };
            _adaClient.OnResponseStarted += () =>
            {
                _adaSource?.ResetFadeIn();
            };

            ua.OnCallHungup += (SIPDialogue dialogue) =>
            {
                Log($"📴 [{callId}] Caller hung up");
                try { cts.Cancel(); } catch { }
            };

            await _adaClient.ConnectAsync(caller, cts.Token);

            int flushCount = 0;
            const int FLUSH_PACKETS = 25;

            mediaSession.OnRtpPacketReceived += async (ep, mt, rtp) =>
            {
                if (mt != SDPMediaTypesEnum.audio) return;
                if (cts.Token.IsCancellationRequested) return;

                flushCount++;
                if (flushCount <= FLUSH_PACKETS) return;

                var payload = rtp.Payload;
                if (payload == null || payload.Length == 0) return;

                try
                {
                    if (_adaClient != null && !cts.Token.IsCancellationRequested)
                    {
                        await _adaClient.SendMuLawAsync(payload);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            };

            // Keep call alive
            while (!cts.IsCancellationRequested && _adaClient?.IsConnected == true && !_disposed)
            {
                await Task.Delay(500, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"❌ [{callId}] Error: {ex.Message}");
        }
        finally
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

            if (_adaSource != null)
            {
                try { _adaSource.Dispose(); } catch { }
                _adaSource = null;
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
    }

    private void Log(string msg)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
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

        try { _adaSource?.Dispose(); _adaSource = null; } catch { }
        try { _adaClient?.Dispose(); _adaClient = null; } catch { }
        try { _callCts?.Dispose(); _callCts = null; } catch { }

        GC.SuppressFinalize(this);
    }
}
