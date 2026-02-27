using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ZaffAdaSystem.Config;
using Microsoft.Extensions.Logging;
using OpenAI.RealtimeConversation;
using System.ClientModel;

#pragma warning disable OPENAI002 // Experimental Realtime API

namespace ZaffAdaSystem.Ai;

/// <summary>
/// OpenAI Realtime API client using official .NET SDK (2.1.0-beta.4) — G.711 A-law passthrough (8kHz).
///
/// Production features:
/// - Native G.711 A-law codec (no resampling, direct SIP passthrough)
/// - Tool calling with async execution (sync_booking_data, book_taxi, end_call)
/// - Deferred response handling (queue response.create if one is active)
/// - Barge-in detection (speech_started while audio streaming)
/// - No-reply watchdog (15s silence → re-prompt, 30s for disambiguation)
/// - Echo guard (playout-aware silence window after response completes)
/// - Goodbye detection with drain-aware hangup
/// - Keepalive heartbeat logging
/// - Per-call state management and reset
/// - Non-blocking channel-based logger
/// - Confirmation-awaiting mode (extended watchdog timeout)
///
/// Version 2.0 — Zaffiqbal247RadioCars (OpenAI .NET SDK beta)
/// </summary>
public sealed class OpenAiSdkClient : IOpenAiClient, IAsyncDisposable
{
    public const string VERSION = "2.0-sdk-g711-beta";

    // =========================
    // CONFIG
    // =========================
    private readonly ILogger<OpenAiSdkClient> _logger;
    private readonly OpenAiSettings _settings;
    private readonly string _systemPrompt;

    // =========================
    // SDK INSTANCES
    // =========================
    private RealtimeConversationClient? _client;
    private RealtimeConversationSession? _session;
    private CancellationTokenSource? _sessionCts;
    private Task? _eventLoopTask;
    private Task? _keepaliveTask;

    // =========================
    // THREAD-SAFE STATE (per-call, reset by ResetCallState)
    // =========================
    private int _disposed;
    private int _callEnded;
    private int _responseActive;
    private int _deferredResponsePending;
    private int _noReplyWatchdogId;
    private int _noReplyCount;
    private int _toolInFlight;
    private int _hasEnqueuedAudio;
    private int _ignoreUserAudio;
    private int _greetingSent;
    private int _syncCallCount;
    private int _bookingConfirmed;
    private long _lastAdaFinishedAt;
    private long _lastUserSpeechAt;

    // Response tracking
    private string? _activeResponseId;
    private string? _lastAdaTranscript;
    private bool _awaitingConfirmation;
    private bool _useSemanticVad = true;   // default: semantic for better address collection
    private float _semanticEagerness = 0.5f; // low = patient, high = quick
    // Transcript comparison: stash Whisper STT for mismatch detection
    private string? _lastUserTranscript;

    /// <summary>Last Whisper STT transcript for mismatch comparison in tool calls.</summary>
    public string? LastUserTranscript => _lastUserTranscript;

    // Caller state
    private string _callerId = "";

    // =========================
    // CONSTANTS
    // =========================
    private const int MAX_NO_REPLY_PROMPTS = 3;
    private const int NO_REPLY_TIMEOUT_MS = 15_000;
    private const int CONFIRMATION_TIMEOUT_MS = 30_000;
    private const int DISAMBIGUATION_TIMEOUT_MS = 30_000;
    private const int ECHO_GUARD_MS = 300;

    // =========================
    // NON-BLOCKING LOGGER
    // =========================
    private readonly Channel<string> _logChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _logTask;
    private volatile bool _loggerRunning;

    // =========================
    // EVENTS
    // =========================
    public bool IsConnected => _session != null && Volatile.Read(ref _disposed) == 0;
    public bool IsResponseActive => Volatile.Read(ref _responseActive) == 1;

    public event Action<byte[]>? OnAudio;
    public event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    public event Action<string>? OnEnded;
    public event Action? OnPlayoutComplete;
    public event Action<string, string>? OnTranscript;

    /// <summary>Fired on barge-in (playout should clear its buffer).</summary>
    public event Action? OnBargeIn;

    /// <summary>Fired when AI response stream completes.</summary>
    public event Action? OnResponseCompleted;

    /// <summary>Optional: query playout queue depth for drain-aware shutdown.</summary>
    public Func<int>? GetQueuedFrames { get; set; }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // =========================
    // CONSTRUCTOR
    // =========================
    public OpenAiSdkClient(
        ILogger<OpenAiSdkClient> logger,
        OpenAiSettings settings,
        string? systemPrompt = null)
    {
        _logger = logger;
        _settings = settings;
        _systemPrompt = systemPrompt ?? GetDefaultSystemPrompt();
    }

    // =========================
    // NON-BLOCKING LOGGING
    // =========================
    private void InitializeLogger()
    {
        if (_loggerRunning) return;
        _loggerRunning = true;
        _logTask = Task.Run(LogLoopAsync);
    }

