using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ZaffAdaSystem.Audio;
using ZaffAdaSystem.Config;
using Microsoft.Extensions.Logging;
using OpenAI.RealtimeConversation;
using System.ClientModel;
using System.Text.RegularExpressions;

#pragma warning disable OPENAI002 // Experimental Realtime API

namespace ZaffAdaSystem.Ai;

/// <summary>
/// OpenAI Realtime API client — HYBRID MODE:
///   Input:  G.711 A-law natively (best STT accuracy, no upsampling artifacts)
///   Output: PCM16 24kHz (best TTS fidelity with proper anti-aliasing)
/// 
/// Egress path: OpenAI PCM16 24kHz → Butterworth LPF → decimate → A-law encode → SIP RTP
/// Ingress path: Raw A-law → OpenAI (no DSP, no resampling)
/// 
/// All other features (tools, watchdog, greeting, barge-in, etc.) are identical.
/// 
/// Version 3.2 — HybridAda (A-law in / PCM16 24kHz out)
/// </summary>
public sealed class OpenAiSdkClientHighSample : IOpenAiClient, IAsyncDisposable
{
    public const string VERSION = "3.2-hybrid-alaw-in-pcm24k-out";

    // =========================
    // CONFIG
    // =========================
    private readonly ILogger<OpenAiSdkClientHighSample> _logger;
    private readonly OpenAiSettings _settings;
    private readonly string _systemPrompt;

    // =========================
    // RESAMPLERS
    // =========================
    private readonly AlawToPcm24kUpsampler _ingressResampler = new();
    private readonly Pcm24kToAlawResampler _egressResampler = new();

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
    private int _responseCancelling;     // #4: tracks cancel-in-progress
    private int _inGreetingPhase;        // #7: suppress watchdog until first user speech
    private long _lastAdaFinishedAt;
    private long _lastUserSpeechAt;

    // Response tracking
    private string? _activeResponseId;
    private string? _lastAdaTranscript;
    private bool _awaitingConfirmation;
    private bool _useSemanticVad = true;
    private float _semanticEagerness = 0.5f;
    private string? _lastUserTranscript;

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
    private const int ECHO_GUARD_MS = 500;  // #6: increased from 200ms

    // #3: CENTRALIZED SDK TYPE NAME CONSTANTS
    private const string TYPE_SESSION_STARTED = "SessionStarted";
    private const string TYPE_SESSION_CREATED = "SessionCreated";
    private const string TYPE_RESPONSE_STARTED = "ResponseStarted";
    private const string TYPE_RESPONSE_CREATED = "ResponseCreated";
    private const string TYPE_RESPONSE_DONE = "ResponseDone";
    private const string TYPE_RESPONSE_FINISHED = "ResponseFinished";

    // Cached JSON options — avoid allocating per tool call
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

    /// <summary>
    /// Fired when audio is ready for playout.
    /// NOTE: Despite using PCM16 24kHz internally, this event emits G.711 A-law bytes
    /// (already resampled + encoded) so the existing ALawRtpPlayout works unchanged.
    /// </summary>
    public event Action<byte[]>? OnAudio;
    public event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    public event Action<string>? OnEnded;
    public event Action? OnPlayoutComplete;
    public event Action<string, string>? OnTranscript;
    public event Action? OnBargeIn;
    public event Action? OnResponseCompleted;
    public event Action? OnGoodbyeWithoutBooking;

    public Func<int>? GetQueuedFrames { get; set; }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // =========================
    // CONSTRUCTOR
    // =========================
    public OpenAiSdkClientHighSample(
        ILogger<OpenAiSdkClientHighSample> logger,
        OpenAiSettings settings,
        string? systemPrompt = null)
    {
        _logger = logger;
        _settings = settings;
        _systemPrompt = systemPrompt ?? OpenAiSdkClient.GetDefaultSystemPromptStatic();

        // Configure resampler gains from settings
        _ingressResampler.IngressGain = (float)settings.IngressGain;
        _egressResampler.OutputGain = (float)settings.EgressGain;
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
                try { _logger.LogInformation("\"{Msg}\"", msg); } catch { }
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
        Interlocked.Exchange(ref _responseCancelling, 0);
        Interlocked.Exchange(ref _inGreetingPhase, 1);  // #7: suppress watchdog until first speech
        Interlocked.Increment(ref _noReplyWatchdogId);

        _activeResponseId = null;
        _lastAdaTranscript = null;
        _lastUserTranscript = null;
        _awaitingConfirmation = false;

        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);

