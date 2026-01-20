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
/// Uses AudioExtrasSource for audio injection.
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

    #region Public Methods

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SipAutoAnswer));

        Log("🚕 SIP Auto-Answer starting...");
        Log($"➡ SIP Server: {_config.SipServer}:{_config.SipPort} ({_config.Transport})");
        Log($"➡ User: {_config.SipUser}");
        Log($"➡ Ada: {_config.AdaWsUrl}");

        _localIp = GetLocalIp();
        Log($"➡ Local IP: {_localIp}");

        InitializeSipTransport();
        InitializeRegistration();
        InitializeUserAgent();

        _regUserAgent!.Start();
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

    #endregion

    #region Initialization

    private void InitializeSipTransport()
    {
        _sipTransport = new SIPTransport();

        switch (_config.Transport)
        {
            case SipTransportType.UDP:
                _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(_localIp!, 0)));
                break;
            case SipTransportType.TCP:
                _sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(_localIp!, 0)));
                break;
        }
    }

    private void InitializeRegistration()
    {
        _regUserAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            _config.SipUser,
            _config.SipPassword,
            _config.SipServer,
            120);

        _regUserAgent.RegistrationSuccessful += OnRegistrationSuccess;
        _regUserAgent.RegistrationFailed += OnRegistrationFailure;
    }

    private void InitializeUserAgent()
    {
        _userAgent = new SIPUserAgent(_sipTransport, null);
        _userAgent.OnIncomingCall += OnIncomingCallAsync;
    }

    #endregion

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
        AudioFormat negotiatedFormat = default;

        try
        {
            mediaSession = await SetupMediaSession(callId, ua, req, cts.Token, f => negotiatedFormat = f);
            if (mediaSession == null) return;

            await SetupAdaConnection(callId, caller, mediaSession, negotiatedFormat, cts);

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
        string callId, SIPUserAgent ua, SIPRequest req, CancellationToken ct,
        Action<AudioFormat> onFormatNegotiated)
    {
        Log($"🔧 [{callId}] Creating VoIPMediaSession...");

        var mediaSession = new VoIPMediaSession();
        mediaSession.AcceptRtpFromAny = true;

        // Disable AudioExtrasSource (hold music, etc.) - we inject audio via SendAudio
        mediaSession.AudioExtrasSource.SetSource(AudioSourcesEnum.None);

        mediaSession.OnAudioFormatsNegotiated += formats =>
        {
            var fmt = formats.FirstOrDefault();
            onFormatNegotiated(fmt);
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

    private async Task SetupAdaConnection(
        string callId, string caller, VoIPMediaSession mediaSession,
        AudioFormat negotiatedFormat, CancellationTokenSource cts)
    {
        Log($"🔧 [{callId}] Creating AdaAudioClient...");

        _adaClient = new AdaAudioClient(_config.AdaWsUrl);
        _adaClient.OnLog += msg => Log(msg);
        _adaClient.OnTranscript += t => OnTranscript?.Invoke(t);

        WireAdaAudioOutput(callId, mediaSession, negotiatedFormat, cts);
        WireHangupHandler(callId, cts);

        Log($"🔧 [{callId}] Connecting to Ada...");
        await _adaClient.ConnectAsync(caller, cts.Token);

        WireRtpInput(callId, mediaSession, cts);

        Log($"✅ [{callId}] Ada audio wired");
    }

    private void WireAdaAudioOutput(
        string callId, VoIPMediaSession mediaSession,
        AudioFormat negotiatedFormat, CancellationTokenSource cts)
    {
        if (_adaClient == null) return;

        int chunkCount = 0;

        // Use reflection so SipAutoAnswer compiles even if an older AdaAudioClient
        // implementation (without OnPcm24Audio) is being referenced by the project.
        try
        {
            var evt = _adaClient.GetType().GetEvent("OnPcm24Audio");
            if (evt != null)
            {
                Action<byte[]> handler = (pcmBytes) =>
                {
                    if (cts.Token.IsCancellationRequested) return;

                    chunkCount++;
                    if (chunkCount <= 5)
                        Log($"🔊 [{callId}] Ada audio #{chunkCount}: {pcmBytes.Length}b");

                    try
                    {
                        // Convert bytes to shorts (24kHz PCM16)
                        var pcm24 = AudioCodecs.BytesToShorts(pcmBytes);

                        // Resample 24kHz → 8kHz
                        var pcm8k = AudioCodecs.Resample(pcm24, 24000, 8000);

                        // Encode to mu-law
                        var ulaw = AudioCodecs.MuLawEncode(pcm8k);

                        // Send via RTP using SendAudio (duration in RTP units = sample count)
                        uint durationRtpUnits = (uint)ulaw.Length;
                        mediaSession.SendAudio(durationRtpUnits, ulaw);

                        if (chunkCount <= 5)
                            Log($"📤 [{callId}] Sent {ulaw.Length}b ulaw via SendAudio");
                    }
                    catch (Exception ex)
                    {
                        if (chunkCount <= 5)
                            Log($"⚠️ [{callId}] SendAudio error: {ex.Message}");
                    }
                };

                evt.AddEventHandler(_adaClient, handler);
                Log($"🔧 [{callId}] Wired Ada audio via OnPcm24Audio → SendAudio");
                return;
            }
            else
            {
                Log($"⚠️ [{callId}] OnPcm24Audio event not found on AdaAudioClient");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{callId}] Failed to wire OnPcm24Audio: {ex.Message}");
        }

        // Fallback: poll mu-law frames produced by AdaAudioClient
        Log($"⚠️ [{callId}] Using mu-law polling fallback → SendAudio");

        _ = Task.Run(async () =>
        {
            int fallbackChunks = 0;
            while (!cts.IsCancellationRequested && !_disposed)
            {
                byte[]? ulaw = null;
                try
                {
                    ulaw = _adaClient?.GetNextMuLawFrame();
                }
                catch { }

                if (ulaw == null)
                {
                    await Task.Delay(5, cts.Token);
                    continue;
                }

                fallbackChunks++;
                if (fallbackChunks <= 5)
                    Log($"🔊 [{callId}] Fallback ulaw #{fallbackChunks}: {ulaw.Length}b");

                try
                {
                    uint durationRtpUnits = (uint)ulaw.Length;
                    mediaSession.SendAudio(durationRtpUnits, ulaw);

                    if (fallbackChunks <= 5)
                        Log($"📤 [{callId}] Sent {ulaw.Length}b ulaw via SendAudio (fallback)");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { }
            }
        }, cts.Token);
    }

    private void WireHangupHandler(string callId, CancellationTokenSource cts)
    {
        _userAgent!.OnCallHungup += dialogue =>
        {
            Log($"📴 [{callId}] Caller hung up");
            try { cts.Cancel(); } catch { }
        };
    }

    private void WireRtpInput(string callId, VoIPMediaSession mediaSession, CancellationTokenSource cts)
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

    /// <summary>
    /// Simple linear resampling from one rate to another.
    /// </summary>
    private static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;
        if (input.Length == 0) return input;

        double ratio = (double)fromRate / toRate;
        int outputLength = (int)(input.Length / ratio);
        var output = new short[outputLength];

        for (int i = 0; i < output.Length; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            if (srcIndex + 1 < input.Length)
                output[i] = (short)(input[srcIndex] * (1 - frac) + input[srcIndex + 1] * frac);
            else if (srcIndex < input.Length)
                output[i] = input[srcIndex];
        }

        return output;
    }

    private void Log(string msg)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    #endregion
}
