using System.Net;
using AdaSipClient.Core;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace AdaSipClient.Sip;

/// <summary>
/// SIPSorcery 10.x based SIP service. Handles registration, incoming calls,
/// RTP audio send/receive. G.711 A-law (PT8) hardcoded per project convention.
/// </summary>
public sealed class SipService : ISipService
{
    private readonly ILogSink _log;
    private bool _disposed;

    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _userAgent;
    private VoIPMediaSession? _mediaSession;

    public bool IsRegistered { get; private set; }
    public bool IsInCall { get; private set; }

    public event Action<string>? OnIncomingCall;
    public event Action? OnCallEnded;
    public event Action<byte[]>? OnAudioReceived;

    public SipService(ILogSink log)
    {
        _log = log;
    }

    public async Task RegisterAsync(string server, int port, string user, string password, string transport)
    {
        _transport = new SIPTransport();

        var proto = transport.ToUpper() switch
        {
            "TCP" => SIPProtocolsEnum.tcp,
            "TLS" => SIPProtocolsEnum.tls,
            _ => SIPProtocolsEnum.udp
        };

        _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));

        var regUri = SIPURI.ParseSIPURI($"sip:{user}@{server}:{port}");

        _regAgent = new SIPRegistrationUserAgent(
            _transport,
            user,
            password,
            server,
            120);

        _regAgent.RegistrationSuccessful += (uri, resp) =>
        {
            IsRegistered = true;
            _log.Log($"[SIP] Registered ✓ ({uri})");
        };
        _regAgent.RegistrationFailed += (uri, resp, err) =>
        {
            IsRegistered = false;
            _log.LogError($"[SIP] Registration failed: {err}");
        };

        // Listen for incoming calls
        _transport.SIPTransportRequestReceived += OnSipRequest;

        _regAgent.Start();
        _log.Log($"[SIP] Registering {user}@{server}:{port} ({transport})...");

        // Give registration a moment to complete
        await Task.Delay(2000);
    }

    private Task OnSipRequest(SIPEndPoint localEp, SIPEndPoint remoteEp, SIPRequest req)
    {
        if (req.Method == SIPMethodsEnum.INVITE)
        {
            var caller = req.Header.From?.FromURI?.User ?? "Unknown";
            _log.Log($"[SIP] Incoming INVITE from {caller}");

            // Store the UAS for answering
            var uas = _transport != null
                ? new SIPServerUserAgent(_transport, null, null, null, SIPCallDirection.In, null, null, null)
                : null;

            _userAgent = new SIPUserAgent(_transport, null);
            _userAgent.OnCallHungup += (dialog) =>
            {
                IsInCall = false;
                OnCallEnded?.Invoke();
                _log.Log("[SIP] Remote party hung up");
            };

            OnIncomingCall?.Invoke(caller);
        }

        return Task.CompletedTask;
    }

    public void AnswerCall()
    {
        if (_userAgent == null || _transport == null) return;

        try
        {
            // Create media session for A-law audio
            var audioFormat = new AudioFormat(AudioCodecsEnum.PCMA, 8, 8000, 1);
            _mediaSession = new VoIPMediaSession(new MediaEndPoint(new AudioFormat[] { audioFormat }));

            _mediaSession.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
            {
                if (mediaType == SDPMediaTypesEnum.audio)
                {
                    OnAudioReceived?.Invoke(rtpPacket.Payload);
                }
            };

            IsInCall = true;
            _log.Log("[SIP] Call answered (G.711 A-law)");
        }
        catch (Exception ex)
        {
            _log.LogError($"[SIP] Answer failed: {ex.Message}");
        }
    }

    public void RejectCall()
    {
        IsInCall = false;
        _userAgent = null;
        _log.Log("[SIP] Call rejected");
    }

    public void HangUp()
    {
        try
        {
            _userAgent?.Hangup();
        }
        catch { /* already disposed */ }

        CleanupCall();
        _log.Log("[SIP] Hung up");
    }

    public void SendAudio(byte[] alawFrame)
    {
        if (!IsInCall || _mediaSession == null) return;

        try
        {
            _mediaSession.SendAudio((uint)alawFrame.Length, alawFrame);
        }
        catch (Exception ex)
        {
            _log.LogError($"[SIP] SendAudio error: {ex.Message}");
        }
    }

    public void Unregister()
    {
        _regAgent?.Stop();
        IsRegistered = false;
        _log.Log("[SIP] Unregistered");
    }

    private void CleanupCall()
    {
        IsInCall = false;
        _mediaSession?.Close(null);
        _mediaSession = null;
        _userAgent = null;
        OnCallEnded?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CleanupCall();
        _regAgent?.Stop();
        _transport?.Shutdown();
        _transport = null;
    }
}
