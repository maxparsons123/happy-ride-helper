using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using NAudio.Wave;

namespace TaxiSipBridge;

/// <summary>
/// Manual call handler - allows operator to answer calls and speak directly via microphone.
/// No AI involved - just routes local mic to RTP and caller audio to speakers.
/// </summary>
public class ManualCallHandler : ISipCallHandler, IDisposable
{
    // ===========================================
    // CALL STATE
    // ===========================================
    private volatile bool _isInCall;
    private volatile bool _disposed;
    private volatile bool _isRinging;
    private volatile bool _isAnswered;

    private VoIPMediaSession? _currentMediaSession;
    private DirectRtpPlayout? _playout;
    private CancellationTokenSource? _callCts;
    private Action<SIPDialogue>? _currentHungupHandler;
    private SIPUserAgent? _currentUa;
    private SIPRequest? _pendingRequest;
    private UASInviteTransaction? _pendingUas;
    private int _hangupFired;

    // ===========================================
    // MICROPHONE CAPTURE
    // ===========================================
    private WaveInEvent? _waveIn;
    private readonly object _micLock = new();

    // Remote SDP payload type → codec mapping
    private readonly Dictionary<int, AudioCodecsEnum> _remotePtToCodec = new();

    // ===========================================
    // EVENTS
    // ===========================================
    public event Action<string>? OnLog;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnCallerAudioMonitor;
    
    /// <summary>Fired when a call is ringing (waiting for manual answer)</summary>
    public event Action<string>? OnRinging;
    
    /// <summary>Fired when call is answered</summary>
    public event Action? OnAnswered;

    // ===========================================
    // PROPERTIES
    // ===========================================
    public bool IsInCall => _isInCall;
    public bool IsRinging => _isRinging;
    public bool IsAnswered => _isAnswered;
    public string? CurrentCaller { get; private set; }
    public string? CurrentCallId { get; private set; }

    // ===========================================
    // CONSTRUCTOR
    // ===========================================
    public ManualCallHandler()
    {
    }

    // ===========================================
    // MAIN CALL HANDLER
    // ===========================================
    public async Task HandleIncomingCallAsync(SIPTransport transport, SIPUserAgent ua, SIPRequest req, string caller)
    {
        if (_disposed) return;

        if (_isInCall)
        {
            Log("⚠️ Already in a call, rejecting");
            var busyResponse = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null);
            await transport.SendResponseAsync(busyResponse);
            return;
        }

        _isInCall = true;
        _isRinging = true;
        _isAnswered = false;
        CurrentCallId = Guid.NewGuid().ToString("N")[..8];
        CurrentCaller = caller;
        _callCts = new CancellationTokenSource();
        _remotePtToCodec.Clear();
        _hangupFired = 0;

        // Store pending call info
        _currentUa = ua;
        _pendingRequest = req;
        
        // Parse remote SDP for codec info
        ParseRemoteSdp(CurrentCallId, req);

