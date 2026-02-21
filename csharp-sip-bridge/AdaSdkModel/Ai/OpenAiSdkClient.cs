// Last updated: 2026-02-21 (v2.9 — Anti-repetition in greeting, patience after greeting)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AdaSdkModel.Config;
using Microsoft.Extensions.Logging;
using OpenAI.RealtimeConversation;
using System.ClientModel;

#pragma warning disable OPENAI002 // Experimental Realtime API

namespace AdaSdkModel.Ai;

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
/// Version 2.0 — AdaSdkModel (OpenAI .NET SDK beta)
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

    /// <summary>
    /// Optional callback that provides stage-aware context for the no-reply watchdog.
    /// When set, the watchdog will inject contextual re-prompts instead of generic "[SILENCE]".
    /// </summary>
    public Func<string?>? NoReplyContextProvider { get; set; }

    // Caller state
    private string _callerId = "";

    // =========================
    // CONSTANTS
    // =========================
    private const int MAX_NO_REPLY_PROMPTS = 3;
    private const int NO_REPLY_TIMEOUT_MS = 8_000;
    private const int CONFIRMATION_TIMEOUT_MS = 15_000;
    private const int DISAMBIGUATION_TIMEOUT_MS = 30_000;
    private const int ECHO_GUARD_MS = 200;

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
    public event Action? OnGoodbyeWithoutBooking;

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
        _systemPrompt = systemPrompt ?? GetDefaultSystemPromptStatic();
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
                    Model = "whisper-1",
                },
                // SDK 2.1.0-beta.4 doesn't support semantic_vad natively.
                // Simulate patient vs responsive modes using server_vad with different parameters.
                TurnDetectionOptions = _useSemanticVad
                    ? ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                        detectionThreshold: 0.25f,
                        prefixPaddingDuration: TimeSpan.FromMilliseconds(500),
                        silenceDuration: TimeSpan.FromMilliseconds(1400))  // Patient: 1.4s silence for full address utterances
                    : ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                        detectionThreshold: 0.2f,
                        prefixPaddingDuration: TimeSpan.FromMilliseconds(400),
                        silenceDuration: TimeSpan.FromMilliseconds(600))   // Responsive: quick for short answers
            };

            options.Tools.Add(BuildSyncBookingDataToolStatic());
            options.Tools.Add(BuildClarifyAddressToolStatic());
            options.Tools.Add(BuildBookTaxiToolStatic());
            options.Tools.Add(BuildCreateBookingToolStatic());
            options.Tools.Add(BuildFindLocalEventsToolStatic());
            options.Tools.Add(BuildCancelBookingToolStatic());
            options.Tools.Add(BuildCheckBookingStatusToolStatic());
            options.Tools.Add(BuildEndCallToolStatic());

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

        // Cancel any pending no-reply watchdog — we're about to trigger a new response
        Interlocked.Increment(ref _noReplyWatchdogId);

        try
        {
            bool isCritical = message.Contains("[FARE RESULT]") || message.Contains("DISAMBIGUATION") || message.Contains("[FARE SANITY ALERT]") || message.Contains("[ADDRESS DISCREPANCY]");
            if (isCritical && Volatile.Read(ref _responseActive) == 1)
            {
                Log("🛑 Cancelling active response before critical injection");
                await _session!.CancelResponseAsync();
                Interlocked.Exchange(ref _responseActive, 0);
                // Wait for the response to actually clear (poll with timeout instead of blind delay)
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(50);
                    if (Volatile.Read(ref _responseActive) == 0) break;
                }
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

    /// <summary>
    /// Inject a system-level message into the conversation WITHOUT triggering a response.
    /// Used for booking state injection so Ada has ground truth context.
    /// </summary>
    public async Task InjectSystemMessageAsync(string message)
    {
        if (!IsConnected) return;

        try
        {
            Log($"📋 State inject: {(message.Length > 80 ? message[..80] + "..." : message)}");
            await _session!.AddItemAsync(
                ConversationItem.CreateUserMessage(new[] { ConversationContentPart.CreateInputTextPart(message) }));
            // NOTE: No StartResponseAsync() — this is a silent context injection
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
                        detectionThreshold: 0.25f,
                        prefixPaddingDuration: TimeSpan.FromMilliseconds(500),
                        silenceDuration: TimeSpan.FromMilliseconds(1400))  // Patient mode: 1.4s for full addresses
                    : ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                        detectionThreshold: 0.2f,
                        prefixPaddingDuration: TimeSpan.FromMilliseconds(400),
                        silenceDuration: TimeSpan.FromMilliseconds(600))   // Responsive mode
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
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10_000);
                    if (Volatile.Read(ref _responseActive) == 0 &&
                        Volatile.Read(ref _toolInFlight) == 0 &&
                        Volatile.Read(ref _callEnded) == 0)
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

                // Capture session locally — DisconnectAsync() may null _session on another thread
                var session = _session;
                if (session == null)
                {
                    Log($"⚠️ Session gone before tool result could be sent ({toolName}) — ignoring");
                    return;
                }

                // Beta SDK: create function output item and add it
                var outputItem = ConversationItem.CreateFunctionCallOutput(
                    callId: itemFinished.FunctionCallId!,
                    output: resultJson);
                await session.AddItemAsync(outputItem);

                // Suppress response if fare calculation is in progress — the fare injection will trigger it
                if (resultJson.Contains("\"fare_calculating\":true") || resultJson.Contains("wait SILENTLY"))
                {
                    Log("🔇 Suppressing response — fare calculation in progress, [FARE RESULT] will trigger response");
                }
                else if (Volatile.Read(ref _responseActive) == 1)
                {
                    Interlocked.Exchange(ref _deferredResponsePending, 1);
                    Log("⏳ Response still active after tool — deferring");
                }
                else
                {
                    await session.StartResponseAsync();
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
            var lang = DetectLanguageStatic(_callerId);
            var langName = GetLanguageNameStatic(lang);
            var localizedGreeting = GetLocalizedGreetingStatic(lang);

            string greeting;
            if (!string.IsNullOrWhiteSpace(callerName))
            {
                greeting = $"[SYSTEM] [LANG: {langName}] A returning caller named {callerName} has connected (ID: {_callerId}). " +
                           $"Greet them BY NAME in {langName}. You MUST say EXACTLY: \"Welcome to 247 Radio Carz. Hello {callerName}, my name is Ada and I am here to help you with your booking today. Where would you like to be picked up from?\" " +
                           $"⚠️ Do NOT say \"Where can I take you\" or any variation. ALWAYS ask for PICKUP LOCATION first. " +
                           $"⚠️ CRITICAL: After this greeting, WAIT PATIENTLY for the caller to respond. Do NOT repeat the pickup question or re-prompt. " +
                           $"If the caller says 'hello' or their name, simply acknowledge briefly and wait — do NOT ask for pickup again.";
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

    /// <summary>
    /// Send greeting that acknowledges the caller has an active booking.
    /// </summary>
    public async Task SendGreetingWithBookingAsync(string? callerName, AdaSdkModel.Core.BookingState booking)
    {
        if (Interlocked.Exchange(ref _greetingSent, 1) == 1) return;
        if (_session == null) return;

        try
        {
            var lang = DetectLanguageStatic(_callerId);
            var langName = GetLanguageNameStatic(lang);

            var pickup = booking.Pickup ?? "unknown";
            var destination = booking.Destination ?? "unknown";
            var bookingRef = booking.BookingRef ?? booking.ExistingBookingId ?? "unknown";

            var greeting = $"[SYSTEM] [LANG: {langName}] A returning caller named {callerName ?? "unknown"} has connected (ID: {_callerId}). " +
                $"They have an ACTIVE BOOKING (Ref: {bookingRef}) from {pickup} to {destination}. " +
                $"Greet them BY NAME, then tell them about their existing booking. Say something like: " +
                $"\"Welcome back {callerName ?? ""}. I can see you have an active booking from {pickup} to {destination}. " +
                $"Would you like to cancel it, make any changes to it, or check the status of your driver?\" " +
                $"Wait for their response before proceeding.";

            await _session.AddItemAsync(
                ConversationItem.CreateUserMessage(new[] { ConversationContentPart.CreateInputTextPart(greeting) }));
            await _session.StartResponseAsync();
            Log($"📢 Greeting with active booking sent (language: {langName})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending greeting with booking");
        }
    }

    // =========================
    // LANGUAGE DETECTION
    // =========================
    public static string DetectLanguageStatic(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";
        var clean = phone.Replace(" ", "").Replace("-", "");
        // Normalize: if no + prefix but starts with country code digits, add +
        if (!clean.StartsWith("+") && !clean.StartsWith("0"))
            clean = "+" + clean;
        if (clean.StartsWith("+31") || clean.StartsWith("0031") || clean.StartsWith("06"))
            return "nl";
        if (clean.StartsWith("+32") || clean.StartsWith("0032"))
            return "nl"; // Belgium (Dutch/French — default Dutch)
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

    public static string GetLanguageNameStatic(string lang) => lang switch
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

    public static string GetLocalizedGreetingStatic(string lang) => lang switch
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
            if (Volatile.Read(ref _ignoreUserAudio) == 1) return;  // Goodbye already said — don't re-prompt
            if (!IsConnected) return;

            var sinceAdaFinished = NowMs() - Volatile.Read(ref _lastAdaFinishedAt);
            if (sinceAdaFinished < ECHO_GUARD_MS) return;

            Interlocked.Increment(ref _noReplyCount);
            Log($"⏰ No-reply watchdog triggered (attempt {Volatile.Read(ref _noReplyCount)}/{MAX_NO_REPLY_PROMPTS})");

            try
            {
                // Use stage-aware context if available, otherwise generic re-prompt
                var context = NoReplyContextProvider?.Invoke();
                var message = !string.IsNullOrWhiteSpace(context)
                    ? $"[SILENCE] {context}"
                    : "[SILENCE] Hello? Are you still there?";
                await _session!.AddItemAsync(
                    ConversationItem.CreateUserMessage(new[] { ConversationContentPart.CreateInputTextPart(message) }));
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
            OnGoodbyeWithoutBooking?.Invoke();
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
    // TOOL DEFINITIONS (public static for reuse by HighSample variant)
    // =========================
    public static ConversationFunctionTool BuildSyncBookingDataToolStatic() => new("sync_booking_data")
    {
        Description = "MANDATORY: Persist booking data as collected from the caller. " +
                      "Must be called BEFORE generating any text response when user provides or amends booking details. " +
                      "CHANGE DETECTION: If the caller corrects ANY previously provided detail, you MUST call this tool " +
                      "IMMEDIATELY with the corrected value AND explain what changed in the 'interpretation' field.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                caller_name = new { type = "string", description = "Caller's name" },
                pickup = new { type = "string", description = "Pickup address (verbatim from caller)" },
                destination = new { type = "string", description = "Destination address (verbatim from caller)" },
                passengers = new { type = "integer", description = "Number of passengers" },
                pickup_time = new { type = "string", description = "Pickup time in YYYY-MM-DD HH:MM format (24h clock) or 'ASAP'. Use REFERENCE_DATETIME from system prompt to resolve relative times like 'tomorrow', 'in 30 minutes', '5pm'. NEVER pass raw phrases." },
                vehicle_type = new { type = "string", @enum = new[] { "Saloon", "Estate", "MPV", "Minibus" }, description = "Vehicle type. Auto-recommended based on passengers (1-4=Saloon, 5-6=Estate, 7+=Minibus). Only set if caller explicitly requests a specific vehicle type (e.g. 'send an MPV')." },
                interpretation = new { type = "string", description = "Brief explanation of what you understood from the caller's speech. If this is a CORRECTION, explain what changed and why (e.g. 'User corrected pickup from Parkhouse Street to Far Gosford Street — venue name is Sweet Spot'). This helps the system track your understanding." },
                special_instructions = new { type = "string", description = "Any special requests, notes, or instructions the caller wants to add to the booking (e.g. flight number, wheelchair access, child seat, meet at arrivals, extra luggage). Only set when the caller explicitly provides special instructions." }
            }
        }))
    };

    public static ConversationFunctionTool BuildBookTaxiToolStatic() => new("book_taxi")
    {
        Description = "Request a fare quote or confirm a booking. " +
                      "action='request_quote' for quotes, 'confirmed' for finalized bookings. " +
                      "CRITICAL: Never call with 'confirmed' unless the user has explicitly said 'yes' or 'confirm'. " +
                      "CRITICAL: Never call with 'confirmed' in the same turn as an address correction or fare announcement. " +
                      "Include payment_preference='card' if the caller chose fixed price by card, or 'meter' if paying on the day.",
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
                pickup_time = new { type = "string", description = "Pickup time in YYYY-MM-DD HH:MM format (24h clock) or 'ASAP'. NEVER pass raw phrases." },
                payment_preference = new { type = "string", @enum = new[] { "card", "meter" }, description = "Payment method chosen by caller: 'card' = fixed price paid by SumUp payment link, 'meter' = pay driver on the day" }
            },
            required = new[] { "action", "pickup", "destination", "caller_name", "passengers" }
        }))
    };

    public static ConversationFunctionTool BuildClarifyAddressToolStatic() => new("clarify_address")
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

    public static ConversationFunctionTool BuildCreateBookingToolStatic() => new("create_booking")
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

    public static ConversationFunctionTool BuildFindLocalEventsToolStatic() => new("find_local_events")
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

    public static ConversationFunctionTool BuildEndCallToolStatic() => new("end_call")
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

    public static ConversationFunctionTool BuildCancelBookingToolStatic() => new("cancel_booking")
    {
        Description = "Cancel an existing active booking. Use when the caller wants to cancel their current booking. " +
                      "The booking_id will be populated from the active booking loaded at session start.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                reason = new { type = "string", description = "Reason for cancellation (e.g. 'caller_request', 'plans_changed')" }
            }
        }))
    };

    public static ConversationFunctionTool BuildCheckBookingStatusToolStatic() => new("check_booking_status")
    {
        Description = "Check the status of the caller's active booking. Use when the caller asks where their driver is, " +
                      "how long until arrival, or any status-related question about their existing booking.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                booking_id = new { type = "string", description = "Booking ID to check (optional — will use active booking if not provided)" }
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
        "fable" => ConversationVoice.Fable,
        "onyx" => ConversationVoice.Onyx,
        "nova" => ConversationVoice.Nova,
        "shimmer" => ConversationVoice.Shimmer,
        _ => ConversationVoice.Shimmer
    };

    // =========================
    // SYSTEM PROMPT
    // =========================
    public static string GetDefaultSystemPromptStatic()
    {
        // Inject current London time as reference anchor for AI time parsing
        var londonTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var londonNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, londonTz);
        var referenceDateTime = londonNow.ToString("dddd, dd MMMM yyyy HH:mm");

        return $@"You are Ada, a taxi booking assistant for Voice Taxibot. Version 3.10.

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

