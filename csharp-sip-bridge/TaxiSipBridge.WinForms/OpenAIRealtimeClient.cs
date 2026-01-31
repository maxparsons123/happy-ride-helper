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
                    { "52 I ain't dead bro", "52A David Road" },
                    { "52 I ain't David", "52A David Road" },
                    { "52 ain't David", "52A David Road" },
                    { "52 a David", "52A David Road" },
                    { "for now", "now" },
                    { "right now", "now" },
                    { "as soon as possible", "now" },
                    { "ASAP", "now" },
                    { "yeah please", "yes please" },
                    { "yep", "yes" },
                    { "yup", "yes" },
                    { "yeah", "yes" },
                    { "that's right", "yes" },
                    { "correct", "yes" },
                    { "go ahead", "yes" },
                    { "book it", "yes" },
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
    private volatile bool _disposed = false;

    // Audio queue for RTP transmission
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 500;

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
    private bool _greetingSent = false;
    private bool _sessionConfigured = false;
    private bool _responseActive = false;

    // Echo guard
    private const int ECHO_GUARD_MS = 300;
    private long _lastAdaFinishedAt = 0;
    private bool _awaitingConfirmation = false;

    // Post-booking hangup
    private const int POST_BOOKING_SILENCE_HANGUP_MS = 10000;
    private const int POST_BOOKING_SILENCE_POLL_MS = 250;
    private CancellationTokenSource? _postBookingHangupCts;
    private long _lastUserSpeechAt = 0;
    private bool _postBookingHangupArmed = false;
    private bool _postBookingFinalSpeechDelivered = false;
    private int _callEndSignaled = 0;

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
    public event Action? OnResponseCompleted;
    public event Action<byte[]>? OnCallerAudioMonitor;
    public event Action<string, Dictionary<string, object>>? OnToolCall;
    public event Action<BookingState>? OnBookingUpdated;
    public event Action? OnCallEnded;

    // ===========================================
    // PROPERTIES
    // ===========================================
    
    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public int PendingFrameCount => _outboundQueue.Count;
    public OutputCodecMode OutputCodec => _outputCodec;
    public string DetectedLanguage => _detectedLanguage;

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
        if (_disposed) throw new ObjectDisposedException(nameof(OpenAIRealtimeClient));

        // Reset state for new call
        _callerId = caller;
        _callId = $"local-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        _detectedLanguage = DetectLanguageFromPhone(caller);
        _greetingSent = false;
        _sessionConfigured = false;
        _booking = new BookingState();
        _lastQuestionAsked = "pickup";
        _audioPacketsSent = 0;
        _inputBufferedMs = 0;
        _awaitingConfirmation = false;
        _postBookingHangupArmed = false;
        _postBookingFinalSpeechDelivered = false;
        _callEndSignaled = 0;
        
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
        if (_disposed) return;

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
        if (!IsConnected || _disposed) return;

        // Echo guard - skip audio right after Ada finishes (unless awaiting confirmation)
        if (!_awaitingConfirmation && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastAdaFinishedAt < ECHO_GUARD_MS)
            return;

        var pcm8k = AudioCodecs.MuLawDecode(ulawData);
        var pcm24k = AudioProcessor.PrepareForOpenAI(pcm8k, 8000, 24000);
        
        _inputBufferedMs += ulawData.Length * 1000.0 / 8000.0;

        await SendAudioBufferAsync(pcm24k);
    }

    public async Task SendPcm8kAsync(byte[] pcm8kBytes)
    {
        if (!IsConnected || _disposed) return;

        if (!_awaitingConfirmation && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastAdaFinishedAt < ECHO_GUARD_MS)
            return;

        var pcm24k = AudioProcessor.PrepareForOpenAI(pcm8kBytes, 8000, 24000);
        _inputBufferedMs += (pcm8kBytes.Length / 2) * 1000.0 / 8000.0;

        await SendAudioBufferAsync(pcm24k);
    }

    public async Task SendPcm8kNoDspAsync(byte[] pcm8kBytes)
    {
        if (!IsConnected || _disposed) return;

        if (!_awaitingConfirmation && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastAdaFinishedAt < ECHO_GUARD_MS)
            return;

        var pcm24k = AudioProcessor.PrepareForOpenAI(pcm8kBytes, 8000, 24000);
        _inputBufferedMs += (pcm8kBytes.Length / 2) * 1000.0 / 8000.0;

        await SendAudioBufferAsync(pcm24k);
    }

    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (!IsConnected || _disposed) return;

        if (!_awaitingConfirmation && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastAdaFinishedAt < ECHO_GUARD_MS)
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
        if (_disposed) return;
        _disposed = true;

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

        while (!_disposed && _ws?.State == WebSocketState.Open && !(_cts?.IsCancellationRequested ?? true))
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
    // PRIVATE - MESSAGE PROCESSING
    // ===========================================
    
    private async Task ProcessMessageAsync(string json)
    {
        if (_disposed) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;

            var type = typeEl.GetString();

            // Log important events (not audio deltas - too noisy)
            if (type != "response.audio.delta" && type != "rate_limits.updated")
            {
                var preview = json.Length > 200 ? json.Substring(0, 200) + "..." : json;
                Log($"📥 {type}");
            }

            switch (type)
            {
                case "session.created":
                    Log("✅ Session created - sending configuration...");
                    await ConfigureSessionAsync();
                    break;

                case "session.updated":
                    Log("✅ Session configured - sending greeting...");
                    _sessionConfigured = true;
                    await SendGreetingAsync();
                    break;

                case "response.created":
                    _responseActive = true;
                    OnResponseStarted?.Invoke();
                    break;

                case "response.audio.delta":
                    await HandleAudioDeltaAsync(doc.RootElement);
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var textDelta))
                    {
                        var text = textDelta.GetString();
                        if (!string.IsNullOrEmpty(text))
                            OnAdaSpeaking?.Invoke(text);
                    }
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var fullText))
                    {
                        var transcript = fullText.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                        {
                            Log($"💬 Ada: {transcript}");
                            OnTranscript?.Invoke($"Ada: {transcript}");
                            
                            if (_booking.Confirmed && _postBookingHangupArmed)
                                _postBookingFinalSpeechDelivered = true;
                        }
                    }
                    break;

                case "input_audio_buffer.speech_started":
                    Log("🎤 User speaking...");
                    _lastUserSpeechAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    break;

                case "input_audio_buffer.speech_stopped":
                    Log("🎤 User stopped speaking");
                    _lastUserSpeechAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                    {
                        var raw = userText.GetString();
                        if (!string.IsNullOrEmpty(raw))
                        {
                            var corrected = ApplySttCorrections(raw);
                            Log($"👤 User: {corrected}");
                            OnTranscript?.Invoke($"You: {corrected}");
                        }
                    }
                    break;

                case "response.done":
                    _responseActive = false;
                    _lastAdaFinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _inputBufferedMs = 0;
                    Log("✅ Response complete");
                    OnResponseCompleted?.Invoke();
                    MaybeStartPostBookingSilenceHangup();
                    break;

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement);
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown";
                        if (msg?.Contains("buffer too small") != true)
                            Log($"❌ OpenAI error: {msg}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Parse error: {ex.Message}");
        }
    }

    private async Task HandleAudioDeltaAsync(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var deltaEl)) return;
        var base64 = deltaEl.GetString();
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            var pcm24k = Convert.FromBase64String(base64);
            OnPcm24Audio?.Invoke(pcm24k);

            // Convert to output codec and queue for RTP
            var samples24k = AudioCodecs.BytesToShorts(pcm24k);

            switch (_outputCodec)
            {
                case OutputCodecMode.Opus:
                    ProcessOpusOutput(samples24k);
                    break;
                case OutputCodecMode.ALaw:
                    ProcessALawOutput(samples24k);
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

    private void ProcessALawOutput(short[] pcm24k)
    {
        // Decimate 24kHz → 8kHz (3:1)
        int outputLen = pcm24k.Length / 3;
        var pcm8k = new short[outputLen];
        for (int i = 0; i < outputLen; i++)
            pcm8k[i] = pcm24k[i * 3];

        var alaw = AudioCodecs.ALawEncode(pcm8k);

        // Split into 20ms frames (160 bytes @ 8kHz)
        for (int i = 0; i < alaw.Length; i += 160)
        {
            int len = Math.Min(160, alaw.Length - i);
            var frame = new byte[160];
            Buffer.BlockCopy(alaw, i, frame, 0, len);
            if (len < 160) Array.Fill(frame, (byte)0xD5, len, 160 - len);
            EnqueueFrame(frame);
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
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = GetLocalizedSystemPrompt(),
                voice = _voice,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.4,
                    prefix_padding_ms = 400,
                    silence_duration_ms = 1000
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };

        Log($"🎧 VAD: threshold=0.4, prefix=400ms, silence=1000ms");
        await SendJsonAsync(config);
    }

    private async Task SendGreetingAsync()
    {
        if (_greetingSent) return;
        _greetingSent = true;

        // Small delay for stability
        await Task.Delay(200);
        
        // Guard: ensure no active response
        if (_responseActive)
        {
            Log("⏳ Skipping greeting — response already in progress");
            return;
        }

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
                            JsonValueKind.Number => prop.Value.GetInt32(),
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
                var action = args.TryGetValue("action", out var a) ? a.ToString() : "unknown";
                
                if (action == "request_quote")
                {
                    var (fare, eta, dist) = await FareCalculator.CalculateFareAsync(_booking.Pickup, _booking.Destination);
                    _booking.Fare = fare;
                    _booking.Eta = eta;
                    OnBookingUpdated?.Invoke(_booking);
                    
                    Log($"💰 Quote: {fare} ({dist:F1} miles)");
                    
                    await SendToolResultAsync(callId, new
                    {
                        success = true,
                        fare,
                        eta,
                        distance_miles = Math.Round(dist, 1),
                        message = $"Your fare is {fare} and your driver will arrive in {eta}. Would you like me to book that?"
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
                    
                    // Inject instruction to end call (with response guard)
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        
                        // Wait for any active response to complete
                        var waitCount = 0;
                        while (_responseActive && waitCount < 20) // Max 2 seconds wait
                        {
                            await Task.Delay(100);
                            waitCount++;
                        }
                        
                        if (_responseActive)
                        {
                            Log("⏳ Post-booking hangup prompt skipped — AI still responding");
                            return;
                        }
                        
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
                    });
                }
                break;

            case "end_call":
                Log("📞 End call requested");
                await SendToolResultAsync(callId, new { success = true });
                TriggerCallEnded("end_call tool");
                break;

            default:
                await SendToolResultAsync(callId, new { error = $"Unknown tool: {toolName}" });
                break;
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
        
        // Guard: only create response if no response is currently active
        if (_responseActive)
        {
            Log("⏳ Skipping response.create — AI response already in progress");
            return;
        }
        
        await SendJsonAsync(new { type = "response.create" });
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

    /// <summary>
    /// Send WhatsApp booking notification via BSQD webhook.
    /// Uses POST with Bearer token and JSON body.
    /// Fire-and-forget - errors are logged but don't affect booking.
    /// </summary>
    private async Task SendWhatsAppNotificationAsync(string? phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber))
        {
            Log("⚠️ WhatsApp notification skipped - no phone number");
            return;
        }

        try
        {
            var formattedPhone = FormatPhoneForWhatsApp(phoneNumber);
            var webhookUrl = "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/Avaya";
            
            Log($"📱 Sending WhatsApp notification to {formattedPhone}...");
            
            // Create request with Bearer auth
            var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", "sriifvfedn5ktsbw4for7noulxtapb2ff6wf326v");
            
            // JSON payload with phoneNumber
            var payload = new { phoneNumber = formattedPhone };
            request.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");
            
            var response = await HttpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
                Log($"✅ WhatsApp notification sent to {formattedPhone}");
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Log($"⚠️ WhatsApp notification failed: HTTP {(int)response.StatusCode} - {errorBody}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ WhatsApp notification error: {ex.Message}");
        }
    }

    /// <summary>
    /// Format phone number for WhatsApp with 00 international prefix:
    /// - Normalize to 00 prefix format (e.g., 0044, 0031)
    /// - For Dutch, remove leading 0 after country code (e.g., 00310652 → 0031652)
    /// - Strip all non-numeric characters
    /// </summary>
    private static string FormatPhoneForWhatsApp(string phone)
    {
        var clean = phone.Replace(" ", "").Replace("-", "");
        
        // Strip non-digits first
        clean = new string(clean.Where(c => char.IsDigit(c) || c == '+').ToArray());
        
        // Convert + prefix to 00 for international format
        if (clean.StartsWith("+"))
            clean = "00" + clean.Substring(1);
        
        // If it doesn't start with 00, assume it needs country code
        // Dutch local numbers starting with 06 → 00316
        if (!clean.StartsWith("00"))
        {
            if (clean.StartsWith("06") || clean.StartsWith("0"))
                clean = "0031" + clean.Substring(1); // Dutch local → international
            else
                clean = "00" + clean; // Assume already has country code without prefix
        }
        
        // For Dutch numbers (0031), remove leading 0 after country code
        // e.g., 00310652... → 0031652...
        if (clean.StartsWith("00310"))
            clean = "0031" + clean.Substring(5);
        
        // Final cleanup - digits only
        return new string(clean.Where(char.IsDigit).ToArray());
    }

    private void Log(string msg)
    {
        if (_disposed) return;
        var formatted = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        Console.WriteLine($"[OpenAI] {formatted}");
        OnLog?.Invoke(formatted);
    }

    private void TriggerCallEnded(string reason)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _callEndSignaled, 1) == 1) return;

        Log($"📴 Call ended: {reason}");
        _postBookingHangupCts?.Cancel();
        OnCallEnded?.Invoke();
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
                while (!token.IsCancellationRequested && !_disposed && IsConnected)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var lastActivity = Math.Max(_lastAdaFinishedAt, _lastUserSpeechAt);

                    if (now - lastActivity >= POST_BOOKING_SILENCE_HANGUP_MS)
                    {
                        TriggerCallEnded("silence_timeout");
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
1. Greet warmly → Ask for their NAME
2. Ask for PICKUP address
3. Ask for DESTINATION
4. Ask for NUMBER OF PASSENGERS
5. Ask for PICKUP TIME
6. Summarize → call book_taxi(action='request_quote')
7. Tell fare/ETA → Ask for confirmation
8. On 'yes' → book_taxi(action='confirmed') → Thank user by name → end_call

# TOOLS
- sync_booking_data: Save each piece of info (including name)
- book_taxi: Request quote or confirm
- end_call: Hang up after goodbye

# RULES
- ONE question at a time
- Be brief (under 20 words)
- Currency: British pounds (£)
- Use the caller's name SPARINGLY - only at greeting, final confirmation, and goodbye
- Do NOT repeat their name after every response
- Always end call after booking confirmation";

    private static string GetDefaultSystemPrompt() => @"You are Ada, a taxi booking assistant.

# BOOKING FLOW
1. Greet warmly → Ask for their NAME
2. Ask for PICKUP address
3. Ask for DESTINATION
4. Ask for NUMBER OF PASSENGERS
5. Ask for PICKUP TIME
6. Get quote with book_taxi(action='request_quote')
7. Confirm with user
8. On yes → book_taxi(action='confirmed') → Thank user by name → end_call

# RULES
- ONE question at a time
- Be brief
- Currency: £ (GBP)
- Use the caller's name SPARINGLY - only at greeting, final confirmation, and goodbye
- Do NOT repeat their name after every response";

    private static object[] GetTools() => new object[]
    {
        new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = "sync_booking_data",
            ["description"] = "Save booking info as you collect it",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["caller_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The caller's name" },
                    ["pickup"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["destination"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["passengers"] = new Dictionary<string, object> { ["type"] = "integer" },
                    ["pickup_time"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["last_question_asked"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "name", "pickup", "destination", "passengers", "time", "confirmation", "none" }
                    }
                }
            }
        },
        new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = "book_taxi",
            ["description"] = "Request quote or confirm booking",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["action"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "request_quote", "confirmed" }
                    }
                },
                ["required"] = new[] { "action" }
            }
        },
        new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = "end_call",
            ["description"] = "End the call after goodbye",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>()
            }
        }
    };
}

/// <summary>
/// Booking state tracking.
/// </summary>
public class BookingState
{
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int? Passengers { get; set; }
    public string? PickupTime { get; set; }
    public string? Fare { get; set; }
    public string? Eta { get; set; }
    public bool Confirmed { get; set; }
    public string? BookingRef { get; set; }
}
