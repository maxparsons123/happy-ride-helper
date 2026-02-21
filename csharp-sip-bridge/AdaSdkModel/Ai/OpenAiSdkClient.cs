// Last updated: 2026-02-21 (v3.0 — Playout-aware mic gate + greeting watchdog suppression)
// Key fixes:
// - Prevent Ada's own TTS audio being transcribed as "User" by gating mic input until RTP playout drains + tail
// - Adds robust mic gate that does NOT rely solely on NotifyPlayoutComplete()
// - Suppresses the no-reply watchdog immediately after greeting until the user speaks once
using AdaSdkModel.Config;
using Microsoft.Extensions.Logging;
using OpenAI.RealtimeConversation;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

#pragma warning disable OPENAI002 // Experimental Realtime API

namespace AdaSdkModel.Ai;

public sealed class OpenAiSdkClient : IOpenAiClient, IAsyncDisposable
{
    public const string VERSION = "3.0-sdk-g711-beta-micgate";

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
    private bool _useSemanticVad = true;
    private float _semanticEagerness = 0.5f;

    // Transcript comparison
    private string? _lastUserTranscript;
    public string? LastUserTranscript => _lastUserTranscript;

    public Func<string?>? NoReplyContextProvider { get; set; }

    // Caller state
    private string _callerId = "";

    // =========================
    // MIC GATE / ECHO CONTROL (v3.0)
    // =========================
    private long _micGateUntilMs;                   // if NowMs() < this, ignore SendAudio()
    private int _drainGateTaskId;                   // prevents overlapping drain tasks
    private int _suppressWatchdogUntilUserSpeech;   // set to 1 after greeting; cleared once user speaks

    // Tune these:
    private const int ECHO_TAIL_MS = 800;           // SIP needs a longer tail than 200ms
    private const int DRAIN_POLL_MS = 20;
    private const int DRAIN_MAX_MS = 2000;          // cap waiting for playout drain

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // =========================
    // CONSTANTS
    // =========================
    private const int MAX_NO_REPLY_PROMPTS = 3;
    private const int NO_REPLY_TIMEOUT_MS = 8_000;
    private const int CONFIRMATION_TIMEOUT_MS = 15_000;
    private const int DISAMBIGUATION_TIMEOUT_MS = 30_000;

    // (Kept, but no longer the only protection)
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
        _systemPrompt = systemPrompt ?? AdaSystemPrompt.Build();
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

        // v3.0: mic gate reset
        Volatile.Write(ref _micGateUntilMs, 0);
        Interlocked.Exchange(ref _drainGateTaskId, 0);
        Interlocked.Exchange(ref _suppressWatchdogUntilUserSpeech, 0);
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

        // Suppress user audio while a critical tool is in flight
        if (Volatile.Read(ref _toolInFlight) == 1) return;

        // v3.0: Strong mic gate — blocks while playout draining + tail
        var gateUntil = Volatile.Read(ref _micGateUntilMs);
        if (gateUntil > 0 && NowMs() < gateUntil) return;

        // Optional: if we can query playout queue, also block while it's non-empty
        if (GetQueuedFrames != null)
        {
            try
            {
                if (GetQueuedFrames() > 0) return;
            }
            catch { }
        }

        // Legacy echo guard (kept as belt-and-suspenders)
        var adaFinished = Volatile.Read(ref _lastAdaFinishedAt);
        if (adaFinished > 0 && NowMs() - adaFinished < ECHO_GUARD_MS) return;