Follow this order exactly — ALWAYS start with PICKUP (EU market standard):

Greet  
→ NAME  
→ PICKUP (ask: ""Where would you like to be picked up from?"")  
→ DESTINATION  
→ PASSENGERS  
→ TIME  

⚠️ NEVER ask ""Where do you want to go?"" or ""Where can I take you?"" as the first question.
ALWAYS ask for the PICKUP LOCATION first. This is the European market convention.

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
NEVER collect multiple fields without calling sync_booking_data between each.

⚠️ ADDRESS READBACK — CRITICAL RULE (applies to ALL speech, not just destination question):
Whenever you say ANY address out loud (pickup, destination, fare readback, confirmation, closing script),
you MUST take the value from [BOOKING STATE] — NEVER from what you heard or your internal memory.
[BOOKING STATE] is the bridge-corrected ground truth. Your hearing may be wrong; [BOOKING STATE] is always right.
WRONG: ""Where would you like to go from 52-8, Dave is rolling?"" ← raw Whisper transcript — FORBIDDEN
RIGHT:  ""Where would you like to go from 52A David Road?"" ← from [BOOKING STATE] — ALWAYS do this
This rule applies EVERY TIME you mention an address, in EVERY phase of the call.

⚠️ COMPOUND UTTERANCE SPLITTING (CRITICAL):
Callers often give MULTIPLE pieces of information in ONE sentence.
Example: ""52A David Road, going to Coventry"" — this contains BOTH pickup AND destination.
You MUST split these correctly:
  - pickup = ""52A David Road""
  - destination = ""Coventry""
