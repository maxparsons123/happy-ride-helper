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

            var options = new ConversationSessionOptions
            {
                Instructions = _systemPrompt,
                Voice = MapVoice(_settings.Voice),
                InputAudioFormat = ConversationAudioFormat.G711Alaw,
                OutputAudioFormat = ConversationAudioFormat.G711Alaw,
                InputTranscriptionOptions = new ConversationInputTranscriptionOptions
                {
                    Model = "whisper-1"
                },
                TurnDetectionOptions = ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                    detectionThreshold: 0.2f,
                    prefixPaddingDuration: TimeSpan.FromMilliseconds(600),
                    silenceDuration: TimeSpan.FromMilliseconds(900))
            };

            options.Tools.Add(BuildSyncBookingDataTool());
            options.Tools.Add(BuildBookTaxiTool());
            options.Tools.Add(BuildEndCallTool());

            await _session.ConfigureSessionAsync(options);

            _logger.LogInformation("✅ Session configured (G.711 A-law, model={Model})", _settings.Model);

            _eventLoopTask = Task.Run(() => ReceiveEventsLoopAsync(_sessionCts.Token));
            _keepaliveTask = Task.Run(() => KeepaliveLoopAsync(_sessionCts.Token));

            await SendGreetingAsync();
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
    private async Task SendGreetingAsync()
    {
        if (Interlocked.Exchange(ref _greetingSent, 1) == 1) return;
        if (_session == null) return;

        try
        {
            var lang = DetectLanguage(_callerId);
            var langName = GetLanguageName(lang);
            var localizedGreeting = GetLocalizedGreeting(lang);

            var greeting = $"[SYSTEM] [LANG: {langName}] A new caller has connected (ID: {_callerId}). " +
                           $"Greet them in {langName}. Say: \"{localizedGreeting}\"";
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
        _ => "Hello, welcome to Taxibot. I'm Ada. What's your name?"
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
                      "Must be called BEFORE generating any text response when user provides or amends booking details.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                caller_name = new { type = "string", description = "Caller's name" },
                pickup = new { type = "string", description = "Pickup address (verbatim from caller)" },
                destination = new { type = "string", description = "Destination address (verbatim from caller)" },
                passengers = new { type = "integer", description = "Number of passengers" },
                pickup_time = new { type = "string", description = "Requested pickup time" }
            }
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
        "fable" => ConversationVoice.Fable,
        "onyx" => ConversationVoice.Onyx,
        "nova" => ConversationVoice.Nova,
        "shimmer" => ConversationVoice.Shimmer,
        _ => ConversationVoice.Shimmer
    };

    // =========================
    // SYSTEM PROMPT
    // =========================
    private string GetDefaultSystemPrompt() =>
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
CONTINUOUSLY MONITOR the caller's spoken language.
If they speak another language or ask to switch, IMMEDIATELY switch for all responses.

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
→ PICKUP  
→ DESTINATION  
→ PASSENGERS  
→ TIME  

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
  User gives pickup → call sync_booking_data(..., pickup=WHAT_THEY_SAID) → THEN ask destination
  User gives destination → call sync_booking_data(..., destination=WHAT_THEY_SAID) → THEN ask passengers
NEVER collect multiple fields without calling sync_booking_data between each.

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

IMPORTANT: If sync_booking_data returns needs_clarification=true,
you MUST ask the user to clarify which location they mean before continuing.
Present the alternatives naturally (e.g. ""Did you mean School Road in Hall Green
or School Road in Moseley?"").

==============================
ADDRESS DISAMBIGUATION (CRITICAL)
==============================

The system resolves ambiguous addresses ONE AT A TIME — pickup first, then destination.

When you receive a [PICKUP DISAMBIGUATION] message:
1. CANCEL any interjection you were about to say (e.g., ""Let me check those addresses..."")
2. Ask ONLY about the PICKUP location — do NOT mention the destination at all
3. Present the pickup options clearly: ""I found a few pickup options. Is it: 1) School Road in Hall Green, or 2) School Road in Moseley?""
4. STOP TALKING and WAIT for the caller to respond
5. After they choose, call sync_booking_data with the clarified pickup address

When you receive a [DESTINATION DISAMBIGUATION] message:
1. Ask ONLY about the DESTINATION location
2. Present the destination options clearly
3. STOP TALKING and WAIT for the caller to respond
4. After they choose, call sync_booking_data with the clarified destination address

RULES FOR ALL DISAMBIGUATION:
- Do NOT mention both pickup and destination ambiguity at the same time
- Do NOT assume or guess which option they want
- NEVER rush through the options — pause after listing them
- Do NOT repeat the same clarification question multiple times (this creates a loop)
- After the caller picks one, call sync_booking_data with the clarified address (include area, e.g. ""1214A Warwick Road, Acocks Green, Birmingham"")

If the caller says a number (""one"", ""the first one"") or a place name (""Acocks Green""),
map it to the correct option and proceed.

CRITICAL: Your response after disambiguation MUST end with a question and silence.
Do NOT add extra sentences after the question. Let the caller answer.


==============================
FINAL CLOSING (MANDATORY – EXACT WORDING)
==============================

When the conversation is complete, say EXACTLY this sentence and nothing else:

""Thank you for using the TaxiBot system. You will shortly receive your booking confirmation over WhatsApp. Goodbye.""

Then IMMEDIATELY call end_call.

DO NOT:
- Rephrase
- Add extra sentences
- Mention the journey
- Mention prices or addresses

==============================
CRITICAL: FRESH SESSION – NO MEMORY
==============================

THIS IS A NEW CALL.
- You have NO prior knowledge of this caller
- NEVER reuse data from earlier turns if the user corrects it
- The user's MOST RECENT wording is always the source of truth

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

The bridge tracks the real state. Your memory alone does NOT persist data.
If you skip a sync_booking_data call, the booking state will be wrong.

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
Speech recognition OFTEN drops or merges the letter, producing wrong numbers.

RULES:
1. If a user says a number that sounds like it MIGHT end with A/B/C/D
   (e.g. ""fifty-two-ay"", ""twelve-fourteen-ay"", ""seven-bee""),
   ALWAYS store the letter: 52A, 1214A, 7B.
2. If the transcript shows a number that seems slightly off from what was
   expected (e.g. ""58"" instead of ""52A"", ""28"" instead of ""2A""),
   and the user then corrects — accept the correction IMMEDIATELY on the
   FIRST attempt. Do NOT require a second correction.
3. When a user corrects ANY part of an address, even just the house number,
   update it IMMEDIATELY via sync_booking_data and acknowledge briefly.
   Do NOT re-ask the same question.
4. If the user says ""no"", ""that's wrong"", or repeats an address with emphasis,
   treat the NEW value as FINAL and move to the next question immediately.

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