    private async Task LogLoopAsync()
    {
        try
        {
            await foreach (var msg in _logChannel.Reader.ReadAllAsync(_sessionCts?.Token ?? CancellationToken.None))
            {
                try { _logger.LogInformation("{Msg}", msg); } catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Log(string msg) => _logChannel.Writer.TryWrite(msg);

    // =========================
    // PER-CALL STATE RESET
    // =========================
    private void ResetCallState(string? caller)
    {
        _callerId = caller ?? "";

        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Exchange(ref _noReplyCount, 0);
        Interlocked.Exchange(ref _toolInFlight, 0);
        Interlocked.Exchange(ref _hasEnqueuedAudio, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _syncCallCount, 0);
        Interlocked.Exchange(ref _bookingConfirmed, 0);
        Interlocked.Increment(ref _noReplyWatchdogId);

        _activeResponseId = null;
        _lastAdaTranscript = null;
        _lastUserTranscript = null;
        _awaitingConfirmation = false;

        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
    }

    // =========================
    // CONNECT
    // =========================
    public async Task ConnectAsync(string callerId, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OpenAiSdkClient));

        ResetCallState(callerId);
        InitializeLogger();

        Log($"📞 Connecting v{VERSION} (caller: {callerId}, codec: A-law 8kHz, model: {_settings.Model})");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Beta SDK: RealtimeConversationClient takes (model, credential)
        _client = new RealtimeConversationClient(
            model: _settings.Model ?? "gpt-4o-realtime-preview",
            credential: new ApiKeyCredential(_settings.ApiKey));

        try
        {
            _session = await _client.StartConversationSessionAsync(_sessionCts.Token);

            // Detect language from caller's phone number and prepend hint to instructions
            var detectedLang = DetectLanguageStatic(callerId);
            var detectedLangName = GetLanguageNameStatic(detectedLang);
            var langPreamble = detectedLang != "en"
                ? $"[LANGUAGE PREWARN] The caller's phone number indicates they are from a {detectedLangName}-speaking country. " +
                  $"EXPECT the caller to speak {detectedLangName}. Transcribe and respond in {detectedLangName} by default. " +
                  $"If they switch to another language, follow them.\n\n"
                : "";

            var options = new ConversationSessionOptions
            {
                Instructions = langPreamble + _systemPrompt,
                Voice = MapVoice(_settings.Voice),
                InputAudioFormat = ConversationAudioFormat.G711Alaw,
                OutputAudioFormat = ConversationAudioFormat.G711Alaw,
                InputTranscriptionOptions = new ConversationInputTranscriptionOptions
                {
                    Model = "whisper-1"
                },
                // SDK 2.1.0-beta.4 doesn't support semantic_vad natively.
                // Simulate patient vs responsive modes using server_vad with different parameters.
                TurnDetectionOptions = _useSemanticVad
                    ? ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                        detectionThreshold: 0.3f,
                        prefixPaddingDuration: TimeSpan.FromMilliseconds(800),
                        silenceDuration: TimeSpan.FromMilliseconds(2000))  // Patient: 2s silence for full address utterances
                    : ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                        detectionThreshold: 0.2f,
                        prefixPaddingDuration: TimeSpan.FromMilliseconds(600),
                        silenceDuration: TimeSpan.FromMilliseconds(900))   // Responsive: quick for short answers
            };

            options.Tools.Add(BuildSyncBookingDataTool());
            options.Tools.Add(BuildClarifyAddressTool());
            options.Tools.Add(BuildBookTaxiTool());
            options.Tools.Add(BuildCreateBookingTool());
            options.Tools.Add(BuildFindLocalEventsTool());
            options.Tools.Add(BuildEndCallTool());

            await _session.ConfigureSessionAsync(options);

            _logger.LogInformation("✅ Session configured (G.711 A-law, model={Model})", _settings.Model);

            _eventLoopTask = Task.Run(() => ReceiveEventsLoopAsync(_sessionCts.Token));
            _keepaliveTask = Task.Run(() => KeepaliveLoopAsync(_sessionCts.Token));

            // NOTE: Do NOT send greeting here — CallSession will call SendGreetingAsync()
            // AFTER injecting caller history so Ada knows the caller's name.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to OpenAI Realtime API");
            await DisconnectAsync();
            throw;
        }
    }

    // =========================
    // DISCONNECT
    // =========================
    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _logger.LogInformation("Disconnecting from OpenAI Realtime API");
        SignalCallEnded("disconnect");

        _sessionCts?.Cancel();

        if (_session != null)
        {
            try { _session.Dispose(); }
            catch { }
            _session = null;
        }

        _loggerRunning = false;
        _logChannel.Writer.TryComplete();
        try { if (_logTask != null) await _logTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

        _sessionCts?.Dispose();
        _sessionCts = null;

        OnAudio = null;
        OnToolCall = null;
        OnEnded = null;
        OnPlayoutComplete = null;
        OnTranscript = null;
        OnBargeIn = null;
        OnResponseCompleted = null;
        GetQueuedFrames = null;

        ResetCallState(null);
        _logger.LogInformation("OpenAiSdkClient fully disposed — all state cleared");
    }

    // =========================
    // AUDIO INPUT (SIP → OpenAI)
    // =========================
    public void SendAudio(byte[] alawData)
    {
        if (!IsConnected || alawData == null || alawData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;
        // Suppress user audio while a critical tool (book_taxi, end_call) is in flight
        // This prevents background noise from confusing the AI mid-tool-execution
        if (Volatile.Read(ref _toolInFlight) == 1) return;

        try
        {
            // Beta SDK: SendInputAudio takes BinaryData
            _session!.SendInputAudio(new BinaryData(alawData));
            Volatile.Write(ref _lastUserSpeechAt, NowMs());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending audio to OpenAI");
        }
    }

    // =========================
    // CANCEL / INJECT
    // =========================
    public async Task CancelResponseAsync()
    {
        if (!IsConnected) return;
        try
        {
            Log("🛑 Cancelling response");
            await _session!.CancelResponseAsync();
            Interlocked.Exchange(ref _responseActive, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling response");
        }
    }

    public async Task InjectMessageAndRespondAsync(string message)
    {
        if (!IsConnected) return;

        try
        {
            bool isCritical = message.Contains("[FARE RESULT]") || message.Contains("DISAMBIGUATION");
            if (isCritical && Volatile.Read(ref _responseActive) == 1)
            {
                Log("🛑 Cancelling active response before critical injection");
                await _session!.CancelResponseAsync();
                Interlocked.Exchange(ref _responseActive, 0);
                await Task.Delay(100);
            }

            Log($"💉 Injecting: {(message.Length > 80 ? message[..80] + "..." : message)}");
            await _session!.AddItemAsync(
                ConversationItem.CreateUserMessage(new[] { ConversationContentPart.CreateInputTextPart(message) }));
            await _session.StartResponseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error injecting message");
        }
    }

    public async Task InjectSystemMessageAsync(string message)
    {
        if (!IsConnected) return;

        try
        {
            Log($"💉 System inject: {(message.Length > 80 ? message[..80] + "..." : message)}");
            await _session!.AddItemAsync(
                ConversationItem.CreateUserMessage(new[] { ConversationContentPart.CreateInputTextPart(message) }));
            // No StartResponseAsync — this is a silent context injection, not a prompt for Ada to respond
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error injecting system message");
        }
    }

    // =========================
    // PLAYOUT COMPLETION (echo guard)
    // =========================
    public void NotifyPlayoutComplete()
    {
        Volatile.Write(ref _lastAdaFinishedAt, NowMs());
        OnPlayoutComplete?.Invoke();
        StartNoReplyWatchdog();
    }

    public void SetAwaitingConfirmation(bool awaiting)
    {
        _awaitingConfirmation = awaiting;
        Log($"🔔 Awaiting confirmation: {awaiting}");
    }

    public void CancelDeferredResponse()
    {
        Interlocked.Exchange(ref _deferredResponsePending, 0);
    }

    /// <summary>
    /// Live-switch between semantic_vad and server_vad mid-session.
    /// Reconfigures the OpenAI session immediately — takes effect on the next turn.
    /// </summary>
    public async Task SetVadModeAsync(bool useSemantic, float eagerness = 0.5f)
    {
        _useSemanticVad = useSemantic;
        _semanticEagerness = Math.Clamp(eagerness, 0.1f, 1.0f);

        if (!IsConnected || _session == null) return;

        var mode = useSemantic ? $"semantic (eagerness={_semanticEagerness:F2})" : "server_vad";
        Log($"🔄 Switching VAD mode → {mode}");

        try
        {
            var options = new ConversationSessionOptions
            {
                // SDK 2.1.0-beta.4: simulate semantic-like patience using server_vad parameters
                TurnDetectionOptions = useSemantic
                    ? ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                        detectionThreshold: 0.3f,
                        prefixPaddingDuration: TimeSpan.FromMilliseconds(800),
                        silenceDuration: TimeSpan.FromMilliseconds(2000))  // Patient mode: 2s for full addresses
                    : ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                        detectionThreshold: 0.2f,
                        prefixPaddingDuration: TimeSpan.FromMilliseconds(600),
                        silenceDuration: TimeSpan.FromMilliseconds(900))   // Responsive mode
            };

            await _session.ConfigureSessionAsync(options);
            Log($"✅ VAD mode switched to {mode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch VAD mode to {Mode}", mode);
        }
    }

    // =========================
    // EVENT LOOP
    // =========================
    private async Task ReceiveEventsLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var update in _session!.ReceiveUpdatesAsync(ct))
            {
                try
                {
                    await ProcessUpdateAsync(update);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing update: {Type}", update?.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("Event loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in event loop");
            SignalCallEnded("event_loop_error");
        }
    }

    private async Task ProcessUpdateAsync(ConversationUpdate? update)
    {
        if (update == null) return;

        switch (update)
        {
            // ── SPEECH DETECTION ──
            case ConversationInputSpeechStartedUpdate:
                HandleSpeechStarted();
                break;

            case ConversationInputSpeechFinishedUpdate:
                Log("🔇 User finished speaking");
                break;

            // ── AUDIO STREAMING + TEXT DELTA ──
            case ConversationItemStreamingPartDeltaUpdate delta:
                if (delta.AudioBytes != null)
                {
                    Interlocked.Exchange(ref _hasEnqueuedAudio, 1);
                    OnAudio?.Invoke(delta.AudioBytes.ToArray());
                }
                break;

            // ── ITEM STREAMING FINISHED (transcript + tool calls) ──
            case ConversationItemStreamingFinishedUpdate itemFinished:
                // Handle function call completion
                if (itemFinished.FunctionCallId is not null)
                {
                    Interlocked.Exchange(ref _toolInFlight, 1);
                    Interlocked.Exchange(ref _responseActive, 0);
                    _ = HandleToolCallAsync(itemFinished);
                }
            // Handle audio transcript from Ada
                else if (itemFinished.MessageContentParts?.Count > 0)
                {
                    foreach (var part in itemFinished.MessageContentParts)
                    {
                        // Fix: extract AudioTranscript explicitly instead of ToString()
                        // ToString() returns the class name (InternalRealtimeResponseAudioContentPart)
                        var transcriptText = part?.AudioTranscript ?? part?.Text ?? part?.ToString();
                        if (!string.IsNullOrEmpty(transcriptText) &&
                            !transcriptText.Contains("InternalRealtime"))
                        {
                            _lastAdaTranscript = transcriptText;
                            OnTranscript?.Invoke("Ada", _lastAdaTranscript);
                            CheckGoodbye(_lastAdaTranscript);
                        }
                    }
                }
                break;

            // ── USER TRANSCRIPT ──
            case ConversationInputTranscriptionFinishedUpdate userTranscript:
                Interlocked.Exchange(ref _noReplyCount, 0);
                var transcript = userTranscript.Transcript;
                _lastUserTranscript = transcript; // Stash for mismatch comparison
                OnTranscript?.Invoke("User", transcript);

                // TRANSCRIPT GROUNDING: inject the exact STT words back into
                // the conversation so the AI uses verbatim text for tool args.
                // This prevents the model from "reinterpreting" addresses
                // (e.g. "David Road" → "Dovey Road") in its tool calls.
                if (!string.IsNullOrWhiteSpace(transcript) && transcript.Length > 2)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_session == null || !IsConnected) return;
                            var grounding = $"[TRANSCRIPT] The caller's exact words were: \"{transcript}\". " +
                                "Use these EXACT words for any tool call arguments — do NOT substitute similar-sounding names.";
                            await _session.AddItemAsync(
                                ConversationItem.CreateUserMessage(new[] {
                                    ConversationContentPart.CreateInputTextPart(grounding) }));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to inject transcript grounding");
                        }
                    });
                }
                break;

            // ── ERROR ──
            case ConversationErrorUpdate errorUpdate:
                _logger.LogError("OpenAI Realtime error: {Msg}", errorUpdate.Message);
                break;

            // ── ALL OTHER UPDATES (session started, response lifecycle, etc.) ──
            default:
                HandleGenericUpdate(update);
                break;
        }
    }

    /// <summary>
    /// Handle response lifecycle and other events that don't have dedicated types in the beta SDK.
    /// We match on the type name since the beta SDK generates internal types for these events.
    /// </summary>
    private void HandleGenericUpdate(ConversationUpdate update)
    {
        var typeName = update.GetType().Name;

        if (typeName.Contains("SessionStarted") || typeName.Contains("SessionCreated"))
        {
            Log("✅ Realtime Session Started");
        }
        else if (typeName.Contains("ResponseStarted") || typeName.Contains("ResponseCreated"))
        {
            Interlocked.Exchange(ref _responseActive, 1);
            Interlocked.Exchange(ref _hasEnqueuedAudio, 0);
            Log("🎤 Response started");
        }
        else if (typeName.Contains("ResponseDone") || typeName.Contains("ResponseFinished"))
        {
            Interlocked.Exchange(ref _responseActive, 0);
            Log("✋ Response finished");
            OnResponseCompleted?.Invoke();

            if (Interlocked.CompareExchange(ref _deferredResponsePending, 0, 1) == 1)
            {
                Log("⏳ Processing deferred response");
                _ = Task.Run(async () =>
                {
                    try { await _session!.StartResponseAsync(); }
                    catch (Exception ex) { _logger.LogError(ex, "Error starting deferred response"); }
                });
            }
            else
            {
                // Fallback watchdog: capture ID + callEnded guard to prevent stale fires
                var wdId = Volatile.Read(ref _noReplyWatchdogId);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10_000);
                    if (Volatile.Read(ref _noReplyWatchdogId) != wdId) return;
                    if (Volatile.Read(ref _callEnded) != 0) return;
                    if (Volatile.Read(ref _responseActive) == 0 &&
                        Volatile.Read(ref _toolInFlight) == 0)
                    {
                        StartNoReplyWatchdog();
                    }
                });
            }
        }
    }

    // =========================
    // TOOL CALL HANDLING
    // =========================
    private async Task HandleToolCallAsync(ConversationItemStreamingFinishedUpdate itemFinished)
    {
        try
        {
            var toolName = itemFinished.FunctionName ?? "unknown";
            var argsJson = itemFinished.FunctionCallArguments ?? "{}";
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            Log($"🔧 Tool call: {toolName} ({args.Count} args)");

            if (OnToolCall != null)
            {
                var result = await OnToolCall(toolName, args);
                var resultJson = result is string s ? s : JsonSerializer.Serialize(result);

                Log($"✅ Tool result: {(resultJson.Length > 120 ? resultJson[..120] + "..." : resultJson)}");

                if (toolName == "sync_booking_data")
                    Interlocked.Increment(ref _syncCallCount);

                // Beta SDK: create function output item and add it
                var outputItem = ConversationItem.CreateFunctionCallOutput(
                    callId: itemFinished.FunctionCallId!,
                    output: resultJson);
                await _session!.AddItemAsync(outputItem);

                if (Volatile.Read(ref _responseActive) == 1)
                {
                    Interlocked.Exchange(ref _deferredResponsePending, 1);
                    Log("⏳ Response still active after tool — deferring");
                }
                else
                {
                    await _session.StartResponseAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling tool call: {Name}", itemFinished.FunctionName);
        }
        finally
        {
            Interlocked.Exchange(ref _toolInFlight, 0);
        }
    }

    // =========================
    // BARGE-IN
    // =========================
    private void HandleSpeechStarted()
    {
        Interlocked.Increment(ref _noReplyWatchdogId);

        if (Volatile.Read(ref _responseActive) == 1 && Volatile.Read(ref _hasEnqueuedAudio) == 1)
        {
            Log("✂️ Barge-in detected — clearing playout");
            OnBargeIn?.Invoke();
        }
    }

    // =========================
    // GREETING
    // =========================
    public async Task SendGreetingAsync(string? callerName = null)
    {
        if (Interlocked.Exchange(ref _greetingSent, 1) == 1) return;
        if (_session == null) return;

        try
        {
            var lang = DetectLanguage(_callerId);
            var langName = GetLanguageName(lang);
            var localizedGreeting = GetLocalizedGreeting(lang);

            string greeting;
            if (!string.IsNullOrWhiteSpace(callerName))
            {
                greeting = $"[SYSTEM] [LANG: {langName}] A returning caller named {callerName} has connected (ID: {_callerId}). " +
                           $"Greet them BY NAME in {langName}. You MUST say EXACTLY: \"Welcome to 247 Radio Carz. Hello {callerName}, my name is Ada and I am here to help you with your booking today. Where would you like to be picked up from?\" " +
                           $"⚠️ Do NOT say \"Where can I take you\" or any variation. ALWAYS ask for PICKUP LOCATION first.";
            }
            else
            {
                greeting = $"[SYSTEM] [LANG: {langName}] A new caller has connected (ID: {_callerId}). " +
                           $"Greet them in {langName}. Say: \"{localizedGreeting}\"";
            }
            await _session.AddItemAsync(
                ConversationItem.CreateUserMessage(new[] { ConversationContentPart.CreateInputTextPart(greeting) }));
            await _session.StartResponseAsync();
            Log($"📢 Greeting sent (language: {langName})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending greeting");
        }
    }

    // =========================
    // LANGUAGE DETECTION
    // =========================
    private static string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";
        var clean = phone.Replace(" ", "").Replace("-", "");
        if (clean.StartsWith("+31") || clean.StartsWith("0031") || clean.StartsWith("06"))
            return "nl";
        if (clean.StartsWith("+33") || clean.StartsWith("0033"))
            return "fr";
        if (clean.StartsWith("+49") || clean.StartsWith("0049"))
            return "de";
        if (clean.StartsWith("+34") || clean.StartsWith("0034"))
            return "es";
        if (clean.StartsWith("+39") || clean.StartsWith("0039"))
            return "it";
        if (clean.StartsWith("+48") || clean.StartsWith("0048"))
            return "pl";
        if (clean.StartsWith("+351") || clean.StartsWith("00351"))
            return "pt";
        return "en";
    }

    private static string GetLanguageName(string lang) => lang switch
    {
        "nl" => "Dutch",
        "fr" => "French",
        "de" => "German",
        "es" => "Spanish",
        "it" => "Italian",
        "pl" => "Polish",
        "pt" => "Portuguese",
        _ => "English"
    };

    private static string GetLocalizedGreeting(string lang) => lang switch
    {
        "nl" => "Hallo, welkom bij Taxibot. Ik ben Ada. Wat is uw naam?",
        "fr" => "Bonjour, bienvenue chez Taxibot. Je suis Ada. Quel est votre nom?",
        "de" => "Hallo, willkommen bei Taxibot. Ich bin Ada. Wie heißen Sie?",
        "es" => "Hola, bienvenido a Taxibot. Soy Ada. ¿Cuál es su nombre?",
        "it" => "Ciao, benvenuto a Taxibot. Sono Ada. Qual è il suo nome?",
        "pl" => "Cześć, witamy w Taxibot. Jestem Ada. Jak się Pan/Pani nazywa?",
        "pt" => "Olá, bem-vindo ao Taxibot. Sou a Ada. Qual é o seu nome?",
        _ => "Welcome to 247 Radio Carz. My name is Ada and I am here to help you with your booking today. May I take your name?"
    };

    // =========================
    // NO-REPLY WATCHDOG
    // =========================
    private void StartNoReplyWatchdog()
    {
        if (Volatile.Read(ref _callEnded) != 0) return;
        if (Volatile.Read(ref _toolInFlight) == 1) return;

        var count = Volatile.Read(ref _noReplyCount);
        if (count >= MAX_NO_REPLY_PROMPTS)
        {
            Log($"⏰ Max no-reply prompts reached ({MAX_NO_REPLY_PROMPTS}), ending call");
            SignalCallEnded("no_reply_timeout");
            return;
        }

        var timeout = _awaitingConfirmation ? CONFIRMATION_TIMEOUT_MS : NO_REPLY_TIMEOUT_MS;
        var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);

        _ = Task.Run(async () =>
        {
            await Task.Delay(timeout);

            if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
            if (Volatile.Read(ref _responseActive) == 1) return;
            if (Volatile.Read(ref _toolInFlight) == 1) return;
            if (Volatile.Read(ref _callEnded) != 0) return;
            if (!IsConnected) return;

            var sinceAdaFinished = NowMs() - Volatile.Read(ref _lastAdaFinishedAt);
            if (sinceAdaFinished < ECHO_GUARD_MS) return;

            Interlocked.Increment(ref _noReplyCount);
            Log($"⏰ No-reply watchdog triggered (attempt {Volatile.Read(ref _noReplyCount)}/{MAX_NO_REPLY_PROMPTS})");

            try
            {
                await _session!.AddItemAsync(
                    ConversationItem.CreateUserMessage(new[] { ConversationContentPart.CreateInputTextPart("[SILENCE] Hello? Are you still there?") }));
                await _session.StartResponseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in no-reply watchdog");
            }
        }, _sessionCts?.Token ?? CancellationToken.None);
    }

    // =========================
    // KEEPALIVE
    // =========================
    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(20_000, ct);

                var active = Volatile.Read(ref _responseActive) == 1;
                var ended = Volatile.Read(ref _callEnded) == 1;
                var tool = Volatile.Read(ref _toolInFlight) == 1;
                Log($"💓 Keepalive: connected={IsConnected}, response_active={active}, tool={tool}, ended={ended}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"⚠️ Keepalive error: {ex.Message}"); }
    }

    // =========================
    // GOODBYE DETECTION
    // =========================
    private void CheckGoodbye(string text)
    {
        if (text.Contains("Goodbye", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("Taxibot", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("TaxiBot", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("taxi", StringComparison.OrdinalIgnoreCase)))
        {
            Log("👋 Goodbye detected in Ada's speech");
            Interlocked.Exchange(ref _ignoreUserAudio, 1);
        }
    }

    // =========================
    // CALL ENDED SIGNAL
    // =========================
    private void SignalCallEnded(string reason)
    {
        if (Interlocked.CompareExchange(ref _callEnded, 1, 0) == 0)
        {
            Log($"📞 Call ended: {reason}");
            OnEnded?.Invoke(reason);
        }
    }

    // =========================
    // TOOL DEFINITIONS
    // =========================
    private static ConversationFunctionTool BuildSyncBookingDataTool() => new("sync_booking_data")
    {
        Description = "MANDATORY: Persist booking data as collected from the caller. " +
                      "Must be called BEFORE generating any text response when user provides or amends booking details. " +
                      "CRITICAL: Include ALL fields the caller mentioned in their utterance — if they say " +
                      "'from X going to Y with 3 passengers', set pickup, destination, AND passengers in ONE call. " +
                      "NEVER split a compound utterance into multiple calls or ignore mentioned fields. " +
                      "CHANGE DETECTION: If the caller corrects ANY previously provided detail, you MUST call this tool " +
                      "IMMEDIATELY with the corrected value AND explain what changed in the 'interpretation' field.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                intent = new { type = "string", @enum = new[] { "update_field", "confirm_booking", "cancel_booking", "provide_info" }, description = "MANDATORY. Describes the caller's intent for this turn. 'update_field' = caller is providing or correcting a booking field (name, pickup, destination, passengers, time). 'confirm_booking' = caller is explicitly confirming the booking (yes, go ahead, book it). 'cancel_booking' = caller wants to cancel or abandon. 'provide_info' = caller is answering your question with new info (default for normal collection flow)." },
                caller_name = new { type = "string", description = "Caller's name" },
                caller_area = new { type = "string", description = "Caller's self-reported area/district (e.g. 'Earlsdon', 'Tile Hill', 'Binley'). Used as a location bias for address resolution. Only set when the caller explicitly states their area." },
                pickup = new { type = "string", description = "Pickup address ONLY — extract the address/place name from the caller's speech. Strip out unrelated info like passenger counts, times, or other details." },
                destination = new { type = "string", description = "Destination address ONLY — extract the address/place name from the caller's speech. Strip out unrelated info like passenger counts, times, or other details. E.g. if caller says '7 Russell Street and 3 passengers', destination='7 Russell Street'." },
                passengers = new { type = "integer", description = "Number of passengers" },
                pickup_time = new { type = "string", description = "Pickup time in YYYY-MM-DD HH:MM format (24h clock) or 'ASAP'. Use REFERENCE_DATETIME from system prompt to resolve relative times like 'tomorrow', 'in 30 minutes', '5pm'. NEVER pass raw phrases." },
                vehicle_type = new { type = "string", @enum = new[] { "Saloon", "Estate", "MPV", "Minibus" }, description = "Vehicle type. Auto-recommended based on passengers and luggage (1-4=Saloon, 5-6=Estate, 7=MPV, 8+=Minibus; heavy luggage upgrades one tier). Only set if caller explicitly requests a specific vehicle type (e.g. 'send an MPV')." },
                luggage = new { type = "string", @enum = new[] { "none", "small", "medium", "heavy" }, description = "Luggage amount. MUST ask about luggage when: (1) destination is an airport, train station, coach station, or seaport, OR (2) 3 or more passengers. Values: none=no luggage, small=hand luggage/backpacks, medium=1-2 suitcases, heavy=3+ suitcases or bulky items." },
                interpretation = new { type = "string", description = "Brief explanation of what you understood from the caller's speech. If this is a CORRECTION, explain what changed and why (e.g. 'User corrected pickup from Parkhouse Street to Far Gosford Street — venue name is Sweet Spot'). This helps the system track your understanding." },
                special_instructions = new { type = "string", description = "Any special requests, notes, or instructions the caller wants to add to the booking (e.g. flight number, wheelchair access, child seat, meet at arrivals, extra luggage). Only set when the caller explicitly provides special instructions." }
            },
            required = new[] { "intent" }
        }))
    };

    private static ConversationFunctionTool BuildBookTaxiTool() => new("book_taxi")
    {
        Description = "Request a fare quote or confirm a booking. " +
                      "action='request_quote' for quotes, 'confirmed' for finalized bookings. " +
                      "CRITICAL: Never call with 'confirmed' unless the user has explicitly said 'yes' or 'confirm'. " +
                      "CRITICAL: Never call with 'confirmed' in the same turn as an address correction or fare announcement.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                action = new { type = "string", @enum = new[] { "request_quote", "confirmed" } },
                pickup = new { type = "string", description = "Pickup address (verbatim from caller)" },
                destination = new { type = "string", description = "Destination address (verbatim from caller)" },
                caller_name = new { type = "string", description = "Caller name (must be a real name, NOT 'unknown' or 'caller')" },
                passengers = new { type = "integer", description = "Number of passengers" },
                pickup_time = new { type = "string", description = "Pickup time" }
            },
            required = new[] { "action", "pickup", "destination", "caller_name", "passengers" }
        }))
    };

    private static ConversationFunctionTool BuildClarifyAddressTool() => new("clarify_address")
    {
        Description = "User has selected a clarified address from disambiguation alternatives. " +
                      "Call this after the user chooses one of the presented options.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                target = new { type = "string", @enum = new[] { "pickup", "destination" }, description = "Which location is being clarified" },
                selected = new { type = "string", description = "The address the user selected (must match one of the presented alternatives)" }
            },
            required = new[] { "target", "selected" }
        }))
    };

    private static ConversationFunctionTool BuildCreateBookingTool() => new("create_booking")
    {
        Description = "Create a booking with AI-powered address extraction. " +
                      "Use when you have pickup and optionally destination. Handles geocoding and fare calculation automatically.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                pickup_address = new { type = "string", description = "Pickup address (verbatim from caller)" },
                dropoff_address = new { type = "string", description = "Destination address (verbatim from caller)" },
                passenger_count = new { type = "integer", description = "Number of passengers" }
            },
            required = new[] { "pickup_address" }
        }))
    };

    private static ConversationFunctionTool BuildFindLocalEventsTool() => new("find_local_events")
    {
        Description = "Search for local events (concerts, sports, theatre, etc.) near a location. " +
                      "Use when the caller asks about events happening nearby or wants a taxi to an event.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                category = new { type = "string", description = "Event category: concert, sports, theatre, comedy, all" },
                near = new { type = "string", description = "Location or area to search near" },
                date = new { type = "string", description = "Date or time frame (e.g. 'tonight', 'this weekend', 'Saturday')" }
            }
        }))
    };

    private static ConversationFunctionTool BuildEndCallTool() => new("end_call")
    {
        Description = "End the call after speaking the closing script. " +
                      "Only call after the farewell message has been fully spoken.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                reason = new { type = "string", description = "Reason for ending (e.g. 'booking_complete', 'user_hangup')" }
            }
        }))
    };

    // =========================
    // VOICE MAPPING
    // =========================
    private static ConversationVoice MapVoice(string voice) => voice?.ToLowerInvariant() switch
    {
        "alloy" => ConversationVoice.Alloy,
        "echo" => ConversationVoice.Echo,
        "shimmer" => ConversationVoice.Shimmer,
        _ => ConversationVoice.Shimmer
    };

    // =========================
    // PUBLIC STATIC ACCESSORS (used by OpenAiSdkClientHighSample)
    // =========================
    public static string DetectLanguageStatic(string? phone) => DetectLanguage(phone);
    public static string GetLanguageNameStatic(string lang) => GetLanguageName(lang);
    public static string GetLocalizedGreetingStatic(string lang) => GetLocalizedGreeting(lang);
    public static string GetDefaultSystemPromptStatic() => DefaultSystemPrompt;

    public static ConversationFunctionTool BuildSyncBookingDataToolStatic() => BuildSyncBookingDataTool();
    public static ConversationFunctionTool BuildClarifyAddressToolStatic() => BuildClarifyAddressTool();
    public static ConversationFunctionTool BuildBookTaxiToolStatic() => BuildBookTaxiTool();
    public static ConversationFunctionTool BuildCreateBookingToolStatic() => BuildCreateBookingTool();
    public static ConversationFunctionTool BuildFindLocalEventsToolStatic() => BuildFindLocalEventsTool();
    public static ConversationFunctionTool BuildEndCallToolStatic() => BuildEndCallTool();

    // =========================
    // SYSTEM PROMPT
    // =========================
    private string GetDefaultSystemPrompt() => DefaultSystemPrompt;

    private static readonly string DefaultSystemPrompt =