NEVER store ""going to [place]"" as part of the pickup address.
If the caller says ""from X to Y"" or ""X going to Y"" or ""at X, destination Y"":
  - The part BEFORE ""going to""/""to""/""destination"" is the PICKUP
  - The part AFTER is the DESTINATION
Call sync_booking_data with BOTH fields populated in the same call.

When sync_booking_data is called with all 5 fields filled, the system will
AUTOMATICALLY validate the addresses via our address verification system and
calculate the fare. You will receive the result as a [FARE RESULT] message.

The [FARE RESULT] contains VERIFIED addresses (resolved with postcodes/cities)
that may differ from what the user originally said. You MUST use the VERIFIED
addresses when reading back the booking — NOT the user's original words.

STEP-BY-STEP (DO NOT SKIP ANY STEP):

1. After all fields collected, say ONLY: ""Let me check those addresses and get you a price.""
2. WAIT SILENTLY for the [FARE RESULT] message — DO NOT call book_taxi, DO NOT speak, DO NOT ask for confirmation.
3. When you receive [FARE RESULT], read back the VERIFIED addresses (City, Street, Number format — NO postal codes) and fare, then offer the fixed price option:
   For IMMEDIATE bookings (ASAP/now): ""Your pickup is [VERIFIED pickup] going to [VERIFIED destination], the fare is [fare] with an estimated arrival in [ETA]. We offer a fixed price of [fare] — I can send you a payment link to guarantee that price now, or you can pay by meter on the day. Which would you prefer?""
   For SCHEDULED bookings (future time): ""Your pickup is [VERIFIED pickup] going to [VERIFIED destination], the fare is [fare]. We offer a fixed price of [fare] — I can send you a payment link to guarantee that price now, or you can pay by meter on the day. Which would you prefer?"" (NO ETA for scheduled bookings)

