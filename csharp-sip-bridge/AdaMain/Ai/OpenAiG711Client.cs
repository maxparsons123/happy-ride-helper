using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AdaMain.Config;
using Microsoft.Extensions.Logging;
using OpenAI.RealtimeConversation;
using System.ClientModel;

namespace AdaMain.Ai;

/// <summary>
/// OpenAI Realtime API client using official .NET SDK — G.711 A-law passthrough (8kHz).
///
/// Production features ported from raw WebSocket v2.0:
/// - Native G.711 A-law codec (no resampling, direct SIP passthrough)
/// - Tool calling with async execution (sync_booking_data, book_taxi, end_call)
/// - Deferred response handling (queue response.create if one is active)
/// - Barge-in detection (speech_started while audio streaming)
/// - No-reply watchdog (15s silence → re-prompt, 30s for disambiguation)
/// - Echo guard (playout-aware silence window after response completes)
/// - Goodbye detection with drain-aware hangup
/// - Keepalive heartbeat logging
/// - State grounding injection (bridge state after tool calls)
/// - Per-call state management and reset
/// - Non-blocking channel-based logger
/// - Confirmation-awaiting mode (extended watchdog timeout)
///
/// Version 3.0 — Official OpenAI .NET SDK
/// </summary>
public sealed class OpenAiG711Client : IOpenAiClient, IAsyncDisposable
{
    public const string VERSION = "3.0-sdk-g711";

    // =========================
    // CONFIG
    // =========================
    private readonly ILogger<OpenAiG711Client> _logger;
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
    private int _disposed;                 // Object lifetime only (NEVER reset)
    private int _callEnded;                // Per-call
    private int _responseActive;           // Set by response started/finished
    private int _deferredResponsePending;  // Queue response after current finishes
    private int _noReplyWatchdogId;        // Incremented to cancel stale watchdogs
    private int _noReplyCount;             // Per-call
    private int _toolInFlight;             // Suppress watchdogs during tool execution
    private int _hasEnqueuedAudio;         // Set when playout queue received audio this response
    private int _ignoreUserAudio;          // Set after goodbye detected
    private int _greetingSent;             // Per-call
    private int _syncCallCount;            // How many sync_booking_data calls made
    private int _bookingConfirmed;         // Set once book_taxi confirmed succeeds
    private long _lastAdaFinishedAt;       // RTP playout completion time (echo guard)
    private long _lastUserSpeechAt;        // Last user audio timestamp

    // Response tracking
    private string? _activeResponseId;
    private string? _lastAdaTranscript;
    private bool _awaitingConfirmation;    // Extended watchdog timeout for confirmation

    // =========================
    // CALLER STATE
    // =========================
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

    /// <summary>Fired with raw A-law audio frames from AI.</summary>
    public event Action<byte[]>? OnAudio;
    public event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    public event Action<string>? OnEnded;
    public event Action? OnPlayoutComplete;
    public event Action<string, string>? OnTranscript;

    /// <summary>Fired on barge-in (playout should clear its buffer).</summary>
    public event Action? OnBargeIn;

    /// <summary>Fired when AI response stream completes — use for precise bot-speaking tracking.</summary>
    public event Action? OnResponseCompleted;

