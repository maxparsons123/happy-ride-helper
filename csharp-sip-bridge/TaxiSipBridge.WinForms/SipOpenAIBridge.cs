using System.Net;
using System.Collections.Generic;
using System.Linq;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Bridge between SIPSorcery and OpenAIRealtimeClient for local AI processing.
/// Handles SIP call setup, RTP audio, and routes audio to/from OpenAI directly.
/// </summary>
public class SipOpenAIBridge : IDisposable
{
    private readonly string _apiKey;
    private readonly string _sipServer;
    private readonly int _sipPort;
    private readonly string _sipUser;
    private readonly string _sipPassword;
    private readonly SipTransportType _transportType;
    private readonly SipAdaBridgeConfig? _config;

    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regUserAgent;
    private SIPUserAgent? _userAgent;
    private VoIPMediaSession? _mediaSession;
    private AdaAudioSource? _adaAudioSource;
    private OpenAIRealtimeClient? _aiClient;
    private CancellationTokenSource? _callCts;

    // Remote SDP payload type → codec mapping (critical for endpoints that use dynamic PTs)
    private readonly Dictionary<int, AudioCodecsEnum> _remotePtToCodec = new();

    private volatile bool _disposed;
    private volatile bool _isInCall;
    private string? _currentCallId;
    private string? _currentCallIdSip;  // SIP Call-ID header for re-INVITE detection

    // Flush initial RTP packets to avoid stale audio
    private const int FLUSH_PACKETS = 25;
    private int _inboundPacketCount;
    private bool _inboundFlushComplete;

    public event Action<string>? OnLog;
    public event Action? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnCallerAudioMonitor;  // Processed caller audio for local playback