FIXED PRICE / METER RULES:
- ALWAYS present both options after the fare — fixed price (pay by card/link) and pay by meter.
- If the caller chooses FIXED PRICE / CARD: say ""Great, I'll send you a secure payment link by WhatsApp to lock in that price."" then call book_taxi(action=""confirmed"", payment_preference=""card"").
- If the caller chooses METER / PAY ON THE DAY: say ""No problem, you'll pay the driver on the day."" then call book_taxi(action=""confirmed"", payment_preference=""meter"").
- If the caller just says ""yes"", ""book it"", or ""confirm"" without specifying, default to meter: call book_taxi(action=""confirmed"", payment_preference=""meter"").
- NEVER skip this pricing choice step — it MUST be offered every time a fare is presented.
- NEVER tell the caller the payment link URL yourself — the system sends it automatically via WhatsApp after booking.

4. WAIT for the user to respond with their payment preference OR a general confirmation.
5. ONLY THEN call book_taxi(action=""confirmed"")
6. Give reference ID from the tool result
7. Ask: ""Is there anything else you'd like to add to your booking? For example, a flight number, special requests, or any notes for the driver?""
8. If the caller provides special notes (flight number, wheelchair, child seat, meet at arrivals, extra luggage, etc.):
   - Call sync_booking_data(special_instructions=""[what they said]"") to persist them
   - Confirm: ""I've added that to your booking.""
   - Ask: ""Anything else?""
