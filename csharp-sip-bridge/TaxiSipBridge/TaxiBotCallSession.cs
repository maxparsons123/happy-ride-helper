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
                _aiClient.OnPcm24kAudio += pcm24k =>
                {
                    // Downsample 24k → 8k and enqueue
                    EnqueueAiAudio(pcm24k);
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

    // DSP state for pre-emphasis filter (maintains continuity across frames)
    private short _lastSample = 0;

    /// <summary>
    /// Process inbound RTP: PCMA → PCM16@8k → DSP → resample to 24k → send to OpenAI.
    /// </summary>
    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || _isHungup || _aiClient == null)
            return;

        var payload = rtpPacket.Payload;
        if (payload == null || payload.Length == 0)
            return;

        // Only handle PCMA (PT=8) - UK carriers use A-law
        if (rtpPacket.Header.PayloadType != 8)
            return;

        // 1. Decode PCMA (A-law) → PCM16 @ 8kHz
        var pcm8k = G711Codec.AlawToPcm16(payload);

        // 2. Apply DSP: volume boost + pre-emphasis for better VAD/STT
        var pcm8kProcessed = ApplyInboundDsp(pcm8k);

        // 3. Resample 8k → 24k directly (3x linear interpolation) for OpenAI
        var pcm24k = Resample8kTo24k(pcm8kProcessed);

        // 4. Send to OpenAI
        _aiClient.SendPcm24kToModel(pcm24k);
    }

    /// <summary>
    /// Apply DSP to inbound audio: 2.5x volume boost + 0.97 pre-emphasis.
    /// Improves VAD sensitivity and consonant clarity.
    /// </summary>
    private short[] ApplyInboundDsp(byte[] pcm8kBytes)
    {
        int samples = pcm8kBytes.Length / 2;
        var pcm = new short[samples];
        Buffer.BlockCopy(pcm8kBytes, 0, pcm, 0, pcm8kBytes.Length);

        const float volumeBoost = 2.5f;
        const float preEmphasis = 0.97f;

        for (int i = 0; i < samples; i++)
        {
            // Pre-emphasis filter: y[n] = x[n] - α * x[n-1]
            float current = pcm[i];
            float previous = (i == 0) ? _lastSample : pcm[i - 1];
            float emphasized = current - (preEmphasis * previous);

            // Volume boost
            float boosted = emphasized * volumeBoost;

            // Soft clip to prevent distortion
            pcm[i] = SoftClip(boosted);
        }

        // Save last sample for next frame continuity
        if (samples > 0)
            _lastSample = pcm[samples - 1];

        return pcm;
    }

    private static short SoftClip(float sample)
    {
        if (sample > 32767) return 32767;
        if (sample < -32768) return -32768;
        return (short)sample;
    }

    /// <summary>
    /// 8kHz → 24kHz upsampling (3x linear interpolation).
    /// </summary>
    private static short[] Resample8kTo24k(short[] pcm8k)
    {
        int outLen = pcm8k.Length * 3;
        var pcm24k = new short[outLen];

        for (int i = 0; i < pcm8k.Length; i++)
        {
            short s0 = pcm8k[i];
            short s1 = (i + 1 < pcm8k.Length) ? pcm8k[i + 1] : s0;

            int outIdx = i * 3;
            pcm24k[outIdx] = s0;
            pcm24k[outIdx + 1] = (short)((s0 * 2 + s1) / 3);
            pcm24k[outIdx + 2] = (short)((s0 + s1 * 2) / 3);
        }

        return pcm24k;
    }

    /// <summary>
    /// Enqueue audio from OpenAI (PCM16@24k) → downsample to 8k → playout.
    /// </summary>
    private void EnqueueAiAudio(short[] pcm24k)
    {
        if (_pcmaPlayout == null || _isHungup) return;

        // Downsample 24k → 8k (take every 3rd sample)
        var pcm8k = new short[pcm24k.Length / 3];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            pcm8k[i] = pcm24k[i * 3];
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
