using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorcery.Net;

public enum SipTransportType { UDP, TCP }

public class SipAdaBridgeConfig
{
    public string SipServer { get; set; } = "206.189.123.28";
    public int SipPort { get; set; } = 5060;
    public string SipUser { get; set; } = "max201";
    public string SipPassword { get; set; } = "qwe70954504118";
    public string AdaWsUrl { get; set; } = "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime";
    public SipTransportType Transport { get; set; } = SipTransportType.UDP;
}

public class SipAdaBridge : IDisposable
{
    private readonly SipAdaBridgeConfig _config;
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regUserAgent;
    private SIPUserAgent? _userAgent;

    // Byte-based jitter buffer (µ-law 8kHz bytes).
    private readonly ConcurrentQueue<byte> _outboundAudio = new ConcurrentQueue<byte>();

    // Max µ-law bytes to keep in buffer (160 bytes = 20ms @ 8kHz).
    // 4000 bytes ≈ 0.5s of audio.
    private const int MaxOutboundBufferBytes = 4000;

    public event Action? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnLog;

    public SipAdaBridge(SipAdaBridgeConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        Log($"🚕 SipAdaBridge starting...");
        Log($"➡ SIP Server: {_config.SipServer}:{_config.SipPort} ({_config.Transport})");
        Log($"➡ SIP User: {_config.SipUser}");
        Log($"➡ WS: {_config.AdaWsUrl}");
        Log($"➡ Registering...");

        _sipTransport = new SIPTransport();

        switch (_config.Transport)
        {
            case SipTransportType.UDP:
                var udpChannel = new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0));
                _sipTransport.AddSIPChannel(udpChannel);
                break;
            case SipTransportType.TCP:
                var tcpChannel = new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0));
                _sipTransport.AddSIPChannel(tcpChannel);
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
            Log("📗 SIP REGISTER OK");
            OnRegistered?.Invoke();
        };

        _regUserAgent.RegistrationFailed += (uri, resp, err) =>
        {
            Log($"❌ SIP REGISTER FAILED: {err}");
            OnRegistrationFailed?.Invoke(err);
        };

        _userAgent = new SIPUserAgent(_sipTransport, null);

        _userAgent.OnIncomingCall += async (ua, req) =>
        {
            Log($"📞 Incoming INVITE from {req.Header.From.FromURI}");
            await HandleIncomingCall(ua, req);
        };

        _regUserAgent.Start();
        Log($"🟢 Bridge Ready (waiting for calls)");
    }

    private async Task HandleIncomingCall(SIPUserAgent ua, SIPRequest req)
    {
        var callId = Guid.NewGuid().ToString("N")[..8];
        var caller = req.Header.From.FromURI.User ?? "unknown";
        Log($"📞 [{callId}] Call from {caller}");
        OnCallStarted?.Invoke(callId, caller);

        // Clear any stale audio from previous call.
        while (_outboundAudio.TryDequeue(out _)) { }

        var rtpSession = new VoIPMediaSession(fmt => fmt.Codec == AudioCodecsEnum.PCMU);
        rtpSession.AcceptRtpFromAny = true;

        var uas = ua.AcceptCall(req);
        bool answered = await ua.Answer(uas, rtpSession);

        if (!answered)
        {
            Log($"❌ [{callId}] Failed to answer call");
            OnCallEnded?.Invoke(callId);
            return;
        }

        Log($"📗 [{callId}] Call answered");
        await rtpSession.AudioExtrasSource.StartAudio();
        Log($"🎛 [{callId}] RTP session started");

        using var ws = new ClientWebSocket();
        var cts = new CancellationTokenSource();

        ua.OnCallHungup += (d) =>
        {
            Log($"📕 [{callId}] Caller hung up");
            OnCallEnded?.Invoke(callId);
            cts.Cancel();
        };

        try
        {
            // Add caller as query param for edge function
            var wsUri = new Uri($"{_config.AdaWsUrl}?caller={Uri.EscapeDataString(caller)}");
            Log($"🔌 [{callId}] Connecting WS → {wsUri}");

            await ws.ConnectAsync(wsUri, cts.Token);
            Log($"🟢 [{callId}] WS Connected");

            // === SIP → WS (Caller → AI) ===
            rtpSession.OnRtpPacketReceived += (ep, mt, rtp) =>
            {
                if (mt != SDPMediaTypesEnum.audio || ws.State != WebSocketState.Open)
                    return;

                var ulaw = rtp.Payload;
                if (ulaw == null || ulaw.Length == 0) return;

                // µ-law 8kHz → PCM16 24kHz
                var pcm8k = AudioCodecs.MuLawDecode(ulaw);
                var pcm24k = AudioCodecs.Resample(pcm8k, 8000, 24000);
                var pcmBytes = AudioCodecs.ShortsToBytes(pcm24k);

                Log($"🎤 [{callId}] RTP→AI ulaw={ulaw.Length}B pcm24k={pcmBytes.Length}B (binary)");

                // Send raw PCM24k as BINARY frame (per edge spec)
                _ = ws.SendAsync(
                    new ArraySegment<byte>(pcmBytes),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            };

            // === WS → Queue (AI → Buffer) ===
            _ = Task.Run(async () =>
            {
                var buffer = new byte[1024 * 128];
                while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await ws.ReceiveAsync(buffer, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [{callId}] WS receive error: {ex.Message}");
                        break;
                    }

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

                    // OpenAI realtime style: { "type": "response.audio.delta", "delta": "<base64 pcm24k>" }
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
                    // Simple audio message: { "type": "audio", "audio": "<base64 pcm24k>" }
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
                    // Transcript delta
                    else if (typeStr == "response.audio_transcript.delta" &&
                             doc.RootElement.TryGetProperty("delta", out var textEl))
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            Log($"📝 [{callId}] {text}");
                        }
                    }
                }
                Log($"🔚 [{callId}] WS read loop ended");
            });

            // === Buffer → SIP (AI → Caller) ===
            _ = Task.Run(async () =>
            {
                byte[] frame = new byte[160]; // 20ms @ 8kHz
                while (!cts.Token.IsCancellationRequested)
                {
                    if (_outboundAudio.Count >= 160)
                    {
                        for (int i = 0; i < 160; i++)
                            _outboundAudio.TryDequeue(out frame[i]);

                        await rtpSession.AudioExtrasSource.SendAudioFromStream(
                            new MemoryStream(frame),
                            AudioSamplingRatesEnum.Rate8KHz);

                        Log($"🔊 [{callId}] RTP send 160B (queue={_outboundAudio.Count}B)");
                    }
                    // 20ms pacing = real-time 8kHz playout
                    await Task.Delay(20);
                }
            });

            while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
                await Task.Delay(500);
        }
        catch (Exception ex)
        {
            Log($"❌ [{callId}] Error: {ex.Message}");
        }
        finally
        {
            Log($"📴 [{callId}] Hangup & cleanup");
            ua.Hangup();
            OnCallEnded?.Invoke(callId);
            // Drain buffer so next call starts clean.
            while (_outboundAudio.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// Convert AI PCM24k bytes to µ-law 8kHz and enqueue into outbound buffer, 
    /// with backlog trimming and OG-style logs.
    /// </summary>
    private void EnqueueAiPcm24(string callId, byte[] pcmBytes)
    {
        // Bytes → shorts (PCM16)
        var pcm24 = AudioCodecs.BytesToShorts(pcmBytes);
        // 24kHz → 8kHz downsample
        var pcm8k = AudioCodecs.Resample(pcm24, 24000, 8000);
        // PCM16 8kHz → µ-law
        var ulaw = AudioCodecs.MuLawEncode(pcm8k);

        Log($"🧠 [{callId}] AI→PCM24k {pcmBytes.Length}B → ulaw {ulaw.Length}B");

        // Enqueue µ-law bytes
        foreach (var b in ulaw)
            _outboundAudio.Enqueue(b);

        // Backlog control: keep roughly MaxOutboundBufferBytes
        var current = _outboundAudio.Count;
        if (current > MaxOutboundBufferBytes)
        {
            int toDrop = current - MaxOutboundBufferBytes;
            int dropped = 0;
            while (dropped < toDrop && _outboundAudio.TryDequeue(out _))
            {
                dropped++;
            }
            Log($"⚠️ [{callId}] Dropped {dropped}B, queue now ≈ {_outboundAudio.Count}B");
        }
    }

    private void Log(string m)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {m}";
        OnLog?.Invoke(line);
        ConsoleLog(line);
    }

    private void ConsoleLog(string msg)
    {
        if (msg.Contains("📗")) Console.ForegroundColor = ConsoleColor.Green;
        else if (msg.Contains("📕") || msg.Contains("❌")) Console.ForegroundColor = ConsoleColor.Red;
        else if (msg.Contains("📞")) Console.ForegroundColor = ConsoleColor.Cyan;
        else if (msg.Contains("🧠") || msg.Contains("🎤")) Console.ForegroundColor = ConsoleColor.Yellow;
        else if (msg.Contains("🔊")) Console.ForegroundColor = ConsoleColor.Magenta;
        else Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(msg);
        Console.ResetColor();
    }

    public void Stop()
    {
        Log("🛑 Bridge stopping...");
        _regUserAgent?.Stop();
        _sipTransport?.Shutdown();
        Log("🛑 Bridge stopped");
    }

    public void Dispose() => Stop();
}

