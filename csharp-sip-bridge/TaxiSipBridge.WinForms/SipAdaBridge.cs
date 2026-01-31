using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

public class SipAdaBridge : IDisposable
{
    private readonly SipAdaBridgeConfig _config;
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regUserAgent;
    private SIPUserAgent? _userAgent;
    private volatile bool _disposed = false;
    private IPAddress? _localIp;

    // Prevent accumulating OnCallHungup handlers across multiple calls.
    private Action<SIPDialogue>? _currentHungupHandler;

    public AudioMonitor? AudioMonitor { get; set; }

    public event Action? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnLog;
    public event Action<byte[]>? OnAudioFrame;

    public SipAdaBridge(SipAdaBridgeConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Get the local IP address that can reach the SIP server.
    /// This ensures the SDP c= field has a routable address (not 0.0.0.0).
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
        if (_disposed) throw new ObjectDisposedException(nameof(SipAdaBridge));
        
        Log($"🚕 SipAdaBridge starting...");
        Log($"➡ SIP Server: {_config.SipServer}:{_config.SipPort} ({_config.Transport})");
        Log($"➡ SIP User: {_config.SipUser}");
        Log($"➡ WS: {_config.AdaWsUrl}");

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
        Log($"🟢 Bridge Ready (waiting for calls)");
    }

    private void OnRegistrationSuccess(SIPURI uri, SIPResponse resp)
    {
        Log("📗 SIP REGISTER OK");
        OnRegistered?.Invoke();
    }

    private void OnRegistrationFailure(SIPURI uri, SIPResponse? resp, string err)
    {
        Log($"❌ SIP REGISTER FAILED: {err}");
        OnRegistrationFailed?.Invoke(err);
    }

    private async void OnIncomingCallAsync(SIPUserAgent ua, SIPRequest req)
    {
        Log($"📞 Incoming INVITE from {req.Header.From.FromURI}");
        await HandleIncomingCall(ua, req);
    }