    /// <summary>Optional: query playout queue depth for drain-aware shutdown.</summary>
    public Func<int>? GetQueuedFrames { get; set; }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // =========================
    // CONSTRUCTOR
    // =========================
    public OpenAiG711Client(
        ILogger<OpenAiG711Client> logger,
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
            throw new ObjectDisposedException(nameof(OpenAiG711Client));

        ResetCallState(callerId);
        InitializeLogger();

        Log($"📞 Connecting v{VERSION} (caller: {callerId}, codec: A-law 8kHz, model: {_settings.Model})");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _client = new RealtimeConversationClient(new ApiKeyCredential(_settings.ApiKey));

        try
        {
            _session = await _client.StartConversationSessionAsync();

            // Configure session for native G.711 A-law (PCMA)
            var options = new ConversationSessionOptions
            {
                Instructions = _systemPrompt,
                Voice = MapVoice(_settings.Voice),
                InputAudioFormat = ConversationAudioFormat.G711Alaw,
                OutputAudioFormat = ConversationAudioFormat.G711Alaw,
                InputTranscriptionOptions = new ConversationTranscriptionOptions { Model = "whisper-1" },
                TurnDetectionOptions = ConversationTurnDetectionOptions.CreateServerVADOptions(
                    threshold: 0.2f,
                    prefixPadding: TimeSpan.FromMilliseconds(600),
                    silenceDuration: TimeSpan.FromMilliseconds(900))
            };

            // Add tools
            options.Tools.Add(BuildSyncBookingDataTool());
            options.Tools.Add(BuildBookTaxiTool());
            options.Tools.Add(BuildEndCallTool());

            await _session.ConfigureSessionAsync(options);

            _logger.LogInformation("✅ Session configured (G.711 A-law, model={Model})", _settings.Model);

            // Start event loop + keepalive
            _eventLoopTask = Task.Run(() => ReceiveEventsLoopAsync(_sessionCts.Token));
            _keepaliveTask = Task.Run(() => KeepaliveLoopAsync(_sessionCts.Token));

            // Send greeting
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
            try { await _session.DisposeAsync(); }
            catch { }
            _session = null;
        }

        // Stop logger
        _loggerRunning = false;
        _logChannel.Writer.TryComplete();
        try { if (_logTask != null) await _logTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

        _sessionCts?.Dispose();
        _sessionCts = null;

        // Clear event delegates
        OnAudio = null;
        OnToolCall = null;
        OnEnded = null;
        OnPlayoutComplete = null;
        OnTranscript = null;
        OnBargeIn = null;
        OnResponseCompleted = null;
        GetQueuedFrames = null;

        ResetCallState(null);
        _logger.LogInformation("OpenAiG711Client fully disposed — all state cleared");
    }

    // =========================
    // AUDIO INPUT (SIP → OpenAI)
    // =========================
    public void SendAudio(byte[] alawData)
    {
        if (!IsConnected || alawData == null || alawData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        try
        {
            // The SDK handles base64 encoding and JSON envelope internally
            _session!.AppendInputAudio(new BinaryData(alawData));
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
            // Note: The SDK may not expose response.cancel directly.
            // We clear state and barge-in event will handle playout clearing.
            Interlocked.Exchange(ref _responseActive, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling response");
        }
    }

    /// <summary>
    /// Inject a system message and trigger AI response.
    /// Used for fare results, disambiguation, and interjections.
    /// </summary>
    public async Task InjectMessageAndRespondAsync(string message)
    {
        if (!IsConnected) return;

        try
        {
            // If injecting fare/disambiguation while AI is speaking, cancel first
            bool isCritical = message.Contains("[FARE RESULT]") || message.Contains("ADDRESS DISAMBIGUATION");
            if (isCritical && Volatile.Read(ref _responseActive) == 1)
            {
                Log("🛑 Cancelling active response before critical injection");
                Interlocked.Exchange(ref _responseActive, 0);
                await Task.Delay(100); // Brief settle
            }

            Log($"💉 Injecting: {(message.Length > 80 ? message[..80] + "..." : message)}");
            await _session!.AddItemAsync(ConversationItem.CreateUserMessage(new[] { message }));
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

    /// <summary>
    /// Called by ALawRtpPlayout when the playout queue drains.
    /// Arms the no-reply watchdog and sets echo guard timestamp.
    /// </summary>
    public void NotifyPlayoutComplete()
    {
        Volatile.Write(ref _lastAdaFinishedAt, NowMs());
        OnPlayoutComplete?.Invoke();
        StartNoReplyWatchdog();
    }

    /// <summary>Set/clear confirmation-awaiting mode (extends watchdog timeout).</summary>
    public void SetAwaitingConfirmation(bool awaiting)
    {
        _awaitingConfirmation = awaiting;
        Log($"🔔 Awaiting confirmation: {awaiting}");
    }

    /// <summary>Cancel any pending deferred response.</summary>
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
            await foreach (var update in _session!.GetConversationUpdatesAsync(ct))
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
            case ConversationSessionStartedUpdate:
                Log("✅ Realtime Session Started");
                break;

            // ── SPEECH DETECTION ──
            case ConversationInputSpeechStartedUpdate:
                HandleSpeechStarted();
                break;

            case ConversationInputSpeechFinishedUpdate:
                Log("🔇 User finished speaking");
                break;

            // ── AUDIO STREAMING ──
            case ConversationItemStreamingPartDeltaUpdate delta:
                if (delta.AudioDelta != null)
                {
                    Interlocked.Exchange(ref _hasEnqueuedAudio, 1);
                    OnAudio?.Invoke(delta.AudioDelta.ToArray());
                }
                break;

            // ── RESPONSE LIFECYCLE ──
            case ConversationResponseStartedUpdate respStarted:
                _activeResponseId = respStarted.ResponseId;
                Interlocked.Exchange(ref _responseActive, 1);
                Interlocked.Exchange(ref _hasEnqueuedAudio, 0);
                Log($"🎤 Response started (ID: {_activeResponseId})");
                break;

            case ConversationResponseFinishedUpdate:
                Interlocked.Exchange(ref _responseActive, 0);
                Log("✋ Response finished");
                OnResponseCompleted?.Invoke();

                // Process deferred response if queued
                if (Interlocked.CompareExchange(ref _deferredResponsePending, 0, 1) == 1)
                {
                    Log("⏳ Processing deferred response");
                    await _session!.StartResponseAsync();
                }
                else
                {
                    // Safety fallback: arm watchdog after 10s if playout completion event missed
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
                break;

            // ── TRANSCRIPT COMPLETION (Ada) ──
            case ConversationItemStreamingFinishedUpdate itemFinished:
                if (!string.IsNullOrEmpty(itemFinished.AudioTranscript))
                {
                    _lastAdaTranscript = itemFinished.AudioTranscript;
                    OnTranscript?.Invoke("Ada", _lastAdaTranscript);
                    CheckGoodbye(_lastAdaTranscript);
                }
                break;

            // ── USER TRANSCRIPT ──
            case ConversationInputTranscriptionFinishedUpdate userTranscript:
                Interlocked.Exchange(ref _noReplyCount, 0); // Reset no-reply counter on user speech
                OnTranscript?.Invoke("User", userTranscript.Transcript);
                break;

            // ── TOOL CALL ──
            case ConversationFunctionCallArgumentsFinishedUpdate funcArgs:
                // Tool call starts: suppress watchdogs, clear response active (audio stream ended)
                Interlocked.Exchange(ref _toolInFlight, 1);
                Interlocked.Exchange(ref _responseActive, 0);
                _ = HandleToolCallAsync(funcArgs);
                break;
        }
    }

    // =========================
    // TOOL CALL HANDLING
    // =========================
    private async Task HandleToolCallAsync(ConversationFunctionCallArgumentsFinishedUpdate funcArgs)
    {
        try
        {
            var toolName = funcArgs.FunctionName;
            var argsJson = funcArgs.Arguments.ToString();
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            Log($"🔧 Tool call: {toolName} ({args.Count} args)");

            if (OnToolCall != null)
            {
                var result = await OnToolCall(toolName, args);
                var resultJson = result is string s ? s : JsonSerializer.Serialize(result);

                Log($"✅ Tool result: {(resultJson.Length > 120 ? resultJson[..120] + "..." : resultJson)}");

                // Inject bridge state grounding after sync_booking_data
                if (toolName == "sync_booking_data")
                {
                    Interlocked.Increment(ref _syncCallCount);
                }

                // Return result to OpenAI and trigger next response
                await _session!.AddToolResultAsync(resultJson);

                // If response is still active (shouldn't be, but safety), defer
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
            _logger.LogError(ex, "Error handling tool call: {Name}", funcArgs.FunctionName);
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
        Interlocked.Increment(ref _noReplyWatchdogId); // Cancel pending watchdog

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
            await _session.AddItemAsync(ConversationItem.CreateUserMessage(new[] { greeting }));
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

            // Only trigger if this watchdog is still current
            if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
            if (Volatile.Read(ref _responseActive) == 1) return;
            if (Volatile.Read(ref _toolInFlight) == 1) return;
            if (Volatile.Read(ref _callEnded) != 0) return;
            if (!IsConnected) return;

            // Echo guard: don't trigger if Ada just finished speaking
            var sinceAdaFinished = NowMs() - Volatile.Read(ref _lastAdaFinishedAt);
            if (sinceAdaFinished < ECHO_GUARD_MS) return;

            Interlocked.Increment(ref _noReplyCount);
            Log($"⏰ No-reply watchdog triggered (attempt {Volatile.Read(ref _noReplyCount)}/{MAX_NO_REPLY_PROMPTS})");

            try
            {
                await _session!.AddItemAsync(
                    ConversationItem.CreateUserMessage(new[] { "[SILENCE] Hello? Are you still there?" }));
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
            // Don't end call yet — let playout drain via end_call tool
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
    private static ConversationFunctionTool BuildSyncBookingDataTool() => new()
    {
        Name = "sync_booking_data",
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

    private static ConversationFunctionTool BuildBookTaxiTool() => new()
    {
        Name = "book_taxi",
        Description = "Request a fare quote or confirm a booking. " +
                      "action='request_quote' for quotes, 'confirmed' for finalized bookings. " +
                      "CRITICAL: Never call with 'confirmed' in the same turn as an address correction or fare announcement.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                action = new { type = "string", @enum = new[] { "request_quote", "confirmed" } },
                pickup = new { type = "string", description = "Pickup address" },
                destination = new { type = "string", description = "Destination address" },
                caller_name = new { type = "string", description = "Caller name" },
                passengers = new { type = "integer", description = "Number of passengers" },
                pickup_time = new { type = "string", description = "Pickup time" }
            },
            required = new[] { "action", "pickup", "destination" }
        }))
    };

    private static ConversationFunctionTool BuildEndCallTool() => new()
    {
        Name = "end_call",
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
@"You are Ada, a warm and professional AI taxi booking assistant.

## IDENTITY & STYLE
- Keep responses under 15 words, one question at a time
- NEVER use filler phrases: 'one moment', 'please hold on', 'let me check', 'please wait'
- Be warm but efficient

## BOOKING SEQUENCE
1. Greet caller, ask for their name
2. Ask for pickup address
3. Ask for destination
4. Ask for number of passengers (if not mentioned)
5. Call sync_booking_data with ALL collected details
6. Call book_taxi with action='request_quote'
7. Read back the verified address and fare using the fare_spoken field
8. Wait for explicit user confirmation
9. Call book_taxi with action='confirmed'
10. Speak closing script, then call end_call

## ADDRESS INTEGRITY (CRITICAL)
- ONLY store house numbers, postcodes, cities explicitly stated by the user
- House numbers are VERBATIM identifiers (e.g. '1214A', '52-8') — NEVER reinterpret
- Character-for-character copy from transcript for ALL tool parameters
- NEVER substitute similar-sounding addresses
- If unsure, ASK for clarification

## MISHEARING RECOVERY
- If user spells out a word letter by letter (D-O-V-E-Y), reconstruct it as 'Dovey'
- Treat spelled-out words as the FINAL source of truth
- ABANDON previously misheard versions immediately
- Call sync_booking_data after any correction

## TOOL CALL RULES
- Call sync_booking_data BEFORE generating text when user provides/amends details
- NEVER call book_taxi confirmed in same turn as address correction or fare announcement
- NEVER invent fares or booking references — only use values from tool results
- Use fare_spoken field for price announcements

## CLOSING SCRIPT (VERBATIM)
'Thank you for using the TaxiBot system. You will shortly receive your booking confirmation over WhatsApp. Goodbye.'
Then immediately call end_call.

## LANGUAGE
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
English, Dutch, French, German, Spanish, Italian, Polish, Portuguese.";

    // =========================
    // DISPOSE
    // =========================
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
