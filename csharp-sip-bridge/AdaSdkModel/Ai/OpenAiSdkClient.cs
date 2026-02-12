using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AdaSdkModel.Config;
using Microsoft.Extensions.Logging;
using OpenAI.Realtime;
using System.ClientModel;

namespace AdaSdkModel.Ai;

/// <summary>
/// OpenAI Realtime API client using official .NET SDK (GA 2.8.0) — G.711 A-law passthrough (8kHz).
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
/// Version 2.0 — AdaSdkModel (OpenAI .NET SDK GA)
/// </summary>
public sealed class OpenAiSdkClient : IOpenAiClient, IAsyncDisposable
{
    public const string VERSION = "2.0-sdk-g711-ga";

    // =========================
    // CONFIG
    // =========================
    private readonly ILogger<OpenAiSdkClient> _logger;
    private readonly OpenAiSettings _settings;
    private readonly string _systemPrompt;

    // =========================
    // SDK INSTANCES
    // =========================
    private RealtimeClient? _client;
    private RealtimeSession? _session;
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
        _client = new RealtimeClient(new ApiKeyCredential(_settings.ApiKey));

        try
        {
            _session = await _client.StartConversationSessionAsync(
                model: _settings.Model ?? "gpt-4o-realtime-preview");

            var options = new ConversationSessionOptions
            {
                Instructions = _systemPrompt,
                Voice = MapVoice(_settings.Voice),
                Audio = new RealtimeSessionAudioConfiguration
                {
                    Input = new RealtimeSessionAudioInputConfiguration
                    {
                        Format = RealtimeAudioFormat.G711Alaw,
                        Transcription = new InputTranscriptionOptions { Model = "whisper-1" }
                    },
                    Output = new RealtimeSessionAudioOutputConfiguration
                    {
                        Format = RealtimeAudioFormat.G711Alaw
                    }
                },
                TurnDetectionOptions = ConversationTurnDetectionOptions.CreateServerVADOptions(
                    threshold: 0.2f,
                    prefixPadding: TimeSpan.FromMilliseconds(600),
                    silenceDuration: TimeSpan.FromMilliseconds(900))
            };

            options.Tools.Add(BuildSyncBookingDataTool());
            options.Tools.Add(BuildBookTaxiTool());
            options.Tools.Add(BuildEndCallTool());

            await _session.ConfigureConversationSessionAsync(options);

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

        try
        {
            // GA SDK uses SendInputAudioAsync with a stream; for raw byte chunks we use BinaryData
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
            bool isCritical = message.Contains("[FARE RESULT]") || message.Contains("ADDRESS DISAMBIGUATION");
            if (isCritical && Volatile.Read(ref _responseActive) == 1)
            {
                Log("🛑 Cancelling active response before critical injection");
                Interlocked.Exchange(ref _responseActive, 0);
                await Task.Delay(100);
            }

            Log($"💉 Injecting: {(message.Length > 80 ? message[..80] + "..." : message)}");
            await _session!.AddItemAsync(RealtimeItem.CreateUserMessage(new[] { message }));
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

    private async Task ProcessUpdateAsync(RealtimeUpdate? update)
    {
        if (update == null) return;

        switch (update)
        {
            case ConversationSessionStartedUpdate:
                Log("✅ Realtime Session Started");
                break;

            // ── SPEECH DETECTION ──
            case InputAudioSpeechStartedUpdate:
                HandleSpeechStarted();
                break;

            case InputAudioSpeechFinishedUpdate:
                Log("🔇 User finished speaking");
                break;

            // ── AUDIO STREAMING + TEXT DELTA ──
            case OutputDeltaUpdate delta:
                if (delta.AudioBytes != null)
                {
                    Interlocked.Exchange(ref _hasEnqueuedAudio, 1);
                    OnAudio?.Invoke(delta.AudioBytes.ToArray());
                }
                break;

            // ── RESPONSE LIFECYCLE ──
            case ResponseStartedUpdate respStarted:
                _activeResponseId = respStarted.ResponseId;
                Interlocked.Exchange(ref _responseActive, 1);
                Interlocked.Exchange(ref _hasEnqueuedAudio, 0);
                Log($"🎤 Response started (ID: {_activeResponseId})");
                break;

            case ResponseFinishedUpdate:
                Interlocked.Exchange(ref _responseActive, 0);
                Log("✋ Response finished");
                OnResponseCompleted?.Invoke();

                if (Interlocked.CompareExchange(ref _deferredResponsePending, 0, 1) == 1)
                {
                    Log("⏳ Processing deferred response");
                    await _session!.StartResponseAsync();
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
                break;

            // ── ITEM STREAMING FINISHED (transcript + tool calls) ──
            case OutputStreamingFinishedUpdate itemFinished:
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
                        if (!string.IsNullOrEmpty(part.AudioTranscript))
                        {
                            _lastAdaTranscript = part.AudioTranscript;
                            OnTranscript?.Invoke("Ada", _lastAdaTranscript);
                            CheckGoodbye(_lastAdaTranscript);
                        }
                    }
                }
                break;

            // ── USER TRANSCRIPT ──
            case InputAudioTranscriptionFinishedUpdate userTranscript:
                Interlocked.Exchange(ref _noReplyCount, 0);
                OnTranscript?.Invoke("User", userTranscript.Transcript);
                break;

            // ── ERROR ──
            case RealtimeErrorUpdate errorUpdate:
                _logger.LogError("OpenAI Realtime error: {Msg}", errorUpdate.Message);
                break;
        }
    }

    // =========================
    // TOOL CALL HANDLING
    // =========================
    private async Task HandleToolCallAsync(OutputStreamingFinishedUpdate itemFinished)
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

                // GA SDK: create function output item and add it
                var outputItem = RealtimeItem.CreateFunctionCallOutput(
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
            var greeting = $"[SYSTEM] A new caller has connected (ID: {_callerId}). " +
                           "Greet them warmly and ask how you can help.";
            await _session.AddItemAsync(RealtimeItem.CreateUserMessage(new[] { greeting }));
            await _session.StartResponseAsync();
            Log("📢 Greeting sent");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending greeting");
        }
    }

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
                    RealtimeItem.CreateUserMessage(new[] { "[SILENCE] Hello? Are you still there?" }));
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
Then immediately call end_call.";

    // =========================
    // DISPOSE
    // =========================
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