    public SipOpenAIBridge(
        string apiKey,
        string sipServer,
        int sipPort,
        string sipUser,
        string sipPassword,
        SipTransportType transportType = SipTransportType.UDP,
        SipAdaBridgeConfig? config = null)
    {
        _apiKey = apiKey;
        _sipServer = sipServer;
        _sipPort = sipPort;
        _sipUser = sipUser;
        _sipPassword = sipPassword;
        _transportType = transportType;
        _config = config;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SipOpenAIBridge));

        Log("🚀 Starting SIP-OpenAI Bridge...");

        _sipTransport = new SIPTransport();

        // Configure STUN for NAT traversal if enabled
        if (_config?.EnableStun == true && !string.IsNullOrEmpty(_config.StunServer))
        {
            var stunUri = new STUNUri(SIPSchemesEnum.stun, _config.StunServer, _config.StunPort);
            Log($"🌐 STUN enabled: {_config.StunServer}:{_config.StunPort}");
            
            // SIPSorcery uses STUNClient for NAT detection - we'll configure it on the channel
            // The actual STUN binding happens when we create RTP sessions
        }

        // Add appropriate channel based on transport type
        if (_transportType == SipTransportType.TCP)
        {
            _sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0)));
            Log("📡 Using TCP transport");
        }
        else
        {
            _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
            Log("📡 Using UDP transport");
        }

        // Set up SIP registration
        // Resolve domain name to IP if needed for SIPSorcery compatibility
        string resolvedServerHost = _sipServer;
        IPAddress? serverIp = null;
        
        if (!IPAddress.TryParse(_sipServer, out serverIp))
        {
            try
            {
                var addresses = Dns.GetHostAddresses(_sipServer);
                serverIp = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (serverIp != null)
                {
                    resolvedServerHost = serverIp.ToString();
                    Log($"📡 Resolved {_sipServer} → {resolvedServerHost}");
                }
                else
                {
                    Log($"⚠️ No IPv4 address found for {_sipServer}, using hostname");
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ DNS resolution failed for {_sipServer}: {ex.Message}, using hostname");
            }
        }
        else
        {
            Log($"📡 Using IP address directly: {_sipServer}");
        }

        // Use resolved IP for SIP registration (SIPSorcery needs literal IP)
        _regUserAgent = new SIPRegistrationUserAgent(_sipTransport, _sipUser, _sipPassword, resolvedServerHost, 120);

        _regUserAgent.RegistrationSuccessful += (uri, resp) =>
        {
            Log($"✅ Registered as {_sipUser}@{_sipServer}");
            OnRegistered?.Invoke();
        };

        _regUserAgent.RegistrationFailed += (uri, resp, err) =>
        {
            var errMsg = err ?? resp?.ReasonPhrase ?? "Registration failed";
            Log($"❌ Registration failed: {errMsg}");
            OnRegistrationFailed?.Invoke(errMsg);
        };

        _regUserAgent.Start();

        // Create user agent for handling calls
        _userAgent = new SIPUserAgent(_sipTransport, null);
        _userAgent.ServerCallCancelled += (uas, cancelReq) => Log("📞 Call cancelled by remote");

        // Listen for incoming calls
        _sipTransport.SIPTransportRequestReceived += OnSipRequestReceived;

        Log("📞 Listening for incoming calls...");
    }

    private async Task OnSipRequestReceived(
        SIPEndPoint localSipEndPoint,
        SIPEndPoint remoteEndPoint,
        SIPRequest sipRequest)
    {
        if (sipRequest.Method == SIPMethodsEnum.INVITE)
        {
            await HandleIncomingCall(sipRequest);
        }
        else if (sipRequest.Method == SIPMethodsEnum.BYE)
        {
            Log("📕 BYE received");
            await EndCallAsync();
            var okResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
            await _sipTransport!.SendResponseAsync(okResponse);
        }
    }

    private async Task HandleIncomingCall(SIPRequest inviteRequest)
    {
        // Check if this is a re-INVITE for the current call (codec renegotiation, hold, etc.)
        var incomingCallId = inviteRequest.Header.CallId;
        if (_isInCall && _currentCallIdSip == incomingCallId)
        {
            // Don't respond here - let the SIPUserAgent/dialogue handle re-INVITEs
            // Sending our own response causes protocol errors (missing SDP body)
            Log($"🔄 [{_currentCallId}] Re-INVITE received (same Call-ID), ignoring (handled by SIP stack)");
            return;
        }
        
        if (_disposed || _isInCall)
        {
            Log($"⚠️ Rejecting call - already in a call or disposed (incoming={incomingCallId}, current={_currentCallIdSip})");
            var busyResponse = SIPResponse.GetResponse(inviteRequest, SIPResponseStatusCodesEnum.BusyHere, null);
            await _sipTransport!.SendResponseAsync(busyResponse);
            return;
        }

        _isInCall = true;
        _currentCallId = Guid.NewGuid().ToString("N")[..8];
        _currentCallIdSip = incomingCallId;  // Store SIP Call-ID for re-INVITE detection
        _callCts = new CancellationTokenSource();
        _inboundPacketCount = 0;
        _inboundFlushComplete = false;
        _remotePtToCodec.Clear();

        var caller = inviteRequest.Header.From.FromURI.User ?? "unknown";
        Log($"📞 [{_currentCallId}] Incoming call from {caller}");
        OnCallStarted?.Invoke(_currentCallId, caller);

        try
        {
            // Parse remote SDP to detect available codecs and capture exact PTs.
            // For incoming calls (UAS), we should answer using ONLY payload types that the remote offered.
            bool remoteOffersOpus = false;
            bool remoteOffersG722 = false;
            bool remoteOffersPcmu = false;
            bool remoteOffersPcma = false;
            int? remoteOpusPt = null;
            int remoteOpusChannels = 1;
            string? remoteOpusFmtp = null;
            try
            {
                var sdpBody = inviteRequest.Body;
                if (!string.IsNullOrEmpty(sdpBody))
                {
                    var sdp = SDP.ParseSDPDescription(sdpBody);
                    var audioMedia = sdp.Media.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
                    if (audioMedia != null)
                    {
                        // Build PT→codec map from remote SDP (don't assume PT=0/8)
                        _remotePtToCodec.Clear();
                        foreach (var f in audioMedia.MediaFormats)
                        {
                            var pt = f.Key;
                            var name = f.Value.Name();
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            if (name.Equals("PCMA", StringComparison.OrdinalIgnoreCase))
                                _remotePtToCodec[pt] = AudioCodecsEnum.PCMA;
                            else if (name.Equals("PCMU", StringComparison.OrdinalIgnoreCase))
                                _remotePtToCodec[pt] = AudioCodecsEnum.PCMU;
                            else if (name.Equals("G722", StringComparison.OrdinalIgnoreCase))
                                _remotePtToCodec[pt] = AudioCodecsEnum.G722;
                            else if (name.Equals("opus", StringComparison.OrdinalIgnoreCase))
                                _remotePtToCodec[pt] = AudioCodecsEnum.OPUS;
                        }

                        remoteOffersOpus = audioMedia.MediaFormats.Any(f => 
                            f.Value.Name()?.Equals("opus", StringComparison.OrdinalIgnoreCase) == true);
                        remoteOffersG722 = audioMedia.MediaFormats.Any(f => 
                            f.Value.Name()?.Equals("G722", StringComparison.OrdinalIgnoreCase) == true);

                        remoteOffersPcmu = audioMedia.MediaFormats.Any(f =>
                            f.Value.Name()?.Equals("PCMU", StringComparison.OrdinalIgnoreCase) == true);
                        remoteOffersPcma = audioMedia.MediaFormats.Any(f =>
                            f.Value.Name()?.Equals("PCMA", StringComparison.OrdinalIgnoreCase) == true);
                        
                        var codecs = audioMedia.MediaFormats
                            .Select(f => $"{f.Value.Name()}({f.Key})")
                            .ToList();
                        Log($"📥 [{_currentCallId}] Remote offers: {string.Join(", ", codecs)}");
                        if (_remotePtToCodec.Count > 0)
                            Log($"📥 [{_currentCallId}] Remote PT map: {string.Join(", ", _remotePtToCodec.Select(kvp => $"PT{kvp.Key}={kvp.Value}"))}");
                        
                        // Detailed Opus parameters if present
                        var opusFormat = audioMedia.MediaFormats.FirstOrDefault(f =>
                            f.Value.Name()?.Equals("opus", StringComparison.OrdinalIgnoreCase) == true);
                        if (!opusFormat.Value.IsEmpty())
                        {
                            remoteOpusPt = opusFormat.Key;
                            remoteOpusChannels = Math.Max(1, opusFormat.Value.Channels());
                            remoteOpusFmtp = opusFormat.Value.Fmtp;
                            Log($"🔍 [{_currentCallId}] Remote Opus: PT={opusFormat.Key}, ClockRate={opusFormat.Value.ClockRate()}, Channels={remoteOpusChannels}, fmtp={remoteOpusFmtp ?? "none"}");
                        }
                        
                        if (remoteOffersOpus)
                            Log($"🎧 [{_currentCallId}] Opus available - 48kHz wideband!");
                        else if (remoteOffersG722)
                            Log($"🎧 [{_currentCallId}] G.722 available - 16kHz wideband");
                        else
                            Log($"📞 [{_currentCallId}] Narrowband only (G.711 8kHz)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ SDP parse error: {ex.Message}");
            }

            // Create AdaAudioSource for outbound audio (AI → SIP)
            // Use JitterBuffer mode with 200ms buffer for smoother playback
            _adaAudioSource = new AdaAudioSource(AudioMode.JitterBuffer, 200);
            _adaAudioSource.OnDebugLog += msg => Log(msg);
            _adaAudioSource.OnQueueEmpty += () => Log($"🔇 [{_currentCallId}] Ada finished speaking");

            // Force Opus negotiation - always offer Opus first regardless of remote SDP
            // This may be rejected by endpoints that don't support Opus
            bool forceOpus = true;  // ENABLED: Force Opus 48kHz for best quality
            bool forceNarrowband = false; // Set to true to force PCMU
            
            if (forceOpus && !forceNarrowband)
            {
                // Force Opus with standard PT 111 (dynamic payload type)
                _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.OPUS);
                Log($"🎯 [{_currentCallId}] FORCING Opus 48kHz (ignoring remote offer)");
            }
            else if (!forceNarrowband && remoteOffersOpus)
            {
                // IMPORTANT: Only answer with the *exact* Opus payload type the remote offered.
                // Offering additional dynamic PTs (e.g. 111) in an answer can cause the remote to reject.
                if (remoteOpusPt.HasValue)
                {
                    int pt = remoteOpusPt.Value;
                    _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.OPUS && fmt.FormatID == pt);
                    Log($"🎯 [{_currentCallId}] Restricting to remote Opus PT={pt} (48kHz), remoteChannels={remoteOpusChannels}, fmtp={remoteOpusFmtp ?? "none"}");
                }
                else
                {
                    _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.OPUS);
                    Log($"🎯 [{_currentCallId}] Restricting to Opus 48kHz (remote PT unknown)");
                }
            }
            else if (!forceNarrowband && remoteOffersG722)
            {
                // Force G.722 only - 16kHz wideband
                _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.G722);
                Log($"🎯 [{_currentCallId}] Restricting to G.722 16kHz");
            }
            else
            {
                // Answer only with what the remote offered (prefer PCMA, then PCMU for desktop compatibility).
                if (remoteOffersPcma)
                {
                    _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMA);
                    Log($"🎯 [{_currentCallId}] Using remote-offered PCMA 8kHz (A-law, forceNarrowband={forceNarrowband})");
                }
                else if (remoteOffersPcmu)
                {
                    _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMU);
                    Log($"🎯 [{_currentCallId}] Using remote-offered PCMU 8kHz (μ-law, forceNarrowband={forceNarrowband})");
                }
                else
                {
                    _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMA || fmt.Codec == AudioCodecsEnum.PCMU);
                    Log($"🎯 [{_currentCallId}] Using PCMA/PCMU 8kHz (remote offer unknown, forceNarrowband={forceNarrowband})");
                }
            }

            // IMPORTANT: after restricting, ensure the audio source has a valid selected format.
            // Some endpoints + negotiation flows can fail if the source is still "selected" to a
            // format that is no longer offered.
            var offeredFormats = _adaAudioSource.GetAudioSourceFormats();
            Log($"🔧 [{_currentCallId}] Offering codecs: {string.Join(", ", offeredFormats.Select(f => $"{f.FormatName}@{f.ClockRate}Hz PT={f.FormatID} ch={f.ChannelCount}"))}");
            if (offeredFormats.Count > 0)
            {
                _adaAudioSource.SetAudioSourceFormat(offeredFormats[0]);
                Log($"🎯 [{_currentCallId}] Selected codec: {offeredFormats[0].FormatName}@{offeredFormats[0].ClockRate}Hz PT={offeredFormats[0].FormatID} ch={offeredFormats[0].ChannelCount}");
            }
            else
            {
                Log($"⚠️ [{_currentCallId}] No codecs left after restriction; falling back to default codec list");
            }

            // Create VoIPMediaSession with our custom audio source
            // NOTE: For Opus to work, we need to use a custom MediaEndPoints setup
            // that properly advertises Opus in SDP
            var mediaEndPoints = new MediaEndPoints
            {
                AudioSource = _adaAudioSource,
                AudioSink = null // We handle inbound audio manually via OnRtpPacketReceived
            };

            _mediaSession = new VoIPMediaSession(mediaEndPoints);
            _mediaSession.AcceptRtpFromAny = true;
            
            // Configure STUN for RTP NAT traversal
            if (_config?.EnableStun == true && !string.IsNullOrEmpty(_config.StunServer))
            {
                var stunUri = new STUNUri(SIPSchemesEnum.stun, _config.StunServer, _config.StunPort);
                
                // For RTP sessions, we need to get the public IP via STUN binding request
                // This helps NAT traversal by advertising the correct external IP in SDP
                try
                {
                    var stunEndPoint = new IPEndPoint(
                        Dns.GetHostAddresses(_config.StunServer).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? IPAddress.Any,
                        _config.StunPort);
                    
                    // SIPSorcery VoIPMediaSession can use SetRemoteDescription or SetDestination
                    // to handle NAT, but for incoming calls we rely on AcceptRtpFromAny
                    Log($"🌐 [{_currentCallId}] STUN configured: {_config.StunServer}:{_config.StunPort}");
                }
                catch (Exception ex)
                {
                    Log($"⚠️ [{_currentCallId}] STUN setup warning: {ex.Message}");
                }
            }
            
            // Debug: Log what formats the media session will advertise
            Log($"🔍 [{_currentCallId}] MediaSession created, AudioTrack formats: {_mediaSession.AudioLocalTrack?.Capabilities?.Count ?? 0} capabilities");

            // Hook up RTP receiver for inbound audio (SIP → AI)
            _mediaSession.OnRtpPacketReceived += OnRtpPacketReceived;

            // Accept the call
            var uas = _userAgent!.AcceptCall(inviteRequest);

            // Send 180 Ringing
            try
            {
                var ringing = SIPResponse.GetResponse(inviteRequest, SIPResponseStatusCodesEnum.Ringing, null);
                await _sipTransport!.SendResponseAsync(ringing);
            }
            catch (Exception ex)
            {
                Log($"⚠️ Failed to send ringing: {ex.Message}");
            }

            await Task.Delay(200);

            // Answer the call (with Opus→PCMU fallback)
            Log($"🔧 [{_currentCallId}] Answering call...");
            
            // Debug: Log UAS state before answer
            Log($"🔍 [{_currentCallId}] UAS state before Answer: Dialogue={uas.SIPDialogue?.CallId ?? "null"}, TransactionId={uas.ClientTransaction?.TransactionId}");
            
            // Debug: Log media session SDP capabilities (with channels)
            var localSdp = _mediaSession.CreateOffer(null);
            if (localSdp != null)
            {
                var audioMedia = localSdp.Media?.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
                if (audioMedia != null)
                {
                    Log($"🔍 [{_currentCallId}] Local SDP audio formats: {string.Join(", ", audioMedia.MediaFormats.Select(f => $"{f.Value.Name()}@{f.Value.ClockRate()}ch{f.Value.Channels()}(PT{f.Key})"))}");
                }
            }
            else
            {
                Log($"⚠️ [{_currentCallId}] CreateOffer returned null");
            }
            
            bool answered = false;
            bool usedFallback = false;
            try
            {
                var answerTask = _userAgent.Answer(uas, _mediaSession);
                var timeoutTask = Task.Delay(5000); // 5 second timeout
                var completedTask = await Task.WhenAny(answerTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    Log($"❌ [{_currentCallId}] Answer() timed out after 5 seconds");
                    answered = false;
                }
                else
                {
                    answered = await answerTask;
                    Log($"🔍 [{_currentCallId}] Answer() returned: {answered}");
                    
                    // If answer failed, try to log any SIP error info from the transaction
                    if (!answered)
                    {
                        try
                        {
                            var txFinalResp = uas.ClientTransaction?.TransactionFinalResponse;
                            if (txFinalResp != null)
                            {
                                Log($"⚠️ [{_currentCallId}] SIP final response: {(int)txFinalResp.Status} {txFinalResp.ReasonPhrase}");
                            }
                            else
                            {
                                Log($"⚠️ [{_currentCallId}] No SIP final response available from transaction");
                            }
                        }
                        catch { /* logging only */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [{_currentCallId}] Exception during Answer(): {ex.GetType().Name}: {ex.Message}");
                Log($"❌ [{_currentCallId}] Stack trace: {ex.StackTrace}");
                answered = false;
            }

            // Opus→PCMU fallback: if Opus answer failed, retry with G.711
            if (!answered && (remoteOffersOpus || remoteOffersG722))
            {
                Log($"⚠️ [{_currentCallId}] Wideband answer failed - falling back to PCMU");
                usedFallback = true;

                // Cleanup first attempt
                try { _mediaSession?.Close("Fallback"); } catch { }
                _mediaSession?.Dispose();
                _adaAudioSource?.Dispose();

                // Recreate with PCMU only (use only remote-offered codecs where possible)
                _adaAudioSource = new AdaAudioSource(AudioMode.JitterBuffer, 200);
                _adaAudioSource.OnDebugLog += msg => Log(msg);
                _adaAudioSource.OnQueueEmpty += () => Log($"🔇 [{_currentCallId}] Ada finished speaking");
                if (remoteOffersPcmu)
                    _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMU);
                else if (remoteOffersPcma)
                    _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMA);
                else
                    _adaAudioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMU || fmt.Codec == AudioCodecsEnum.PCMA);

                var fallbackFormats = _adaAudioSource.GetAudioSourceFormats();
                Log($"🔧 [{_currentCallId}] Fallback codecs: {string.Join(", ", fallbackFormats.Select(f => $"{f.FormatName}@{f.ClockRate}Hz PT={f.FormatID}"))}");
                if (fallbackFormats.Count > 0)
                    _adaAudioSource.SetAudioSourceFormat(fallbackFormats[0]);

                Log($"🔧 [{_currentCallId}] Creating fallback media session...");
                var fallbackEndPoints = new MediaEndPoints
                {
                    AudioSource = _adaAudioSource,
                    AudioSink = null
                };
                _mediaSession = new VoIPMediaSession(fallbackEndPoints);
                _mediaSession.AcceptRtpFromAny = true;
                _mediaSession.OnRtpPacketReceived += OnRtpPacketReceived;
                Log($"🔧 [{_currentCallId}] Fallback media session created, attempting answer...");

                // Re-accept the original INVITE (SIPSorcery allows re-answer with different session)
                try
                {
                    var answerTask2 = _userAgent.Answer(uas, _mediaSession);
                    var timeoutTask2 = Task.Delay(3000);
                    var completed2 = await Task.WhenAny(answerTask2, timeoutTask2);
                    if (completed2 == timeoutTask2)
                    {
                        Log($"❌ [{_currentCallId}] Fallback Answer() timed out after 3 seconds (INVITE likely no longer answerable)");
                        answered = false;
                    }
                    else
                    {
                        answered = await answerTask2;
                        Log($"🔧 [{_currentCallId}] Fallback answer returned: {answered}");
                    }
                }
                catch (Exception ex2)
                {
                    Log($"❌ [{_currentCallId}] Fallback answer exception: {ex2.GetType().Name}: {ex2.Message}");
                    answered = false;
                }
            }

            if (!answered)
            {
                try
                {
                    var offered = _adaAudioSource?.GetAudioSourceFormats();
                    if (offered != null)
                        Log($"🔧 [{_currentCallId}] Offered codecs at failure: {string.Join(", ", offered.Select(f => $"{f.FormatName}@{f.ClockRate}Hz PT={f.FormatID} ch={f.ChannelCount}"))}");
                }
                catch { /* never crash on diagnostics */ }

                Log($"❌ [{_currentCallId}] Failed to answer call");
                await CleanupCall();
                return;
            }

            if (usedFallback)
                Log($"⚠️ [{_currentCallId}] Answered with PCMU fallback (Opus negotiation failed)");

            // Start media session
            await _mediaSession.Start();
            
            // Log negotiated codec details
            var audioTrack = _mediaSession.AudioLocalTrack;
            var remoteRtp = _mediaSession.AudioDestinationEndPoint;
            var codecName = "unknown";
            var clockRate = 0;
            if (audioTrack?.Capabilities?.Count > 0)
            {
                var fmt = audioTrack.Capabilities[0];
                codecName = fmt.Name() ?? "unknown";
                clockRate = fmt.ClockRate();
            }
            Log($"✅ [{_currentCallId}] Call answered, RTP started");
            Log($"📡 [{_currentCallId}] RTP → {remoteRtp}");
            Log($"🎵 [{_currentCallId}] Negotiated codec: {codecName} @ {clockRate}Hz");

            // Connect to OpenAI Realtime API
            _aiClient = new OpenAIRealtimeClient(
                _apiKey,
                model: "gpt-4o-mini-realtime-preview-2024-12-17"
            );
            _aiClient.OnLog += msg => Log(msg);
            _aiClient.OnTranscript += t =>
            {
                Log($"💬 {t}");
                OnTranscript?.Invoke(t);
            };
            _aiClient.OnPcm24Audio += OnAiAudioReceived;
            _aiClient.OnCallEnded += async () => await EndCallAsync();
            _aiClient.OnCallerAudioMonitor += data => OnCallerAudioMonitor?.Invoke(data);

            await _aiClient.ConnectAsync(caller, _callCts.Token);
            Log($"🤖 [{_currentCallId}] Connected to OpenAI Realtime API");

            // Handle call hangup
            _userAgent.OnCallHungup += (dialogue) =>
            {
                Log($"📕 [{_currentCallId}] Remote hung up");
                _ = EndCallAsync();
            };
        }
        catch (Exception ex)
        {
            Log($"❌ [{_currentCallId}] Error: {ex.Message}");
            await CleanupCall();
        }
    }

    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || _aiClient == null || !_aiClient.IsConnected)
            return;

        _inboundPacketCount++;

        // Flush initial packets to avoid stale audio
        if (!_inboundFlushComplete)
        {
            if (_inboundPacketCount <= FLUSH_PACKETS)
            {
                if (_inboundPacketCount == 1)
                    Log($"🧹 Flushing first {FLUSH_PACKETS} inbound packets...");
                return;
            }
            _inboundFlushComplete = true;
            Log("✅ Inbound flush complete");
        }

        var payload = rtpPacket.Payload;
        if (payload == null || payload.Length == 0) return;

        // If Opus is negotiated, RTP payload will be Opus frames (typically PT 106/111).
        // Decode Opus -> PCM16 and send using the generic PCM path with correct sample rate.
        var pt = rtpPacket.Header.PayloadType;

        // Determine codec from remote SDP mapping (fallback: infer from well-known PTs)
        AudioCodecsEnum codec;
        if (!_remotePtToCodec.TryGetValue(pt, out codec))
        {
            codec = pt switch
            {
                0 => AudioCodecsEnum.PCMU,
                8 => AudioCodecsEnum.PCMA,
                9 => AudioCodecsEnum.G722,
                _ => AudioCodecsEnum.PCMU
            };
        }
        
        // Log first few packets to verify RTP is arriving
        if (_inboundPacketCount <= 30 || _inboundPacketCount % 500 == 0)
        {
            Log($"📦 RTP #{_inboundPacketCount}: PT={pt}, codec={codec}, len={payload.Length}");
        }

        if (codec == AudioCodecsEnum.OPUS)
        {
            try
            {
                var pcm48 = AudioCodecs.OpusDecode(payload);

                // Downmix stereo (interleaved) to mono for the AI.
                // Decoder returns stereo when OPUS_DECODE_CHANNELS == 2
                short[] mono;
                if (AudioCodecs.OPUS_DECODE_CHANNELS == 2 && pcm48.Length % 2 == 0)
                {
                    mono = new short[pcm48.Length / 2];
                    for (int i = 0, j = 0; i < pcm48.Length; i += 2, j++)
                    {
                        int mixed = (pcm48[i] + pcm48[i + 1]) / 2;
                        mono[j] = (short)mixed;
                    }
                }
                else
                {
                    mono = pcm48;
                }

                // Log first few processed packets
                if (_inboundPacketCount <= 30)
                {
                    int peak = 0;
                    foreach (var s in mono) { int abs = Math.Abs((int)s); if (abs > peak) peak = abs; }
                    Log($"🎙️ Opus→Mono: {mono.Length} samples, peak={peak}");
                }

                var pcmBytes = AudioCodecs.ShortsToBytes(mono);
                _ = _aiClient.SendAudioAsync(pcmBytes, AudioCodecs.OPUS_SAMPLE_RATE);
                return;
            }
            catch (Exception ex)
            {
                Log($"⚠️ Opus decode error (PT={pt}): {ex.Message}");
                return;
            }
        }

        // G.722 (16kHz) inbound
        if (codec == AudioCodecsEnum.G722)
        {
            try
            {
                var pcm16k = AudioCodecs.G722Decode(payload);
                var pcmBytes16k = AudioCodecs.ShortsToBytes(pcm16k);
                _ = _aiClient.SendAudioAsync(pcmBytes16k, AudioCodecs.G722_SAMPLE_RATE);
            }
            catch (Exception ex)
            {
                Log($"⚠️ G.722 decode error (PT={pt}): {ex.Message}");
            }
            return;
        }

        // G.711 inbound (PCMA/PCMU)
        bool isAlaw = codec == AudioCodecsEnum.PCMA;

        var pcm16 = new short[payload.Length];
        for (int i = 0; i < payload.Length; i++)
        {
            pcm16[i] = isAlaw
                ? NAudio.Codecs.ALawDecoder.ALawToLinearSample(payload[i])
                : NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(payload[i]);
        }

        // Lightweight diagnostics (first few packets only)
        if (_inboundPacketCount <= 10)
        {
            long sumSq = 0;
            int peak = 0;
            for (int i = 0; i < pcm16.Length; i++)
            {
                int s = pcm16[i];
                int abs = s == short.MinValue ? 32768 : Math.Abs(s);
                if (abs > peak) peak = abs;
                sumSq += (long)s * s;
            }
            double rms = pcm16.Length > 0 ? Math.Sqrt(sumSq / (double)pcm16.Length) : 0;
            Log($"🎙️ RTP decode: codec={codec}, samples={pcm16.Length}, rms={rms:F0}, peak={peak}");
        }

        var pcmBytes = new byte[pcm16.Length * 2];
        Buffer.BlockCopy(pcm16, 0, pcmBytes, 0, pcmBytes.Length);

        // PCMA: no-DSP + volume boost path (prevents noise gate killing speech)
        // PCMU: existing DSP path
        if (isAlaw)
            _ = _aiClient.SendPcm8kNoDspAsync(pcmBytes);
        else
            _ = _aiClient.SendPcm8kAsync(pcmBytes);
    }

    private void OnAiAudioReceived(byte[] pcm24kBytes)
    {
        if (_disposed || _adaAudioSource == null) return;

        // Apply TTS pre-conditioning (de-ess, harmonic soften, micro-noise, gain norm)
        // This cleans up OpenAI's synthetic audio before telephony DSP
        var pcm24k = new short[pcm24kBytes.Length / 2];
        Buffer.BlockCopy(pcm24kBytes, 0, pcm24k, 0, pcm24kBytes.Length);
        
        var conditioned = TtsPreConditioner.Process(pcm24k);
        
        var conditionedBytes = new byte[conditioned.Length * 2];
        Buffer.BlockCopy(conditioned, 0, conditionedBytes, 0, conditionedBytes.Length);

        // Feed conditioned PCM24 audio to AdaAudioSource - it handles:
        // - Resampling 24kHz → 8kHz
        // - G.711 encoding
        // - RTP frame pacing (20ms timer)
        _adaAudioSource.EnqueuePcm24(conditionedBytes);
    }

    public async Task EndCallAsync()
    {
        if (!_isInCall) return;

        var callId = _currentCallId;
        Log($"📞 [{callId}] Ending call...");

        try { _callCts?.Cancel(); } catch { }

        // Hang up SIP
        try { _userAgent?.Hangup(); } catch { }

        await CleanupCall();

        if (callId != null)
            OnCallEnded?.Invoke(callId);
    }

    private async Task CleanupCall()
    {
        _isInCall = false;

        // Disconnect AI
        if (_aiClient != null)
        {
            try { await _aiClient.DisconnectAsync(); } catch { }
            _aiClient.Dispose();
            _aiClient = null;
        }

        // Close media session
        if (_mediaSession != null)
        {
            try { _mediaSession.Close("Call ended"); } catch { }
            _mediaSession.Dispose();
            _mediaSession = null;
        }

        // Dispose audio source
        if (_adaAudioSource != null)
        {
            _adaAudioSource.Dispose();
            _adaAudioSource = null;
        }

        _callCts?.Dispose();
        _callCts = null;
        _currentCallId = null;
        _currentCallIdSip = null;
    }

    public void Stop()
    {
        Log("🛑 Stopping SIP-OpenAI Bridge...");

        _ = EndCallAsync();

        try { _regUserAgent?.Stop(); } catch { }
        try { _sipTransport?.Shutdown(); } catch { }

        Log("✅ Bridge stopped");
    }

    private void Log(string message)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        _regUserAgent = null;
        _userAgent = null;
        _sipTransport = null;

        GC.SuppressFinalize(this);
    }
}