        // Reset resampler filter state
        _egressResampler.Reset();
    }

    // =========================
    // CONNECT — PCM16 24kHz
    // =========================
    public async Task ConnectAsync(string callerId, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OpenAiSdkClientHighSample));

        ResetCallState(callerId);
        InitializeLogger();

        Log($"📞 Connecting v{VERSION} (caller: {callerId}, codec: A-law→PCM16 hybrid, model: {_settings.Model})");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _client = new RealtimeConversationClient(
            model: _settings.Model ?? "gpt-4o-realtime-preview",
            credential: new ApiKeyCredential(_settings.ApiKey));

        try
        {
            _session = await _client.StartConversationSessionAsync(_sessionCts.Token);

            // ══════════════════════════════════════════════
            // KEY DIFFERENCE: PCM16 instead of G711Alaw
            // ══════════════════════════════════════════════
            // Detect language from caller's phone number and prepend hint to instructions
            var detectedLang = OpenAiSdkClient.DetectLanguageStatic(_callerId);
            var detectedLangName = OpenAiSdkClient.GetLanguageNameStatic(detectedLang);
            var langPreamble = detectedLang != "en"
                ? $"[LANGUAGE PREWARN] The caller's phone number indicates they are from a {detectedLangName}-speaking country. " +
                  $"EXPECT the caller to speak {detectedLangName}. Transcribe and respond in {detectedLangName} by default. " +
                  $"If they switch to another language, follow them.\n\n"
                : "";

            var options = new ConversationSessionOptions
            {
                Instructions = langPreamble + _systemPrompt,
                Voice = MapVoice(_settings.Voice),
                InputAudioFormat = ConversationAudioFormat.G711Alaw,   // Native A-law in = best STT
                OutputAudioFormat = ConversationAudioFormat.Pcm16,      // PCM16 24kHz out = best TTS fidelity
                InputTranscriptionOptions = new ConversationInputTranscriptionOptions
                {
                    Model = "whisper-1"
                },
                TurnDetectionOptions = BuildVadOptions(_useSemanticVad)
            };

            options.Tools.Add(OpenAiSdkClient.BuildSyncBookingDataToolStatic());
            options.Tools.Add(OpenAiSdkClient.BuildClarifyAddressToolStatic());
            options.Tools.Add(OpenAiSdkClient.BuildBookTaxiToolStatic());
            options.Tools.Add(OpenAiSdkClient.BuildCreateBookingToolStatic());
            options.Tools.Add(OpenAiSdkClient.BuildFindLocalEventsToolStatic());
            options.Tools.Add(OpenAiSdkClient.BuildEndCallToolStatic());

            await _session.ConfigureSessionAsync(options);

            _logger.LogInformation("✅ Session configured (A-law in / PCM16 24kHz out, model={Model})", _settings.Model);

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

        _logger.LogInformation("Disconnecting from OpenAI Realtime API (HighSample)");
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
        OnGoodbyeWithoutBooking = null;
        GetQueuedFrames = null;
        NoReplyContextProvider = null;

        ResetCallState(null);
        _logger.LogInformation("OpenAiSdkClientHighSample fully disposed — all state cleared");
    }

    // =========================
    // AUDIO INPUT (SIP A-law → OpenAI natively — no upsampling)
    // =========================
    public void SendAudio(byte[] alawData)
    {
        if (!IsConnected || alawData == null || alawData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;
        if (Volatile.Read(ref _toolInFlight) == 1) return;
        // ── Echo suppression: block mic audio in the echo guard window after Ada finishes ──
        var adaFinished = Volatile.Read(ref _lastAdaFinishedAt);
        if (adaFinished > 0 && NowMs() - adaFinished < ECHO_GUARD_MS) return;

        try
        {
            // ══════════════════════════════════════════════
            // HYBRID MODE: Send raw G.711 A-law directly to OpenAI
            // No upsampling — native A-law gives cleanest STT
            // ══════════════════════════════════════════════
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
            Interlocked.Exchange(ref _responseCancelling, 1);
            await _session!.CancelResponseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling response");
            Interlocked.Exchange(ref _responseCancelling, 0);
            Interlocked.Exchange(ref _responseActive, 0);
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
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(50);
                    if (Volatile.Read(ref _responseActive) == 0) break;
                }
                Interlocked.Exchange(ref _responseCancelling, 0);
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
    // PLAYOUT COMPLETION
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
                TurnDetectionOptions = BuildVadOptions(useSemantic)
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
            case ConversationInputSpeechStartedUpdate:
                Interlocked.Exchange(ref _inGreetingPhase, 0);
                HandleSpeechStarted();
                break;

            case ConversationInputSpeechFinishedUpdate:
                Log("🔇 User finished speaking");
                break;

            case ConversationItemStreamingPartDeltaUpdate delta:
                if (delta.AudioBytes != null)
                {
                    Interlocked.Exchange(ref _hasEnqueuedAudio, 1);

                    // ══════════════════════════════════════════════
                    // KEY DIFFERENCE: Receive PCM16 24kHz, convert to A-law
                    // Anti-aliasing LPF + decimation + A-law encoding
                    // ══════════════════════════════════════════════
                    var pcm24k = delta.AudioBytes.ToArray();
                    var alawOut = _egressResampler.Convert(pcm24k);
                    OnAudio?.Invoke(alawOut);
                }
                break;

            case ConversationItemStreamingFinishedUpdate itemFinished:
                if (itemFinished.FunctionCallId is not null)
                {
                    Interlocked.Exchange(ref _toolInFlight, 1);
                    Interlocked.Exchange(ref _responseActive, 0);
                    _ = HandleToolCallAsync(itemFinished);
                }
                else if (itemFinished.MessageContentParts?.Count > 0)
                {
                    foreach (var part in itemFinished.MessageContentParts)
                    {
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

            case ConversationInputTranscriptionFinishedUpdate userTranscript:
                Interlocked.Exchange(ref _noReplyCount, 0);
                var transcript = userTranscript.Transcript;
                _lastUserTranscript = transcript;
                OnTranscript?.Invoke("User", transcript);

                if (!string.IsNullOrWhiteSpace(transcript) && transcript.Length > 2)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_session == null || !IsConnected) return;
                            var grounding =
                                "[SYSTEM: TRANSCRIPT GROUNDING — DO NOT SPEAK THIS]\n" +
                                $"Caller exact words (verbatim): \"{transcript}\".\n" +
                                "Use these EXACT words for tool arguments only. Never repeat them aloud.";
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

            case ConversationErrorUpdate errorUpdate:
                _logger.LogError("OpenAI Realtime error: {Msg}", errorUpdate.Message);
                break;

            default:
                HandleGenericUpdate(update);
                break;
        }
    }

    private void HandleGenericUpdate(ConversationUpdate update)
    {
        var typeName = update.GetType().Name;

        if (typeName.Contains(TYPE_SESSION_STARTED) || typeName.Contains(TYPE_SESSION_CREATED))
        {
            Log("✅ Realtime Session Started (A-law in / PCM16 24kHz out)");
        }
        else if (typeName.Contains(TYPE_RESPONSE_STARTED) || typeName.Contains(TYPE_RESPONSE_CREATED))
        {
            Interlocked.Exchange(ref _responseActive, 1);
            Interlocked.Exchange(ref _hasEnqueuedAudio, 0);
            Interlocked.Increment(ref _noReplyWatchdogId);
            Log("🎤 Response started");
        }
        else if (typeName.Contains(TYPE_RESPONSE_DONE) || typeName.Contains(TYPE_RESPONSE_FINISHED))
        {
            Interlocked.Exchange(ref _responseActive, 0);
            Interlocked.Exchange(ref _responseCancelling, 0);
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
                // Fallback watchdog: wait for playout to drain, then if no reply after 10s,
                // start no-reply prompting. This prevents premature firing during long audio.
                var wdId = Volatile.Read(ref _noReplyWatchdogId);
                _ = Task.Run(async () =>
                {
                    // Wait for playout queue to drain before starting the 10s countdown
                    for (int i = 0; i < 600; i++) // max 60s
                    {
                        if (Volatile.Read(ref _noReplyWatchdogId) != wdId) return;
                        if (Volatile.Read(ref _callEnded) != 0) return;
                        try
                        {
                            if (GetQueuedFrames == null || GetQueuedFrames() <= 0) break;
                        }
                        catch { break; }
                        await Task.Delay(100);
                    }
                    
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
        else
        {
            if (!typeName.Contains("InputAudio") && !typeName.Contains("RateLimit"))
                Log($"📎 Unknown update type: {typeName}");
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
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>?>(argsJson, _jsonOptions) ?? new();

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

                var outputItem = ConversationItem.CreateFunctionCallOutput(
                    callId: itemFinished.FunctionCallId!,
                    output: resultJson);
                await session.AddItemAsync(outputItem);

                // ── Mid-tool goodbye guard ──
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
            var lang = OpenAiSdkClient.DetectLanguageStatic(_callerId);
            var langName = OpenAiSdkClient.GetLanguageNameStatic(lang);
            var localizedGreeting = OpenAiSdkClient.GetLocalizedGreetingStatic(lang);

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
    // NO-REPLY WATCHDOG
    // =========================
    private void StartNoReplyWatchdog()
    {
        if (Volatile.Read(ref _callEnded) != 0) return;
        if (Volatile.Read(ref _toolInFlight) == 1) return;
        if (Volatile.Read(ref _inGreetingPhase) == 1) return;  // #7

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
                var noReply = Volatile.Read(ref _noReplyCount);
                var syncs = Volatile.Read(ref _syncCallCount);
                var queueDepth = -1;
                try { queueDepth = GetQueuedFrames?.Invoke() ?? -1; } catch { }
                Log($"💓 Keepalive: connected={IsConnected}, resp={active}, tool={tool}, ended={ended}, " +
                    $"noReply={noReply}/{MAX_NO_REPLY_PROMPTS}, syncs={syncs}, queue={queueDepth}, codec=hybrid-alaw-pcm24k");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"⚠️ Keepalive error: {ex.Message}"); }
    }

    private static readonly Regex _adaGoodbyePattern = new(
        @"\b(goodbye|good\s*bye|bye\s*bye)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void CheckGoodbye(string text)
    {
        // Only fire if Ada is saying goodbye AND no booking was confirmed this call
        if (_adaGoodbyePattern.IsMatch(text) && Volatile.Read(ref _bookingConfirmed) == 0)
        {
            Log("👋 Goodbye detected in Ada's speech (no booking confirmed)");
            Interlocked.Exchange(ref _ignoreUserAudio, 1);
            OnGoodbyeWithoutBooking?.Invoke();
        }
    }

    private static readonly Regex _userGoodbyePattern = new(
        @"\b(bye|goodbye|good\s*bye|cheers|that'?s? (all|it)|thank(s| you).*bye|no.*(thank|that'?s? all))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private bool UserSaidGoodbyeDuringTool()
    {
        var transcript = _lastUserTranscript;
        if (string.IsNullOrWhiteSpace(transcript)) return false;
        return _userGoodbyePattern.IsMatch(transcript);
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
    // VOICE MAPPING
    // =========================
    private static ConversationVoice MapVoice(string voice) => voice?.ToLowerInvariant() switch
    {
        "alloy" => ConversationVoice.Alloy,
        "echo" => ConversationVoice.Echo,
        "shimmer" => ConversationVoice.Shimmer,
        _ => ConversationVoice.Shimmer
    };

    /// <summary>
    /// Build VAD turn detection options — centralised to avoid duplication between
    /// ConnectAsync and SetVadModeAsync.
    /// </summary>
    private static ConversationTurnDetectionOptions BuildVadOptions(bool useSemantic) =>
        useSemantic
            ? ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                detectionThreshold: 0.25f,
                prefixPaddingDuration: TimeSpan.FromMilliseconds(500),
                silenceDuration: TimeSpan.FromMilliseconds(1400))
            : ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                detectionThreshold: 0.2f,
                prefixPaddingDuration: TimeSpan.FromMilliseconds(400),
                silenceDuration: TimeSpan.FromMilliseconds(600));

    // =========================
    // DISPOSE
    // =========================
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