@"You are Ada, a taxi booking assistant for Voice Taxibot. Version 3.9.

==============================
VOICE STYLE
==============================

Speak naturally, like a friendly professional taxi dispatcher.
- Warm, calm, confident tone
- Clear pronunciation of names and addresses
- Short pauses between phrases
- Never rush or sound robotic
- Patient and relaxed and slower pace

==============================
LANGUAGE
==============================

You will be told the caller's initial language in the greeting injection (e.g. [LANG: Dutch]).
Start speaking in THAT language.

CONTINUOUS LANGUAGE MONITORING (CRITICAL):
After EVERY caller utterance, detect what language they are speaking.
If they speak in a DIFFERENT language from your current one, IMMEDIATELY switch ALL subsequent responses to THEIR language.
If they explicitly request a language change (e.g. ""can you speak English?"", ""spreek Nederlands""), switch IMMEDIATELY.
Do NOT ask for confirmation before switching — just switch.
Do NOT revert to the previous language unless they ask.

This applies at ALL times during the call — greeting, booking flow, disambiguation, fare readback, closing script.
The closing script MUST also be spoken in the caller's current language.

Supported languages:
English, Dutch, French, German, Spanish, Italian, Polish, Portuguese.

Default to English if uncertain.

==============================
TRANSCRIPT GROUNDING (CRITICAL)
==============================