// --- AUDIO HELPERS ---
public static class AudioCodecs
{
    public static short[] MuLawDecode(byte[] data)
    {
        var pcm = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            int mulaw = ~data[i];
            int sign = (mulaw & 0x80) != 0 ? -1 : 1;
            int exponent = (mulaw >> 4) & 0x07;
            int mantissa = mulaw & 0x0F;
            pcm[i] = (short)(sign * (((mantissa << 3) + 0x84) << exponent) - 0x84);
        }
        return pcm;
    }

    public static byte[] MuLawEncode(short[] pcm)
    {
        var ulaw = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            int s = pcm[i];
            int mask = 0x80, seg = 8;
            if (s < 0) { s = -s; mask = 0x00; }
            s += 0x84;
            if (s > 0x7FFF) s = 0x7FFF;
            for (int j = 0x4000; (s & j) == 0 && seg > 0; j >>= 1) seg--;
            ulaw[i] = (byte)~(mask | (seg << 4) | ((s >> (seg + 3)) & 0x0F));
        }
        return ulaw;
    }

    public static short[] Resample(short[] input, int from, int to)
    {
        if (from == to) return input;
        double ratio = (double)from / to;
        var output = new short[(int)(input.Length / ratio)];
        for (int i = 0; i < output.Length; i++)
            output[i] = input[Math.Min((int)(i * ratio), input.Length - 1)];
        return output;
    }

    public static short[] BytesToShorts(byte[] b)
    {
        var s = new short[b.Length / 2];
        for (int i = 0; i < s.Length; i++)
            s[i] = BitConverter.ToInt16(b, i * 2);
        return s;
    }

    public static byte[] ShortsToBytes(short[] s)
    {
        var b = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, b, 0, b.Length);
        return b;
    }
}

// --- MAIN ENTRY POINT ---
public class Program
{
    public static void Main(string[] args)
    {
        var config = new SipAdaBridgeConfig();

        // Parse command-line args
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length)
                config.SipServer = args[++i];
            else if (args[i] == "--user" && i + 1 < args.Length)
                config.SipUser = args[++i];
            else if (args[i] == "--password" && i + 1 < args.Length)
                config.SipPassword = args[++i];
            else if (args[i] == "--ws" && i + 1 < args.Length)
                config.AdaWsUrl = args[++i];
            else if (args[i] == "--tcp")
                config.Transport = SipTransportType.TCP;
        }

        using var bridge = new SipAdaBridge(config);
        bridge.Start();

        Console.WriteLine("Press Ctrl+C to exit...");
        var exitEvent = new ManualResetEvent(false);
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; exitEvent.Set(); };
        exitEvent.WaitOne();

        bridge.Stop();
    }
}
