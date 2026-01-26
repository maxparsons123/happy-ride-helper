using System.Net;
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

    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regUserAgent;
    private SIPUserAgent? _userAgent;
    private VoIPMediaSession? _mediaSession;
    private AdaAudioSource? _adaAudioSource;
    private OpenAIRealtimeClient? _aiClient;
    private CancellationTokenSource? _callCts;

    private volatile bool _disposed;
    private volatile bool _isInCall;
    private string? _currentCallId;

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

    public SipOpenAIBridge(
        string apiKey,
        string sipServer,
        int sipPort,
        string sipUser,
        string sipPassword,
        SipTransportType transportType = SipTransportType.UDP)
    {
        _apiKey = apiKey;
        _sipServer = sipServer;
        _sipPort = sipPort;
        _sipUser = sipUser;
        _sipPassword = sipPassword;
        _transportType = transportType;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SipOpenAIBridge));

        Log("🚀 Starting SIP-OpenAI Bridge...");

        _sipTransport = new SIPTransport();

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
        if (_disposed || _isInCall)
        {
            Log("⚠️ Rejecting call - already in a call or disposed");
            var busyResponse = SIPResponse.GetResponse(inviteRequest, SIPResponseStatusCodesEnum.BusyHere, null);
            await _sipTransport!.SendResponseAsync(busyResponse);
            return;
        }

        _isInCall = true;
        _currentCallId = Guid.NewGuid().ToString("N")[..8];
        _callCts = new CancellationTokenSource();
        _inboundPacketCount = 0;
        _inboundFlushComplete = false;

        var caller = inviteRequest.Header.From.FromURI.User ?? "unknown";
        Log($"📞 [{_currentCallId}] Incoming call from {caller}");
        OnCallStarted?.Invoke(_currentCallId, caller);

        try
        {
            // Create AdaAudioSource for outbound audio (AI → SIP)
            // Use JitterBuffer mode with 200ms buffer for smoother playback
            _adaAudioSource = new AdaAudioSource(AudioMode.JitterBuffer, 200);
            _adaAudioSource.OnDebugLog += msg => Log(msg);
            _adaAudioSource.OnQueueEmpty += () => Log($"🔇 [{_currentCallId}] Ada finished speaking");

            // Create VoIPMediaSession with our custom audio source
            var mediaEndPoints = new MediaEndPoints
            {
                AudioSource = _adaAudioSource,
                AudioSink = null // We handle inbound audio manually
            };

            _mediaSession = new VoIPMediaSession(mediaEndPoints);
            _mediaSession.AcceptRtpFromAny = true;

            // Log incoming SDP offer codecs
            LogIncomingSdp(inviteRequest);

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

            // Answer the call
            bool answered = await _userAgent.Answer(uas, _mediaSession);
            if (!answered)
            {
                Log($"❌ [{_currentCallId}] Failed to answer call");
                await CleanupCall();
                return;
            }

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
                clockRate = fmt.ClockRate;
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

        // Decode µ-law to PCM16 using NAudio (matches SIPSorcery example pattern)
        var pcm16 = new short[payload.Length];
        for (int i = 0; i < payload.Length; i++)
        {
            pcm16[i] = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(payload[i]);
        }

        // Convert to bytes for OpenAI (16-bit little-endian)
        var pcmBytes = new byte[pcm16.Length * 2];
        Buffer.BlockCopy(pcm16, 0, pcmBytes, 0, pcmBytes.Length);

        // Send PCM16 @ 8kHz to OpenAI (it will resample to 24kHz)
        _ = _aiClient.SendPcm8kAsync(pcmBytes);
    }

    private void OnAiAudioReceived(byte[] pcm24kBytes)
    {
        if (_disposed || _adaAudioSource == null) return;

        // Feed PCM24 audio to AdaAudioSource - it handles:
        // - Resampling 24kHz → 8kHz
        // - G.711 µ-law encoding
        // - RTP frame pacing (20ms timer)
        _adaAudioSource.EnqueuePcm24(pcm24kBytes);
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

    /// <summary>
    /// Parse and log codecs from incoming SDP offer
    /// </summary>
    private void LogIncomingSdp(SIPRequest inviteRequest)
    {
        try
        {
            var sdpBody = inviteRequest.Body;
            if (string.IsNullOrEmpty(sdpBody)) return;

            var sdp = SDP.ParseSDPDescription(sdpBody);
            var audioMedia = sdp.Media.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
            if (audioMedia == null) return;

            var codecs = audioMedia.MediaFormats
                .Select(f => $"{f.Value.Name()}({f.Key})")
                .ToList();

            Log($"📥 [{_currentCallId}] Remote offers: {string.Join(", ", codecs)}");

            // Check for wideband codecs
            bool hasG722 = audioMedia.MediaFormats.Any(f => 
                f.Value.Name()?.Equals("G722", StringComparison.OrdinalIgnoreCase) == true);
            bool hasOpus = audioMedia.MediaFormats.Any(f => 
                f.Value.Name()?.Equals("opus", StringComparison.OrdinalIgnoreCase) == true);
            
            if (hasOpus)
                Log($"🎧 [{_currentCallId}] Opus available - 48kHz wideband!");
            else if (hasG722)
                Log($"🎧 [{_currentCallId}] G.722 available - 16kHz wideband");
            else
                Log($"📞 [{_currentCallId}] Narrowband only (G.711 8kHz)");
        }
        catch (Exception ex)
        {
            Log($"⚠️ SDP parse error: {ex.Message}");
        }
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