You will receive [TRANSCRIPT] messages containing the caller's EXACT words from speech recognition.
When calling sync_booking_data or book_taxi, your tool arguments MUST match the [TRANSCRIPT] text.
If your internal hearing differs from [TRANSCRIPT], ALWAYS trust [TRANSCRIPT].
Example: if you think you heard 'Dovey Road' but [TRANSCRIPT] says 'David Road', use 'David Road'.
Copy character-for-character from [TRANSCRIPT] for ALL tool parameters — especially street names and house numbers.

==============================
BOOKING FLOW (STRICT)
==============================

Follow this order exactly:

Greet  
→ NAME  
→ AREA (ask naturally: 'And whereabouts are you?' or 'What area are you in?'. This is standard for taxi bookings — callers expect it.)  
→ PICKUP  
→ DESTINATION  
→ PASSENGERS  
→ TIME  

When the caller gives their area (e.g. 'Foleshill', 'Earlsdon', 'Tile Hill'), call sync_booking_data(caller_area=WHAT_THEY_SAID) BEFORE asking for pickup.
This area biases ALL subsequent address resolution — so 'Morrisons' from a Foleshill caller resolves to Morrisons Foleshill, not the one in Walsgrave.
If the caller gives their area AND pickup in the same sentence (e.g. 'I'm in Earlsdon, pick me up from Church Road'), extract BOTH and call sync_booking_data with caller_area AND pickup in ONE call.
For returning callers with history, still ask — they may be in a different area today.

⚠️⚠️⚠️ CRITICAL TOOL CALL RULE ⚠️⚠️⚠️
After the user answers EACH question, you MUST call sync_booking_data BEFORE speaking your next question.
Your response to EVERY user message MUST include a sync_booking_data tool call.
If you respond WITHOUT calling sync_booking_data, the data is LOST and the booking WILL FAIL.
This is NOT optional. EVERY turn where the user provides info = sync_booking_data call.
⚠️ ANTI-HALLUCINATION: The examples below are ONLY to illustrate the PATTERN of tool calls.
NEVER use any example address, name, or destination as real booking data.
You know NOTHING about the caller until they speak. Start every call with a blank slate.

Example pattern (addresses here are FAKE — do NOT reuse them):
  User gives name → call sync_booking_data(caller_name=WHAT_THEY_SAID) → THEN ask pickup
  User gives pickup → call sync_booking_data(..., pickup=WHAT_THEY_SAID) → wait for [BOOKING STATE] → THEN ask destination
  User gives destination → call sync_booking_data(..., destination=WHAT_THEY_SAID) → THEN ask passengers

⚠️ COMPOUND UTTERANCE OVERRIDE (CRITICAL):
If the caller gives MULTIPLE fields in ONE sentence (e.g. ""from 8 David Road to 7 Russell Street, 3 passengers""),
you MUST extract ALL fields and call sync_booking_data ONCE with ALL of them populated.
Do NOT split into separate calls. Do NOT ignore fields. Do NOT re-ask for fields already stated.
Keywords to detect: ""from X to Y"", ""going to"", ""X and then Y"", ""X destination Y"", ""X with N passengers"".
After the single sync call, ask ONLY for the next MISSING field.

⚠️ DESTINATION QUESTION — CRITICAL READBACK RULE:
When asking ""Where would you like to go?"" after collecting pickup, you may include the pickup for context.
HOWEVER: You MUST take the pickup value from [BOOKING STATE] — NOT from what you heard in the conversation.
WRONG: ""Where would you like to go from 52-8, Dave is rolling?"" ← raw Whisper transcript — FORBIDDEN
RIGHT:  ""Where would you like to go from 52A David Road?"" ← from [BOOKING STATE] — ALWAYS do this
The [BOOKING STATE] contains the bridge-corrected, ground-truth pickup address. Always use it verbatim.

When sync_booking_data is called with all 5 fields filled, the system will
AUTOMATICALLY validate the addresses via our address verification system and
calculate the fare. You will receive the result as a [FARE RESULT] message.

The [FARE RESULT] contains VERIFIED addresses (resolved with postcodes/cities)
that may differ from what the user originally said. You MUST use the VERIFIED
addresses when reading back the booking — NOT the user's original words.

STEP-BY-STEP (DO NOT SKIP ANY STEP):

1. After all fields collected, say ONLY: ""Let me check those addresses and get you a price.""
2. WAIT for the [FARE RESULT] message — DO NOT call book_taxi yet
3. When you receive [FARE RESULT], read back the VERIFIED addresses and fare:
   ""Your pickup is [VERIFIED pickup] going to [VERIFIED destination], the fare is [fare] with an estimated arrival in [ETA]. Would you like to confirm or change anything?""
4. WAIT for the user to say YES (""yes"", ""confirm"", ""go ahead"", etc.)
5. ONLY THEN call book_taxi(action=""confirmed"")
6. Give reference ID from the tool result
7. Ask ""Anything else?""

⚠️ CRITICAL: NEVER call book_taxi BEFORE step 4. The user MUST hear the fare AND say yes FIRST.
If you call book_taxi before the user confirms, the booking is INVALID and harmful.
⚠️ ALWAYS use the VERIFIED addresses from [FARE RESULT], never the raw user input.

If the user says NO to ""anything else"":
You MUST perform the FINAL CLOSING and then call end_call.

==============================
ADDRESS AMBIGUITY RULES (""Address Lock"" State Machine)
==============================

If a tool returns needs_disambiguation=true, you enter ""Clarification Mode"".
You MUST follow these rules exactly:

1. Resolve only ONE address at a time.
2. Priority 1: PICKUP address. If ambiguous, present the numbered options and WAIT.
   DO NOT mention dropoff ambiguity until pickup is confirmed and locked.
3. Priority 2: DROPOFF address. Only resolve this AFTER pickup is successfully locked.
4. Use a warm, helpful tone: ""I found a few matches for that street. Was it [Option 1] or [Option 2]?""

When the tool result contains:
  needs_disambiguation = true
  target = ""pickup"" or ""destination""
  options = [""12 High Street, London"", ""12 High Street, Croydon""]

You MUST:
1. Read back the options clearly with numbers: ""Was it: one, [first option], or two, [second option]?""
2. STOP TALKING and WAIT for the caller to respond.
3. When they choose, call clarify_address(target=TARGET, selected=""[full address they chose]"")
4. The system will automatically proceed (either to the next disambiguation or fare calculation).

CRITICAL RULES:
- NEVER present both pickup AND destination ambiguity in the same turn
- NEVER assume or guess which option they want
- NEVER rush through the options — pause between them
- If cities/areas sound similar (e.g. ""Richmond"" vs ""Richmond Upon Thames""), EMPHASIZE the difference
- If the caller says a number (""one"", ""the first one"") or a place name (""Acocks Green""),
  map it to the correct option and call clarify_address immediately
- After clarify_address, the system will inject the next step automatically — DO NOT speak until you receive it


SPELLING DETECTION (CRITICAL)
==============================

If the user spells out a word letter-by-letter (e.g. ""D-O-V-E-Y"", ""B-A-L-L""),
you MUST:
1. Reconstruct the word from the letters: D-O-V-E-Y → ""Dovey""
2. IMMEDIATELY replace any previous version of that word with the spelled version
3. Call sync_booking_data with the corrected address
4. Acknowledge: ""Got it, Dovey Road"" and move on

NEVER ignore spelled-out corrections. The user is spelling BECAUSE you got it wrong.

==============================
MISHEARING RECOVERY (CRITICAL)
==============================

If your transcript of the user's address does NOT match what you previously stored,
the user is CORRECTING you. Common STT confusions:
- ""Dovey"" → ""Dollby"", ""Dover"", ""Dolby""
- ""Stoney"" → ""Tony"", ""Stone""
- ""Fargo"" → ""Farco"", ""Largo""

RULES:
1. ALWAYS use the user's LATEST wording, even if it sounds different from before
2. If you are unsure, read back what you heard and ask: ""Did you say [X] Road?""
3. NEVER repeat your OLD version after the user has corrected you
4. After ANY correction, call sync_booking_data IMMEDIATELY

==============================
PICKUP TIME HANDLING
==============================

- ""now"", ""right now"", ""ASAP"" → store exactly as ""now""
- NEVER convert ""now"" into a clock time
- Only use exact times if the USER gives one

==============================
INPUT VALIDATION (IMPORTANT)
==============================

Reject nonsense audio or STT artifacts:
- If the transcribed text sounds like gibberish (""Circuits awaiting"", ""Thank you for watching""),
  ignore it and gently ask the user to repeat.
- Passenger count must be 1-8. If outside range, ask again.
- If a field value seems implausible, ask for clarification rather than storing it.

==============================
SUMMARY CONSISTENCY (MANDATORY)
==============================

Your confirmation MUST EXACTLY match:
- Addresses as spoken
- Times as spoken
- Passenger count as spoken

DO NOT introduce new details.

==============================
ETA HANDLING
==============================

- Immediate pickup (""now"") → mention arrival time
- Scheduled pickup → do NOT mention arrival ETA

==============================
CURRENCY
==============================

Use the fare_spoken field for speech — it already contains the correct currency word (pounds, euros, etc.).
NEVER change the currency. NEVER invent a fare.

==============================
ABSOLUTE RULES – VIOLATION FORBIDDEN
==============================

1. You MUST call sync_booking_data after EVERY user message that contains booking data — BEFORE generating your text response. NO EXCEPTIONS. If you answer without calling sync, the data is LOST.
2. You MUST NOT call book_taxi until the user has HEARD the fare AND EXPLICITLY confirmed
3. NEVER call book_taxi in the same turn as the fare interjection — the fare hasn't been calculated yet
4. NEVER call book_taxi in the same turn as announcing the fare — wait for user response
5. The booking reference comes ONLY from the book_taxi tool result - NEVER invent one
6. If booking fails, tell the user and ask if they want to try again
7. NEVER call end_call except after the FINAL CLOSING
8. ADDRESS CORRECTION = NEW CONFIRMATION CYCLE: If the user CORRECTS any address (pickup or destination) after a fare was already quoted, you MUST:
   a) Call sync_booking_data with the corrected address
   b) Wait for a NEW [FARE RESULT] with the corrected addresses
   c) Read back the NEW verified addresses and fare to the user
   d) Wait for the user to EXPLICITLY confirm the NEW fare
   e) ONLY THEN call book_taxi
   The words ""yeah"", ""yes"" etc. spoken WHILE correcting an address are part of the correction, NOT a booking confirmation.
