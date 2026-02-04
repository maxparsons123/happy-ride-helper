using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// Output codec mode for AI audio output.
/// </summary>
public enum OutputCodecMode
{
    MuLaw,   // G.711 µ-law @ 8kHz (narrowband)
    ALaw,    // G.711 A-law @ 8kHz (narrowband)
    Opus     // Opus @ 48kHz (wideband)
}

/// <summary>
/// Direct OpenAI Realtime API client with full booking flow.
/// Connects directly to OpenAI - no edge function required.
/// Version 5.2 - Full STT grounding, response cancellation, late confirmation detection.
/// </summary>
public class OpenAIRealtimeClient : IAudioAIClient
{
    // ===========================================
    // LAZY-INITIALIZED STATIC DATA (avoids TypeInitializationException)
    // ===========================================
    
    private static Dictionary<string, string>? _countryCodeMap;
    private static Dictionary<string, string> CountryCodeToLanguage
    {
        get
        {
            if (_countryCodeMap == null)
            {
                _countryCodeMap = new Dictionary<string, string>
                {
                    { "31", "nl" }, // Netherlands
                    { "32", "nl" }, // Belgium (Dutch)
                    { "33", "fr" }, // France
                    { "41", "de" }, // Switzerland
                    { "43", "de" }, // Austria
                    { "44", "en" }, // UK
                    { "49", "de" }, // Germany
                };
            }
            return _countryCodeMap;
        }
    }

    private static Dictionary<string, string>? _greetingsMap;
    private static Dictionary<string, string> LocalizedGreetings
    {
        get
        {
            if (_greetingsMap == null)
            {
                _greetingsMap = new Dictionary<string, string>
                {
                    { "en", "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. Where would you like to be picked up?" },
                    { "nl", "Hallo, en welkom bij de Taxibot demo. Ik ben Ada, uw taxi boekingsassistent. Waar wilt u worden opgehaald?" },
                    { "fr", "Bonjour et bienvenue à la démo Taxibot. Je suis Ada. Où souhaitez-vous être pris en charge?" },
                    { "de", "Hallo und willkommen zur Taxibot-Demo. Ich bin Ada. Wo möchten Sie abgeholt werden?" },
                };
            }
            return _greetingsMap;
        }
    }