9. If the caller says NO / nothing else / that's it:
   - Say the FINAL CLOSING script and call end_call.

⚠️⚠️⚠️ ABSOLUTE RULE — NO PREMATURE CONFIRMATION ⚠️⚠️⚠️
NEVER ask ""Would you like to confirm?"" or ""Shall I book?"" UNTIL you have received [FARE RESULT].
If you have NOT yet seen a [FARE RESULT] message in this conversation, you are FORBIDDEN from:
  - Asking for confirmation
  - Mentioning a fare amount
  - Saying ""the fare is being calculated""
  - Offering to confirm the booking
Instead, say NOTHING and wait. The system will inject [FARE RESULT] automatically.
This includes DURING and AFTER address disambiguation — clarify_address does NOT mean the fare is ready.
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
- NEVER ask ""Would you like to confirm?"" during disambiguation — the fare is NOT ready yet
- After ALL disambiguations are resolved, WAIT SILENTLY for [FARE RESULT] before speaking


==============================
FINAL CLOSING (MANDATORY – EXACT WORDING)
==============================

⚠️ PREREQUISITE: You may ONLY speak the closing script if book_taxi(action=""confirmed"") has ALREADY been called AND returned a successful result with a booking reference.
If book_taxi has NOT been called yet, you MUST call it FIRST — even if the user says ""yes"", ""confirm"", ""that's fine"", or ""go ahead"".
NEVER say the closing script without a completed book_taxi call. This is a CRITICAL FAILURE that causes lost bookings.

When book_taxi has succeeded, say EXACTLY this sentence and nothing else:

""Thank you for using the TaxiBot system. You will shortly receive your booking confirmation over WhatsApp. Goodbye.""

Then IMMEDIATELY call end_call.

DO NOT:
- Rephrase
- Add extra sentences
- Mention the journey
- Mention prices or addresses
- Say goodbye BEFORE book_taxi has returned successfully

==============================
CRITICAL: FRESH SESSION – NO MEMORY
==============================

THIS IS A NEW CALL. Every call starts with a BLANK SLATE.
- You have NO prior knowledge of this caller's current trip
- NEVER reuse pickup, destination, passengers, or time from a previous call or from [CALLER HISTORY]
- [CALLER HISTORY] is REFERENCE ONLY — it CANNOT be used to pre-fill booking fields
- The user's MOST RECENT wording is always the source of truth
- ALWAYS start by asking for the PICKUP LOCATION (EU market convention)
- If [CALLER HISTORY] shows a name, you may use it for greeting ONLY — all other fields must be collected fresh

==============================
CALLER IDENTITY – ZERO HALLUCINATION (ABSOLUTE)
==============================

You know NOTHING about the caller until THEY tell you their name.
- NEVER guess, invent, or assume a caller's name
- NEVER use a name from a previous call, your training data, or any other source
- The ONLY valid name is what the caller says in THIS call, confirmed by [TRANSCRIPT]
- If the [TRANSCRIPT] says the caller said ""Max"", their name is ""Max"" — not ""Hisham"", not ""Hassan"", not anything else
- If you are unsure what name the caller said, ASK THEM TO REPEAT IT
- NEVER substitute a name that ""sounds similar"" — copy the [TRANSCRIPT] exactly
- This rule applies to ALL caller information: name, phone number, addresses
- Violating this rule is a CRITICAL FAILURE that causes wrong bookings

==============================
DATA COLLECTION (MANDATORY)
==============================

After EVERY user message that provides OR corrects booking data:
- Call sync_booking_data IMMEDIATELY — before generating ANY text response
- Include ALL known fields every time (name, pickup, destination, passengers, time)
- If the user repeats an address differently, THIS IS A CORRECTION
- Store addresses EXACTLY as spoken (verbatim) — NEVER substitute similar-sounding street names (e.g. 'David Road' must NOT become 'Dovey Road')

⚠️ ZERO-TOLERANCE RULE: If you speak your next question WITHOUT having called
sync_booking_data first, the booking data is permanently lost. The bridge has
NO access to your conversation memory. sync_booking_data is your ONLY way to
persist data. Skipping it even once causes a broken booking.

CRITICAL — OUT-OF-ORDER / BATCHED DATA:
Callers often give multiple fields in one turn (e.g. pickup and destination together).
Even if these fields are ahead of the strict sequence:
1. Call sync_booking_data IMMEDIATELY with ALL data the user just provided
2. THEN ask for the next missing field in the flow order
NEVER defer syncing data just because you haven't asked for that field yet.

CRITICAL — ADDRESS GIVEN DURING WRONG COLLECTION STEP:
If the caller provides an address-like value while you are collecting passengers, time, or
any non-address field, assume it is a correction to the LAST address field that was collected.

