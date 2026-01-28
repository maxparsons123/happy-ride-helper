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
/// Direct OpenAI Realtime API client - FULLY LOCALIZED.
/// No edge function required - connects directly to OpenAI with dispatch webhook support.
/// </summary>
public class OpenAIRealtimeClient : IAudioAIClient
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _systemPrompt;
    private readonly string? _dispatchWebhookUrl;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed = false;

    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 500;

    private bool _needsFadeIn = true;
    private const int FadeInSamples = 48;

    // Pre-emphasis for telephony clarity (matches edge function)
    private const float PRE_EMPHASIS_COEFF = 0.97f;
    private short _lastSample = 0;
    private int _audioPacketsSent = 0;

    // Output codec mode - set by SipOpenAIBridge based on negotiated codec
    private OutputCodecMode _outputCodec = OutputCodecMode.MuLaw;
    private short[] _opusResampleBuffer = Array.Empty<short>();  // Buffer for 24kHz→48kHz upsampling

    // Session state
    private string? _callerId;
    private string _callId;
    private string _lastQuestionAsked = "pickup";
    private BookingState _booking = new();
    private bool _greetingSent = false;
    private bool _waitingForQuote = false;
    private bool _responseActive = false;

    // Retry logic for transient OpenAI errors
    private int _greetingRetryCount = 0;
    private const int MAX_GREETING_RETRIES = 3;
    private bool _pendingGreetingRetry = false;

    // Echo guard timing - REDUCED for confirmation responsiveness
    // 300ms is enough to filter Ada's echo but not block quick "Yes!" responses
    private const int ECHO_GUARD_MS = 300;
    private long _lastAdaFinishedAt = 0;

    // Confirmation awareness - disable echo guard when waiting for yes/no
    private bool _awaitingConfirmation = false;

    // Audio buffer tracking - prevent "buffer too small" errors on commit
    // OpenAI requires at least 100ms of audio before commit
    private double _inputBufferedMs = 0;
    private const double MIN_COMMIT_MS = 120; // Safety margin above 100ms requirement

    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnAdaSpeaking;
    public event Action<byte[]>? OnPcm24Audio;
    public event Action? OnResponseStarted;
    public event Action<byte[]>? OnCallerAudioMonitor;

    // Tool call events for external handling
    public event Action<string, Dictionary<string, object>>? OnToolCall;
    public event Action<BookingState>? OnBookingUpdated;
    public event Action? OnCallEnded;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public int PendingFrameCount => _outboundQueue.Count;
    public OutputCodecMode OutputCodec => _outputCodec;

    /// <summary>
    /// Set the output codec mode for AI audio.
    /// Call this after codec negotiation to enable Opus output.
    /// </summary>
    public void SetOutputCodec(OutputCodecMode codec)
    {
        _outputCodec = codec;
        Log($"🎵 Output codec set to: {codec}");
    }

    /// <summary>
    /// Create a direct OpenAI Realtime client (FULLY LOCALIZED - no edge function).
    /// </summary>
    /// <param name="apiKey">OpenAI API key</param>
    /// <param name="model">Model name (default: gpt-4o-mini-realtime-preview-2024-12-17)</param>
    /// <param name="voice">Voice name (default: shimmer)</param>
    /// <param name="systemPrompt">Custom system prompt (null for default taxi booking prompt)</param>
    /// <param name="dispatchWebhookUrl">Webhook URL for dispatch integration (required for real bookings)</param>
    public OpenAIRealtimeClient(
        string apiKey,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "shimmer",
        string? systemPrompt = null,
        string? dispatchWebhookUrl = null)
    {
        _apiKey = apiKey;
        _model = model;
        _voice = voice;
        _systemPrompt = systemPrompt ?? GetDefaultSystemPrompt();
        _dispatchWebhookUrl = dispatchWebhookUrl;
        _callId = $"local-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenAIRealtimeClient));

        _callerId = caller;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _greetingSent = false;
        _booking = new BookingState();
        _lastQuestionAsked = "pickup";

        _ws = new ClientWebSocket();

        // OpenAI Realtime API requires specific subprotocols
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        // Resolve api.openai.com to IP address for reliable connection
        const string openAiHost = "api.openai.com";
        string resolvedIp = openAiHost;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(openAiHost);
            if (addresses.Length > 0)
            {
                // Prefer IPv4 for compatibility
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                resolvedIp = (ipv4 ?? addresses[0]).ToString();
                Log($"📡 Resolved {openAiHost} → {resolvedIp}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ DNS resolution failed, using hostname: {ex.Message}");
        }

        // For WebSocket with TLS, we must use the original hostname for SNI
        // The DNS resolution is for logging/debugging - ClientWebSocket handles the rest
        var uri = new Uri($"wss://{openAiHost}/v1/realtime?model={_model}");

        Log($"🔌 Connecting to OpenAI Realtime: {_model} (IP: {resolvedIp})");

        try
        {
            await _ws.ConnectAsync(uri, _cts.Token);
            Log("✅ Connected to OpenAI Realtime API");
            OnConnected?.Invoke();
            _ = ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to connect: {ex.Message}");
            throw;
        }
    }

    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (_disposed || _ws?.State != WebSocketState.Open) return;
        if (_cts?.Token.IsCancellationRequested == true) return;

        // Echo guard - ignore audio right after Ada finishes speaking
        // BUT: Skip guard if awaiting confirmation (user saying "yes" is critical!)
        if (!_awaitingConfirmation)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastAdaFinishedAt < ECHO_GUARD_MS)
                return;
        }

        // Decode µ-law → PCM16 @ 8kHz
        var pcm8k = AudioCodecs.MuLawDecode(ulawData);

        // Apply pre-emphasis for consonant clarity (matches edge function)
        for (int i = 0; i < pcm8k.Length; i++)
        {
            short current = pcm8k[i];
            pcm8k[i] = (short)Math.Clamp(current - (int)(PRE_EMPHASIS_COEFF * _lastSample), short.MinValue, short.MaxValue);
            _lastSample = current;
        }

        // Volume boost for telephony - INCREASED to 2.5x for better VAD detection
        // Telephony audio is often very quiet compared to desktop mics
        for (int i = 0; i < pcm8k.Length; i++)
            pcm8k[i] = (short)Math.Clamp(pcm8k[i] * 2.5, short.MinValue, short.MaxValue);

        // Resample 8kHz → 24kHz
        var pcm24k = AudioCodecs.Resample(pcm8k, 8000, 24000);
        var pcmBytes = AudioCodecs.ShortsToBytes(pcm24k);

        // Track buffered audio duration: G.711 µ-law @ 8kHz = 1 byte/sample
        // durationMs = (bytes / 8000) * 1000
        var durationMs = (double)ulawData.Length * 1000.0 / 8000.0;
        _inputBufferedMs += durationMs;

        // Send as JSON text (matches edge function expectation)
        // Edge function expects: { type: "input_audio_buffer.append", audio: "base64..." }
        var base64 = Convert.ToBase64String(pcmBytes);
        var msg = JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = base64 });

        try
        {
            _audioPacketsSent++;
            if (_audioPacketsSent <= 3 || _audioPacketsSent % 100 == 0)
                Log($"📤 Sent audio #{_audioPacketsSent}: {ulawData.Length}b ulaw → {pcmBytes.Length}b PCM24, {base64.Length} chars base64");

            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log($"⚠️ WS send error: {ex.Message}"); }
    }

    /// <summary>
    /// Send PCM16 audio at 8kHz (already decoded from µ-law via NAudio).
    /// This matches the SIPSorcery example pattern using NAudio.Codecs.MuLawDecoder.
    /// </summary>
    public async Task SendPcm8kAsync(byte[] pcm8kBytes)
    {
        if (_disposed || _ws?.State != WebSocketState.Open) return;
        if (_cts?.Token.IsCancellationRequested == true) return;

        // Echo guard
        if (!_awaitingConfirmation)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastAdaFinishedAt < ECHO_GUARD_MS)
                return;
        }

        // Convert bytes to shorts
        var pcm8k = AudioCodecs.BytesToShorts(pcm8kBytes);

        // PASSTHROUGH MODE: No DSP - just simple point upsampling 8kHz → 24kHz
        // Pick each sample 3x (no filtering, no boost, no pre-emphasis)
        var pcm24k = new short[pcm8k.Length * 3];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            pcm24k[i * 3] = pcm8k[i];
            pcm24k[i * 3 + 1] = pcm8k[i];
            pcm24k[i * 3 + 2] = pcm8k[i];
        }
        var pcmBytes = AudioCodecs.ShortsToBytes(pcm24k);

        // Track buffered audio duration: PCM16 @ 8kHz = 2 bytes/sample
        var sampleCount = pcm8kBytes.Length / 2;
        var durationMs = (double)sampleCount * 1000.0 / 8000.0;
        _inputBufferedMs += durationMs;

        var base64 = Convert.ToBase64String(pcmBytes);
        var msg = JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = base64 });

        try
        {
            _audioPacketsSent++;
            if (_audioPacketsSent <= 3 || _audioPacketsSent % 100 == 0)
                Log($"📤 Sent PCM8k #{_audioPacketsSent}: {pcm8kBytes.Length}b → {pcmBytes.Length}b PCM24");

            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log($"⚠️ WS send error: {ex.Message}"); }
    }

    /// <summary>
    /// Send PCM16 audio at 8kHz with minimal DSP (for A-law path).
    /// Only applies volume boost, no pre-emphasis or noise gate.
    /// </summary>
    public async Task SendPcm8kNoDspAsync(byte[] pcm8kBytes)
    {
        if (_disposed || _ws?.State != WebSocketState.Open) return;
        if (_cts?.Token.IsCancellationRequested == true) return;

        // Echo guard
        if (!_awaitingConfirmation)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastAdaFinishedAt < ECHO_GUARD_MS)
                return;
        }

        // Convert bytes to shorts
        var pcm8k = AudioCodecs.BytesToShorts(pcm8kBytes);

        // Apply 2.5x volume boost only (no pre-emphasis, no noise gate)
        for (int i = 0; i < pcm8k.Length; i++)
            pcm8k[i] = (short)Math.Clamp(pcm8k[i] * 2.5, short.MinValue, short.MaxValue);

        // Simple point upsampling 8kHz → 24kHz
        var pcm24k = new short[pcm8k.Length * 3];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            pcm24k[i * 3] = pcm8k[i];
            pcm24k[i * 3 + 1] = pcm8k[i];
            pcm24k[i * 3 + 2] = pcm8k[i];
        }
        var pcmBytes = AudioCodecs.ShortsToBytes(pcm24k);

        // Track buffered audio duration
        var sampleCount = pcm8kBytes.Length / 2;
        var durationMs = (double)sampleCount * 1000.0 / 8000.0;
        _inputBufferedMs += durationMs;

        var base64 = Convert.ToBase64String(pcmBytes);
        var msg = JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = base64 });

        try
        {
            _audioPacketsSent++;
            if (_audioPacketsSent <= 3 || _audioPacketsSent % 100 == 0)
                Log($"📤 Sent PCM8k (A-law) #{_audioPacketsSent}: {pcm8kBytes.Length}b → {pcmBytes.Length}b PCM24");

            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log($"⚠️ WS send error: {ex.Message}"); }
    }

    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (_disposed || _ws?.State != WebSocketState.Open) return;
        if (_cts?.Token.IsCancellationRequested == true) return;

        // Echo guard for HD audio path (same logic as µ-law)
        if (!_awaitingConfirmation)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastAdaFinishedAt < ECHO_GUARD_MS)
                return;
        }

        byte[] audioToSend;

        if (sampleRate != 24000)
        {
            var samples = AudioCodecs.BytesToShorts(pcmData);
            var resampled = AudioCodecs.Resample(samples, sampleRate, 24000);
            audioToSend = AudioCodecs.ShortsToBytes(resampled);
        }
        else
        {
            audioToSend = pcmData;
        }

        // Track buffered duration for PCM24 path (covers browser/WebRTC scenarios)
        var sampleCount = audioToSend.Length / 2; // 2 bytes per sample
        var durationMs = (double)sampleCount * 1000.0 / 24000.0;
        _inputBufferedMs += durationMs;

        var base64 = Convert.ToBase64String(audioToSend);
        var msg = JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = base64 });

        try
        {
            _audioPacketsSent++;
            if (_audioPacketsSent <= 3 || _audioPacketsSent % 100 == 0)
                Log($"📤 Sent PCM audio #{_audioPacketsSent}: {pcmData.Length}b → {base64.Length} chars base64");

            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public byte[]? GetNextMuLawFrame()
    {
        if (_outboundQueue.TryDequeue(out var frame))
            return frame;
        return null;
    }

    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1024 * 64];
        Log("🔄 Receive loop started");

        while (!_disposed && _ws?.State == WebSocketState.Open && !(_cts?.Token.IsCancellationRequested ?? true))
        {
            try
            {
                var result = await _ws.ReceiveAsync(buffer, _cts!.Token);
                Log($"📨 Received {result.Count} bytes, type={result.MessageType}");

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log("🔌 WebSocket closed by OpenAI");
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await ProcessMessageAsync(json);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                Log($"❌ WebSocket error: {ex}");
                break;
            }
            catch (Exception ex)
            {
                Log($"❌ Receive loop error: {ex}");
                break;
            }
        }

        Log("🔌 Receive loop ended");
        OnDisconnected?.Invoke();
    }

    private async Task ProcessMessageAsync(string json)
    {
        if (_disposed) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                return;

            var type = typeEl.GetString();

            // Verbose logging for debugging - show all response events
            if (type?.StartsWith("response.") == true && type != "response.audio.delta")
            {
                Log($"📋 {type}: {json.Substring(0, Math.Min(500, json.Length))}");
            }

            switch (type)
            {
                case "session.created":
                    Log("📋 Session created - configuring...");
                    await ConfigureSessionAsync();
                    break;

                case "session.updated":
                    Log("✅ Session configured - sending greeting");
                    await SendGreetingAsync();
                    break;

                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var delta))
                    {
                        var base64 = delta.GetString();
                        if (!string.IsNullOrEmpty(base64))
                        {
                            var pcmBytes = Convert.FromBase64String(base64);
                            Log($"🔊 Audio delta: {pcmBytes.Length} bytes PCM");
                            OnPcm24Audio?.Invoke(pcmBytes);
                            ProcessAdaAudio(pcmBytes);
                        }
                    }
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var textDelta))
                    {
                        var transcript = textDelta.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                            OnAdaSpeaking?.Invoke(transcript);
                    }
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var fullText))
                    {
                        var transcript = fullText.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                            OnTranscript?.Invoke($"Ada: {transcript}");
                    }
                    break;

                case "input_audio_buffer.speech_started":
                    Log("🎤 User speaking...");
                    break;

                case "input_audio_buffer.speech_stopped":
                    // VAD-driven commit - this is the proper way to commit for telephony
                    Log($"🎤 VAD stopped — attempting commit ({_inputBufferedMs:F1}ms)");
                    await CommitInputAudioIfReadyAsync();
                    break;

                case "response.created":
                    Log("🤖 Response started");
                    _needsFadeIn = true;
                    _responseActive = true;
                    OnResponseStarted?.Invoke();
                    break;

                case "response.done":
                    Log("✅ Response done");
                    _responseActive = false;
                    _lastAdaFinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _inputBufferedMs = 0; // Reset buffer tracking after response

                    // DO NOT commit after greeting — SIP callers wait silently.
                    // Wait for VAD speech_stopped instead.
                    if (_greetingSent && !_awaitingConfirmation)
                    {
                        Log("⏳ Waiting for VAD/user speech — not committing turn after greeting");
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                    {
                        var transcript = userText.GetString();
                        if (!string.IsNullOrEmpty(transcript))
                        {
                            OnTranscript?.Invoke($"You: {transcript}");
                            Log($"👤 User: \"{transcript}\"");

                            // Send context pairing hint to OpenAI
                            await SendContextHintAsync(transcript);
                        }
                    }
                    break;

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement);
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";

                        // Ignore "buffer too small" errors - these happen when VAD triggers on background noise
                        if (msg?.Contains("buffer too small") == true)
                        {
                            // Don't log as error - just noise from background audio
                            break;
                        }

                        Log($"❌ OpenAI error: {msg}");

                        // Retry greeting on transient server errors
                        if (msg?.Contains("server had an error") == true || msg?.Contains("retry") == true)
                        {
                            await HandleTransientErrorRetryAsync();
                        }
                    }
                    break;

                default:
                    if (!type.StartsWith("rate_limits"))
                        Log($"📨 Event: {type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Parse error: {ex.Message}");
        }
    }

    private async Task ConfigureSessionAsync()
    {
        if (_ws?.State != WebSocketState.Open) return;

        // EXACT MATCH to taxi-realtime-desktop edge function settings
        // See: supabase/functions/taxi-realtime-desktop/index.ts lines 693-707
        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },  // Text first (matches edge function)
                instructions = _systemPrompt,
                voice = _voice,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.4,           // Raised to reduce false triggers from background noise
                    prefix_padding_ms = 400,   // Reduced slightly for better responsiveness
                    silence_duration_ms = 1000 // Standard wait for user to respond
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };

        Log("🎧 Config: VAD=0.4, prefix=400ms, silence=1000ms, volume=2.5x (telephony-optimized)");

        var json = JsonSerializer.Serialize(sessionUpdate);
        await _ws.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
            WebSocketMessageType.Text,
            true,
            _cts?.Token ?? CancellationToken.None);
    }

    private async Task SendGreetingAsync()
    {
        if (_greetingSent || _ws?.State != WebSocketState.Open) return;
        _greetingSent = true;
        _greetingRetryCount = 0;

        // Match edge function: 200ms delay for stability after session.updated
        await Task.Delay(200);

        await SendGreetingRequestAsync();
    }

    private async Task SendGreetingRequestAsync()
    {
        if (_ws?.State != WebSocketState.Open) return;

        _lastQuestionAsked = "pickup";

        // EXACT MATCH to taxi-realtime-desktop edge function (lines 664-672)
        // Uses response.create with modalities ["text", "audio"] and inline instructions
        // DO NOT pre-create an assistant message - let the model generate it with audio
        var responseCreate = new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },  // Text first (matches edge function exactly)
                instructions = "IMPORTANT: You are starting a new taxi booking call. Greet the customer warmly and ask for their pickup location. Say: 'Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. So, let's get started. Where would you like to be picked up?' Do NOT call any tools yet - just greet the user and wait for their response."
            }
        };

        await SendJsonAsync(responseCreate);

        Log($"🎤 Greeting triggered (attempt {_greetingRetryCount + 1}/{MAX_GREETING_RETRIES})");
    }

    private async Task HandleTransientErrorRetryAsync()
    {
        _greetingRetryCount++;

        if (_greetingRetryCount >= MAX_GREETING_RETRIES)
        {
            Log($"❌ Max retries ({MAX_GREETING_RETRIES}) reached - giving up");
            return;
        }

        // Exponential backoff: 500ms, 1000ms, 2000ms
        var delayMs = 500 * (1 << (_greetingRetryCount - 1));
        Log($"🔄 Retrying in {delayMs}ms (attempt {_greetingRetryCount + 1}/{MAX_GREETING_RETRIES})...");

        await Task.Delay(delayMs);

        // If WebSocket closed, attempt full reconnection
        if (_ws?.State != WebSocketState.Open)
        {
            Log("🔄 WebSocket closed before retry - attempting full reconnection...");
            await ReconnectAsync();
            return; // Greeting will be triggered by session.updated after reconnect
        }

        // Clear any stale state and retry
        await SendJsonAsync(new { type = "input_audio_buffer.clear" });
        await SendGreetingRequestAsync();

        // Give OpenAI a moment to respond - if it closes immediately, we'll catch it
        await Task.Delay(200);

        if (_ws?.State != WebSocketState.Open)
        {
            Log("🔄 WebSocket closed after retry - attempting full reconnection...");
            await ReconnectAsync();
        }
    }

    private async Task ReconnectAsync()
    {
        if (_disposed) return;

        try
        {
            // Dispose old WebSocket
            _ws?.Dispose();

            // Create new WebSocket
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
            _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

            var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_model}");
            Log($"🔄 Reconnecting to OpenAI...");

            await _ws.ConnectAsync(uri, _cts?.Token ?? CancellationToken.None);
            Log("✅ Reconnected to OpenAI Realtime API");

            // Reset greeting flag so it triggers again after session.updated
            _greetingSent = false;

            // Restart receive loop
            _ = ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ Reconnection failed: {ex.Message}");
        }
    }

    private async Task SendContextHintAsync(string userText)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var hint = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "system",
                content = new[] { new {
                    type = "input_text",
                    text = $"[CONTEXT] You asked about \"{_lastQuestionAsked}\". User said: \"{userText}\". " +
                           $"Save to the CORRECT field using sync_booking_data. " +
                           $"Current: pickup={_booking.Pickup ?? "empty"}, destination={_booking.Destination ?? "empty"}, passengers={_booking.Passengers?.ToString() ?? "empty"}"
                }}
            }
        };

        await SendJsonAsync(hint);
    }

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

        string? callId = null;
        if (root.TryGetProperty("call_id", out var callIdEl))
            callId = callIdEl.GetString();

        switch (toolName)
        {
            case "sync_booking_data":
                if (args.TryGetValue("pickup", out var pickup))
                    _booking.Pickup = pickup.ToString();
                if (args.TryGetValue("destination", out var dest))
                    _booking.Destination = dest.ToString();
                if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax.ToString(), out var paxInt))
                    _booking.Passengers = paxInt;
                if (args.TryGetValue("pickup_time", out var time))
                    _booking.PickupTime = time.ToString();
                if (args.TryGetValue("last_question_asked", out var lastQ))
                    _lastQuestionAsked = lastQ.ToString() ?? "none";

                OnBookingUpdated?.Invoke(_booking);

                await SendToolResultAsync(callId, new { success = true, state = _booking });
                break;

            case "book_taxi":
                var action = args.TryGetValue("action", out var a) ? a.ToString() : "unknown";
                Log($"📦 Book taxi: {action}");

                if (action == "request_quote")
                {
                    _waitingForQuote = true;

                    // Call real dispatch webhook if configured
                    if (!string.IsNullOrEmpty(_dispatchWebhookUrl))
                    {
                        var quoteResult = await CallDispatchWebhookAsync("request_quote");
                        if (quoteResult.success)
                        {
                            _booking.Fare = quoteResult.fare;
                            _booking.Eta = quoteResult.eta;
                            OnBookingUpdated?.Invoke(_booking);
                            await SendToolResultAsync(callId, new
                            {
                                success = true,
                                fare = quoteResult.fare,
                                eta = quoteResult.eta,
                                message = quoteResult.message ?? $"Your fare is {quoteResult.fare} and the driver will arrive in {quoteResult.eta}"
                            });
                        }
                        else
                        {
                            await SendToolResultAsync(callId, new
                            {
                                success = false,
                                error = quoteResult.error ?? "Failed to get quote"
                            });
                        }
                    }
                    else
                    {
                        // Mock response for testing without webhook
                        await SendToolResultAsync(callId, new
                        {
                            success = true,
                            fare = "£12.50",
                            eta = "5 minutes",
                            message = "Your fare is £12.50 and your driver will arrive in 5 minutes. Would you like me to book that?"
                        });
                    }
                    _waitingForQuote = false;

                    // CRITICAL: Enable confirmation mode - bypass echo guard for "yes" responses
                    _awaitingConfirmation = true;
                    Log("🎯 Awaiting confirmation - echo guard disabled for quick 'yes' responses");
                }
                else if (action == "confirmed")
                {
                    // Confirmation received - reset the awaiting flag
                    _awaitingConfirmation = false;
                    Log("✅ Confirmation received - echo guard re-enabled");
                    if (!string.IsNullOrEmpty(_dispatchWebhookUrl))
                    {
                        var confirmResult = await CallDispatchWebhookAsync("confirmed");
                        if (confirmResult.success)
                        {
                            _booking.Confirmed = true;
                            _booking.BookingRef = confirmResult.bookingRef;
                            OnBookingUpdated?.Invoke(_booking);
                            await SendToolResultAsync(callId, new
                            {
                                success = true,
                                booking_ref = confirmResult.bookingRef,
                                message = confirmResult.message ?? "Your taxi is booked!"
                            });
                        }
                        else
                        {
                            await SendToolResultAsync(callId, new { success = false, error = confirmResult.error });
                        }
                    }
                    else
                    {
                        _booking.Confirmed = true;
                        _booking.BookingRef = $"TAXI-{DateTime.Now:yyyyMMddHHmmss}";
                        OnBookingUpdated?.Invoke(_booking);
                        await SendToolResultAsync(callId, new
                        {
                            success = true,
                            booking_ref = _booking.BookingRef,
                            message = "Your taxi is booked! Your driver will arrive shortly."
                        });
                    }
                }
                break;

            case "end_call":
                Log("📞 End call requested");
                await SendToolResultAsync(callId, new { success = true });
                OnCallEnded?.Invoke();
                break;

            default:
                await SendToolResultAsync(callId, new { error = $"Unknown tool: {toolName}" });
                break;
        }
    }

    /// <summary>
    /// Call the dispatch webhook for quotes and confirmations.
    /// </summary>
    private async Task<(bool success, string? fare, string? eta, string? bookingRef, string? message, string? error)> CallDispatchWebhookAsync(string action)
    {
        try
        {
            var payload = new
            {
                job_id = Guid.NewGuid().ToString(),
                call_id = _callId,
                caller_phone = _callerId,
                action = action,
                ada_pickup = _booking.Pickup,
                ada_destination = _booking.Destination,
                passengers = _booking.Passengers ?? 1,
                pickup_time = _booking.PickupTime ?? "now",
                timestamp = DateTime.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(payload);
            Log($"📡 Calling dispatch: {action}");

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            content.Headers.Add("X-Call-ID", _callId);

            var response = await _httpClient.PostAsync(_dispatchWebhookUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            Log($"📬 Dispatch response: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, null, null, null, $"HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var fare = root.TryGetProperty("fare", out var f) ? f.GetString() :
                       root.TryGetProperty("estimated_fare", out var ef) ? ef.GetString() : null;
            var eta = root.TryGetProperty("eta", out var e) ? e.GetString() :
                      root.TryGetProperty("eta_minutes", out var em) ? $"{em.GetInt32()} minutes" : null;
            var bookingRef = root.TryGetProperty("booking_ref", out var br) ? br.GetString() : null;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;

            return (true, fare, eta, bookingRef, message, null);
        }
        catch (Exception ex)
        {
            Log($"❌ Dispatch error: {ex.Message}");
            return (false, null, null, null, null, ex.Message);
        }
    }

    private async Task SendToolResultAsync(string? callId, object result)
    {
        if (callId == null || _ws?.State != WebSocketState.Open) return;

        var msg = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = JsonSerializer.Serialize(result)
            }
        };

        await SendJsonAsync(msg);
        await SendJsonAsync(new { type = "response.create" });
    }

    private async Task SendJsonAsync(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(obj);
        await _ws.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
            WebSocketMessageType.Text,
            true,
            _cts?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// Safely commit the input audio buffer only if we have enough audio.
    /// Prevents "buffer too small" errors when there's SIP dead-air after greeting.
    /// OpenAI requires at least 100ms of audio before commit.
    /// </summary>
    private async Task CommitInputAudioIfReadyAsync()
    {
        if (_ws?.State != WebSocketState.Open) return;

        // Don't commit if we don't have enough audio buffered
        if (_inputBufferedMs < MIN_COMMIT_MS)
        {
            Log($"⏳ Skipping commit: only {_inputBufferedMs:F1}ms buffered (<{MIN_COMMIT_MS}ms) - waiting for user audio");
            return;
        }

        try
        {
            await SendJsonAsync(new { type = "input_audio_buffer.commit" });
            Log($"🔄 Turn continuation committed ({_inputBufferedMs:F1}ms buffered)");
            _inputBufferedMs = 0; // Reset after commit
        }
        catch (Exception ex)
        {
            Log($"⚠️ Commit error: {ex.Message}");
        }
    }

    // Stateful resampler state for 24kHz → 8kHz (3:1 decimation)
    private short _resampleLastSample = 0;
    private int _resamplePhase = 0;

    private void ProcessAdaAudio(byte[] pcm24kBytes)
    {
        if (_disposed) return;

        var pcm24k = AudioCodecs.BytesToShorts(pcm24kBytes);

        switch (_outputCodec)
        {
            case OutputCodecMode.Opus:
                ProcessAdaAudioOpus(pcm24k);
                break;
            case OutputCodecMode.ALaw:
                ProcessAdaAudioALaw(pcm24k);
                break;
            default:
                ProcessAdaAudioMuLaw(pcm24k);
                break;
        }
    }

    /// <summary>
    /// Process 24kHz PCM to Opus 48kHz frames.
    /// </summary>
    private void ProcessAdaAudioOpus(short[] pcm24k)
    {
        // Upsample 24kHz → 48kHz (2x linear interpolation)
        var pcm48k = new short[pcm24k.Length * 2];
        for (int i = 0; i < pcm24k.Length; i++)
        {
            pcm48k[i * 2] = pcm24k[i];
            // Interpolate between samples
            if (i < pcm24k.Length - 1)
                pcm48k[i * 2 + 1] = (short)((pcm24k[i] + pcm24k[i + 1]) / 2);
            else
                pcm48k[i * 2 + 1] = pcm24k[i];
        }

        // Accumulate into buffer for 20ms Opus frames (960 samples @ 48kHz)
        int newLen = _opusResampleBuffer.Length + pcm48k.Length;
        var newBuffer = new short[newLen];
        Array.Copy(_opusResampleBuffer, newBuffer, _opusResampleBuffer.Length);
        Array.Copy(pcm48k, 0, newBuffer, _opusResampleBuffer.Length, pcm48k.Length);
        _opusResampleBuffer = newBuffer;

        // Encode complete 960-sample frames
        const int OPUS_FRAME = 960;
        while (_opusResampleBuffer.Length >= OPUS_FRAME)
        {
            var frame = new short[OPUS_FRAME];
            Array.Copy(_opusResampleBuffer, frame, OPUS_FRAME);

            // Encode to Opus
            var opusFrame = AudioCodecs.OpusEncode(frame);

            if (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                _outboundQueue.TryDequeue(out _);

            _outboundQueue.Enqueue(opusFrame);

            // Remove processed samples
            var remaining = new short[_opusResampleBuffer.Length - OPUS_FRAME];
            Array.Copy(_opusResampleBuffer, OPUS_FRAME, remaining, 0, remaining.Length);
            _opusResampleBuffer = remaining;
        }
    }

    /// <summary>
    /// Process 24kHz PCM to A-law 8kHz frames.
    /// </summary>
    private void ProcessAdaAudioALaw(short[] pcm24k)
    {
        // Decimate 24kHz → 8kHz (3:1)
        int outputLen = pcm24k.Length / 3;
        var pcm8k = new short[outputLen];
        for (int i = 0; i < outputLen; i++)
            pcm8k[i] = pcm24k[i * 3];

        // Encode to A-law
        var alaw = AudioCodecs.ALawEncode(pcm8k);

        // Split into 20ms frames (160 bytes @ 8kHz)
        for (int i = 0; i < alaw.Length; i += 160)
        {
            int len = Math.Min(160, alaw.Length - i);
            var frame = new byte[160];
            Buffer.BlockCopy(alaw, i, frame, 0, len);

            // Pad short frames with A-law silence (0xD5)
            if (len < 160)
                Array.Fill(frame, (byte)0xD5, len, 160 - len);

            if (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                _outboundQueue.TryDequeue(out _);

            _outboundQueue.Enqueue(frame);
        }
    }

    /// <summary>
    /// Process 24kHz PCM to µ-law 8kHz frames.
    /// </summary>
    private void ProcessAdaAudioMuLaw(short[] pcm24k)
    {
        // Decimate 24kHz → 8kHz (3:1)
        int outputLen = pcm24k.Length / 3;
        var pcm8k = new short[outputLen];
        for (int i = 0; i < outputLen; i++)
            pcm8k[i] = pcm24k[i * 3];

        // Encode to µ-law
        var ulaw = AudioCodecs.MuLawEncode(pcm8k);

        // Split into 20ms frames (160 bytes @ 8kHz)
        for (int i = 0; i < ulaw.Length; i += 160)
        {
            int len = Math.Min(160, ulaw.Length - i);
            var frame = new byte[160];
            Buffer.BlockCopy(ulaw, i, frame, 0, len);

            // Pad short frames with µ-law silence (0xFF)
            if (len < 160)
                Array.Fill(frame, (byte)0xFF, len, 160 - len);

            if (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                _outboundQueue.TryDequeue(out _);

            _outboundQueue.Enqueue(frame);
        }
    }

    private static object[] GetTools() => new object[]
    {
        new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = "sync_booking_data",
            ["description"] = "Save user answers to the correct field.",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["pickup"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Pickup address" },
                    ["destination"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Destination address" },
                    ["passengers"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of passengers" },
                    ["pickup_time"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Pickup time" },
                    ["last_question_asked"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "pickup", "destination", "passengers", "time", "confirmation", "none" },
                        ["description"] = "What question you are about to ask NEXT"
                    }
                }
            }
        },
        new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = "book_taxi",
            ["description"] = "Request quote or confirm booking.",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["action"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "request_quote", "confirmed" }
                    },
                    ["pickup"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["destination"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["passengers"] = new Dictionary<string, object> { ["type"] = "integer", ["minimum"] = 1 },
                    ["time"] = new Dictionary<string, object> { ["type"] = "string" }
                },
                ["required"] = new[] { "action" }
            }
        },
        new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = "end_call",
            ["description"] = "Disconnect the call after goodbye.",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>()
            }
        }
    };

    private static string GetDefaultSystemPrompt() => @"You are Ada, a professional taxi booking assistant for a UK taxi company.

# PERSONALITY
- Warm, efficient, and professional
- Brief responses (under 20 words when possible)
- Natural British English

# BOOKING FLOW (STRICT ORDER)
1. Greet → Ask for PICKUP address
2. Acknowledge briefly → Ask for DESTINATION  
3. Acknowledge briefly → Ask for NUMBER OF PASSENGERS
4. Acknowledge briefly → Ask if NOW or scheduled time
5. Summarize using EXACT addresses spoken → Call book_taxi(action=""request_quote"")
6. When you receive fare/ETA, tell customer and ask: ""Would you like me to book that?""
7. If YES/confirm → Call book_taxi(action=""confirmed"")
8. Thank them, give booking ref → Call end_call()

# ANTI-HALLUCINATION RULES (CRITICAL)
❌ NEVER make up, guess, or alter addresses - repeat EXACTLY what user said
❌ NEVER substitute street names (e.g., don't change 'David Road' to 'David Close')
❌ NEVER drop house numbers or suffixes (52A must stay 52A)
❌ NEVER add details the user didn't say (no postcodes, no landmarks)
❌ If unsure, ASK the user to repeat - don't guess

# SUMMARY PHASE RULES
When summarizing, use the EXACT words the user spoke:
- If user said ""52A David Road"" → say ""52A David Road"" (not ""52 David"" or ""David Close"")
- If user said ""the Tesco on Main Street"" → say ""the Tesco on Main Street""
- Keep house numbers EXACTLY as spoken including letters (A, B, C)

# CONTEXT PAIRING
Map user responses to the question you just asked:
- Asked PICKUP → response is pickup
- Asked DESTINATION → response is destination  
- Asked PASSENGERS → response is passenger count
- Asked TIME → response is pickup time

# CRITICAL RULES
✅ Accept ANY address exactly as spoken - NEVER ask for clarification
✅ Move to next question immediately after each answer
✅ When customer confirms (yes, yeah, go ahead, book it) → IMMEDIATELY call book_taxi(action=""confirmed"")

❌ NEVER ask for house numbers, postcodes, or spell addresses
❌ NEVER make up fares - wait for book_taxi response

# TOOLS
- sync_booking_data: Save each field EXACTLY as spoken
- book_taxi: action=""request_quote"" for pricing, action=""confirmed"" after yes
- end_call: After confirmed booking and goodbye";

    private void Log(string message)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public async Task DisconnectAsync()
    {
        if (_disposed) return;

        try { _cts?.Cancel(); } catch { }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", closeCts.Token);
            }
            catch { }
        }

        try { _ws?.Dispose(); _ws = null; } catch { }
        ClearPendingFrames();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }

        ClearPendingFrames();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Booking state for the taxi booking flow.
/// </summary>
public class BookingState
{
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int? Passengers { get; set; }
    public string? PickupTime { get; set; }
    public string? Fare { get; set; }
    public string? Eta { get; set; }
    public string? BookingRef { get; set; }
    public bool Confirmed { get; set; }
}
