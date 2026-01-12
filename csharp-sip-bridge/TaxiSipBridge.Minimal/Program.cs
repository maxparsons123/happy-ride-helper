using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge.Minimal;

/// <summary>
/// Fixed SIP-to-WebSocket bridge with proper RTP audio playback.
/// Key fixes:
/// 1. Uses SendRtpRaw() for direct µ-law RTP injection - NOT AudioExtrasSource
/// 2. Proper 20ms frame pacing with Stopwatch
/// 3. Binary WebSocket frames for inbound audio (33% bandwidth savings)
/// 4. Anti-aliased resampling for better audio quality
/// </summary>
public class Program
{
    // Configuration - Update these for your setup
    private const string EdgeFunctionUrl = "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime";
    private const string SipServer = "206.189.123.28";
    private const string SipUsername = "max201";
    private const string SipPassword = "qwe70954504118";
    private const int SipPort = 5060;

    private static ClientWebSocket? _webSocket;
    private static RTPSession? _rtpSession;
    private static CancellationTokenSource? _cts;
    
    // Frame-based queue for proper audio timing
    private static readonly ConcurrentQueue<byte[]> _outboundFrames = new();
    
    // RTP timestamp tracking
    private static uint _rtpTimestamp = 0;
    private static ushort _rtpSeqNum = 0;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("🚕 Taxi SIP Bridge - Fixed RTP Audio");
        Console.WriteLine("=====================================");