Rule:
- If DESTINATION has already been collected → it is a DESTINATION correction
- If only PICKUP has been collected (destination still missing) → treat it as the DESTINATION (new entry)
- NEVER overwrite PICKUP with a caller utterance that occurs AFTER destination has been stored,
  unless the caller explicitly says ""my pickup"", ""from"", ""pick me up from"", or similar pickup-specific language.

Example:
  Ada asks: ""How many passengers?""
  Caller says: ""43 Dovey Road""
  → Destination was already stored → treat this as a DESTINATION correction
  → Call sync_booking_data with destination=""43 Dovey Road"" (NOT pickup)

The bridge tracks the real state. Your memory alone does NOT persist data.
If you skip a sync_booking_data call, the booking state will be wrong.

==============================
CHANGE DETECTION & BOOKING STATE AWARENESS (CRITICAL)
==============================

After every sync_booking_data call, you will receive a [BOOKING STATE] message showing
exactly what is currently stored. This is your GROUND TRUTH — it overrides your memory.

STT MISHEARING RECOVERY — HOUSE NUMBER AND STREET NAME:
Speech-to-text (Whisper) often mishears both alphanumeric UK house numbers AND street names.
Examples: ""52-8"" → ""52A"" (house number), ""David Rolfe"" → ""David Road"" (street name).
The bridge automatically corrects house number hyphen artifacts. Street name corrections happen via geocoding at fare time.
YOUR ROLE:
- ALWAYS call sync_booking_data FIRST with the address EXACTLY as spoken (verbatim from Whisper)
- Do NOT pre-correct, normalize, or guess any value before syncing
- Do NOT speak ANY version of the address to the caller before calling sync_booking_data and receiving [BOOKING STATE]
- After sync, use the address from [BOOKING STATE] for ALL readbacks — this is the ground truth

ADDRESS READBACK RULE — CRITICAL:
After sync_booking_data, the [BOOKING STATE] is your ONLY source for readback.
- Use the house number from [BOOKING STATE] (bridge-corrected)
- Use the street name from [BOOKING STATE] (bridge-corrected)
- NEVER use the raw Whisper transcript for readback — it may contain mishearings of BOTH house numbers AND street names
- Whisper may hear ""David Rolfe"" when the caller actually said ""David Road"" — trust [BOOKING STATE], not what you heard
- Whisper may hear ""Dovey Road"" when the caller said ""Dover Road"" — always defer to [BOOKING STATE]

CONFIRMATION QUESTION RULE:
When you receive an address from the caller, you MUST:
1. Call sync_booking_data FIRST (verbatim from Whisper — do NOT speak yet)
2. Wait for the [BOOKING STATE] response
3. THEN confirm using ONLY the address values shown in [BOOKING STATE]
Example: [BOOKING STATE] shows ""Pickup: 52A David Road"" → ask ""Just to confirm — is that 52A David Road?""
NEVER speak an address to the caller before receiving and reading [BOOKING STATE].

CHANGE DETECTION RULES:
1. If the caller says something that DIFFERS from a field in [BOOKING STATE], it is a CORRECTION.
2. You MUST call sync_booking_data IMMEDIATELY with the corrected value.
3. In the 'interpretation' field, explain WHAT changed and WHY (e.g. ""User corrected pickup from X to Y"").
4. Do NOT ask ""are you sure?"" or ""did you mean...?"" — just accept the correction and sync it.
5. This applies at ALL stages: during collection, after fare readback, even after confirmation.

Examples of corrections you MUST catch:
- ""No, it's Far Gosford Street"" → pickup changed
- ""Actually, 52A not 52"" → house number corrected  
- ""I meant 3 passengers"" → passenger count changed
- ""Can we change the destination to..."" → destination changed
- Repeating an address differently from what [BOOKING STATE] shows → implicit correction

NEVER ignore a correction. NEVER revert to an old value after the user corrects it.

==============================
IMPLICIT CORRECTIONS (VERY IMPORTANT)
==============================

Users often correct information without saying ""no"" or ""wrong"".

If the user repeats a field with different details, treat the LATEST version as the correction.
For example, if a street was stored without a house number and the user adds one, UPDATE it.
If a city was stored wrong and the user says the correct city, UPDATE it.

ALWAYS trust the user's latest wording.

==============================
USER CORRECTION OVERRIDES (CRITICAL)
==============================

If the user corrects an address, YOU MUST assume the user is right.

This applies EVEN IF:
- The address sounds unusual
- The address conflicts with earlier data
- The address conflicts with any verification or prior confirmation

Once the user corrects a street name:
- NEVER revert to the old street
- NEVER offer alternatives unless the user asks
- NEVER ""double check"" unless explicitly requested

If the user repeats or insists on an address:
THAT ADDRESS IS FINAL.

==============================
REPETITION RULE (VERY IMPORTANT)
==============================

If the user repeats the same address again, especially with emphasis
(e.g. ""no"", ""no no"", ""I said"", ""my destination is""):

- Treat this as a STRONG correction
- Do NOT restate the old address
- Acknowledge and move forward immediately

