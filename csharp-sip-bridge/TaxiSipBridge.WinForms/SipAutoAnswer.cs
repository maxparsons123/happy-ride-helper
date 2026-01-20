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
            Log($"🔧 [{callId}] Creating AdaAudioSource...");
            
            // Create AdaAudioSource - implements IAudioSource for proper codec negotiation
            _adaSource = new AdaAudioSource();
            TryWireAdaSourceDebug(_adaSource);
            
            // ENABLE TEST TONE MODE - sends 440Hz sine wave to verify codec works
            // Comment out this line to use real Ada audio
            _adaSource.EnableTestTone(true);
            Log($"🔊 [{callId}] TEST TONE MODE ENABLED - you should hear a 440Hz beep");
            // Create VoIPMediaSession with our custom audio source
            var mediaEndPoints = new MediaEndPoints 
            { 
                AudioSource = _adaSource, 
                AudioSink = null  // We handle inbound audio via AdaAudioClient
            };
            Log($"🔧 [{callId}] Creating VoIPMediaSession...");
            mediaSession = new VoIPMediaSession(mediaEndPoints);
            mediaSession.AcceptRtpFromAny = true;
            
            _currentMediaSession = mediaSession;

            Log($"🔧 [{callId}] Accepting call...");
            var uas = ua.AcceptCall(req);

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

            await Task.Delay(300, cts.Token);

            Log($"🔧 [{callId}] Answering call...");
            bool answered = await ua.Answer(uas, mediaSession);

            if (!answered)
            {
                Log($"❌ [{callId}] ua.Answer() returned false");
                return;
            }

            Log($"🔧 [{callId}] Starting media session...");
            await mediaSession.Start();
            Log($"✅ [{callId}] Media session started");

            // Log the negotiated codec
            var selectedFormat = mediaSession.AudioLocalTrack?.Capabilities?.FirstOrDefault();
            if (selectedFormat.HasValue && !selectedFormat.Value.IsEmpty())
                Log($"🎵 [{callId}] Negotiated codec: ID {selectedFormat.Value.ID} @ {selectedFormat.Value.ClockRate}Hz");
            else
                Log($"⚠️ [{callId}] No codec negotiated!");

            // Log RTP endpoint info
            Log($"📡 [{callId}] Media session AcceptRtpFromAny: {mediaSession.AcceptRtpFromAny}");
            Log($"🔧 [{callId}] Creating AdaAudioClient...");
            _adaClient = new AdaAudioClient(_config.AdaWsUrl);
            _adaClient.OnLog += msg => Log(msg);
            _adaClient.OnTranscript += t => OnTranscript?.Invoke(t);
            
            // Wire up AdaAudioClient to feed AdaAudioSource (reflection-based so build doesn't break if events are missing)
            if (_adaClient != null && _adaSource != null)
            {
                WireAdaClientToSource(_adaClient, _adaSource, cts.Token);
            }

            ua.OnCallHungup += (SIPDialogue dialogue) =>
            {
                Log($"📴 [{callId}] Caller hung up");
                try { cts.Cancel(); } catch { }
            };

            Log($"🔧 [{callId}] Connecting to Ada...");
            await _adaClient.ConnectAsync(caller, cts.Token);

            int flushCount = 0;
            int rtpPackets = 0;
            const int FLUSH_PACKETS = 25;

            mediaSession.OnRtpPacketReceived += async (ep, mt, rtp) =>
            {
                if (mt != SDPMediaTypesEnum.audio) return;
                if (cts.Token.IsCancellationRequested) return;

                flushCount++;
                rtpPackets++;
                
                // Log first few packets
                if (rtpPackets <= 3)
                    Log($"📥 [{callId}] RTP #{rtpPackets}: {rtp.Payload?.Length ?? 0}b from {ep}");
                
                // Log every 100 packets
                if (rtpPackets % 100 == 0)
                    Log($"📥 [{callId}] RTP packets received: {rtpPackets}");

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

            Log($"✅ [{callId}] Call fully established - waiting for hangup");
            
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

    private void TryWireAdaSourceDebug(AdaAudioSource source)
    {
        try
        {
            var evt = source.GetType().GetEvent("OnDebugLog");
            if (evt == null) return;

            Action<string> handler = (msg) => Log(msg);
            evt.AddEventHandler(source, handler);
        }
        catch { }
    }

    private void WireAdaClientToSource(AdaAudioClient client, AdaAudioSource source, CancellationToken ct)
    {
        bool wiredPcm = false;

        // Preferred path: subscribe to OnPcm24Audio if present.
        try
        {
            var evt = client.GetType().GetEvent("OnPcm24Audio");
            if (evt != null)
            {
                Action<byte[]> handler = (pcmBytes) => source.EnqueuePcm24(pcmBytes);
                evt.AddEventHandler(client, handler);
                wiredPcm = true;
            }
        }
        catch { }

        // Optional: reset fade-in on response start.
        try
        {
            var evt = client.GetType().GetEvent("OnResponseStarted");
            if (evt != null)
            {
                Action handler = () => source.ResetFadeIn();
                evt.AddEventHandler(client, handler);
            }
        }
        catch { }

        // Fallback path (older AdaAudioClient): poll ulaw frames and convert to PCM24.
        if (!wiredPcm)
        {
            Log("⚠️ OnPcm24Audio not available; using ulaw polling fallback");

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && !_disposed)
                {
                    var ulaw = client.GetNextMuLawFrame();
                    if (ulaw == null)
                    {
                        await Task.Delay(5, ct);
                        continue;
                    }

                    try
                    {
                        var pcm8k = AudioCodecs.MuLawDecode(ulaw);
                        var pcm24k = AudioCodecs.Resample(pcm8k, 8000, 24000);
                        var pcmBytes = AudioCodecs.ShortsToBytes(pcm24k);
                        source.EnqueuePcm24(pcmBytes);
                    }
                    catch { }
                }
            }, ct);
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
