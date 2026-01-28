using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// Manages a single taxi bot call session.
/// Handles SIP call lifecycle and bridges audio between RTP and OpenAI.
/// </summary>
public class TaxiBotCallSession
{
    private readonly SIPUserAgent _ua;
    private readonly string _apiKey;
    private readonly string _callId;

    private RTPMediaSession? _rtpSession;
    private PcmaRtpPlayout? _pcmaPlayout;
    private OpenAiRealtimeClient? _aiClient;

    private bool _isHungup;
    private CancellationTokenSource? _cts;

    public event Action<string>? OnLog;
    public event Action<string>? OnCallFinished;
    public event Action<string>? OnTranscript;

    public TaxiBotCallSession(SIPUserAgent ua, string apiKey)
    {
        _ua = ua;
        _apiKey = apiKey;
        _callId = Guid.NewGuid().ToString("N")[..8];
    }

    private void Log(string msg) => OnLog?.Invoke($"[CALL {_callId}] {msg}");

    public async Task<bool> AcceptIncomingCall(SIPRequest invite)
    {
        var caller = invite.Header.From?.FriendlyDescription() ?? "Unknown";
        Log($"Accepting call from {caller}");

        _cts = new CancellationTokenSource();

        try
        {
            // Build media session with PCMA codec only
            var audioFormats = new List<AudioFormat>
            {
                new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)
            };

            _rtpSession = new RTPMediaSession(new MediaEndPoints
            {
                AudioSink = null,
                AudioSource = null
            }, audioFormats);

            _rtpSession.AcceptRtpFromAny = true;

            // Wire up inbound RTP
            _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;

            // Answer the call
            var uas = _ua.AcceptCall(invite);
            bool answered = await _ua.Answer(uas, _rtpSession);

            if (!answered)
            {
                Log("Failed to answer call");
                return false;
            }

            await _rtpSession.Start();
            Log("Call answered, RTP started");

            // Get RTP channel and remote endpoint for playout
            var rtpChannel = _rtpSession.AudioRtpChannel;
            var remoteEP = _rtpSession.AudioDestinationEndPoint;

            if (rtpChannel == null || remoteEP == null)
            {
                Log("Missing RTP channel or remote endpoint");
                await Hangup("RTP setup failed");
                return false;
            }

            // Create PCMA playout engine with direct RTP control
            _pcmaPlayout = new PcmaRtpPlayout(rtpChannel, remoteEP);
            _pcmaPlayout.OnDebugLog += msg => Log(msg);
            _pcmaPlayout.Start();

            // Create OpenAI client
            if (!string.IsNullOrEmpty(_apiKey))
            {
                _aiClient = new OpenAiRealtimeClient(_apiKey);
                _aiClient.OnLog += msg => Log(msg);
                _aiClient.OnTranscript += t => OnTranscript?.Invoke(t);
                _aiClient.OnPcm16kAudio += pcm16k =>
                {
                    // Downsample 16k → 8k and enqueue
                    EnqueueAiAudio(pcm16k);
                };

                Log("Connecting to OpenAI Realtime API...");
                await _aiClient.ConnectAsync(caller, _cts.Token);
                Log("OpenAI connected");
            }
            else
            {
                Log("⚠️ No API key - AI features disabled");
            }

            // Hook hangup
            _ua.OnCallHungup += async dialogue =>
            {
                Log("Remote hangup detected");
                await Hangup("Remote hangup");
            };

            Log("Call fully established");

            // Keep session alive in background
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested && 
                           _aiClient?.IsConnected == true && 
                           !_isHungup)
                    {
                        await Task.Delay(500, _cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    await Hangup("Session ended");
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            Log($"Error accepting call: {ex.Message}");
            await Hangup("Error during setup");
            return false;
        }
    }

    /// <summary>
    /// Process inbound RTP: PCMA → PCM16@8k → resample to 16k → send to OpenAI.
    /// </summary>
    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || _isHungup || _aiClient == null)
            return;

        var payload = rtpPacket.Payload;
        if (payload == null || payload.Length == 0)
            return;

        // Only handle PCMA (PT=8)
        if (rtpPacket.Header.PayloadType != 8)
            return;

        // Decode PCMA → PCM16 @ 8kHz
        var pcm8k = G711Codec.AlawToPcm16(payload);

        // Resample 8k → 16k for OpenAI (simple 2x interpolation)
        var pcm16k = Resample8kTo16k(pcm8k);

        // Send to OpenAI
        _aiClient.SendPcm16kToModel(pcm16k);
    }

    /// <summary>
    /// Simple 8kHz → 16kHz upsampling (2x linear interpolation).
    /// </summary>
    private short[] Resample8kTo16k(byte[] pcm8kBytes)
    {
        int samples8k = pcm8kBytes.Length / 2;
        var pcm8k = new short[samples8k];
        Buffer.BlockCopy(pcm8kBytes, 0, pcm8k, 0, pcm8kBytes.Length);

        var pcm16k = new short[samples8k * 2];

        for (int i = 0; i < samples8k; i++)
        {
            short sample = pcm8k[i];
            short nextSample = (i + 1 < samples8k) ? pcm8k[i + 1] : sample;

            pcm16k[i * 2] = sample;
            pcm16k[i * 2 + 1] = (short)((sample + nextSample) / 2);
        }

        return pcm16k;
    }

    /// <summary>
    /// Enqueue audio from OpenAI (PCM16@16k) → downsample to 8k → playout.
    /// </summary>
    private void EnqueueAiAudio(short[] pcm16k)
    {
        if (_pcmaPlayout == null || _isHungup) return;

        // Downsample 16k → 8k (take every 2nd sample)
        var pcm8k = new short[pcm16k.Length / 2];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            pcm8k[i] = pcm16k[i * 2];
        }

        // Split into 20ms frames (160 samples @ 8kHz)
        const int frameSamples = 160;
        for (int offset = 0; offset < pcm8k.Length; offset += frameSamples)
        {
            int len = Math.Min(frameSamples, pcm8k.Length - offset);
            var frame = new short[frameSamples];
            Array.Copy(pcm8k, offset, frame, 0, len);
            _pcmaPlayout.EnqueuePcm8kFrame(frame);
        }
    }

    public async Task Hangup(string reason)
    {
        if (_isHungup) return;
        _isHungup = true;

        Log($"Hanging up: {reason}");

        try { _cts?.Cancel(); } catch { }
        try { _pcmaPlayout?.Stop(); _pcmaPlayout?.Dispose(); } catch { }
        try { _aiClient?.Dispose(); } catch { }

        try
        {
            if (_ua?.IsCallActive == true)
                _ua.Hangup();
        }
        catch { }

        try { _rtpSession?.Close(null); } catch { }

        OnCallFinished?.Invoke(_callId);
        await Task.CompletedTask;
    }
}
