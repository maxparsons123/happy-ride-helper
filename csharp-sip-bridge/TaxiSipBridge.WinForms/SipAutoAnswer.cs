using System.Net;
using System.Diagnostics;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// SIP endpoint that auto-answers calls and bridges audio to Ada via NAudio.
/// FIXES: Proper SIP registration with realm, VoIPMediaSession constructor for SIPSorcery 6.x
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
    private volatile bool _isInCall = false;
    private volatile bool _disposed = false;

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
        
        Log($"🚕 SIP Auto-Answer starting...");
        Log($"➡ SIP Server: {_config.SipServer}:{_config.SipPort} ({_config.Transport})");
        Log($"➡ User: {_config.SipUser}");
        Log($"➡ Ada: {_config.AdaWsUrl}");

        _sipTransport = new SIPTransport();

        switch (_config.Transport)
        {
            case SipTransportType.UDP:
                _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
                break;
            case SipTransportType.TCP:
                _sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0)));
                break;
        }

        // FIX: Proper SIP registration with explicit realm and registrar host to avoid 401 errors
        var sipUri = SIPURI.ParseSIPURIRelax($"sip:{_config.SipUser}@{_config.SipServer}");
        
        _regUserAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            null,                    // outbound proxy
            sipUri,                  // SIP URI to register
            _config.SipUser,         // auth username
            _config.SipPassword,     // auth password
            _config.SipServer,       // realm (domain for auth)
            null,                    // custom contact URI
            120,                     // registration expiry in seconds
            null,                    // custom headers
            null,                    // auth username override (null = use SipUser)
            _config.SipServer);      // registrar host

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
        var statusCode = resp?.StatusCode.ToString() ?? "no response";
        Log($"❌ Registration failed: {err} (Status: {statusCode})");
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

        uint rtpTimestamp = (uint)Random.Shared.Next(0, int.MaxValue);
        VoIPMediaSession? rtpSession = null;
        
        try
        {
            // FIX: Use MediaEndPoints constructor for SIPSorcery 6.x
            // The session will auto-negotiate PCMU codec
            var mediaEndPoints = new MediaEndPoints { AudioSource = null, AudioSink = null };
            rtpSession = new VoIPMediaSession(mediaEndPoints);
            rtpSession.AcceptRtpFromAny = true;
            _currentMediaSession = rtpSession;

            // Accept the incoming call
            var uas = ua.AcceptCall(req);

            // Send 180 Ringing
            try
            {
                var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
                await _sipTransport!.SendResponseAsync(ringing);
                Log($"☎️ [{callId}] Ringing...");
            }
            catch (Exception ex)
            {
                Log($"⚠️ [{callId}] Failed to send ringing: {ex.Message}");
            }

            // Brief delay before answering
            await Task.Delay(300, cts.Token);

            // Answer the call with the RTP session
            Log($"🔄 [{callId}] Attempting to answer...");
            bool answered = await ua.Answer(uas, rtpSession);
            
            if (!answered)
            {
                Log($"❌ [{callId}] Failed to answer - check SDP negotiation");
                Log($"   Local SDP: {rtpSession.CreateOffer(null)?.ToString() ?? "null"}");
                return;
            }

            // Start the RTP session
            await rtpSession.Start();
            Log($"✅ [{callId}] Call answered, RTP active");

            // Connect to Ada WebSocket
            _adaClient = new AdaAudioClient(_config.AdaWsUrl);
            _adaClient.OnLog += msg => Log(msg);
            _adaClient.OnTranscript += t => OnTranscript?.Invoke(t);

            ua.OnCallHungup += (d) =>
            {
                Log($"📴 [{callId}] Caller hung up");
                try { cts.Cancel(); } catch { }
            };

            await _adaClient.ConnectAsync(caller, cts.Token);

            // Flush initial RTP packets (connection noise)
            int flushCount = 0;
            const int FLUSH_PACKETS = 25;

            // SIP → Ada (caller voice to AI)
            rtpSession.OnRtpPacketReceived += async (ep, mt, rtp) =>
            {
                if (mt != SDPMediaTypesEnum.audio) return;
                if (cts.Token.IsCancellationRequested) return;

                flushCount++;
                if (flushCount <= FLUSH_PACKETS) return;

                var ulaw = rtp.Payload;
                if (ulaw == null || ulaw.Length == 0) return;

                try
                {
                    if (_adaClient != null && !cts.Token.IsCancellationRequested)
                    {
                        await _adaClient.SendMuLawAsync(ulaw);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            };

            // Ada → SIP (AI voice to caller)
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                long nextFrame = 0;

                while (!cts.Token.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        long now = sw.ElapsedMilliseconds;
                        if (now < nextFrame)
                        {
                            int delay = (int)(nextFrame - now);
                            if (delay > 0 && delay < 100)
                                await Task.Delay(delay, cts.Token);
                        }

                        var frame = _adaClient?.GetNextMuLawFrame();
                        if (frame != null && rtpSession != null)
                        {
                            rtpSession.SendRtpRaw(
                                SDPMediaTypesEnum.audio,
                                frame,
                                rtpTimestamp,
                                0,
                                0
                            );

                            rtpTimestamp += 160;
                            nextFrame = sw.ElapsedMilliseconds + 20;
                        }
                        else
                        {
                            await Task.Delay(5, cts.Token);
                            nextFrame = sw.ElapsedMilliseconds;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, cts.Token);

            // Wait for call to end
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
            
            if (rtpSession != null)
            {
                try
                {
                    rtpSession.Close("call ended");
                }
                catch { }
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
        
        try
        {
            _adaClient?.Dispose();
            _adaClient = null;
        }
        catch { }
        
        try
        {
            _callCts?.Dispose();
            _callCts = null;
        }
        catch { }
        
        GC.SuppressFinalize(this);
    }
}