        // Create SIP transport
        var sipTransport = new SIPTransport();
        sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, SipPort)));

        // Register with SIP server
        var regUserAgent = new SIPRegistrationUserAgent(
            sipTransport,
            SipUsername,
            SipPassword,
            SipServer,
            120);

        regUserAgent.RegistrationSuccessful += (uri, resp) =>
        {
            Console.WriteLine("📗 SIP REGISTER OK");
        };

        regUserAgent.RegistrationFailed += (uri, resp, err) =>
        {
            Console.WriteLine($"❌ SIP REGISTER FAILED: {err}");
        };

        // Create user agent
        var userAgent = new SIPUserAgent(sipTransport, null);

        // Handle incoming calls
        userAgent.OnIncomingCall += async (ua, req) =>
        {
            Console.WriteLine($"📞 Incoming call from: {req.Header.From.FromURI}");
            await HandleIncomingCall(ua, userAgent, req);
        };

        // Start registration
        regUserAgent.Start();

        Console.WriteLine($"🎧 Registering to {SipServer} as {SipUsername}...");
        Console.WriteLine("Press Ctrl+C to exit");

        // Keep running
        var exitEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };
        exitEvent.Wait();

        regUserAgent.Stop();
        sipTransport.Shutdown();
    }

    private static async Task HandleIncomingCall(SIPUserAgent ua, SIPUserAgent userAgent, SIPRequest req)
    {
        var callId = Guid.NewGuid().ToString("N")[..8];
        var caller = req.Header.From.FromURI.User ?? "unknown";
        
        Console.WriteLine($"📞 [{callId}] Call from {caller}");
        
        // Clear stale audio from previous calls
        while (_outboundFrames.TryDequeue(out _)) { }
        _rtpTimestamp = 0;
        _rtpSeqNum = 0;

        try
        {
            _cts = new CancellationTokenSource();
            
            // Create media session with µ-law codec - PCMU is payload type 0
            var mediaSession = new VoIPMediaSession(new MediaEndPoints
            {
                AudioSource = new AudioExtrasSource(
                    new AudioEncoder(),
                    new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence }
                ),
                AudioSink = new AudioExtrasSource(new AudioEncoder())
            });

            mediaSession.AcceptRtpFromAny = true;

            // Get the RTP session for direct packet injection
            _rtpSession = mediaSession.RtpSession;

            // Subscribe to RTP audio events from caller
            _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;

            // Answer the call
            var uas = userAgent.AcceptCall(req);
            var result = await userAgent.Answer(uas, mediaSession);

            if (!result)
            {
                Console.WriteLine($"❌ [{callId}] Failed to answer call");
                return;
            }

            Console.WriteLine($"✅ [{callId}] Call answered");
            Console.WriteLine($"🎛️ [{callId}] RTP session started - using direct SendRtpRaw()");

            // Connect to edge function
            _webSocket = new ClientWebSocket();
            var wsUrl = $"{EdgeFunctionUrl}?caller={Uri.EscapeDataString(caller)}";
            Console.WriteLine($"🔌 [{callId}] Connecting to {wsUrl}");
            
            await _webSocket.ConnectAsync(new Uri(wsUrl), _cts.Token);
            Console.WriteLine($"✅ [{callId}] WebSocket connected");

            // Handle call hangup
            userAgent.OnCallHungup += (dialog) =>
            {
                Console.WriteLine($"📵 [{callId}] Call ended");
                _cts?.Cancel();
            };

            // Start receive task
            var receiveTask = Task.Run(() => ReceiveFromEdgeFunction(callId));
            
            // Start playback task with proper RTP injection
            var playbackTask = Task.Run(() => PlaybackWithRtpRaw(callId));

            // Wait for call to end
            while (!_cts.Token.IsCancellationRequested && 
                   _webSocket?.State == WebSocketState.Open &&
                   userAgent.IsCallActive)
            {
                await Task.Delay(500);
            }

            await Task.WhenAny(receiveTask, playbackTask);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [{callId}] Error: {ex.Message}");
        }
        finally
        {
            Console.WriteLine($"📴 [{callId}] Cleanup");
            _cts?.Cancel();
            _webSocket?.Dispose();
            _webSocket = null;
            _rtpSession = null;
        }
    }

    /// <summary>
    /// Called when RTP audio packet is received from caller.
    /// Sends as BINARY WebSocket frame for 33% bandwidth savings.
    /// </summary>
    private static async void OnRtpPacketReceived(IPEndPoint remoteEP, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || _webSocket?.State != WebSocketState.Open)
            return;

        try
        {
            byte[] ulawData = rtpPacket.Payload;
            byte[] pcm24k = ConvertUlawToPcm24k(ulawData);

            // Send as BINARY frame - 33% less bandwidth than base64
            await _webSocket.SendAsync(
                new ArraySegment<byte>(pcm24k),
                WebSocketMessageType.Binary,
                true,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ RTP→WS error: {ex.Message}");
        }
    }

    /// <summary>
    /// Receive audio from edge function and queue for playback
    /// </summary>
    private static async Task ReceiveFromEdgeFunction(string callId)
    {
        var buffer = new byte[32768];

        while (_webSocket?.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(buffer, _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"🔌 [{callId}] WebSocket closed");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    using var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.TryGetProperty("type", out var typeEl))
                    {
                        var type = typeEl.GetString();

                        // Handle audio from AI - response.audio.delta format
                        if (type == "response.audio.delta" && 
                            doc.RootElement.TryGetProperty("delta", out var deltaEl))
                        {
                            var base64Audio = deltaEl.GetString();
                            if (string.IsNullOrEmpty(base64Audio)) continue;

                            var pcmData = Convert.FromBase64String(base64Audio);
                            var ulawData = ConvertPcm24kToUlaw(pcmData);

                            // Queue as 160-byte frames (20ms at 8kHz µ-law)
                            for (int i = 0; i < ulawData.Length; i += 160)
                            {
                                int len = Math.Min(160, ulawData.Length - i);
                                var frame = new byte[160];
                                Buffer.BlockCopy(ulawData, i, frame, 0, len);
                                _outboundFrames.Enqueue(frame);
                            }

                            Console.WriteLine($"🧠 [{callId}] Queued {ulawData.Length}B µ-law → {_outboundFrames.Count} frames");
                        }
                        // Handle transcripts
                        else if (type == "response.audio_transcript.delta" &&
                                 doc.RootElement.TryGetProperty("delta", out var textEl))
                        {
                            Console.Write(textEl.GetString());
                        }
                        // Also handle simple "audio" type from edge function wrapper
                        else if (type == "audio" && 
                                 doc.RootElement.TryGetProperty("audio", out var audioEl))
                        {
                            var base64Audio = audioEl.GetString();
                            if (string.IsNullOrEmpty(base64Audio)) continue;

                            var pcmData = Convert.FromBase64String(base64Audio);
                            var ulawData = ConvertPcm24kToUlaw(pcmData);

                            for (int i = 0; i < ulawData.Length; i += 160)
                            {
                                int len = Math.Min(160, ulawData.Length - i);
                                var frame = new byte[160];
                                Buffer.BlockCopy(ulawData, i, frame, 0, len);
                                _outboundFrames.Enqueue(frame);
                            }
                            
                            Console.WriteLine($"🧠 [{callId}] Queued {ulawData.Length}B µ-law → {_outboundFrames.Count} frames");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ [{callId}] Receive error: {ex.Message}");
            }
        }

        Console.WriteLine($"🔚 [{callId}] Receive loop ended");
    }

    /// <summary>
    /// Play audio to caller using SendRtpRaw for direct RTP packet injection.
    /// This bypasses AudioExtrasSource which doesn't work for µ-law playback.
    /// Maintains strict 20ms timing between frames.
    /// </summary>
    private static async Task PlaybackWithRtpRaw(string callId)
    {
        var stopwatch = Stopwatch.StartNew();
        long nextFrameTime = 0;
        int framesPlayed = 0;

        Console.WriteLine($"🔊 [{callId}] Playback loop started - using SendRtpRaw()");

        while (!_cts!.Token.IsCancellationRequested)
        {
            try
            {
                // Wait until it's time for the next frame
                long now = stopwatch.ElapsedMilliseconds;
                if (now < nextFrameTime)
                {
                    int delay = (int)(nextFrameTime - now);
                    if (delay > 0 && delay < 100)
                    {
                        await Task.Delay(delay, _cts.Token);
                    }
                }

                // Try to dequeue and send a frame
                if (_outboundFrames.TryDequeue(out var frame) && _rtpSession != null)
                {
                    // Send raw RTP packet with µ-law payload (payload type 0 = PCMU)
                    // The RTP session handles the packet construction
                    _rtpSession.SendRtpRaw(
                        SDPMediaTypesEnum.audio,
                        frame,
                        _rtpTimestamp,
                        0,  // Marker bit - 0 for continuation
                        0   // Payload type 0 = PCMU (µ-law)
                    );
                    
                    _rtpTimestamp += 160; // 160 samples per 20ms frame at 8kHz
                    _rtpSeqNum++;
                    framesPlayed++;
                    
                    // Log every 25 frames (500ms)
                    if (framesPlayed % 25 == 0)
                    {
                        Console.WriteLine($"🔊 [{callId}] Played {framesPlayed} frames, queue: {_outboundFrames.Count}");
                    }
                    
                    nextFrameTime = stopwatch.ElapsedMilliseconds + 20; // Schedule next frame in 20ms
                }
                else
                {
                    // No audio available, wait a bit and reset timing
                    await Task.Delay(5, _cts.Token);
                    nextFrameTime = stopwatch.ElapsedMilliseconds;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ [{callId}] Playback error: {ex.Message}");
            }
        }

        Console.WriteLine($"🔚 [{callId}] Playback loop ended - played {framesPlayed} total frames");
    }

    #region Audio Conversion

    /// <summary>
    /// Convert µ-law 8kHz to PCM16 24kHz (3x upsample with interpolation)
    /// </summary>
    private static byte[] ConvertUlawToPcm24k(byte[] ulawData)
    {
        // Decode µ-law to PCM16 at 8kHz
        short[] pcm8k = new short[ulawData.Length];
        for (int i = 0; i < ulawData.Length; i++)
        {
            pcm8k[i] = MuLawDecode(ulawData[i]);
        }

        // Upsample 8kHz -> 24kHz with linear interpolation
        short[] pcm24k = new short[pcm8k.Length * 3];
        for (int i = 0; i < pcm8k.Length - 1; i++)
        {
            short s0 = pcm8k[i];
            short s1 = pcm8k[i + 1];

            pcm24k[i * 3] = s0;
            pcm24k[i * 3 + 1] = (short)((s0 * 2 + s1) / 3);
            pcm24k[i * 3 + 2] = (short)((s0 + s1 * 2) / 3);
        }
        
        // Handle last sample
        int lastIdx = (pcm8k.Length - 1) * 3;
        pcm24k[lastIdx] = pcm8k[^1];
        pcm24k[lastIdx + 1] = pcm8k[^1];
        pcm24k[lastIdx + 2] = pcm8k[^1];

        // Convert to bytes (little-endian)
        byte[] result = new byte[pcm24k.Length * 2];
        Buffer.BlockCopy(pcm24k, 0, result, 0, result.Length);
        return result;
    }

    /// <summary>
    /// Convert PCM16 24kHz to µ-law 8kHz (3x downsample with averaging)
    /// </summary>
    private static byte[] ConvertPcm24kToUlaw(byte[] pcmData)
    {
        // Convert bytes to shorts
        short[] pcm24k = new short[pcmData.Length / 2];
        Buffer.BlockCopy(pcmData, 0, pcm24k, 0, pcmData.Length);

        // Downsample 24kHz -> 8kHz with averaging for anti-aliasing
        short[] pcm8k = new short[pcm24k.Length / 3];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            int idx = i * 3;
            if (idx + 2 < pcm24k.Length)
            {
                // Average 3 samples for anti-aliasing
                pcm8k[i] = (short)((pcm24k[idx] + pcm24k[idx + 1] + pcm24k[idx + 2]) / 3);
            }
            else
            {
                pcm8k[i] = pcm24k[idx];
            }
        }

        // Encode to µ-law
        byte[] ulawData = new byte[pcm8k.Length];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            ulawData[i] = MuLawEncode(pcm8k[i]);
        }

        return ulawData;
    }

    #endregion

    #region µ-law Codec with Lookup Tables

    private static readonly short[] MuLawDecompressTable = new short[256];
    private static readonly byte[] MuLawCompressTable = new byte[65536];

    static Program()
    {
        // Initialize µ-law lookup tables for fast conversion
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