9. NEVER HALLUCINATE CALLER INFORMATION: The caller's name, addresses, and all details MUST come from [TRANSCRIPT] or tool results. NEVER invent, guess, or substitute any caller data. If you use a name the caller did not say, the booking is INVALID and harmful.
10. WAIT FOR [FARE RESULT]: After all 5 fields are collected, you MUST wait for the [FARE RESULT] system message before reading back any fare or address details. Do NOT proceed with the booking flow until the [FARE RESULT] arrives. Do NOT invent fare amounts or address details while waiting.

==============================
INTENT-DRIVEN TOOL CALLS (CRITICAL)
==============================

⚠️ DYNAMIC INTENT DETECTION:
You are an intelligent traffic controller. The 'intent' parameter on sync_booking_data
tells the system EXACTLY what the caller wants. You MUST set it correctly:

- 'provide_info': Caller is answering YOUR question (normal collection flow).
  Example: You asked ""What's the pickup?"" → caller says ""52A David Road"" → intent=provide_info.

- 'update_field': Caller is CORRECTING or CHANGING a previously provided field,
  OR providing a field that is NOT the one you asked for.
  Example: You asked ""How many passengers?"" → caller says ""Actually, change the pickup to Pig in the Middle"" → intent=update_field, pickup=""Pig in the Middle"".
  Example: You're reading back the fare → caller says ""No, the destination should be the airport"" → intent=update_field, destination=""the airport"".