    private static Dictionary<string, string>? _sttCorrectionsMap;
    private static Dictionary<string, string> SttCorrections
    {
        get
        {
            if (_sttCorrectionsMap == null)
            {
                _sttCorrectionsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Address mishearings
                    { "52 I ain't dead bro", "52A David Road" },
                    { "52 I ain't David", "52A David Road" },
                    { "52 ain't David", "52A David Road" },
                    { "52 a David", "52A David Road" },
                    { "baby girl", "David Road" },
                    { "Coltree", "Coventry" },
                    { "Coal Tree", "Coventry" },
                    { "in Country", "in Coventry" },
                    // Time expressions
                    { "for now", "now" },
                    { "right now", "now" },
                    { "as soon as possible", "now" },
                    { "ASAP", "now" },
                    { "straight away", "now" },
                    { "immediately", "now" },
                    // Affirmatives
                    { "yeah please", "yes please" },
                    { "yep", "yes" },
                    { "yup", "yes" },
                    { "yeah", "yes" },
                    { "yesy", "yes" },
                    { "that's right", "yes" },
                    { "correct", "yes" },
                    { "go ahead", "yes" },
                    { "book it", "yes" },
                    { "sure", "yes" },
                    { "okay", "yes" },
                    { "alright", "yes" },
                };
            }
            return _sttCorrectionsMap;
        }
    }

    // ===========================================
    // INSTANCE FIELDS
    // ===========================================
    
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _systemPrompt;
    private readonly string? _dispatchWebhookUrl;

    // Lazy HttpClient to avoid static init issues
    private HttpClient? _httpClientBacking;
    private HttpClient HttpClient => _httpClientBacking ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _disposed = 0;

    // Audio queue for RTP transmission
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 800;

    // Audio processor (lazy init)
    private OptimizedAudioProcessor? _audioProcessor;
    private OptimizedAudioProcessor AudioProcessor => _audioProcessor ??= new OptimizedAudioProcessor();
    private int _audioPacketsSent = 0;

    // Output codec
    private OutputCodecMode _outputCodec = OutputCodecMode.ALaw;
    private short[] _opusResampleBuffer = Array.Empty<short>();

    // Session state
    private string? _callerId;
    private string _callId = "";
    private string _detectedLanguage = "en";
    private string _lastQuestionAsked = "pickup";
    private BookingState _booking = new();
    private int _greetingSent = 0;
    private bool _sessionConfigured = false;
    
    // ===========================================
    // RESPONSE LIFECYCLE TRACKING (v5.2)
    // ===========================================
    private int _responseActive = 0;
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;
    private int _transcriptPending = 0;           // 1 = waiting for STT transcript
    private int _waitingForSttTranscript = 0;     // v5.2: Block response until transcript arrives
    private long _responseCreatedAt = 0;
    private long _speechStoppedAt = 0;
    private long _speechStartedAt = 0;
    private int _deferredResponsePending = 0;
    private int _noReplyWatchdogId = 0;
    private int _ignoreUserAudio = 0;
    private string? _pendingUserTranscript = null; // v5.2: Store transcript that arrived during response

    // Echo guard
    private const int ECHO_GUARD_MS = 300;
    private const int BARGE_IN_GRACE_MS = 500;    // v5.2: Grace period after Ada finishes before barge-in cancels response
    private long _lastAdaFinishedAt = 0;
    private bool _awaitingConfirmation = false;

    // Post-booking hangup
    private const int POST_BOOKING_SILENCE_HANGUP_MS = 10000;
    private const int POST_BOOKING_SILENCE_POLL_MS = 250;
    private CancellationTokenSource? _postBookingHangupCts;
    private long _lastUserSpeechAt = 0;
    private bool _postBookingHangupArmed = false;
    private bool _postBookingFinalSpeechDelivered = false;
    private int _callEnded = 0;

    // Audio buffer tracking
    private double _inputBufferedMs = 0;

    // ===========================================
    // EVENTS
    // ===========================================
    
    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnAdaSpeaking;
    public event Action<byte[]>? OnPcm24Audio;
    public event Action? OnResponseStarted;
    public event Action<byte[]>? OnCallerAudioMonitor;
    public event Action<string, Dictionary<string, object>>? OnToolCall;
    public event Action<BookingState>? OnBookingUpdated;
    public event Action? OnCallEnded;
    public event Action? OnResponseCompleted;
    
    // v4.2: A-law output event for direct SIP passthrough
    public event Action<byte[]>? OnALawAudio;

    // ===========================================
    // PROPERTIES
    // ===========================================
    
    public bool IsConnected => _ws?.State == WebSocketState.Open && Volatile.Read(ref _disposed) == 0;
    public int PendingFrameCount => _outboundQueue.Count;
    public OutputCodecMode OutputCodec => _outputCodec;
    public string DetectedLanguage => _detectedLanguage;
    public bool IsResponseActive => Volatile.Read(ref _responseActive) == 1;

    // ===========================================
    // CONSTRUCTOR
    // ===========================================
    
    public OpenAIRealtimeClient(
        string apiKey,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "shimmer",
        string? systemPrompt = null,
        string? dispatchWebhookUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _systemPrompt = systemPrompt ?? GetDefaultSystemPrompt();
        _dispatchWebhookUrl = dispatchWebhookUrl;
        _callId = $"local-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    // ===========================================
    // PUBLIC METHODS
    // ===========================================
    
    public void SetOutputCodec(OutputCodecMode codec)
    {
        _outputCodec = codec;
        Log($"🎵 Output codec: {codec}");
    }

    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(OpenAIRealtimeClient));

        // Reset state for new call
        _callerId = caller;
        _callId = $"local-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        _detectedLanguage = DetectLanguageFromPhone(caller);
        Interlocked.Exchange(ref _greetingSent, 0);
        _sessionConfigured = false;
        _booking = new BookingState();
        _lastQuestionAsked = "pickup";
        _audioPacketsSent = 0;
        _inputBufferedMs = 0;
        _awaitingConfirmation = false;
        _postBookingHangupArmed = false;
        _postBookingFinalSpeechDelivered = false;
        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _transcriptPending, 0);
        Interlocked.Exchange(ref _waitingForSttTranscript, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        _activeResponseId = null;
        _lastCompletedResponseId = null;
        _pendingUserTranscript = null;
        Volatile.Write(ref _speechStartedAt, 0);
        Volatile.Write(ref _speechStoppedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastAdaFinishedAt, 0);
        
        AudioProcessor.Reset();
        ClearPendingFrames();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Log($"🌐 Language: {_detectedLanguage} (caller: {caller ?? "unknown"})");
        Log($"📞 Call ID: {_callId}");

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_model}");
        Log($"🔌 Connecting to: {uri}");

        try
        {
            await _ws.ConnectAsync(uri, _cts.Token);
            Log($"✅ WebSocket connected! State: {_ws.State}");
            OnConnected?.Invoke();

            // Start receive loop in background
            _ = Task.Run(() => ReceiveLoopAsync(), _cts.Token);
        }
        catch (Exception ex)
        {
            Log($"❌ Connection failed: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        Log("📴 Disconnecting...");

        try
        {
            _postBookingHangupCts?.Cancel();
            
            if (_ws?.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
            }
        }
        catch { }
        finally
        {
            _cts?.Cancel();
            OnDisconnected?.Invoke();
        }
    }

    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (!IsConnected) return;

        // Skip audio if call ended or ignoring user audio
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        // Echo guard - skip audio right after Ada finishes (unless awaiting confirmation)
        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < ECHO_GUARD_MS)
            return;

        var pcm8k = AudioCodecs.MuLawDecode(ulawData);
        var pcm24k = AudioProcessor.PrepareForOpenAI(pcm8k, 8000, 24000);
        
        _inputBufferedMs += ulawData.Length * 1000.0 / 8000.0;

        await SendAudioBufferAsync(pcm24k);
    }

    public async Task SendPcm8kAsync(byte[] pcm8kBytes)
    {
        if (!IsConnected) return;

        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < ECHO_GUARD_MS)
            return;

        var pcm24k = AudioProcessor.PrepareForOpenAI(pcm8kBytes, 8000, 24000);
        _inputBufferedMs += (pcm8kBytes.Length / 2) * 1000.0 / 8000.0;

        await SendAudioBufferAsync(pcm24k);
    }

    public async Task SendPcm8kNoDspAsync(byte[] pcm8kBytes)
    {
        if (!IsConnected) return;

        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < ECHO_GUARD_MS)
            return;

        var pcm24k = AudioProcessor.PrepareForOpenAI(pcm8kBytes, 8000, 24000);
        _inputBufferedMs += (pcm8kBytes.Length / 2) * 1000.0 / 8000.0;

        await SendAudioBufferAsync(pcm24k);
    }

    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (!IsConnected) return;

        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < ECHO_GUARD_MS)
            return;

        byte[] pcm24k;
        if (sampleRate != 24000)
        {
            var samples = AudioCodecs.BytesToShorts(pcmData);
            var resampled = AudioCodecs.Resample(samples, sampleRate, 24000);
            pcm24k = AudioCodecs.ShortsToBytes(resampled);
        }
        else
        {
            pcm24k = pcmData;
        }

        _inputBufferedMs += (pcm24k.Length / 2) * 1000.0 / 24000.0;
        await SendAudioBufferAsync(pcm24k);
    }

    public byte[]? GetNextMuLawFrame()
    {
        return _outboundQueue.TryDequeue(out var frame) ? frame : null;
    }

    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        Log("🔌 Disposing client...");

        try { _postBookingHangupCts?.Cancel(); } catch { }
        try { _postBookingHangupCts?.Dispose(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }

        try
        {
            if (_ws?.State == WebSocketState.Open)
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposed", CancellationToken.None).Wait(500);
        }
        catch { }

        try { _ws?.Dispose(); } catch { }
        try { _httpClientBacking?.Dispose(); } catch { }

        ClearPendingFrames();
        _opusResampleBuffer = Array.Empty<short>();

        GC.SuppressFinalize(this);
    }

    // ===========================================
    // PRIVATE - AUDIO SENDING
    // ===========================================
    
    private async Task SendAudioBufferAsync(byte[] pcm24k)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            _audioPacketsSent++;
            if (_audioPacketsSent <= 3 || _audioPacketsSent % 100 == 0)
                Log($"📤 Audio #{_audioPacketsSent}: {pcm24k.Length}b PCM24");

            var msg = JsonSerializer.Serialize(new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(pcm24k)
            });

            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log($"⚠️ Send error: {ex.Message}"); }
    }

    private async Task SendJsonAsync(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var json = JsonSerializer.Serialize(obj);
            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log($"⚠️ Send error: {ex.Message}"); }
    }

    // ===========================================
    // PRIVATE - RECEIVE LOOP
    // ===========================================
    
    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1024 * 64];
        var messageBuilder = new StringBuilder();

        Log("🔄 Receive loop started");

        while (Volatile.Read(ref _disposed) == 0 && _ws?.State == WebSocketState.Open && !(_cts?.IsCancellationRequested ?? true))
        {
            try
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts!.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log("📴 WebSocket closed by server");
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var json = messageBuilder.ToString();
                    messageBuilder.Clear();
                    await ProcessMessageAsync(json);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                Log($"❌ WebSocket error: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                Log($"❌ Receive error: {ex.Message}");
                break;
            }
        }

        Log("🔄 Receive loop ended");
        OnDisconnected?.Invoke();
    }

    // ===========================================
    // PRIVATE - MESSAGE PROCESSING (v5.2 Full)
    // ===========================================
    
    private async Task ProcessMessageAsync(string json)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;

            var type = typeEl.GetString();
            if (string.IsNullOrEmpty(type)) return;

            switch (type)
            {
                case "session.created":
                    await ConfigureSessionAsync().ConfigureAwait(false);
                    break;

                case "session.updated":
                    if (Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
                        await SendGreetingAsync().ConfigureAwait(false);
                    break;

                case "response.created":
                    await HandleResponseCreatedAsync(doc.RootElement).ConfigureAwait(false);
                    break;

                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var deltaEl))
                    {
                        var delta = deltaEl.GetString();
                        if (!string.IsNullOrEmpty(delta))
                            ProcessAudioDelta(delta);
                    }
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var d))
                        OnAdaSpeaking?.Invoke(d.GetString() ?? "");
                    break;

                case "response.audio_transcript.done":
                    HandleAdaTranscriptDone(doc.RootElement);
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    await HandleUserTranscriptAsync(doc.RootElement).ConfigureAwait(false);
                    break;

                case "input_audio_buffer.speech_started":
                    HandleSpeechStarted();
                    break;

                case "input_audio_buffer.speech_stopped":
                    HandleSpeechStopped();
                    break;

                case "response.done":
                    await HandleResponseDoneAsync(doc.RootElement).ConfigureAwait(false);
                    break;

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement).ConfigureAwait(false);
                    break;

                case "error":
                    HandleError(doc.RootElement);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Parse error: {ex.Message}");
        }
    }

    // ===========================================
    // PRIVATE - EVENT HANDLERS (v5.2)
    // ===========================================

    private async Task HandleResponseCreatedAsync(JsonElement root)
    {
        var responseId = TryGetResponseId(root);
        if (responseId != null && _activeResponseId == responseId)
            return; // Ignore duplicate

        _activeResponseId = responseId;
        Interlocked.Exchange(ref _responseActive, 1);
        Volatile.Write(ref _responseCreatedAt, NowMs());

        // v5.2: Check if we're still waiting for a user transcript
        // If so, cancel this response and wait for the transcript
        if (Volatile.Read(ref _waitingForSttTranscript) == 1)
        {
            Log("🚫 Response created while waiting for STT - cancelling to prevent hallucination");
            await SendJsonAsync(new { type = "response.cancel" }).ConfigureAwait(false);
            return;
        }

        // v5.2: Only clear audio buffer if no transcript is pending
        // This prevents losing the user's speech when response starts
        if (Volatile.Read(ref _transcriptPending) == 0)
        {
            await ClearInputAudioBufferAsync().ConfigureAwait(false);
        }
        else
        {
            Log("⏳ Transcript pending - NOT clearing audio buffer");
        }

        Log("🤖 AI response started");
        OnResponseStarted?.Invoke();
    }

    private void HandleSpeechStarted()
    {
        var now = NowMs();
        Volatile.Write(ref _lastUserSpeechAt, now);
        Volatile.Write(ref _speechStartedAt, now);
        
        // Cancel any pending no-reply watchdog
        Interlocked.Increment(ref _noReplyWatchdogId);
        
        // Mark that we're expecting a transcript
        Interlocked.Exchange(ref _transcriptPending, 1);
        Interlocked.Exchange(ref _waitingForSttTranscript, 1);

        Log("🎤 Speech started - awaiting transcript");

        // v5.2: If AI is currently responding and user starts speaking (barge-in),
        // cancel the response ONLY if we're past the grace period after Ada finished
        if (Volatile.Read(ref _responseActive) == 1)
        {
            var timeSinceAdaFinished = now - Volatile.Read(ref _lastAdaFinishedAt);
            
            // Don't cancel during echo period (user might be echo of Ada's voice)
            if (timeSinceAdaFinished > BARGE_IN_GRACE_MS)
            {
                Log("🛑 Barge-in detected - cancelling AI response");
                _ = SendJsonAsync(new { type = "response.cancel" });
            }
            else
            {
                Log($"⏳ Speech during echo window ({timeSinceAdaFinished}ms) - not cancelling");
            }
        }
    }

    private void HandleSpeechStopped()
    {
        Volatile.Write(ref _lastUserSpeechAt, NowMs());
        Volatile.Write(ref _speechStoppedAt, NowMs());
        
        // Commit audio buffer - OpenAI will transcribe
        _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
        Log("📝 Committed audio buffer (awaiting transcript)");
    }

    private async Task HandleUserTranscriptAsync(JsonElement root)
    {
        // Clear the waiting flags
        Interlocked.Exchange(ref _transcriptPending, 0);
        Interlocked.Exchange(ref _waitingForSttTranscript, 0);

        if (!root.TryGetProperty("transcript", out var u) || string.IsNullOrWhiteSpace(u.GetString()))
            return;

        var text = ApplySttCorrections(u.GetString()!);

        // Calculate timing for logging
        var msSinceSpeechStopped = NowMs() - Volatile.Read(ref _speechStoppedAt);
        var msSinceResponseCreated = NowMs() - Volatile.Read(ref _responseCreatedAt);
        var responseActive = Volatile.Read(ref _responseActive) == 1;

        if (responseActive)
        {
            Log($"📝 Transcript arrived {msSinceSpeechStopped}ms after speech stopped, {msSinceResponseCreated}ms after response.created: {text}");
        }
        else
        {
            Log($"📝 Transcript: {text}");
        }

        Log($"👤 User: {text}");
        OnTranscript?.Invoke($"You: {text}");

        // v5.2: LATE CONFIRMATION DETECTION
        // If no response is active and we're awaiting confirmation, check for affirmative
        if (!responseActive && _awaitingConfirmation)
        {
            if (IsAffirmativeResponse(text))
            {
                Log("✅ Late confirmation detected - injecting system prompt and triggering response");
                await HandleLateConfirmationAsync(text).ConfigureAwait(false);
                return;
            }
        }

        // v5.2: If response was cancelled or never started, trigger a new one now that we have the transcript
        if (!responseActive)
        {
            Log("🔄 No active response - triggering response.create after transcript");
            await QueueResponseCreateAsync(delayMs: 50, waitForCurrentResponse: false).ConfigureAwait(false);
        }
    }

    private void HandleAdaTranscriptDone(JsonElement root)
    {
        if (!root.TryGetProperty("transcript", out var t) || string.IsNullOrWhiteSpace(t.GetString()))
            return;

        var text = t.GetString()!;
        Log($"💬 Ada: {text}");
        OnTranscript?.Invoke($"Ada: {text}");

        // Track if post-booking speech has been delivered
        if (_booking.Confirmed && _postBookingHangupArmed)
            _postBookingFinalSpeechDelivered = true;

        // Goodbye detection watchdog
        if (text.Contains("Goodbye", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("Taxibot", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Thank you for using", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("for calling", StringComparison.OrdinalIgnoreCase)))
        {
            Log("👋 Goodbye phrase detected - arming 5s hangup watchdog");
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000).ConfigureAwait(false);
                if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                    return;
                Log("⏰ Goodbye watchdog triggered - forcing end_call");
                Interlocked.Exchange(ref _ignoreUserAudio, 1);
                SignalCallEnded("goodbye_watchdog");
            });
        }
    }

    private async Task HandleResponseDoneAsync(JsonElement root)
    {
        var responseId = TryGetResponseId(root);

        // Ignore duplicate or stale response.done
        if (responseId == null || responseId == _lastCompletedResponseId)
            return;
        if (_activeResponseId != null && responseId != _activeResponseId)
            return;

        _lastCompletedResponseId = responseId;
        _activeResponseId = null;
        Interlocked.Exchange(ref _responseActive, 0);
        Volatile.Write(ref _lastAdaFinishedAt, NowMs());

        Log("🤖 AI response completed");
        OnResponseCompleted?.Invoke();

        // Flush deferred response if pending
        if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1)
        {
            Log("🔄 Flushing deferred response.create");
            _ = Task.Run(async () =>
            {
                await Task.Delay(20).ConfigureAwait(false);
                if (Volatile.Read(ref _callEnded) == 0 && Volatile.Read(ref _disposed) == 0)
                {
                    await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                    Log("🔄 response.create sent (deferred)");
                }
            });
        }

        MaybeStartPostBookingSilenceHangup();

        // Start no-reply watchdog
        StartNoReplyWatchdog();
    }

    private void HandleError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
        {
            var msg = m.GetString() ?? "";
            // Ignore common non-critical errors
            if (!msg.Contains("buffer too small", StringComparison.OrdinalIgnoreCase) &&
                !msg.Contains("already has an active response", StringComparison.OrdinalIgnoreCase))
            {
                Log($"❌ OpenAI: {msg}");
            }
        }
    }

    // ===========================================
    // PRIVATE - HELPER METHODS
    // ===========================================
    
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? TryGetResponseId(JsonElement root)
    {
        if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("id", out var id))
            return id.GetString();
        return null;
    }

    private async Task ClearInputAudioBufferAsync()
    {
        await SendJsonAsync(new { type = "input_audio_buffer.clear" }).ConfigureAwait(false);
        Log("🧹 Cleared OpenAI input audio buffer");
    }

    private static bool IsAffirmativeResponse(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("yes") ||
               lower.Contains("yeah") ||
               lower.Contains("yep") ||
               lower.Contains("yup") ||
               lower.Contains("correct") ||
               lower.Contains("that's right") ||
               lower.Contains("go ahead") ||
               lower.Contains("confirm") ||
               lower.Contains("book it") ||
               lower.Contains("sure") ||
               lower.Contains("please") ||
               lower.Contains("okay") ||
               lower.Contains("alright");
    }

    private async Task HandleLateConfirmationAsync(string text)
    {
        // Inject a system message to force the AI to confirm the booking
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "system",
                content = new[]
                {
                    new
                    {
                        type = "input_text",
                        text = $"[USER CONFIRMED BOOKING] The user said '{text}' which is a confirmation. Call book_taxi(action='confirmed') NOW."
                    }
                }
            }
        }).ConfigureAwait(false);

        await QueueResponseCreateAsync(delayMs: 20, waitForCurrentResponse: false).ConfigureAwait(false);
    }

    private void StartNoReplyWatchdog()
    {
        var watchdogDelayMs = _awaitingConfirmation ? 20000 : 15000;
        var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);
        
        _ = Task.Run(async () =>
        {
            await Task.Delay(watchdogDelayMs).ConfigureAwait(false);

            // Abort if cancelled, call ended, user spoke, or transcript pending
            if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
            if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;
            if (Volatile.Read(ref _responseActive) == 1) return;
            if (Volatile.Read(ref _transcriptPending) == 1) return;
            
            // v5.1: Don't interrupt if user spoke recently
            if (NowMs() - Volatile.Read(ref _lastUserSpeechAt) < 3000) return;

            Log($"⏰ No-reply watchdog triggered ({watchdogDelayMs}ms) - prompting user");

            await SendJsonAsync(new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "system",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "[SILENCE DETECTED] The user has not responded. Gently ask if they're still there or repeat your last question briefly."
                        }
                    }
                }
            }).ConfigureAwait(false);

            await QueueResponseCreateAsync(delayMs: 20, waitForCurrentResponse: false).ConfigureAwait(false);
        });
    }

    private async Task QueueResponseCreateAsync(int delayMs = 0, bool waitForCurrentResponse = true, int maxWaitMs = 2500)
    {
        if (waitForCurrentResponse)
        {
            // Wait for current response to complete
            var waited = 0;
            while (Volatile.Read(ref _responseActive) == 1 && waited < maxWaitMs)
            {
                await Task.Delay(20).ConfigureAwait(false);
                waited += 20;
            }

            if (Volatile.Read(ref _responseActive) == 1)
            {
                // Still active - defer the response
                Log("⏳ Response still active - deferring response.create");
                Interlocked.Exchange(ref _deferredResponsePending, 1);
                return;
            }
        }

        if (delayMs > 0)
            await Task.Delay(delayMs).ConfigureAwait(false);

        if (Volatile.Read(ref _callEnded) == 0 && Volatile.Read(ref _disposed) == 0)
        {
            await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
        }
    }

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.Exchange(ref _callEnded, 1) == 1) return;
        Log($"📴 Call ended: {reason}");
        _postBookingHangupCts?.Cancel();
        OnCallEnded?.Invoke();
    }

    private void ProcessAudioDelta(string base64)
    {
        if (!IsConnected) return;
        
        try
        {
            var audioBytes = Convert.FromBase64String(base64);

            // When using A-law mode, OpenAI sends raw G.711 A-law bytes directly
            if (_outputCodec == OutputCodecMode.ALaw)
            {
                OnALawAudio?.Invoke(audioBytes);

                // Split into 20ms frames (160 bytes @ 8kHz)
                for (int i = 0; i < audioBytes.Length; i += 160)
                {
                    int len = Math.Min(160, audioBytes.Length - i);
                    var frame = new byte[160];
                    Buffer.BlockCopy(audioBytes, i, frame, 0, len);
                    if (len < 160) Array.Fill(frame, (byte)0xD5, len, 160 - len);
                    EnqueueFrame(frame);
                }
                return;
            }

            // PCM16 path for other codecs
            var pcm24k = audioBytes;
            OnPcm24Audio?.Invoke(pcm24k);

            var samples24k = AudioCodecs.BytesToShorts(pcm24k);

            switch (_outputCodec)
            {
                case OutputCodecMode.Opus:
                    ProcessOpusOutput(samples24k);
                    break;
                default:
                    ProcessMuLawOutput(samples24k);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Audio delta error: {ex.Message}");
        }
    }

    private void ProcessOpusOutput(short[] pcm24k)
    {
        // Upsample 24kHz → 48kHz
        var pcm48k = new short[pcm24k.Length * 2];
        for (int i = 0; i < pcm24k.Length; i++)
        {
            pcm48k[i * 2] = pcm24k[i];
            pcm48k[i * 2 + 1] = i < pcm24k.Length - 1 
                ? (short)((pcm24k[i] + pcm24k[i + 1]) / 2) 
                : pcm24k[i];
        }

        // Accumulate for 20ms Opus frames (960 samples @ 48kHz)
        var newBuffer = new short[_opusResampleBuffer.Length + pcm48k.Length];
        Array.Copy(_opusResampleBuffer, newBuffer, _opusResampleBuffer.Length);
        Array.Copy(pcm48k, 0, newBuffer, _opusResampleBuffer.Length, pcm48k.Length);
        _opusResampleBuffer = newBuffer;

        const int OPUS_FRAME = 960;
        while (_opusResampleBuffer.Length >= OPUS_FRAME)
        {
            var frame = new short[OPUS_FRAME];
            Array.Copy(_opusResampleBuffer, frame, OPUS_FRAME);

            var opusFrame = AudioCodecs.OpusEncode(frame);
            EnqueueFrame(opusFrame);

            var remaining = new short[_opusResampleBuffer.Length - OPUS_FRAME];
            Array.Copy(_opusResampleBuffer, OPUS_FRAME, remaining, 0, remaining.Length);
            _opusResampleBuffer = remaining;
        }
    }

    private void ProcessMuLawOutput(short[] pcm24k)
    {
        int outputLen = pcm24k.Length / 3;
        var pcm8k = new short[outputLen];
        for (int i = 0; i < outputLen; i++)
            pcm8k[i] = pcm24k[i * 3];

        var ulaw = AudioCodecs.MuLawEncode(pcm8k);

        for (int i = 0; i < ulaw.Length; i += 160)
        {
            int len = Math.Min(160, ulaw.Length - i);
            var frame = new byte[160];
            Buffer.BlockCopy(ulaw, i, frame, 0, len);
            if (len < 160) Array.Fill(frame, (byte)0xFF, len, 160 - len);
            EnqueueFrame(frame);
        }
    }

    private void EnqueueFrame(byte[] frame)
    {
        if (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
            _outboundQueue.TryDequeue(out _);
        _outboundQueue.Enqueue(frame);
    }

    // ===========================================
    // PRIVATE - SESSION CONFIGURATION
    // ===========================================
    
    private async Task ConfigureSessionAsync()
    {
        // Determine output format based on codec mode
        var outputFormat = _outputCodec == OutputCodecMode.ALaw ? "g711_alaw" : "pcm16";
        
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = GetLocalizedSystemPrompt(),
                voice = _voice,
                input_audio_format = "pcm16",
                output_audio_format = outputFormat,
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.35,           // v5.2: Lower threshold for better detection
                    prefix_padding_ms = 350,    // v5.2: Slightly shorter prefix
                    silence_duration_ms = 800   // v5.2: Faster turn detection
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };

        Log($"🎧 VAD: threshold=0.35, prefix=350ms, silence=800ms, output={outputFormat}");
        await SendJsonAsync(config);
    }

    private async Task SendGreetingAsync()
    {
        await Task.Delay(200);

        var greeting = GetLocalizedGreeting();
        var langName = GetLanguageName(_detectedLanguage);

        var responseCreate = new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = $"Greet the caller warmly in {langName}. Say: \"{greeting}\""
            }
        };

        Log($"📢 Triggering greeting in {_detectedLanguage}");
        await SendJsonAsync(responseCreate);
    }

    // ===========================================
    // PRIVATE - TOOL HANDLING
    // ===========================================
    
    private async Task HandleToolCallAsync(JsonElement root)
    {
        if (!root.TryGetProperty("name", out var nameEl)) return;
        var toolName = nameEl.GetString();

        var args = new Dictionary<string, object>();
        if (root.TryGetProperty("arguments", out var argsEl))
        {
            try
            {
                var argsJson = argsEl.GetString();
                if (!string.IsNullOrEmpty(argsJson))
                {
                    using var argsDoc = JsonDocument.Parse(argsJson);
                    foreach (var prop in argsDoc.RootElement.EnumerateObject())
                    {
                        args[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString() ?? "",
                            JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString()
                        };
                    }
                }
            }
            catch { }
        }

        Log($"🔧 Tool: {toolName} {JsonSerializer.Serialize(args)}");
        OnToolCall?.Invoke(toolName ?? "", args);

        string? callId = root.TryGetProperty("call_id", out var cid) ? cid.GetString() : null;

        switch (toolName)
        {
            case "sync_booking_data":
                if (args.TryGetValue("pickup", out var p)) _booking.Pickup = p.ToString();
                if (args.TryGetValue("destination", out var d)) _booking.Destination = d.ToString();
                if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax.ToString(), out var n)) _booking.Passengers = n;
                if (args.TryGetValue("pickup_time", out var t)) _booking.PickupTime = t.ToString();
                if (args.TryGetValue("last_question_asked", out var q)) _lastQuestionAsked = q.ToString() ?? "none";
                OnBookingUpdated?.Invoke(_booking);
                await SendToolResultAsync(callId, new { success = true, state = _booking });
                break;

            case "book_taxi":
                await HandleBookTaxiAsync(args, callId);
                break;

            case "end_call":
                Log("📞 End call requested");
                Interlocked.Exchange(ref _ignoreUserAudio, 1);
                await SendToolResultAsync(callId, new { success = true });
                SignalCallEnded("end_call tool");
                break;

            default:
                await SendToolResultAsync(callId, new { error = $"Unknown tool: {toolName}" });
                break;
        }
    }

    private async Task HandleBookTaxiAsync(Dictionary<string, object> args, string? callId)
    {
        var action = args.TryGetValue("action", out var a) ? a.ToString() : "unknown";
        
        if (action == "request_quote")
        {
            // Use Lovable AI edge function for address resolution + fare
            var fareResult = await FareCalculator.CalculateFareWithCoordsAsync(
                _booking.Pickup, 
                _booking.Destination, 
                _callerId);
            
            _booking.Fare = fareResult.Fare;
            _booking.Eta = fareResult.Eta;
            _booking.DistanceMiles = fareResult.DistanceMiles;
            
            // Populate geocoding data
            _booking.PickupLat = fareResult.PickupLat;
            _booking.PickupLon = fareResult.PickupLon;
            _booking.PickupStreet = fareResult.PickupStreet;
            _booking.PickupNumber = fareResult.PickupNumber;
            _booking.PickupCity = fareResult.PickupCity;
            _booking.PickupPostalCode = fareResult.PickupPostalCode;
            _booking.PickupFormatted = fareResult.PickupFormatted;
            
            _booking.DestLat = fareResult.DestLat;
            _booking.DestLon = fareResult.DestLon;
            _booking.DestStreet = fareResult.DestStreet;
            _booking.DestNumber = fareResult.DestNumber;
            _booking.DestCity = fareResult.DestCity;
            _booking.DestPostalCode = fareResult.DestPostalCode;
            _booking.DestFormatted = fareResult.DestFormatted;
            
            OnBookingUpdated?.Invoke(_booking);
            
            Log($"💰 Quote: {fareResult.Fare} ({fareResult.DistanceMiles:F1} miles)");
            
            await SendToolResultAsync(callId, new
            {
                success = true,
                fare = fareResult.Fare,
                eta = fareResult.Eta,
                distance_miles = Math.Round(fareResult.DistanceMiles, 1),
                message = $"Your fare is {fareResult.Fare} and your driver will arrive in {fareResult.Eta}. Would you like me to book that?"
            });

            _awaitingConfirmation = true;
            Log("🎯 Awaiting confirmation - echo guard disabled");
        }
        else if (action == "confirmed")
        {
            _awaitingConfirmation = false;
            _booking.Confirmed = true;
            _booking.BookingRef = $"TAXI-{DateTime.Now:yyyyMMddHHmmss}";
            OnBookingUpdated?.Invoke(_booking);
            
            Log($"✅ Booking confirmed: {_booking.BookingRef}");
            
            // Send dispatch to BSQD webhook
            _ = SendDispatchWebhookAsync();
            
            // Send WhatsApp notification (fire and forget)
            _ = SendWhatsAppNotificationAsync(_callerId);
            
            await SendToolResultAsync(callId, new
            {
                success = true,
                booking_ref = _booking.BookingRef,
                message = "Your taxi is booked! Your driver will arrive shortly.",
                next_action = "Say thank you and goodbye, then call end_call."
            });

            _postBookingHangupArmed = true;
            
            // Inject instruction to end call
            await Task.Delay(3000);
            await SendJsonAsync(new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "system",
                    content = new[] { new { type = "input_text", text = "[BOOKING COMPLETE] Say goodbye and call end_call NOW." } }
                }
            });
            await SendJsonAsync(new { type = "response.create" });
        }
    }

    private async Task SendToolResultAsync(string? callId, object result)
    {
        if (callId == null) return;

        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = JsonSerializer.Serialize(result)
            }
        });
        await SendJsonAsync(new { type = "response.create" });
    }

    // ===========================================
    // PRIVATE - DISPATCH & NOTIFICATIONS
    // ===========================================

    private async Task SendDispatchWebhookAsync()
    {
        if (string.IsNullOrEmpty(_dispatchWebhookUrl))
        {
            // Use default BSQD webhook
            var webhookUrl = "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/voice_AI_taxibot";
            
            try
            {
                var payload = new
                {
                    event_type = "taxi_booking",
                    call_id = _callId,
                    caller_phone = _callerId,
                    booking = new
                    {
                        pickup = _booking.Pickup,
                        pickup_lat = _booking.PickupLat,
                        pickup_lon = _booking.PickupLon,
                        pickup_street = _booking.PickupStreet,
                        pickup_number = _booking.PickupNumber,
                        pickup_city = _booking.PickupCity,
                        pickup_postal = _booking.PickupPostalCode,
                        pickup_formatted = _booking.PickupFormatted,
                        destination = _booking.Destination,
                        dest_lat = _booking.DestLat,
                        dest_lon = _booking.DestLon,
                        dest_street = _booking.DestStreet,
                        dest_number = _booking.DestNumber,
                        dest_city = _booking.DestCity,
                        dest_postal = _booking.DestPostalCode,
                        dest_formatted = _booking.DestFormatted,
                        passengers = _booking.Passengers,
                        pickup_time = _booking.PickupTime,
                        fare = _booking.Fare,
                        eta = _booking.Eta,
                        distance_miles = _booking.DistanceMiles,
                        booking_ref = _booking.BookingRef
                    },
                    timestamp = DateTime.UtcNow.ToString("o")
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                content.Headers.Add("Authorization", "Bearer sriifvfedn5ktsbw4for7noulxtapb2ff6wf326v");

                Log($"📤 Sending dispatch to BSQD...");
                var response = await HttpClient.PostAsync(webhookUrl, content);
                
                if (response.IsSuccessStatusCode)
                    Log($"✅ Dispatch sent to BSQD");
                else
                    Log($"⚠️ Dispatch failed: HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Dispatch error: {ex.Message}");
            }
        }
    }

    private async Task SendWhatsAppNotificationAsync(string? phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber))
        {
            Log("⚠️ WhatsApp notification skipped - no phone number");
            return;
        }

        try
        {
            var cli = FormatPhoneForWhatsApp(phoneNumber);
            var webhookUrl = $"https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/Avaya?api_key=sriifvfedn5ktsbw4for7noulxtapb2ff6wf326v&phoneNumber={cli}";
            
            Log($"📱 Sending WhatsApp notification to {cli}...");
            
            var response = await HttpClient.GetAsync(webhookUrl);
            
            if (response.IsSuccessStatusCode)
                Log($"✅ WhatsApp notification sent to {cli}");
            else
                Log($"⚠️ WhatsApp notification failed: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log($"⚠️ WhatsApp notification error: {ex.Message}");
        }
    }

    private static string FormatPhoneForWhatsApp(string phone)
    {
        var clean = phone.Replace(" ", "").Replace("-", "");
        
        if (clean.StartsWith("00"))
            clean = "+" + clean.Substring(2);
        
        clean = clean.TrimStart('+');
        
        if (clean.StartsWith("310"))
            clean = "31" + clean.Substring(3);
        
        return new string(clean.Where(char.IsDigit).ToArray());
    }

    // ===========================================
    // PRIVATE - UTILITIES
    // ===========================================
    
    private static string DetectLanguageFromPhone(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";

        var norm = phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");

        // Dutch mobile: 06xxxxxxxx
        if (norm.StartsWith("06") && norm.Length == 10 && norm.All(char.IsDigit)) return "nl";
        if (norm.StartsWith("0") && !norm.StartsWith("00") && norm.Length == 10) return "nl";

        // Strip + or 00 prefix
        if (norm.StartsWith("+")) norm = norm.Substring(1);
        else if (norm.StartsWith("00") && norm.Length > 4) norm = norm.Substring(2);

        // Check country code
        if (norm.Length >= 2)
        {
            var code = norm.Substring(0, 2);
            if (CountryCodeToLanguage.TryGetValue(code, out var lang)) return lang;
        }

        return "en";
    }

    private string GetLocalizedGreeting()
    {
        return LocalizedGreetings.TryGetValue(_detectedLanguage, out var g) ? g : LocalizedGreetings["en"];
    }

    private static string GetLanguageName(string code) => code switch
    {
        "nl" => "Dutch",
        "fr" => "French",
        "de" => "German",
        _ => "English"
    };

    private static string ApplySttCorrections(string transcript)
    {
        if (string.IsNullOrEmpty(transcript)) return transcript;
        var corrected = transcript.Trim();
        
        if (SttCorrections.TryGetValue(corrected, out var exact))
            return exact;

        foreach (var (pattern, replacement) in SttCorrections)
        {
            if (corrected.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                corrected = corrected.Replace(pattern, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return corrected;
    }

    private void Log(string msg)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var formatted = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        Console.WriteLine($"[OpenAI] {formatted}");
        OnLog?.Invoke(formatted);
    }

    private void MaybeStartPostBookingSilenceHangup()
    {
        if (!_booking.Confirmed || !_postBookingHangupArmed || !_postBookingFinalSpeechDelivered) return;
        if (_postBookingHangupCts != null && !_postBookingHangupCts.IsCancellationRequested) return;

        _postBookingHangupCts = new CancellationTokenSource();
        var token = _postBookingHangupCts.Token;

        Log($"⏱️ Starting silence hangup watchdog ({POST_BOOKING_SILENCE_HANGUP_MS}ms)");

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && Volatile.Read(ref _disposed) == 0 && IsConnected)
                {
                    var now = NowMs();
                    var lastActivity = Math.Max(Volatile.Read(ref _lastAdaFinishedAt), Volatile.Read(ref _lastUserSpeechAt));

                    if (now - lastActivity >= POST_BOOKING_SILENCE_HANGUP_MS)
                    {
                        SignalCallEnded("silence_timeout");
                        return;
                    }

                    await Task.Delay(POST_BOOKING_SILENCE_POLL_MS, token);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private string GetLocalizedSystemPrompt()
    {
        var lang = GetLanguageName(_detectedLanguage);
        return $@"You are Ada, a professional taxi booking assistant. Speak in {lang}.

# BOOKING FLOW
1. Greet → Ask for PICKUP address
2. Ask for DESTINATION
3. Ask for NUMBER OF PASSENGERS
4. Ask for PICKUP TIME
5. Summarize → call book_taxi(action='request_quote')
6. Tell fare/ETA → Ask for confirmation
7. On 'yes' → book_taxi(action='confirmed') → Thank user → end_call

# TOOLS
- sync_booking_data: Save each piece of info as you collect it
- book_taxi: Request quote or confirm booking
- end_call: Hang up after goodbye

# RULES
- ONE question at a time
- Be brief (under 20 words per response)
- Currency: British pounds (£)
- Always end call after booking confirmation
- If user says something unclear, ask for clarification
- If user gives multiple pieces of info at once, acknowledge all and move forward";
    }

    private static string GetDefaultSystemPrompt() => @"You are Ada, a taxi booking assistant.

# BOOKING FLOW
1. Greet → Ask for pickup
2. Ask for destination
3. Ask for passengers
4. Ask for time
5. Get quote with book_taxi(action='request_quote')
6. Confirm with user
7. On yes → book_taxi(action='confirmed') → end_call

# RULES
- ONE question at a time
- Be brief
- Currency: £ (GBP)";

    private static object[] GetTools() => new object[]
    {
        new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = "sync_booking_data",
            ["description"] = "Save booking info as you collect it. Call this after each piece of information is provided.",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["pickup"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Pickup address" },
                    ["destination"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Destination address" },
                    ["passengers"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of passengers" },
                    ["pickup_time"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Pickup time (e.g., 'now', '3pm', '15:00')" },
                    ["last_question_asked"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "pickup", "destination", "passengers", "time", "confirmation", "none" }
                    }
                }
            }
        },
        new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = "book_taxi",
            ["description"] = "Request a fare quote or confirm the booking. Use request_quote after collecting all info, use confirmed after user says yes.",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["action"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "request_quote", "confirmed" },
                        ["description"] = "request_quote to get fare, confirmed to finalize booking"
                    }
                },
                ["required"] = new[] { "action" }
            }
        },
        new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = "end_call",
            ["description"] = "End the call. Use this after saying goodbye.",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>()
            }
        }
    };
}

/// <summary>
/// Booking state tracking with full geocoding support.
/// </summary>
public class BookingState
{
    // Core booking fields
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int? Passengers { get; set; }
    public string? PickupTime { get; set; }
    public string? Fare { get; set; }
    public string? Eta { get; set; }
    public bool Confirmed { get; set; }
    public string? BookingRef { get; set; }
    
    // Pickup geocoding
    public double? PickupLat { get; set; }
    public double? PickupLon { get; set; }
    public string? PickupStreet { get; set; }
    public string? PickupNumber { get; set; }
    public string? PickupCity { get; set; }
    public string? PickupPostal { get; set; }
    public string? PickupPostalCode { get; set; }
    public string? PickupFormatted { get; set; }
    
    // Destination geocoding
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }
    public string? DestStreet { get; set; }
    public string? DestNumber { get; set; }
    public string? DestCity { get; set; }
    public string? DestPostal { get; set; }
    public string? DestPostalCode { get; set; }
    public string? DestFormatted { get; set; }
    
    // Additional fields
    public string? CallerName { get; set; }
    public string? SpecialRequests { get; set; }
    public int? Luggage { get; set; }
    public double? DistanceMiles { get; set; }
}
