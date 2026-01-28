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

    private VoIPMediaSession? _mediaSession;
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
            // Create media session with PCMA codec
            var audioFormats = new List<AudioFormat>
            {
                new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)
            };

            _mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = null, AudioSink = null });
            _mediaSession.AcceptRtpFromAny = true;

            // Wire up inbound RTP
            _mediaSession.OnRtpPacketReceived += OnRtpPacketReceived;

            // Answer the call
            var uas = _ua.AcceptCall(invite);
            bool answered = await _ua.Answer(uas, _mediaSession);

            if (!answered)
            {
                Log("Failed to answer call");
                return false;
            }

            await _mediaSession.Start();
            Log("Call answered, RTP started");

            // Create PCMA playout engine
            _pcmaPlayout = new PcmaRtpPlayout(_mediaSession);
            _pcmaPlayout.OnDebugLog += msg => Log(msg);
            _pcmaPlayout.Start();

            // Create OpenAI client
            if (!string.IsNullOrEmpty(_apiKey))
            {
                _aiClient = new OpenAiRealtimeClient(_apiKey);
                _aiClient.OnLog += msg => Log(msg);
                _aiClient.OnTranscript += t => OnTranscript?.Invoke(t);
                _aiClient.OnPcm24Audio += pcmBytes =>
                {
                    _pcmaPlayout?.EnqueuePcm24(pcmBytes);
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
    /// Process inbound RTP: PCMA → PCM16@8k → resample to 24k → send to OpenAI.
    /// </summary>
    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio || _isHungup || _aiClient == null)
            return;

        var payload = rtpPacket.Payload;
        if (payload == null || payload.Length == 0)
            return;

        // Only handle PCMA (PT=8) for now
        if (rtpPacket.Header.PayloadType != 8)
            return;

        // Decode PCMA → PCM16 @ 8kHz
        var pcm8k = G711Codec.AlawToPcm16(payload);

        // Resample 8k → 24k for OpenAI (simple 3x interpolation)
        var pcm24k = Resample8kTo24k(pcm8k);

        // Send to OpenAI
        _aiClient.SendAudioToModel(pcm24k);
    }

    /// <summary>
    /// Simple 8kHz → 24kHz upsampling (3x linear interpolation).
    /// </summary>
    private byte[] Resample8kTo24k(byte[] pcm8k)
    {
        int samples8k = pcm8k.Length / 2;
        int samples24k = samples8k * 3;
        var pcm24k = new byte[samples24k * 2];

        for (int i = 0; i < samples8k; i++)
        {
            short sample = (short)(pcm8k[i * 2] | (pcm8k[i * 2 + 1] << 8));
            short nextSample = (i + 1 < samples8k)
                ? (short)(pcm8k[(i + 1) * 2] | (pcm8k[(i + 1) * 2 + 1] << 8))
                : sample;

            // Write 3 interpolated samples
            int outIdx = i * 3;
            WriteSample(pcm24k, outIdx, sample);
            WriteSample(pcm24k, outIdx + 1, (short)((sample * 2 + nextSample) / 3));
            WriteSample(pcm24k, outIdx + 2, (short)((sample + nextSample * 2) / 3));
        }

        return pcm24k;
    }

    private static void WriteSample(byte[] buffer, int sampleIndex, short value)
    {
        int byteIndex = sampleIndex * 2;
        if (byteIndex + 1 < buffer.Length)
        {
            buffer[byteIndex] = (byte)(value & 0xFF);
            buffer[byteIndex + 1] = (byte)((value >> 8) & 0xFF);
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

        try { _mediaSession?.Close(null); } catch { }

        OnCallFinished?.Invoke(_callId);
        await Task.CompletedTask;
    }
}
