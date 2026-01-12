using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge.Minimal;

/// <summary>
/// Minimal SIP-to-WebSocket bridge that:
/// 1. Registers with a SIP server
/// 2. Accepts incoming calls
/// 3. Captures RTP audio (µ-law 8kHz)
/// 4. Converts to PCM16 24kHz and sends as BINARY WebSocket frames (33% less bandwidth)
/// 5. Receives audio back and plays to caller
/// </summary>
public class Program
{
    // Configuration
    private const string EdgeFunctionUrl = "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime";
    private const string SipServer = "your-sip-server.com";
    private const string SipUsername = "your-username";
    private const string SipPassword = "your-password";
    private const int SipPort = 5060;

    private static ClientWebSocket? _webSocket;
    private static VoIPMediaSession? _mediaSession;
    private static CancellationTokenSource? _cts;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("🚕 Taxi SIP Bridge - Minimal");
        Console.WriteLine("============================");

        // Create SIP transport
        var sipTransport = new SIPTransport();
        sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, SipPort)));

        // Create user agent
        var userAgent = new SIPUserAgent(sipTransport, null);

        // Handle incoming calls
        userAgent.OnIncomingCall += async (ua, req) =>
        {
            Console.WriteLine($"📞 Incoming call from: {req.Header.From.FromURI}");

            try
            {
                // Create media session with µ-law codec
                _mediaSession = new VoIPMediaSession(new MediaEndPoints
                {
                    AudioSource = new AudioExtrasSource(
                        new AudioEncoder(),
                        new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence }
                    ),
                    AudioSink = new AudioExtrasSource(new AudioEncoder())
                });

                // Subscribe to RTP audio events
                _mediaSession.OnRtpPacketReceived += OnRtpPacketReceived;

                // Answer the call
                var uas = userAgent.AcceptCall(req);
                var result = await userAgent.Answer(uas, _mediaSession);

                if (result)
                {
                    Console.WriteLine("✅ Call answered");
                    
                    // Connect to edge function
                    await ConnectToEdgeFunction(req.Header.From.FromURI.User);
                }
                else
                {
                    Console.WriteLine("❌ Failed to answer call");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error handling call: {ex.Message}");
            }
        };

        // Handle call ended
        userAgent.OnCallHungup += (dialog) =>
        {
            Console.WriteLine("📵 Call ended");
            DisconnectFromEdgeFunction();
        };

        Console.WriteLine($"🎧 Listening for SIP calls on port {SipPort}...");
        Console.WriteLine("Press Ctrl+C to exit");

        // Keep running
        var exitEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };
        exitEvent.Wait();

        sipTransport.Shutdown();
    }

    /// <summary>
    /// Called when RTP audio packet is received from caller
    /// Uses BINARY WebSocket frames to reduce bandwidth by 33% (no base64 overhead)
    /// </summary>
    private static async void OnRtpPacketReceived(IPEndPoint remoteEP, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || _webSocket?.State != WebSocketState.Open)
            return;

        try
        {
            // Extract µ-law audio payload
            byte[] ulawData = rtpPacket.Payload;

            // Convert µ-law 8kHz to PCM16 24kHz
            byte[] pcm24k = ConvertUlawToPcm24k(ulawData);

            // Send as BINARY frame directly - no base64 encoding needed!
            // Edge function receives raw PCM16 bytes
            await _webSocket.SendAsync(
                new ArraySegment<byte>(pcm24k),
                WebSocketMessageType.Binary,
                true,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ RTP processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Connect to edge function WebSocket
    /// </summary>
    private static async Task ConnectToEdgeFunction(string callerPhone)
    {
        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();

        try
        {
            Console.WriteLine($"🔌 Connecting to edge function...");

            // Add caller info as query param
            var uri = new Uri($"{EdgeFunctionUrl}?caller={Uri.EscapeDataString(callerPhone)}");
            await _webSocket.ConnectAsync(uri, _cts.Token);

            Console.WriteLine("✅ Connected to edge function");

            // Start receiving audio from AI
            _ = Task.Run(ReceiveFromEdgeFunction);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ WebSocket connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Receive audio from edge function and play to caller
    /// </summary>
    private static async Task ReceiveFromEdgeFunction()
    {
        var buffer = new byte[16384];

        while (_webSocket?.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(buffer, _cts.Token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var message = JsonSerializer.Deserialize<JsonElement>(json);

                    if (message.TryGetProperty("type", out var typeEl))
                    {
                        var type = typeEl.GetString();

                        // Handle audio delta from AI
                        if (type == "response.audio.delta" && 
                            message.TryGetProperty("delta", out var deltaEl))
                        {
                            string base64Audio = deltaEl.GetString()!;
                            byte[] pcmData = Convert.FromBase64String(base64Audio);

                            // Convert PCM16 24kHz back to µ-law 8kHz for RTP
                            byte[] ulawData = ConvertPcm24kToUlaw(pcmData);

                            // Send to caller via RTP
                            SendAudioToRtp(ulawData);
                        }
                        // Handle transcripts for logging
                        else if (type == "response.audio_transcript.delta" &&
                                 message.TryGetProperty("delta", out var textEl))
                        {
                            Console.Write(textEl.GetString());
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("🔌 Edge function closed connection");
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Receive error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Convert µ-law 8kHz to PCM16 24kHz (3x upsample)
    /// </summary>
    private static byte[] ConvertUlawToPcm24k(byte[] ulawData)
    {
        // Step 1: Decode µ-law to PCM16 at 8kHz
        short[] pcm8k = new short[ulawData.Length];
        for (int i = 0; i < ulawData.Length; i++)
        {
            pcm8k[i] = MuLawDecode(ulawData[i]);
        }

        // Step 2: Upsample 8kHz -> 24kHz (3x linear interpolation)
        short[] pcm24k = new short[pcm8k.Length * 3];
        for (int i = 0; i < pcm8k.Length - 1; i++)
        {
            short s0 = pcm8k[i];
            short s1 = pcm8k[i + 1];

            pcm24k[i * 3] = s0;
            pcm24k[i * 3 + 1] = (short)((s0 * 2 + s1) / 3);
            pcm24k[i * 3 + 2] = (short)((s0 + s1 * 2) / 3);
        }
        // Last sample
        pcm24k[^3] = pcm8k[^1];
        pcm24k[^2] = pcm8k[^1];
        pcm24k[^1] = pcm8k[^1];

        // Step 3: Convert to bytes (little-endian)
        byte[] result = new byte[pcm24k.Length * 2];
        Buffer.BlockCopy(pcm24k, 0, result, 0, result.Length);
        return result;
    }

    /// <summary>
    /// Convert PCM16 24kHz to µ-law 8kHz (3x downsample)
    /// </summary>
    private static byte[] ConvertPcm24kToUlaw(byte[] pcmData)
    {
        // Step 1: Convert bytes to shorts
        short[] pcm24k = new short[pcmData.Length / 2];
        Buffer.BlockCopy(pcmData, 0, pcm24k, 0, pcmData.Length);

        // Step 2: Downsample 24kHz -> 8kHz (take every 3rd sample with averaging)
        short[] pcm8k = new short[pcm24k.Length / 3];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            int idx = i * 3;
            if (idx + 2 < pcm24k.Length)
            {
                pcm8k[i] = (short)((pcm24k[idx] + pcm24k[idx + 1] + pcm24k[idx + 2]) / 3);
            }
            else
            {
                pcm8k[i] = pcm24k[idx];
            }
        }

        // Step 3: Encode to µ-law
        byte[] ulawData = new byte[pcm8k.Length];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            ulawData[i] = MuLawEncode(pcm8k[i]);
        }

        return ulawData;
    }

    /// <summary>
    /// Send audio to caller via RTP
    /// </summary>
    private static void SendAudioToRtp(byte[] ulawData)
    {
        // Send as RTP packets (160 samples = 20ms at 8kHz)
        const int frameSize = 160;

        for (int offset = 0; offset < ulawData.Length; offset += frameSize)
        {
            int len = Math.Min(frameSize, ulawData.Length - offset);
            byte[] frame = new byte[len];
            Buffer.BlockCopy(ulawData, offset, frame, 0, len);

            // Queue audio for playback
            // Note: In production, use proper RTP timing
            _mediaSession?.AudioExtrasSource?.SendAudioFrame(frame);
        }
    }

    /// <summary>
    /// Disconnect from edge function
    /// </summary>
    private static void DisconnectFromEdgeFunction()
    {
        _cts?.Cancel();
        _webSocket?.Dispose();
        _webSocket = null;
        _mediaSession = null;
    }

    #region µ-law Codec

    private static readonly short[] MuLawDecompressTable = new short[256];
    private static readonly byte[] MuLawCompressTable = new byte[65536];

    static Program()
    {
        // Initialize µ-law tables
        for (int i = 0; i < 256; i++)
        {
            MuLawDecompressTable[i] = MuLawDecodeValue((byte)i);
        }

        for (int i = short.MinValue; i <= short.MaxValue; i++)
        {
            MuLawCompressTable[(ushort)i] = MuLawEncodeValue((short)i);
        }
    }

    private static short MuLawDecode(byte ulaw) => MuLawDecompressTable[ulaw];
    private static byte MuLawEncode(short pcm) => MuLawCompressTable[(ushort)pcm];

    private static short MuLawDecodeValue(byte ulaw)
    {
        ulaw = (byte)~ulaw;
        int sign = (ulaw & 0x80);
        int exponent = (ulaw >> 4) & 0x07;
        int mantissa = ulaw & 0x0F;
        int sample = (mantissa << 4) + 8;
        sample <<= exponent;
        sample -= 128;
        return (short)(sign != 0 ? -sample : sample);
    }

    private static byte MuLawEncodeValue(short pcm)
    {
        const int BIAS = 0x84;
        const int MAX = 32635;

        int sign = (pcm >> 8) & 0x80;
        if (sign != 0) pcm = (short)-pcm;
        if (pcm > MAX) pcm = MAX;

        pcm = (short)(pcm + BIAS);
        int exponent = 7;
        for (int expMask = 0x4000; (pcm & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }

        int mantissa = (pcm >> (exponent + 3)) & 0x0F;
        byte ulawByte = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)~ulawByte;
    }

    #endregion
}
