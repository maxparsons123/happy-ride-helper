using System.Net;
using System.Diagnostics;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// SIP endpoint that auto-answers calls and bridges audio to Ada via NAudio.
/// MEMORY LEAK FIXES: Proper disposal of all resources, event unsubscription.
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
    private bool _isInCall = false;
    private bool _disposed = false;

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
        
        // MEMORY LEAK FIX: Create new CTS for this call
        _callCts = new CancellationTokenSource();
        var cts = _callCts;

        OnCallStarted?.Invoke(callId, caller);

        // RTP setup
        uint rtpTimestamp = (uint)Random.Shared.Next(0, int.MaxValue);
        VoIPMediaSession? rtpSession = null;
        
        try
        {
            rtpSession = new VoIPMediaSession(fmt => fmt.Codec == AudioCodecsEnum.PCMU);
            rtpSession.AcceptRtpFromAny = true;
            _currentMediaSession = rtpSession;

            var uas = ua.AcceptCall(req);

            // Send ringing
            try
            {
                var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
                await _sipTransport!.SendResponseAsync(ringing);
                Log($"☎️ [{callId}] Ringing...");
            }
            catch { }

            await Task.Delay(300, cts.Token); // Brief ring

            // Answer
            bool answered = await ua.Answer(uas, rtpSession);
            if (!answered)
            {
                Log($"❌ [{callId}] Failed to answer");
                return;
            }

            await rtpSession.Start();
            Log($"✅ [{callId}] Call answered");

            // Connect to Ada
            _adaClient = new AdaAudioClient(_config.AdaWsUrl);
            _adaClient.OnLog += msg => Log(msg);
            _adaClient.OnTranscript += t => OnTranscript?.Invoke(t);

            // MEMORY LEAK FIX: Store event handler reference for cleanup
            Action<string>? adaSpeakingHandler = t => { };
            _adaClient.OnAdaSpeaking += adaSpeakingHandler;

            ua.OnCallHungup += (d) =>
            {
                Log($"📴 [{callId}] Caller hung up");
                try { cts.Cancel(); } catch { }
            };

            await _adaClient.ConnectAsync(caller, cts.Token);

            // Flush initial packets
            int flushCount = 0;
            const int FLUSH_PACKETS = 25;

            // SIP → Ada (caller voice to AI)
            // MEMORY LEAK FIX: Store handler for potential cleanup
            RtpPacketReceivedHandler rtpHandler = async (ep, mt, rtp) =>
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
            rtpSession.OnRtpPacketReceived += rtpHandler;

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

            // MEMORY LEAK FIX: Unsubscribe from RTP events
            rtpSession.OnRtpPacketReceived -= rtpHandler;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"❌ [{callId}] Error: {ex.Message}");
        }
        finally
        {
            Log($"📴 [{callId}] Call ended - cleaning up");
            
            // Hangup SIP
            try { ua.Hangup(); } catch { }
            
            // MEMORY LEAK FIX: Properly dispose Ada client
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
            
            // MEMORY LEAK FIX: Close VoIPMediaSession
            if (rtpSession != null)
            {
                try
                {
                    rtpSession.Close("call ended");
                }
                catch { }
            }
            _currentMediaSession = null;
            
            // MEMORY LEAK FIX: Dispose call CTS
            if (_callCts == cts)
            {
                try { _callCts?.Dispose(); } catch { }
                _callCts = null;
            }
            
            _isInCall = false;
            OnCallEnded?.Invoke(callId);
        }
    }

    // Delegate type for RTP handler
    private delegate void RtpPacketReceivedHandler(IPEndPoint ep, SDPMediaTypesEnum mt, RTPPacket rtp);

    private void Log(string msg)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    public void Stop()
    {
        if (_disposed) return;
        
        Log("🛑 Stopping...");
        
        // Cancel any ongoing call
        try { _callCts?.Cancel(); } catch { }
        
        // MEMORY LEAK FIX: Unsubscribe from events before stopping
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
        
        // MEMORY LEAK FIX: Close current media session
        if (_currentMediaSession != null)
        {
            try { _currentMediaSession.Close("stopping"); } catch { }
            _currentMediaSession = null;
        }
        
        // MEMORY LEAK FIX: Shutdown transport and dispose channels
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
        
        // MEMORY LEAK FIX: Dispose Ada client if still active
        try
        {
            _adaClient?.Dispose();
            _adaClient = null;
        }
        catch { }
        
        // MEMORY LEAK FIX: Dispose CTS
        try
        {
            _callCts?.Dispose();
            _callCts = null;
        }
        catch { }
        
        GC.SuppressFinalize(this);
    }
}