    private async Task HandleIncomingCall(SIPUserAgent ua, SIPRequest req)
    {
        if (_disposed) return;
        
        var callId = Guid.NewGuid().ToString("N")[..8];
        var caller = SipCallerId.Extract(req);

        Log($"📞 [{callId}] Call from {caller}");
        OnCallStarted?.Invoke(callId, caller);

        const int FLUSH_PACKETS = 25;
        int inboundPacketCount = 0;
        bool inboundFlushComplete = false;

        AdaAudioSource? adaSource = null;
        VoIPMediaSession? rtpSession = null;
        ClientWebSocket? ws = null;
        CancellationTokenSource? cts = null;

        // Ensure we only signal call end once per call.
        int callEndedSignaled = 0;
        void SignalCallEndedOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref callEndedSignaled, 1) == 1) return;
            OnCallEnded?.Invoke(callId);
        }

        try
        {
            // Create AdaAudioSource - implements IAudioSource for proper codec negotiation
            adaSource = new AdaAudioSource();

            // Create VoIPMediaSession with our custom audio source
            var mediaEndPoints = new MediaEndPoints 
            { 
                AudioSource = adaSource, 
                AudioSink = null  // We handle inbound audio manually
            };
            rtpSession = new VoIPMediaSession(mediaEndPoints);
            rtpSession.AcceptRtpFromAny = true;
            
            Log($"☎️ [{callId}] Sending 180 Ringing...");
            var uas = ua.AcceptCall(req);

            try
            {
                var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
                await _sipTransport!.SendResponseAsync(ringing);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [{callId}] Ringing send failed: {ex.Message}");
            }

            await Task.Delay(200);

            Log($"📞 [{callId}] Answering call...");
            bool answered = await ua.Answer(uas, rtpSession);
            if (!answered)
            {
                Log($"❌ [{callId}] Failed to answer - UAS state may be invalid");
                OnCallEnded?.Invoke(callId);
                return;
            }

            await rtpSession.Start();
            Log($"📗 [{callId}] Call answered and RTP started");

            // Log the negotiated codec
            var selectedFormat = rtpSession.AudioLocalTrack?.Capabilities?.FirstOrDefault();
            if (selectedFormat.HasValue && !selectedFormat.Value.IsEmpty())
                Log($"🎵 [{callId}] Negotiated codec: ID {selectedFormat.Value.ID} @ {selectedFormat.Value.ClockRate}Hz");
            ws = new ClientWebSocket();
            cts = new CancellationTokenSource();

            // Remove any previous handler (old calls) from the same UA.
            if (_currentHungupHandler != null)
            {
                try { ua.OnCallHungup -= _currentHungupHandler; } catch { }
                _currentHungupHandler = null;
            }

            _currentHungupHandler = (SIPDialogue dialogue) =>
            {
                Log($"📕 [{callId}] Caller hung up");
                SignalCallEndedOnce();
                try { cts.Cancel(); } catch { }

                // Unsubscribe immediately to avoid duplicate callbacks.
                if (_currentHungupHandler != null)
                {
                    try { ua.OnCallHungup -= _currentHungupHandler; } catch { }
                    _currentHungupHandler = null;
                }
            };
            ua.OnCallHungup += _currentHungupHandler;

            var wsUri = new Uri($"{_config.AdaWsUrl}?caller={Uri.EscapeDataString(caller)}");
            Log($"🔌 [{callId}] Connecting WS → {wsUri}");

            await ws.ConnectAsync(wsUri, cts.Token);
            Log($"🟢 [{callId}] WS Connected");

            // Handle inbound RTP (caller → Ada)
            rtpSession.OnRtpPacketReceived += (ep, mt, rtp) =>
            {
                if (mt != SDPMediaTypesEnum.audio || ws.State != WebSocketState.Open)
                    return;

                inboundPacketCount++;
                if (!inboundFlushComplete)
                {
                    if (inboundPacketCount <= FLUSH_PACKETS)
                    {
                        if (inboundPacketCount == 1)
                            Log($"🧹 [{callId}] Flushing inbound audio ({FLUSH_PACKETS} packets)...");
                        return;
                    }
                    inboundFlushComplete = true;
                    Log($"✅ [{callId}] Inbound flush complete");
                }

                var payload = rtp.Payload;
                if (payload == null || payload.Length == 0) return;

                // Send µ-law directly to WebSocket
                _ = ws.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            };

            // WebSocket receive loop (Ada → caller)
            _ = Task.Run(async () =>
            {
                var buffer = new byte[1024 * 64];
                while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        var result = await ws.ReceiveAsync(buffer, cts.Token);

                        if (result.MessageType == WebSocketCloseStatus.NormalClosure || result.MessageType == WebSocketMessageType.Close)
                        {
                            Log($"🔌 [{callId}] WS closed by server");
                            break;
                        }

                        if (result.MessageType != WebSocketMessageType.Text)
                            continue;

                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        using var doc = JsonDocument.Parse(json);

                        if (!doc.RootElement.TryGetProperty("type", out var t))
                            continue;

                        var typeStr = t.GetString();

                        if (typeStr == "response.audio.delta" &&
                            doc.RootElement.TryGetProperty("delta", out var deltaEl))
                        {
                            var base64 = deltaEl.GetString();
                            if (!string.IsNullOrEmpty(base64))
                            {
                                var pcmBytes = Convert.FromBase64String(base64);
                                adaSource.EnqueuePcm24(pcmBytes);
                            }
                        }
                        else if (typeStr == "audio" &&
                                 doc.RootElement.TryGetProperty("audio", out var audioEl))
                        {
                            var base64 = audioEl.GetString();
                            if (!string.IsNullOrEmpty(base64))
                            {
                                var pcmBytes = Convert.FromBase64String(base64);
                                adaSource.EnqueuePcm24(pcmBytes);
                            }
                        }
                        else if (typeStr == "response.audio_transcript.delta" &&
                                 doc.RootElement.TryGetProperty("delta", out var textEl))
                        {
                            var text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                                Log($"📝 [{callId}] {text}");
                        }
                        else if (typeStr == "keepalive")
                        {
                            long? ts = null;
                            if (doc.RootElement.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number)
                                ts = tsEl.GetInt64();

                            string ackCallId = callId;
                            if (doc.RootElement.TryGetProperty("call_id", out var callIdEl) && callIdEl.ValueKind == JsonValueKind.String)
                                ackCallId = callIdEl.GetString() ?? callId;

                            try
                            {
                                var ack = JsonSerializer.Serialize(new Dictionary<string, object?>
                                {
                                    ["type"] = "keepalive_ack",
                                    ["timestamp"] = ts,
                                    ["call_id"] = ackCallId,
                                });

                                await ws.SendAsync(
                                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(ack)),
                                    WebSocketMessageType.Text,
                                    true,
                                    CancellationToken.None);
                            }
                            catch { }
                        }
                        else if (typeStr == "response.created" || typeStr == "response.audio.started")
                        {
                            adaSource.ResetFadeIn();
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [{callId}] WS receive error: {ex.Message}");
                        break;
                    }
                }
                Log($"🔚 [{callId}] WS read loop ended");
            }, cts.Token);

            // Keep call alive
            while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open && !_disposed)
                await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"❌ [{callId}] Error: {ex.Message}");
        }
        finally
        {
            Log($"📴 [{callId}] Hangup & cleanup");

            // Detach hangup handler to prevent duplicates across calls.
            if (_currentHungupHandler != null)
            {
                try { ua.OnCallHungup -= _currentHungupHandler; } catch { }
                _currentHungupHandler = null;
            }
            
            try { ua.Hangup(); } catch { }
            
            if (ws != null)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token);
                    }
                }
                catch { }
                try { ws.Dispose(); } catch { }
            }
            
            if (rtpSession != null)
            {
                try { rtpSession.Close("call ended"); } catch { }
            }

            if (adaSource != null)
            {
                try { adaSource.Dispose(); } catch { }
            }
            
            try { cts?.Dispose(); } catch { }
            
            SignalCallEndedOnce();
        }
    }

    private void Log(string m)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {m}");
    }

    public void Stop()
    {
        if (_disposed) return;
        
        Log("🛑 Bridge stopping...");
        
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
        
        try { _sipTransport?.Shutdown(); } catch { }
        
        Log("🛑 Bridge stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Stop();
        AudioMonitor?.Dispose();
        
        GC.SuppressFinalize(this);
    }
}
