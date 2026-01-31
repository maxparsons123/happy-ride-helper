using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Linq;
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
    private AdaAudioSource? _adaAudioSource;
    private VoIPMediaSession? _currentMediaSession;
    private CancellationTokenSource? _callCts;
    private volatile bool _isInCall;
    private volatile bool _disposed;
    private IPAddress? _localIp;

    // Prevent accumulating OnCallHungup handlers across multiple calls.
    private Action<SIPDialogue>? _currentHungupHandler;

    // 2-way audio safety: prevent echo by muting SIP→Ada while Ada is speaking
    private volatile bool _isBotSpeaking = false;
    private const int RMS_NOISE_FLOOR = 650;  // Below this = background noise, skip
    private const int RMS_ECHO_CEILING = 20000; // Above this = likely echo/clipping
    private const int GREETING_PROTECTION_PACKETS = 150; // ~3 seconds @ 20ms packets

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

            if (_currentHungupHandler != null)
            {
                try { _userAgent.OnCallHungup -= _currentHungupHandler; } catch { }
                _currentHungupHandler = null;
            }
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
        var caller = SipCallerId.Extract(req);
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

            // Attach RTP handler immediately so we can see packets even if Ada connect is slow.
            WireRtpInput(callId, mediaSession, cts);

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
        Log($"🔧 [{callId}] Creating AdaAudioSource + VoIPMediaSession...");
        Log($"🔧 [{callId}] Audio mode: {_config.AudioMode}");

        // Log incoming SDP to see what codecs the remote supports
        LogIncomingSdp(callId, req);

        // Create AdaAudioSource - this implements IAudioSource and will handle
        // encoding + RTP pacing via its internal timer
        _adaAudioSource = new AdaAudioSource(_config.AudioMode, _config.JitterBufferMs);
        _adaAudioSource.OnDebugLog += msg => Log(msg);

        // Log our supported codecs
        var ourFormats = _adaAudioSource.GetAudioSourceFormats();
        Log($"🔧 [{callId}] Our supported codecs: {string.Join(", ", ourFormats.Select(f => $"{f.FormatName}@{f.ClockRate}Hz"))}");

        // Create media endpoints with our custom audio source
        var mediaEndPoints = new MediaEndPoints
        {
            AudioSource = _adaAudioSource
        };

        var mediaSession = new VoIPMediaSession(mediaEndPoints);
        mediaSession.AcceptRtpFromAny = true;

        // Log our SDP offer
        LogOutgoingSdp(callId, mediaSession);

        Log($"🔧 [{callId}] AcceptRtpFromAny={mediaSession.AcceptRtpFromAny}");

        mediaSession.OnAudioFormatsNegotiated += formats =>
        {
            var fmt = formats.FirstOrDefault();
            onFormatNegotiated(fmt);
            
            // Detailed codec info
            var codecName = fmt.FormatName ?? "UNKNOWN";
            var codecType = fmt.Codec switch
            {
                AudioCodecsEnum.PCMU => "G.711 μ-law (PCMU)",
                AudioCodecsEnum.PCMA => "G.711 A-law (PCMA)",
                AudioCodecsEnum.OPUS => "Opus",
                AudioCodecsEnum.G722 => "G.722",
                AudioCodecsEnum.G729 => "G.729",
                _ => codecName
            };
            
            Log($"═══════════════════════════════════════════════════════");
            Log($"🎵 [{callId}] NEGOTIATED CODEC: {codecType}");
            Log($"   ├─ Format Name: {codecName}");
            Log($"   ├─ Clock Rate: {fmt.ClockRate}Hz");
            Log($"   ├─ Payload Type: {fmt.FormatID}");
            Log($"   └─ Channels: {fmt.ChannelCount}");
            Log($"═══════════════════════════════════════════════════════");
            
            // Set the format on our audio source so it knows how to encode
            _adaAudioSource?.SetAudioSourceFormat(fmt);
        };

        // Add handler for RTP events at session level for diagnostics
        mediaSession.OnRtpEvent += (ep, evt, hdr) =>
        {
            Log($"📡 [{callId}] RTP Event: {evt}");
        };

        mediaSession.OnTimeout += (mt) =>
        {
            Log($"⏰ [{callId}] Media timeout: {mt}");
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

        Log($"🔧 [{callId}] Starting media session (this starts AdaAudioSource timer)...");
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

        Log($"✅ [{callId}] Ada audio wired");
    }

    private void WireAdaAudioOutput(
        string callId, VoIPMediaSession mediaSession,
        AudioFormat negotiatedFormat, CancellationTokenSource cts)
    {
        if (_adaClient == null || _adaAudioSource == null) return;

        int chunkCount = 0;

        Log($"🔧 [{callId}] Wiring Ada audio → AdaAudioSource.EnqueuePcm24");

        // Debug: log what type we have and what events are available
        var clientType = _adaClient.GetType();
        Log($"🔍 [{callId}] AdaAudioClient type: {clientType.FullName}");
        var allEvents = clientType.GetEvents();
        Log($"🔍 [{callId}] Available events: {string.Join(", ", allEvents.Select(e => e.Name))}");

        // IMPORTANT: to stay compatible with older AdaAudioClient builds (where these events may not exist),
        // we wire via reflection and fall back to mu-law polling.
        try
        {
            var pcmEvt = clientType.GetEvent("OnPcm24Audio");
            if (pcmEvt != null)
            {
                Action<byte[]> pcmHandler = (pcmBytes) =>
                {
                    if (cts.Token.IsCancellationRequested) return;

                    // Set bot-speaking flag when Ada starts sending audio
                    _isBotSpeaking = true;

                    chunkCount++;
                    if (chunkCount <= 5)
                        Log($"🔊 [{callId}] Ada audio #{chunkCount}: {pcmBytes.Length}b → AdaAudioSource");

                    _adaAudioSource?.EnqueuePcm24(pcmBytes);
                };

                pcmEvt.AddEventHandler(_adaClient, pcmHandler);

                var responseEvt = clientType.GetEvent("OnResponseStarted");
                if (responseEvt != null)
                {
                    Action responseHandler = () => _adaAudioSource?.ResetFadeIn();
                    responseEvt.AddEventHandler(_adaClient, responseHandler);
                }

                // Wire up queue empty detection to clear bot-speaking flag
                if (_adaAudioSource != null)
                {
                    _adaAudioSource.OnQueueEmpty += () =>
                    {
                        _isBotSpeaking = false;
                    };
                }

                Log($"✅ [{callId}] Ada audio wired via OnPcm24Audio (reflection) + bot-speaking protection");
                return;
            }

            Log($"⚠️ [{callId}] OnPcm24Audio not found in {allEvents.Length} events; using mu-law polling fallback");
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{callId}] Failed to wire PCM events; using fallback. Error: {ex.Message}");
        }

        // Fallback: poll mu-law frames from AdaAudioClient and inject directly.
        // This only works for G.711 calls (PCMU/PCMA). Opus requires PCM.
        if (negotiatedFormat.Codec == AudioCodecsEnum.OPUS)
        {
            Log($"⚠️ [{callId}] Opus negotiated but PCM event not available - cannot send Ada audio.");
            return;
        }

        _ = Task.Run(async () =>
        {
            int fallbackChunks = 0;
            while (!cts.IsCancellationRequested && !_disposed)
            {
                byte[]? ulaw = null;
                try { ulaw = _adaClient?.GetNextMuLawFrame(); } catch { }

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
                    mediaSession.SendAudio((uint)ulaw.Length, ulaw);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, cts.Token);
    }

    private void WireHangupHandler(string callId, CancellationTokenSource cts)
    {
        // Ensure we only cancel once per call.
        int callEndedSignaled = 0;

        // Remove any previous handler (old call) from the UA.
        if (_currentHungupHandler != null)
        {
            try { _userAgent!.OnCallHungup -= _currentHungupHandler; } catch { }
            _currentHungupHandler = null;
        }

        _currentHungupHandler = dialogue =>
        {
            if (System.Threading.Interlocked.Exchange(ref callEndedSignaled, 1) == 1) return;
            Log($"📴 [{callId}] Caller hung up");
            try { cts.Cancel(); } catch { }

            // Unsubscribe immediately to avoid duplicates.
            if (_currentHungupHandler != null)
            {
                try { _userAgent!.OnCallHungup -= _currentHungupHandler; } catch { }
                _currentHungupHandler = null;
            }
        };
        _userAgent!.OnCallHungup += _currentHungupHandler;
    }

    private void WireRtpInput(string callId, VoIPMediaSession mediaSession, CancellationTokenSource cts)
    {
        Log($"🔧 [{callId}] Wiring RTP input handler with RMS gating...");

        int rtpPackets = 0;
        int sentToAda = 0;
        int skippedNoClient = 0;
        int skippedNotConnected = 0;
        int skippedBotSpeaking = 0;
        int skippedLowRms = 0;
        int skippedHighRms = 0;
        DateTime lastStats = DateTime.Now;

        mediaSession.OnRtpPacketReceived += async (ep, mt, rtp) =>
        {
            if (mt != SDPMediaTypesEnum.audio) return;
            if (cts.Token.IsCancellationRequested) return;

            rtpPackets++;

            if (rtpPackets <= 3)
                Log($"📥 [{callId}] RTP #{rtpPackets}: {rtp.Payload?.Length ?? 0}b");

            if (rtpPackets % 100 == 0)
                Log($"📥 [{callId}] RTP total: {rtpPackets}");

            // Greeting protection: skip first ~3 seconds to let Ada's greeting play
            if (rtpPackets <= GREETING_PROTECTION_PACKETS) return;

            var payload = rtp.Payload;
            if (payload == null || payload.Length == 0) return;

            // Bot-speaking protection: don't forward audio while Ada is speaking (prevents echo)
            if (_isBotSpeaking)
            {
                skippedBotSpeaking++;
                return;
            }

            try
            {
                var client = _adaClient;
                if (client == null)
                {
                    skippedNoClient++;
                    if (skippedNoClient <= 3)
                        Log($"⚠️ [{callId}] RTP→Ada skip: _adaClient is null");
                    return;
                }

                if (!client.IsConnected)
                {
                    skippedNotConnected++;
                    if (skippedNotConnected <= 3)
                        Log($"⚠️ [{callId}] RTP→Ada skip: WS not connected yet");
                    return;
                }

                // RMS-based noise gate: only forward if audio is meaningful speech
                // Decode mu-law/A-law and calculate RMS
                bool isMuLaw = rtp.Header.PayloadType == 0;
                long sumOfSquares = 0;
                
                for (int i = 0; i < payload.Length; i++)
                {
                    short sample = isMuLaw
                        ? MuLawDecode(payload[i])
                        : ALawDecode(payload[i]);
                    sumOfSquares += (long)sample * sample;
                }
                
                double rms = Math.Sqrt(sumOfSquares / (double)payload.Length);

                // Filter: too quiet = noise, too loud = echo/clipping
                if (rms < RMS_NOISE_FLOOR)
                {
                    skippedLowRms++;
                    return;
                }
                
                if (rms > RMS_ECHO_CEILING)
                {
                    skippedHighRms++;
                    return;
                }

                if (!cts.Token.IsCancellationRequested)
                {
                    await client.SendMuLawAsync(payload);
                    sentToAda++;
                    
                    if (sentToAda <= 5)
                        Log($"🎙️ [{callId}] RTP→Ada #{sentToAda}: {payload.Length}b, RMS={rms:F0}");
                    else if ((DateTime.Now - lastStats).TotalSeconds >= 5)
                    {
                        Log($"📤 [{callId}] RTP→Ada: sent={sentToAda}, botSpeak={skippedBotSpeaking}, lowRms={skippedLowRms}, highRms={skippedHighRms}");
                        lastStats = DateTime.Now;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) 
            { 
                Log($"⚠️ [{callId}] RTP→Ada error: {ex.Message}");
            }
        };
    }

    // Simple mu-law decode (inline for performance)
    private static short MuLawDecode(byte mulaw)
    {
        mulaw = (byte)~mulaw;
        int sign = (mulaw & 0x80) != 0 ? -1 : 1;
        int exponent = (mulaw >> 4) & 0x07;
        int mantissa = mulaw & 0x0F;
        return (short)(sign * (((mantissa << 3) + 0x84) << exponent) - 0x84);
    }

    // Simple A-law decode (inline for performance)
    private static short ALawDecode(byte alaw)
    {
        alaw ^= 0x55;
        int sign = (alaw & 0x80) != 0 ? -1 : 1;
        int exponent = (alaw >> 4) & 0x07;
        int mantissa = alaw & 0x0F;
        int magnitude = exponent == 0
            ? (mantissa << 4) + 8
            : ((mantissa << 4) + 0x108) << (exponent - 1);
        return (short)(sign * magnitude);
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

        if (_adaAudioSource != null)
        {
            try { _adaAudioSource.Dispose(); } catch { }
            _adaAudioSource = null;
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
        try
        {
            // Log local (what we send) and remote (what we receive) formats
            var localFormat = mediaSession.AudioLocalTrack?.Capabilities?.FirstOrDefault();
            var remoteFormat = mediaSession.AudioRemoteTrack?.Capabilities?.FirstOrDefault();
            
            Log($"🔊 [{callId}] Audio Track Summary:");
            
            if (localFormat.HasValue && !localFormat.Value.IsEmpty())
            {
                var f = localFormat.Value;
                Log($"   ├─ TX (to SIP): {f.Name()} @ {f.ClockRate()}Hz (PT={f.ID})");
            }
            else
            {
                Log($"   ├─ TX: Not negotiated");
            }
            
            if (remoteFormat.HasValue && !remoteFormat.Value.IsEmpty())
            {
                var f = remoteFormat.Value;
                Log($"   └─ RX (from SIP): {f.Name()} @ {f.ClockRate()}Hz (PT={f.ID})");
            }
            else
            {
                Log($"   └─ RX: Not negotiated");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{callId}] LogNegotiatedCodec error: {ex.Message}");
        }
    }

    /// <summary>
    /// Log SDP offer from incoming INVITE to see what codecs the remote supports.
    /// </summary>
    private void LogIncomingSdp(string callId, SIPRequest req)
    {
        try
        {
            if (req.Body == null || string.IsNullOrEmpty(req.Body))
            {
                Log($"📋 [{callId}] No SDP in INVITE");
                return;
            }

            var sdp = SDP.ParseSDPDescription(req.Body);
            if (sdp == null)
            {
                Log($"📋 [{callId}] Could not parse SDP");
                return;
            }

            Log($"═══════════════════════════════════════════════════════");
            Log($"📋 [{callId}] INCOMING SDP OFFER (what remote supports):");
            
            var audioAnnouncement = sdp.Media?.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
            if (audioAnnouncement != null)
            {
                var formats = audioAnnouncement.MediaFormats;
                Log($"   Remote audio codecs ({formats.Count} total):");
                
                foreach (var fmt in formats.Values.OrderBy(f => f.ID))
                {
                    var codecName = fmt.ID switch
                    {
                        0 => "PCMU (G.711 μ-law)",
                        8 => "PCMA (G.711 A-law)",
                        9 => "G.722 (16kHz wideband)",
                        _ => fmt.Name()
                    };
                    Log($"      PT {fmt.ID,3}: {codecName} @ {fmt.ClockRate()}Hz");
                }
                
                // Check for wideband codec support
                bool hasG722 = formats.ContainsKey(9);
                bool hasOpus = formats.Values.Any(f => f.Name().ToUpper().Contains("OPUS"));
                
                if (hasOpus)
                    Log($"   ✅ Remote supports OPUS (48kHz HD)");
                else if (hasG722)
                    Log($"   ✅ Remote supports G.722 (16kHz wideband)");
                else
                    Log($"   ⚠️ Remote only supports narrowband (G.711)");
            }
            else
            {
                Log($"   No audio media in SDP");
            }
            
            Log($"═══════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{callId}] LogIncomingSdp error: {ex.Message}");
        }
    }

    /// <summary>
    /// Log our SDP answer to show what codecs we're offering.
    /// </summary>
    private void LogOutgoingSdp(string callId, VoIPMediaSession mediaSession)
    {
        try
        {
            var sdp = mediaSession.CreateOffer(null);
            if (sdp == null)
            {
                Log($"📋 [{callId}] Could not get our SDP offer");
                return;
            }

            Log($"═══════════════════════════════════════════════════════");
            Log($"📋 [{callId}] OUR SDP OFFER (what we advertise):");
            
            var audioAnnouncement = sdp.Media?.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
            if (audioAnnouncement != null)
            {
                var formats = audioAnnouncement.MediaFormats;
                Log($"   Our audio codecs ({formats.Count} total):");
                
                foreach (var fmt in formats.Values.OrderBy(f => f.ID))
                {
                    var codecName = fmt.ID switch
                    {
                        0 => "PCMU (G.711 μ-law)",
                        8 => "PCMA (G.711 A-law)",
                        9 => "G.722 (16kHz wideband)",
                        111 => "OPUS (48kHz HD)",
                        _ => fmt.Name()
                    };
                    Log($"      PT {fmt.ID,3}: {codecName} @ {fmt.ClockRate()}Hz");
                }
            }
            
            Log($"═══════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{callId}] LogOutgoingSdp error: {ex.Message}");
        }
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