        try
        {
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
            // Don't force _responseActive=0 here; let lifecycle updates do it.
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
            bool isCritical = message.Contains("[FARE RESULT]") || message.Contains("DISAMBIGUATION") ||
                              message.Contains("[FARE SANITY ALERT]") || message.Contains("[ADDRESS DISCREPANCY]");

            if (isCritical && Volatile.Read(ref _responseActive) == 1)
            {
                Log("🛑 Cancelling active response before critical injection");
                await _session!.CancelResponseAsync();
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
    /// NOTE: Beta SDK doesn't expose true system-role items here; this is best-effort.
    /// </summary>
    public async Task InjectSystemMessageAsync(string message)
    {
        if (!IsConnected) return;

        try
        {
            Log($"📋 State inject: {(message.Length > 80 ? message[..80] + "..." : message)}");
            await _session!.AddItemAsync(
                ConversationItem.CreateUserMessage(new[] { ConversationContentPart.CreateInputTextPart(message) }));
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
        // If your RTP layer calls this reliably when queue hits 0, we arm the mic gate tail here too.
        var now = NowMs();
        Volatile.Write(ref _lastAdaFinishedAt, now);
        Volatile.Write(ref _micGateUntilMs, now + ECHO_TAIL_MS);
        Log($"✅ Playout complete — mic gated for tail {ECHO_TAIL_MS}ms");

        OnPlayoutComplete?.Invoke();

        // Watchdog should only start once user has spoken (post-greeting); StartNoReplyWatchdog handles suppression.
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
                    // #2: await tool call instead of fire-and-forget to prevent race conditions
                    await HandleToolCallAsync(itemFinished);
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
                // First user speech seen — allow watchdog now
                Interlocked.Exchange(ref _suppressWatchdogUntilUserSpeech, 0);

                Interlocked.Exchange(ref _noReplyCount, 0);
                var transcript = userTranscript.Transcript;
                _lastUserTranscript = transcript;
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

            // Cancel watchdog while Ada is speaking
            Interlocked.Increment(ref _noReplyWatchdogId);

            // v3.0: While response is active, proactively gate mic.
            Volatile.Write(ref _micGateUntilMs, NowMs() + 250);
            Log("🎤 Response started");
        }
        else if (typeName.Contains("ResponseDone") || typeName.Contains("ResponseFinished"))
        {
            Interlocked.Exchange(ref _responseActive, 0);
            Log("✋ Response finished");
            OnResponseCompleted?.Invoke();

            // v3.0: Arm a drain-aware mic gate even if NotifyPlayoutComplete is missing/unreliable.
            ArmMicGateAfterDrain();

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
    // MIC GATE ARMING (v3.0)
    // =========================
    private void ArmMicGateAfterDrain()
    {
        // Ensure only the latest drain task controls the gate.
        var taskId = Interlocked.Increment(ref _drainGateTaskId);

        _ = Task.Run(async () =>
        {
            try
            {
                var start = NowMs();
                int lastQ = -1;

                // Wait for RTP queue to drain (if available), but cap the wait
                if (GetQueuedFrames != null)
                {
                    while (NowMs() - start < DRAIN_MAX_MS)
                    {
                        int q;
                        try { q = GetQueuedFrames(); }
                        catch { break; }

                        if (q != lastQ)
                        {
                            lastQ = q;
                            Log($"🔊 Playout queue depth: {q} frames");
                        }

                        if (q <= 0) break;
                        await Task.Delay(DRAIN_POLL_MS);
                    }
                }

                // Only apply if still the latest task
                if (Interlocked.CompareExchange(ref _drainGateTaskId, taskId, taskId) != taskId)
                    return;

                var now = NowMs();
                Volatile.Write(ref _lastAdaFinishedAt, now);
                Volatile.Write(ref _micGateUntilMs, now + ECHO_TAIL_MS);

                Log($"🔇 Mic gate armed until {DateTimeOffset.FromUnixTimeMilliseconds(now + ECHO_TAIL_MS):HH:mm:ss.fff} (tail {ECHO_TAIL_MS}ms)");
            }
            catch { }
        });
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

                // ── Mid-tool goodbye guard ──
                // If the user said goodbye while the tool was executing, inject context
                // so Ada wraps up instead of reading out tool results
                if (toolName != "end_call" && toolName != "book_taxi" && UserSaidGoodbyeDuringTool())
                {
                    Log("👋 User said goodbye during tool execution — injecting wrap-up context");
                    await session.AddItemAsync(
                        ConversationItem.CreateUserMessage(new[] {
                            ConversationContentPart.CreateInputTextPart(
                                "[SYSTEM] The caller said goodbye while you were processing. " +
                                "Do NOT read out the full tool result. Simply say a brief goodbye: " +
                                "\"No problem, goodbye!\" and call end_call.")
                        }));
                }

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
        // User spoke: allow watchdog from here onward
        Interlocked.Exchange(ref _suppressWatchdogUntilUserSpeech, 0);

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
            // v3.0: suppress watchdog until the user speaks once
            Interlocked.Exchange(ref _suppressWatchdogUntilUserSpeech, 1);
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
            // v3.0: suppress watchdog until the user speaks once
            Interlocked.Exchange(ref _suppressWatchdogUntilUserSpeech, 1);
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

        // v3.0: After greeting, do not nag until the user speaks at least once.
        if (Volatile.Read(ref _suppressWatchdogUntilUserSpeech) == 1)
        {
            Log("⏳ Watchdog suppressed (waiting for first user speech)");
            return;
        }

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
            if (Volatile.Read(ref _ignoreUserAudio) == 1) return;
            if (!IsConnected) return;

            // v3.0: if mic is currently gated, don't watchdog prompt
            var gateUntil = Volatile.Read(ref _micGateUntilMs);
            if (gateUntil > 0 && NowMs() < gateUntil) return;

            Interlocked.Increment(ref _noReplyCount);
            Log($"⏰ No-reply watchdog triggered (attempt {Volatile.Read(ref _noReplyCount)}/{MAX_NO_REPLY_PROMPTS})");

            try
            {
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
                var gate = Volatile.Read(ref _micGateUntilMs);
                var q = -1;
                try { if (GetQueuedFrames != null) q = GetQueuedFrames(); } catch { }

                Log($"💓 Keepalive: connected={IsConnected}, response_active={active}, tool={tool}, ended={ended}, mic_gate_ms={(gate > NowMs() ? gate - NowMs() : 0)}, queued_frames={q}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"⚠️ Keepalive error: {ex.Message}"); }
    }

    private static readonly Regex _userGoodbyePattern = new(
        @"\b(bye|goodbye|good\s*bye|cheers|that'?s? (all|it)|thank(s| you).*bye|no.*(thank|that'?s? all))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Check if the user said goodbye while a tool was in flight.
    /// Used to short-circuit tool result delivery and end the call gracefully.
    /// </summary>
    private bool UserSaidGoodbyeDuringTool()
    {
        var transcript = _lastUserTranscript;
        if (string.IsNullOrWhiteSpace(transcript)) return false;
        return _userGoodbyePattern.IsMatch(transcript);
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
        Description = "Cancel an existing active booking. " +
                      "WORKFLOW: 1) Ask the caller to confirm cancellation verbally. " +
                      "2) WAIT for their response. 3) If they say yes/yeah/correct/sure, " +
                      "call this tool with confirmed=true. " +
                      "NEVER call this tool with confirmed=false — that will always be rejected. " +
                      "Only call this tool ONCE, AFTER hearing verbal confirmation, with confirmed=true.",
        Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                reason = new { type = "string", description = "Reason for cancellation (e.g. 'caller_request', 'plans_changed')" },
                confirmed = new { type = "boolean", description = "MUST be true. NEVER pass false — the call will be rejected. Only call this tool after the caller has verbally confirmed." }
            },
            required = new[] { "confirmed" }
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
        "shimmer" => ConversationVoice.Shimmer,
        _ => ConversationVoice.Shimmer
    };

    /// <summary>Kept for backward compatibility; delegates to AdaSystemPrompt.Build().</summary>
    public static string GetDefaultSystemPromptStatic() => AdaSystemPrompt.Build();
    // =========================
    // DISPOSE
    // =========================
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