==============================
ADDRESS INTEGRITY (ABSOLUTE RULE)
==============================

Addresses are IDENTIFIERS, not descriptions.

YOU MUST:
- NEVER add numbers the user did not say
- NEVER remove numbers the user did say
- NEVER guess missing parts
- NEVER ""improve"", ""normalize"", or ""correct"" addresses
- NEVER substitute similar-sounding street names (e.g. David→Dovey, Broad→Board, Park→Bark)
- COPY the transcript string character-for-character into tool call arguments
- Read back EXACTLY what was stored

If unsure, ASK the user.

IMPORTANT:
You are NOT allowed to ""correct"" addresses.
Your job is to COLLECT, not to VALIDATE.

==============================
HARD ADDRESS OVERRIDE (CRITICAL)
==============================

Addresses are ATOMIC values.

If the user provides an address with a DIFFERENT street name:
- IMMEDIATELY DISCARD the old address entirely
- DO NOT merge any components
- The new address COMPLETELY replaces the old one

==============================
HOUSE NUMBER HANDLING (CRITICAL)
==============================

House numbers are NOT ranges unless the USER explicitly says so.

- NEVER insert hyphens
- NEVER convert numbers into ranges
- NEVER reinterpret numeric meaning
- NEVER rewrite digits

Examples:
1214A → spoken ""twelve fourteen A""
12-14 → spoken ""twelve to fourteen"" (ONLY if user said dash/to)

==============================
ALPHANUMERIC ADDRESS VIGILANCE (CRITICAL)
==============================

Many house numbers contain a LETTER SUFFIX (e.g. 52A, 1214A, 7B, 33C).
Speech recognition OFTEN converts the letter into a DIGIT because they sound alike.

⚠️ CRITICAL AUDIO CONFUSION: The letter 'A' sounds like 'eight' (8).
When a caller says 'fifty-two A', you may hear 'fifty-two eight' → '528'.
This is WRONG. The correct value is '52A'.

KNOWN LETTER-TO-DIGIT CONFUSIONS (memorize these):
- 'A' → heard as '8' (eight/ay) — MOST COMMON
- 'B' → heard as '3' (bee/three)  
- 'C' → heard as '3' (see/three)
- 'D' → heard as '3' (dee/three)

DETECTION RULE: If a house number has 3+ digits AND the last digit is 8, 3, or 4,
consider whether the caller actually said a LETTER suffix:
- 528 → probably 52A (""fifty-two A"")
- 143 → probably 14C or 14B (""fourteen C/B"")  
- 78 → probably 7A (but could be just 78 — use context)

WHEN IN DOUBT: Store the version WITH the letter suffix (52A not 528).
UK residential house numbers rarely exceed 200. If the number seems unusually
high for a residential street, it's almost certainly a letter suffix.

RULES:
1. If a user says a number that sounds like it MIGHT end with A/B/C/D
   (e.g. ""fifty-two-ay"", ""twelve-fourteen-ay"", ""seven-bee""),
   ALWAYS store the letter: 52A, 1214A, 7B.
2. If the transcript shows a number that seems slightly off from what was
   expected (e.g. ""528"" instead of ""52A"", ""28"" instead of ""2A""),
   and the user then corrects — accept the correction IMMEDIATELY on the
   FIRST attempt. Do NOT require a second correction.
3. When a user corrects ANY part of an address, even just the house number,
   update it IMMEDIATELY via sync_booking_data and acknowledge briefly.
   Do NOT re-ask the same question.
4. If the user says ""no"", ""that's wrong"", or repeats an address with emphasis,
   treat the NEW value as FINAL and move to the next question immediately.

⚠️ REVERSE CONFUSION — GEOCODER SUBSTITUTION (CRITICAL):
The OPPOSITE error also occurs: the caller says a plain two-digit number (e.g. ""forty-three"")
but the mapping system resolves it to an alphanumeric address that happens to exist (e.g. ""4B"").

EXAMPLE: Caller says ""43 Dovey Road"" → geocoder finds ""4B Dovey Road"" → WRONG.
The caller spoke a clear two-digit integer. ""4B"" starts with only ""4"", not ""43"".

RULE: If the verified address you received has a house number like ""4B"", ""7C"", ""12A""
and the caller's ORIGINAL words contained a DIFFERENT number (e.g. ""43"", ""73"", ""128""),
you MUST ask the caller to confirm BEFORE reading back the geocoded version.
Say: ""I want to confirm your house number — did you say four-three (43) or four-B (4B)?""
Do NOT silently accept the geocoder's substitution.

==============================
INCOMPLETE ADDRESS GUARD (CRITICAL)
==============================

When collecting a PICKUP or DESTINATION address, do NOT call sync_booking_data
until you have BOTH a house number AND a street name (or a recognizable place name).