        // Send 180 Ringing
        Log($"📞 [{CurrentCallId}] Incoming call from {caller} - RINGING (waiting for manual answer)");
        try
        {
            var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
            await transport.SendResponseAsync(ringing);
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{CurrentCallId}] Ringing send failed: {ex.Message}");
        }

        // Accept the call (creates UAS transaction)
        _pendingUas = ua.AcceptCall(req);
        
        OnRinging?.Invoke(caller);
        OnCallStarted?.Invoke(CurrentCallId, caller);

        // Wait for manual answer or hangup
        try
        {
            while (!_callCts.IsCancellationRequested && _isRinging && !_disposed)
            {
                await Task.Delay(100, _callCts.Token);
            }
        }
        catch (OperationCanceledException) { }

        // If cancelled before answer, cleanup
        if (!_isAnswered)
        {
            await CleanupAsync(CurrentCallId, ua);
        }
    }

    /// <summary>
    /// Answer the pending call and start audio.
    /// </summary>
    public async Task AnswerCallAsync()
    {
        if (!_isRinging || _pendingUas == null || _currentUa == null || _pendingRequest == null)
        {
            Log("⚠️ No pending call to answer");
            return;
        }

        var callId = CurrentCallId ?? "unknown";
        var cts = _callCts!;

        try
        {
            _isRinging = false;
            _isAnswered = true;

            // Setup media session
            var audioEncoder = new UnifiedAudioEncoder();
            AudioCodecs.ResetAllCodecs();

            var audioSource = new AudioExtrasSource(
                audioEncoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None }
            );

            var mediaEndPoints = new MediaEndPoints { AudioSource = audioSource };
            _currentMediaSession = new VoIPMediaSession(mediaEndPoints);
            _currentMediaSession.AcceptRtpFromAny = true;

            // Answer call
            Log($"📞 [{callId}] Answering call...");
            bool answered = await _currentUa.Answer(_pendingUas, _currentMediaSession);
            if (!answered)
            {
                Log($"❌ [{callId}] Failed to answer");
                await CleanupAsync(callId, _currentUa);
                return;
            }

            await _currentMediaSession.Start();
            Log($"📗 [{callId}] Call answered and RTP started");

            // Create DirectRtpPlayout for outbound audio
            _playout = new DirectRtpPlayout(_currentMediaSession);
            _playout.OnLog += msg => Log(msg);
            _playout.Start();
            Log($"🎵 [{callId}] DirectRtpPlayout started");

            // Wire inbound RTP to speakers
            WireRtpInput(callId, cts);

            // Start microphone capture
            StartMicrophoneCapture(callId);

            // Wire hangup handler
            _currentHungupHandler = _ =>
            {
                if (Interlocked.Exchange(ref _hangupFired, 1) != 0) return;
                Log($"📕 [{callId}] Caller hung up");
                try { cts.Cancel(); } catch { }
            };
            _currentUa.OnCallHungup += _currentHungupHandler;

            OnAnswered?.Invoke();
            Log($"✅ [{callId}] Manual call fully established - speak into your microphone!");

            // Keep call alive
            while (!cts.IsCancellationRequested && !_disposed)
                await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"❌ [{callId}] Error: {ex.Message}");
        }
        finally
        {
            await CleanupAsync(callId, _currentUa!);
        }
    }

    /// <summary>
    /// Hang up the current call.
    /// </summary>
    public void HangUp()
    {
        if (_callCts != null && !_callCts.IsCancellationRequested)
        {
            Log($"📴 [{CurrentCallId}] Operator hung up");
            try { _callCts.Cancel(); } catch { }
        }
    }

    /// <summary>
    /// Reject the pending call.
    /// </summary>
    public void RejectCall()
    {
        if (_isRinging)
        {
            Log($"📴 [{CurrentCallId}] Call rejected");
            _isRinging = false;
            try { _callCts?.Cancel(); } catch { }
        }
    }

    // ===========================================
    // SDP PARSING
    // ===========================================
    private void ParseRemoteSdp(string callId, SIPRequest req)
    {
        try
        {
            var sdpBody = req.Body;
            if (string.IsNullOrEmpty(sdpBody)) return;

            var sdp = SDP.ParseSDPDescription(sdpBody);
            var audioMedia = sdp.Media.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
            if (audioMedia == null) return;

            var codecList = new List<string>();
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

                codecList.Add($"{name}(PT{pt})");
            }

            Log($"🎧 [{callId}] Available codecs: {string.Join(", ", codecList)}");
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{callId}] SDP parse error: {ex.Message}");
        }
    }

    // ===========================================
    // MICROPHONE CAPTURE
    // ===========================================
    private void StartMicrophoneCapture(string callId)
    {
        lock (_micLock)
        {
            StopMicrophoneCapture();

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(24000, 16, 1), // 24kHz PCM16 mono
                BufferMilliseconds = 20
            };

            _waveIn.DataAvailable += (s, e) =>
            {
                if (_disposed || _playout == null || e.BytesRecorded <= 0) return;

                // Copy buffer
                var buffer = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

                // Send to RTP via DirectRtpPlayout
                _playout.BufferAiAudio(buffer);
            };

            _waveIn.StartRecording();
            Log($"🎤 [{callId}] Microphone capture started (24kHz PCM16)");
        }
    }

    private void StopMicrophoneCapture()
    {
        lock (_micLock)
        {
            if (_waveIn != null)
            {
                try { _waveIn.StopRecording(); } catch { }
                try { _waveIn.Dispose(); } catch { }
                _waveIn = null;
            }
        }
    }

    // ===========================================
    // RTP INPUT HANDLING (Caller audio → Speakers)
    // ===========================================
    private void WireRtpInput(string callId, CancellationTokenSource cts)
    {
        if (_currentMediaSession == null) return;

        _currentMediaSession.OnRtpPacketReceived += (ep, mt, rtp) =>
        {
            if (mt != SDPMediaTypesEnum.audio) return;
            if (cts.Token.IsCancellationRequested) return;

            var payload = rtp.Payload;
            if (payload == null || payload.Length == 0) return;

            // Decode based on payload type
            var pt = rtp.Header.PayloadType;
            if (!_remotePtToCodec.TryGetValue(pt, out var codec))
            {
                codec = pt == 8 ? AudioCodecsEnum.PCMA : AudioCodecsEnum.PCMU;
            }

            short[] pcm16;
            int sampleRate = 8000;

            switch (codec)
            {
                case AudioCodecsEnum.PCMU:
                    pcm16 = payload.Select(b => SIPSorcery.Media.MuLawDecoder.MuLawToLinearSample(b)).ToArray();
                    break;

                case AudioCodecsEnum.PCMA:
                    pcm16 = payload.Select(b => SIPSorcery.Media.ALawDecoder.ALawToLinearSample(b)).ToArray();
                    break;

                case AudioCodecsEnum.OPUS:
                    try
                    {
                        var pcm48 = AudioCodecs.OpusDecode(payload);
                        short[] mono;
                        if (AudioCodecs.OPUS_DECODE_CHANNELS == 2 && pcm48.Length % 2 == 0)
                        {
                            mono = new short[pcm48.Length / 2];
                            for (int j = 0; j < mono.Length; j++)
                                mono[j] = (short)((pcm48[j * 2] + pcm48[j * 2 + 1]) / 2);
                        }
                        else
                        {
                            mono = pcm48;
                        }
                        pcm16 = new short[mono.Length / 2];
                        for (int j = 0; j < pcm16.Length; j++)
                            pcm16[j] = mono[j * 2];
                        sampleRate = 24000;
                    }
                    catch
                    {
                        return;
                    }
                    break;

                case AudioCodecsEnum.G722:
                    try
                    {
                        pcm16 = AudioCodecs.G722Decode(payload);
                        sampleRate = 16000;
                    }
                    catch
                    {
                        return;
                    }
                    break;

                default:
                    return;
            }

            // Resample to 24kHz for speaker output
            short[] monitorPcm24 = sampleRate == 24000
                ? pcm16
                : AudioCodecs.Resample(pcm16, sampleRate, 24000);

            var monitorBytes24 = new byte[monitorPcm24.Length * 2];
            Buffer.BlockCopy(monitorPcm24, 0, monitorBytes24, 0, monitorBytes24.Length);
            OnCallerAudioMonitor?.Invoke(monitorBytes24);
        };
    }

    // ===========================================
    // CLEANUP
    // ===========================================
    private async Task CleanupAsync(string callId, SIPUserAgent ua)
    {
        Log($"📴 [{callId}] Cleanup...");

        _isInCall = false;
        _isRinging = false;
        _isAnswered = false;
        CurrentCaller = null;
        _pendingRequest = null;
        _pendingUas = null;

        StopMicrophoneCapture();

        // Unsubscribe hangup handler
        if (_currentHungupHandler != null && _currentUa != null)
        {
            _currentUa.OnCallHungup -= _currentHungupHandler;
            _currentHungupHandler = null;
        }

        try { ua.Hangup(); } catch { }

        if (_playout != null)
        {
            try { _playout.Stop(); } catch { }
            try { _playout.Dispose(); } catch { }
            _playout = null;
        }

        if (_currentMediaSession != null)
        {
            try { _currentMediaSession.Close("call ended"); } catch { }
            _currentMediaSession = null;
        }

        try { _callCts?.Dispose(); } catch { }
        _callCts = null;

        OnCallEnded?.Invoke(callId);
    }

    private void Log(string msg)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    // ===========================================
    // DISPOSAL
    // ===========================================
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopMicrophoneCapture();

        if (_currentHungupHandler != null && _currentUa != null)
        {
            try { _currentUa.OnCallHungup -= _currentHungupHandler; } catch { }
            _currentHungupHandler = null;
        }

        try { _callCts?.Cancel(); } catch { }
        try { _playout?.Dispose(); } catch { }
        try { _currentMediaSession?.Close("disposed"); } catch { }

        GC.SuppressFinalize(this);
    }
}