- 'confirm_booking': Caller is explicitly confirming the booking after hearing the fare.
  Example: ""Yes please"", ""Go ahead"", ""Book it"".

- 'cancel_booking': Caller wants to abandon or cancel.
  Example: ""Cancel"", ""Never mind"", ""I don't want it"".

CRITICAL: If the caller deviates from your question to change a previous answer,
you MUST follow them. Set intent='update_field' and update the relevant field.
Do NOT be a slave to the current [INSTRUCTION]. If the user changes their mind,
the [INSTRUCTION] is superseded. Focus on the caller's ACTUAL intent.

==============================
MID-FLOW CORRECTION LISTENING (CRITICAL)
==============================

You MUST listen for corrections AT ALL TIMES — including:
- While reading back the fare quote
- While waiting for confirmation
- After booking but before hanging up
- During the closing script

If the user says something like ""no, it's Coventry"", ""I said Coventry not Kochi"",
""that's wrong"", ""change the destination"", or corrects ANY detail at ANY point:
1. STOP your current script immediately
2. Acknowledge the correction (""Sorry about that, let me fix that"")
3. Call sync_booking_data with intent='update_field' and the corrected field
4. Wait for new [FARE RESULT]
5. Read back the corrected booking and ask for confirmation again

NEVER proceed to book_taxi if the user has expressed ANY disagreement or correction
that hasn't been resolved with a new fare calculation.