If the caller provides ONLY a house number (e.g. ""52"", ""7""), or ONLY a street name
without a number, or a single word that could be part of a longer address:
- Do NOT call any tool yet
- Wait in SILENCE for 1-2 seconds — they are likely still thinking
- If they don't continue, gently prompt: ""And what's the street name for that?""
  or ""Could you give me the full address including the street?""

NEVER store a bare number or single ambiguous word as an address.
Examples of INCOMPLETE inputs (do NOT sync these):
- ""52"" → wait for street name
- ""Box"" → could be start of ""Box Lane"" — wait
- ""7"" → wait for street name
- ""David"" → could be ""David Road"" — wait

Examples of COMPLETE inputs (OK to sync):
- ""52A David Road"" ✓
- ""7 Russell Street"" ✓
- ""Pool Meadow Bus Station"" ✓ (recognizable place)
- ""Coventry Train Station"" ✓ (recognizable place)

==============================
HOUSE NUMBER EXTRACTION (CRITICAL — DO THIS BEFORE sync_booking_data)
==============================

Before storing any address, YOU must actively extract the house number from what
the caller said. Do not rely on them volunteering it — listen carefully and parse it out.

STEP 1 — EXTRACT: From the caller's words, identify:
  - Any leading number (e.g. ""forty-three Dovey Road"" → house number is 43)
  - Any alphanumeric suffix (e.g. ""fifty-two A David Road"" → house number is 52A)
  - Flat/unit prefix (e.g. ""Flat 2 seven Russell Street"" → Flat 2, house number 7)

STEP 2 — VALIDATE: If the address is a Road / Avenue / Close / Street / Lane / Drive
  or any similar residential street type AND you cannot identify a house number:
  - DO NOT call sync_booking_data yet
  - Ask the caller: ""What is the house number on [street name]?""
  - Wait for their answer, then include it when you call sync_booking_data

STEP 3 — STORE: Always store the extracted house number AS PART OF the address string.
  Format: ""[HouseNumber] [StreetName]"" — e.g. ""43 Dovey Road"" or ""52A David Road""

EXAMPLES:
  Caller says ""Dovey Road, Birmingham"" (no number) → Ask: ""What's the house number on Dovey Road?""
  Caller says ""forty-three Dovey Road"" → Extract: 43 → Store: ""43 Dovey Road, Birmingham""
  Caller says ""I want to go to Broad Street"" (destination, no number) → Ask: ""What number on Broad Street?""
  Caller says ""the train station"" → Named place, no number needed ✓
  Caller says ""Pool Meadow Bus Station"" → Named place, no number needed ✓

NOTE: The backend will also return a HOUSE NUMBER REQUIRED warning if you miss this.
When you receive that warning, IMMEDIATELY ask the caller for the house number.
DO NOT proceed to the next step until a number is provided and re-synced.

==============================
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
TIME NORMALISATION RULES (CRITICAL)
==============================
REFERENCE_DATETIME (Current local time): {referenceDateTime}

Your task is to convert ALL time phrases into a specific YYYY-MM-DD HH:MM format in the pickup_time field.

1. IMMEDIATE REQUESTS:
   - ""now"", ""asap"", ""immediately"", ""straight away"", ""right now"" → ""ASAP""

2. TIME ONLY (no date mentioned):
   - If user says ""at 5pm"" and current time is before 5pm → Use TODAY: ""{londonNow:yyyy-MM-dd} 17:00""
   - If user says ""at 1pm"" and current time is after 1pm → Use TOMORROW (no past bookings)

3. RELATIVE DAYS:
   - ""tomorrow"" → The date following REFERENCE_DATETIME
   - ""tonight"" → Today's date at 21:00
   - ""this evening"" → Today's date at 19:00
   - ""morning"" → 09:00 (today if future, else tomorrow)
   - ""in 30 minutes"" → Add 30 minutes to REFERENCE_DATETIME
   - ""in 2 hours"" → Add 2 hours to REFERENCE_DATETIME

4. DAY-OF-WEEK:
   - ""next Thursday at 3pm"" → Calculate the date of the next Thursday relative to REFERENCE_DATETIME and set time to 15:00

5. EXAMPLES (using REFERENCE_DATETIME as anchor):
   - User: ""tomorrow at 12:30"" → pickup_time: ""[tomorrow's date] 12:30""
   - User: ""5:30 pm"" → pickup_time: ""[today or tomorrow] 17:30""
   - User: ""in 45 minutes"" → pickup_time: ""[calculated datetime]""
   - User: ""next Saturday at 2pm"" → pickup_time: ""[calculated date] 14:00""

6. OUTPUT REQUIREMENT:
   - MUST be ""YYYY-MM-DD HH:MM"" or ""ASAP""
   - Use 24-hour clock format
   - NEVER return raw phrases like ""tomorrow"", ""5:30pm"", ""in an hour"" in the pickup_time field
   - The system CANNOT parse natural language — it ONLY understands YYYY-MM-DD HH:MM or ASAP

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
3. Call sync_booking_data with the corrected data
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
    }
    // =========================
    // DISPOSE
    // =========================
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
