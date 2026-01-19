using System.Net;
using System.Diagnostics;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// SIP endpoint that auto-answers calls and bridges audio to Ada via NAudio.
/// </summary>
public class SipAutoAnswer : IDisposable
{
    private readonly SipAdaBridgeConfig _config;
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regUserAgent;
    private SIPUserAgent? _userAgent;
    private AdaAudioClient? _adaClient;
    private bool _isInCall = false;

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

        _regUserAgent.RegistrationSuccessful += (uri, resp) =>
        {
            Log("✅ SIP Registered - Ready for calls");
            IsRegistered = true;
            OnRegistered?.Invoke();
        };

        _regUserAgent.RegistrationFailed += (uri, resp, err) =>
        {
            Log($"❌ Registration failed: {err}");
            IsRegistered = false;
            OnRegistrationFailed?.Invoke(err);
        };

        _userAgent = new SIPUserAgent(_sipTransport, null);

        _userAgent.OnIncomingCall += async (ua, req) =>
        {
            var caller = req.Header.From.FromURI.User ?? "unknown";
            Log($"📞 Incoming call from {caller}");
            await HandleIncomingCall(ua, req, caller);
        };

        _regUserAgent.Start();
        Log("🟢 Waiting for registration...");
    }

    private async Task HandleIncomingCall(SIPUserAgent ua, SIPRequest req, string caller)
    {
        if (_isInCall)
        {
            Log("⚠️ Already in a call, rejecting");
            var busy = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null);
            await _sipTransport!.SendResponseAsync(busy);
            return;
        }

        _isInCall = true;
        var callId = Guid.NewGuid().ToString("N")[..8];

        OnCallStarted?.Invoke(callId, caller);

        // RTP setup
        uint rtpTimestamp = (uint)Random.Shared.Next(0, int.MaxValue);
        var rtpSession = new VoIPMediaSession(fmt => fmt.Codec == AudioCodecsEnum.PCMU);
        rtpSession.AcceptRtpFromAny = true;

        var uas = ua.AcceptCall(req);

        // Send ringing
        try
        {
            var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
            await _sipTransport!.SendResponseAsync(ringing);
            Log($"☎️ [{callId}] Ringing...");
        }
        catch { }

        await Task.Delay(300); // Brief ring

        // Answer
        bool answered = await ua.Answer(uas, rtpSession);
        if (!answered)
        {
            Log($"❌ [{callId}] Failed to answer");
            _isInCall = false;
            OnCallEnded?.Invoke(callId);
            return;
        }

        await rtpSession.Start();
        Log($"✅ [{callId}] Call answered");

        // Connect to Ada
        _adaClient = new AdaAudioClient(_config.AdaWsUrl);
        _adaClient.OnLog += msg => Log(msg);
        _adaClient.OnTranscript += t => OnTranscript?.Invoke(t);
        _adaClient.OnAdaSpeaking += t => { }; // Real-time speaking indicator

        var cts = new CancellationTokenSource();

        ua.OnCallHungup += (d) =>
        {
            Log($"📴 [{callId}] Caller hung up");
            cts.Cancel();
        };

        try
        {
            await _adaClient.ConnectAsync(caller, cts.Token);

            // Flush initial packets
            int flushCount = 0;
            const int FLUSH_PACKETS = 25;

            // SIP → Ada (caller voice to AI)
            rtpSession.OnRtpPacketReceived += async (ep, mt, rtp) =>
            {
                if (mt != SDPMediaTypesEnum.audio) return;

                flushCount++;
                if (flushCount <= FLUSH_PACKETS) return; // Flush initial audio

                var ulaw = rtp.Payload;
                if (ulaw == null || ulaw.Length == 0) return;

                try
                {
                    await _adaClient.SendMuLawAsync(ulaw);
                }
                catch { }
            };

            // Ada → SIP (AI voice to caller)
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                long nextFrame = 0;

                while (!cts.Token.IsCancellationRequested)
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

                        var frame = _adaClient.GetNextMuLawFrame();
                        if (frame != null)
                        {
                            rtpSession.SendRtpRaw(
                                SDPMediaTypesEnum.audio,
                                frame,
                                rtpTimestamp,
                                0, // marker
                                0  // PCMU payload type
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
            });

            // Wait for call to end
            while (!cts.IsCancellationRequested && _adaClient.IsConnected)
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
            Log($"📴 [{callId}] Call ended");
            ua.Hangup();
            await (_adaClient?.DisconnectAsync() ?? Task.CompletedTask);
            _adaClient = null;
            _isInCall = false;
            OnCallEnded?.Invoke(callId);
        }
    }

    private void Log(string msg)
    {
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    public void Stop()
    {
        Log("🛑 Stopping...");
        _regUserAgent?.Stop();
        _sipTransport?.Shutdown();
        IsRegistered = false;
    }

    public void Dispose()
    {
        Stop();
        _adaClient?.Dispose();
    }
}