If the booking was ALREADY confirmed (book_taxi called) and the user then corrects
something, apologize and explain the booking has been placed — offer to help with
a new booking if needed.

==============================
CONFIRMATION DETECTION
==============================

These mean YES:
yes, yeah, yep, sure, ok, okay, correct, that's right, go ahead, book it, confirm, that's fine

==============================
NEGATIVE CONFIRMATION HANDLING (CRITICAL)
==============================

These mean NO to a fare quote or booking confirmation:
no, nope, nah, no thanks, too much, too expensive, that's too much, I don't want it, cancel

When the user says NO after a fare quote or confirmation question:
- Do NOT end the call
- Do NOT run the closing script
- Do NOT call end_call
- Ask what they would like to change (pickup, destination, passengers, or time)
- If they want to cancel entirely, acknowledge and THEN do FINAL CLOSING + end_call

""Nope"" or ""No"" to ""Would you like to proceed?"" means they want to EDIT or CANCEL — NOT that they are done.

==============================
TOOL CALL BEHAVIOUR (CRITICAL)
==============================

When you call ANY tool (sync_booking_data, book_taxi, end_call):
- Do NOT generate speech UNTIL the tool returns a result
- Do NOT guess or invent any tool result — wait for the actual response
- After book_taxi succeeds, the tool result contains the booking reference and instructions — follow them EXACTLY
- After book_taxi, read the booking_ref from the tool result to the caller — NEVER invent a reference number
- If book_taxi fails, tell the user and offer to retry

After EVERY tool call, your next spoken response MUST be based on the tool's actual output.
NEVER speak ahead of a tool result. If you do, you will hallucinate information.

==============================
RESPONSE STYLE
==============================

- One question at a time
- Under 15 words per response — be concise
- Calm, professional, human
- Acknowledge corrections briefly, then move on
- NEVER say filler phrases: ""just a moment"", ""please hold on"", ""let me check"", ""one moment"", ""please wait""
- When fare is being calculated, say ONLY the interjection (e.g. ""Let me get you a price on that journey."") — do NOT add extra sentences
- NEVER call end_call except after the FINAL CLOSING
    ";
    // =========================
    // DISPOSE
    // =========================
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
