using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.SDP;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

public class SipAdaBridge : IDisposable
{
    private readonly SipAdaBridgeConfig _config;
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regUserAgent;
    private SIPUserAgent? _userAgent;
    private volatile bool _disposed = false;

    private readonly ConcurrentQueue<byte[]> _outboundFrames = new();
    private const int MAX_OUTBOUND_FRAMES = 250;

    private bool _needsFadeIn = true;
    private const int FadeInSamples = 48;

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

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SipAdaBridge));
        
        Log($"🚕 SipAdaBridge starting...");
        Log($"➡ SIP Server: {_config.SipServer}:{_config.SipPort} ({_config.Transport})");
        Log($"➡ SIP User: {_config.SipUser}");
        Log($"➡ WS: {_config.AdaWsUrl}");

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
        var caller = req.Header.From.FromURI.User ?? "unknown";

        Log($"📞 [{callId}] Call from {caller}");
        OnCallStarted?.Invoke(callId, caller);

        while (_outboundFrames.TryDequeue(out _)) { }
        _needsFadeIn = true;

        const int FLUSH_PACKETS = 25;
        int inboundPacketCount = 0;
        bool inboundFlushComplete = false;

        uint rtpTimestamp = (uint)Random.Shared.Next(0, int.MaxValue);
        VoIPMediaSession? rtpSession = null;
        ClientWebSocket? ws = null;
        CancellationTokenSource? cts = null;

        try
        {
            var mediaEndPoints = new MediaEndPoints 
            { 
                AudioSource = null, 
                AudioSink = null 
            };
            rtpSession = new VoIPMediaSession(mediaEndPoints);
            rtpSession.AcceptRtpFromAny = true;

            var uas = ua.AcceptCall(req);

            try
            {
                var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
                await _sipTransport!.SendResponseAsync(ringing);
                Log($"☎️ [{callId}] Sent 180 Ringing");
            }
            catch { }

            await Task.Delay(300);

            bool answered = await ua.Answer(uas, rtpSession);
            if (!answered)
            {
                Log($"❌ [{callId}] Failed to answer call");
                OnCallEnded?.Invoke(callId);
                return;
            }

            await rtpSession.Start();
            Log($"📗 [{callId}] Call answered");

            ws = new ClientWebSocket();
            cts = new CancellationTokenSource();

            ua.OnCallHungup += (SIPDialogue dialogue) =>
            {
                Log($"📕 [{callId}] Caller hung up");
                OnCallEnded?.Invoke(callId);
                try { cts.Cancel(); } catch { }
            };

            var wsUri = new Uri($"{_config.AdaWsUrl}?caller={Uri.EscapeDataString(caller)}");
            Log($"🔌 [{callId}] Connecting WS → {wsUri}");

            await ws.ConnectAsync(wsUri, cts.Token);
            Log($"🟢 [{callId}] WS Connected");

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

                var ulaw = rtp.Payload;
                if (ulaw == null || ulaw.Length == 0) return;

                _ = ws.SendAsync(
                    new ArraySegment<byte>(ulaw),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            };

            _ = Task.Run(async () =>
            {
                var buffer = new byte[1024 * 64];
                while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        var result = await ws.ReceiveAsync(buffer, cts.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
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
                                EnqueueAiPcm24(callId, pcmBytes);
                            }
                        }
                        else if (typeStr == "audio" &&
                                 doc.RootElement.TryGetProperty("audio", out var audioEl))
                        {
                            var base64 = audioEl.GetString();
                            if (!string.IsNullOrEmpty(base64))
                            {
                                var pcmBytes = Convert.FromBase64String(base64);
                                EnqueueAiPcm24(callId, pcmBytes);
                            }
                        }
                        else if (typeStr == "response.audio_transcript.delta" &&
                                 doc.RootElement.TryGetProperty("delta", out var textEl))
                        {
                            var text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                                Log($"📝 [{callId}] {text}");
                        }
                        else if (typeStr == "response.created" || typeStr == "response.audio.started")
                        {
                            _needsFadeIn = true;
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

            _ = Task.Run(async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                long nextFrameTime = 0;
                int framesPlayed = 0;

                while (!cts.Token.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        long now = stopwatch.ElapsedMilliseconds;
                        if (now < nextFrameTime)
                        {
                            int delay = (int)(nextFrameTime - now);
                            if (delay > 0 && delay < 100)
                                await Task.Delay(delay, cts.Token);
                        }

                        if (_outboundFrames.TryDequeue(out var frame))
                        {
                            rtpSession.SendRtpRaw(
                                SDPMediaTypesEnum.audio,
                                frame,
                                rtpTimestamp,
                                0,
                                0
                            );

                            AudioMonitor?.AddFrame(frame);
                            OnAudioFrame?.Invoke(frame);

                            rtpTimestamp += 160;
                            framesPlayed++;
                            nextFrameTime = stopwatch.ElapsedMilliseconds + 20;
                        }
                        else
                        {
                            await Task.Delay(5, cts.Token);
                            nextFrameTime = stopwatch.ElapsedMilliseconds;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }

                Log($"🔚 [{callId}] Playback loop ended - played {framesPlayed} frames");
            }, cts.Token);

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
            
            try { cts?.Dispose(); } catch { }
            
            while (_outboundFrames.TryDequeue(out _)) { }
            
            OnCallEnded?.Invoke(callId);
        }
    }

    private void EnqueueAiPcm24(string callId, byte[] pcmBytes)
    {
        if (_disposed) return;
        
        var pcm24 = AudioCodecs.BytesToShorts(pcmBytes);

        if (_needsFadeIn && pcm24.Length > 0)
        {
            int fadeLen = Math.Min(FadeInSamples, pcm24.Length);
            for (int i = 0; i < fadeLen; i++)
            {
                float gain = (float)i / fadeLen;
                pcm24[i] = (short)(pcm24[i] * gain);
            }
            _needsFadeIn = false;
        }

        var pcm8k = AudioCodecs.Resample(pcm24, 24000, 8000);
        var ulaw = AudioCodecs.MuLawEncode(pcm8k);

        for (int i = 0; i < ulaw.Length; i += 160)
        {
            int len = Math.Min(160, ulaw.Length - i);
            var frame = new byte[160];
            Buffer.BlockCopy(ulaw, i, frame, 0, len);
            
            if (_outboundFrames.Count >= MAX_OUTBOUND_FRAMES)
            {
                _outboundFrames.TryDequeue(out _);
            }
            _outboundFrames.Enqueue(frame);
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
        
        while (_outboundFrames.TryDequeue(out _)) { }
        
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
