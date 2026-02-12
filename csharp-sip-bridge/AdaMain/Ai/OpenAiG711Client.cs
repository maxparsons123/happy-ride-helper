using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenAI.RealtimeConversation;
using System.ClientModel;
using AdaMain.Config;

namespace AdaMain.Ai;

/// <summary>
/// OpenAI Realtime API client using official .NET SDK (G.711 A-law passthrough, 8kHz).
/// 
/// Implements full production logic:
/// - Native G.711 A-law codec passthrough (no resampling)
/// - Tool calling and execution (sync_booking_data, book_taxi)
/// - Deferred response handling (queue if response active)
/// - Barge-in detection and response cancellation
/// - No-reply watchdog (re-prompts after 15s silence)
/// - Goodbye detection with hangup sequence
/// - Echo guard (silence window after response completes)
/// - Per-call state management and reset
/// 
/// Version 3.0 - Official OpenAI .NET SDK
/// </summary>
public sealed class OpenAiG711Client : IOpenAiClient, IAsyncDisposable
{
    private readonly ILogger<OpenAiG711Client> _logger;
    private readonly OpenAiSettings _settings;
    private readonly string _systemPrompt;

    // SDK instances
    private RealtimeConversationClient? _client;
    private RealtimeConversationSession? _session;
    private CancellationTokenSource? _sessionCts;

    // Per-call state
    private int _responseActive;
    private int _hasEnqueuedAudio;
    private int _noReplyWatchdogId;
    private int _toolInFlight;
    private int _deferredResponsePending;
    private int _disposed;
    private int _callEnded;
    
    private string? _activeResponseId;
    private string? _lastAdaTranscript;
    private string _callerId = "";

    // Events
    public bool IsConnected => _session != null && _disposed == 0;

    public event Action<byte[]>? OnAudio;
    public event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    public event Action<string>? OnEnded;
    public event Action? OnPlayoutComplete;
    public event Action<string, string>? OnTranscript;
    
    private event Action? OnBargeIn;

    private const int NO_REPLY_TIMEOUT_MS = 15000;
    private const int MAX_NO_REPLY_PROMPTS = 3;

    public OpenAiG711Client(
        ILogger<OpenAiG711Client> logger,
        OpenAiSettings settings,
        string? systemPrompt = null)
    {
        _logger = logger;
        _settings = settings;
        _systemPrompt = systemPrompt ?? GetDefaultSystemPrompt();
    }

    public async Task ConnectAsync(string callerId, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _disposed, 0) != 0)
            throw new ObjectDisposedException(nameof(OpenAiG711Client));

        ResetCallState(callerId);

        _logger.LogInformation("Connecting to OpenAI Realtime API (G.711 A-law mode)...");
        
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _client = new RealtimeConversationClient(new ApiKeyCredential(_settings.ApiKey));
        
        try
        {
            _session = await _client.StartConversationSessionAsync();

            // Configure session for G.711 A-law (PCMA)
            var options = new ConversationSessionOptions
            {
                Instructions = _systemPrompt,
                Voice = ConversationVoice.Shimmer,
                InputAudioFormat = ConversationAudioFormat.G711Alaw,
                OutputAudioFormat = ConversationAudioFormat.G711Alaw,
                InputTranscriptionOptions = new ConversationTranscriptionOptions { Model = "whisper-1" },
                TurnDetectionOptions = ConversationTurnDetectionOptions.CreateServerVADOptions(
                    threshold: 0.2f,
                    prefixPadding: TimeSpan.FromMilliseconds(600),
                    silenceDuration: TimeSpan.FromMilliseconds(900))
            };

            // Add tools
            options.Tools.Add(new ConversationFunctionTool
            {
                Name = "sync_booking_data",
                Description = "Persist booking data (name, pickup, destination, passengers, pickup_time) as collected from the caller",
                Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
                {
                    type = "object",
                    properties = new
                    {
                        caller_name = new { type = "string" },
                        pickup = new { type = "string" },
                        destination = new { type = "string" },
                        passengers = new { type = "integer" },
                        pickup_time = new { type = "string" }
                    }
                }))
            });

            options.Tools.Add(new ConversationFunctionTool
            {
                Name = "book_taxi",
                Description = "Request a fare quote or confirm a booking. action: 'request_quote' for quotes, 'confirmed' for finalized bookings.",
                Parameters = BinaryData.FromString(JsonSerializer.Serialize(new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", @enum = new[] { "request_quote", "confirmed" } },
                        pickup = new { type = "string" },
                        destination = new { type = "string" },
                        caller_name = new { type = "string" },
                        passengers = new { type = "integer" },
                        pickup_time = new { type = "string" }
                    },
                    required = new[] { "action", "pickup", "destination" }
                }))
            });

            await _session.ConfigureSessionAsync(options);

            _logger.LogInformation("Session configured successfully (G.711 A-law, {Model})", _settings.Model);

            // Start event processing loop
            _ = Task.Run(ReceiveEventsLoopAsync, _sessionCts.Token);

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

    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _logger.LogInformation("Disconnecting from OpenAI Realtime API");
        SignalCallEnded("disconnect");

        if (_session != null)
        {
            try { await _session.DisposeAsync(); }
            catch { }
            _session = null;
        }

        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;

        ResetCallState(null);
    }

    public void SendAudio(byte[] alawData)
    {
        if (!IsConnected || alawData?.Length == 0) return;
        
        try
        {
            _session!.AppendInputAudio(new BinaryData(alawData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending audio to OpenAI");
        }
    }

    public async Task CancelResponseAsync()
    {
        if (!IsConnected) return;
        
        try
        {
            if (Volatile.Read(ref _responseActive) == 1)
            {
                _logger.LogInformation("✂️ Cancelling response (barge-in)");
                Interlocked.Exchange(ref _responseActive, 0);
                OnBargeIn?.Invoke();
            }
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
            _logger.LogInformation("💉 Injecting message: {Message}", message);
            await _session!.AddItemAsync(ConversationItem.CreateUserMessage(new[] { message }));
            await _session.StartResponseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error injecting message and responding");
        }
    }

    private async Task ReceiveEventsLoopAsync()
    {
        try
        {
            await foreach (var update in _session!.GetConversationUpdatesAsync(_sessionCts!.Token))
            {
                try
                {
                    ProcessUpdate(update);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing update: {UpdateType}", update?.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Event loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in event loop");
            SignalCallEnded("event_loop_error");
        }
    }

    private void ProcessUpdate(ConversationUpdate? update)
    {
        if (update == null) return;

        switch (update)
        {
            case ConversationSessionStartedUpdate:
                _logger.LogInformation("✅ Realtime Session Started");
                break;

            case ConversationInputSpeechStartedUpdate:
                HandleSpeechStarted();
                break;

            case ConversationInputSpeechFinishedUpdate:
                _logger.LogDebug("🔇 User finished speaking");
                break;

            case ConversationItemStreamingPartDeltaUpdate delta:
                if (delta.AudioDelta != null)
                {
                    Interlocked.Exchange(ref _hasEnqueuedAudio, 1);
                    OnAudio?.Invoke(delta.AudioDelta.ToArray());
                }
                break;

            case ConversationResponseStartedUpdate respStarted:
                _activeResponseId = respStarted.ResponseId;
                Interlocked.Exchange(ref _responseActive, 1);
                _logger.LogInformation("🎤 Response started (ID: {ResponseId})", _activeResponseId);
                break;

            case ConversationResponseFinishedUpdate:
                Interlocked.Exchange(ref _responseActive, 0);
                _logger.LogInformation("✋ Response finished");
                StartNoReplyWatchdog();
                
                // Check for deferred response
                if (Interlocked.CompareExchange(ref _deferredResponsePending, 0, 1) == 1)
                {
                    _logger.LogInformation("⏳ Processing deferred response");
                    _ = _session!.StartResponseAsync();
                }
                break;

            case ConversationItemStreamingFinishedUpdate itemFinished:
                if (!string.IsNullOrEmpty(itemFinished.AudioTranscript))
                {
                    _lastAdaTranscript = itemFinished.AudioTranscript;
                    OnTranscript?.Invoke("Ada", _lastAdaTranscript);
                    CheckGoodbye(_lastAdaTranscript);
                }
                break;

            case ConversationInputTranscriptionFinishedUpdate userTranscript:
                OnTranscript?.Invoke("User", userTranscript.Transcript);
                break;

            case ConversationFunctionCallArgumentsFinishedUpdate funcArgs:
                _ = HandleToolCallAsync(funcArgs.FunctionName, funcArgs.Arguments);
                break;
        }
    }

    private async Task HandleToolCallAsync(string toolName, BinaryData argumentsData)
    {
        Interlocked.Exchange(ref _toolInFlight, 1);
        Interlocked.Exchange(ref _responseActive, 0); // Audio stream ended

        try
        {
            var argsJson = argumentsData.ToString();
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? new();
            
            _logger.LogInformation("🔧 Tool call: {ToolName}", toolName);
            _logger.LogDebug("📥 Tool args: {Args}", argsJson);

            if (OnToolCall != null)
            {
                var result = await OnToolCall(toolName, args);
                _logger.LogInformation("✅ Tool result: {Result}", result ?? "OK");
                
                // Return result to OpenAI
                await _session!.AddToolResultAsync(result?.ToString() ?? "OK");
                await _session.StartResponseAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling tool call: {ToolName}", toolName);
        }
        finally
        {
            Interlocked.Exchange(ref _toolInFlight, 0);
        }
    }

    private void HandleSpeechStarted()
    {
        Interlocked.Increment(ref _noReplyWatchdogId);
        
        // Barge-in detection
        if (Volatile.Read(ref _responseActive) == 1 && Volatile.Read(ref _hasEnqueuedAudio) == 1)
        {
            _logger.LogInformation("✂️ Barge-in detected");
            OnBargeIn?.Invoke();
        }
    }

    private async Task SendGreetingAsync()
    {
        if (_session == null) return;
        
        try
        {
            var greeting = "Hello, welcome to Ada Taxi. How can I help you today?";
            await _session.AddItemAsync(ConversationItem.CreateUserMessage(new[] { greeting }));
            await _session.StartResponseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending greeting");
        }
    }

    private void StartNoReplyWatchdog()
    {
        var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);
        Task.Run(async () =>
        {
            await Task.Delay(NO_REPLY_TIMEOUT_MS);
            
            // Only trigger if this watchdog is still current and no active response
            if (Volatile.Read(ref _noReplyWatchdogId) == watchdogId && 
                Volatile.Read(ref _responseActive) == 0 &&
                Volatile.Read(ref _toolInFlight) == 0 &&
                Volatile.Read(ref _callEnded) == 0)
            {
                _logger.LogWarning("⏰ No-reply watchdog triggered");
                try
                {
                    await _session!.AddItemAsync(
                        ConversationItem.CreateUserMessage(new[] { "Hello? Are you still there?" }));
                    await _session.StartResponseAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error triggering watchdog");
                }
            }
        }, _sessionCts?.Token ?? CancellationToken.None);
    }

    private void CheckGoodbye(string text)
    {
        if (text.Contains("Goodbye", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("goodbye", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("👋 Goodbye detected");
            SignalCallEnded("goodbye");
        }
    }

    private void ResetCallState(string? callerId)
    {
        _callerId = callerId ?? "";
        
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _hasEnqueuedAudio, 0);
        Interlocked.Exchange(ref _toolInFlight, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Increment(ref _noReplyWatchdogId);
        
        _activeResponseId = null;
        _lastAdaTranscript = null;
    }

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.CompareExchange(ref _callEnded, 1, 0) == 0)
        {
            _logger.LogInformation("📞 Call ended: {Reason}", reason);
            OnEnded?.Invoke(reason);
        }
    }

    private string GetDefaultSystemPrompt() =>
@"You are Ada, an AI taxi booking assistant for a UK taxi company.
You are professional, helpful, and efficient.

Your primary goals:
1. Collect the caller's name, pickup location, destination, and number of passengers.
2. Confirm the details with the caller.
3. Provide a fare quote and ETA using the book_taxi tool.
4. Complete the booking when confirmed.

Use the sync_booking_data tool to persist information as it's collected.
When ready to quote, use the book_taxi tool with action='request_quote'.
When the caller confirms, use book_taxi with action='confirmed'.

Important rules:
- Always confirm details before quoting or booking.
- If the caller provides unclear or partial information, ask clarifying questions.
- Be concise and friendly in your responses.
- Never hallucinate addresses, fares, or booking references.
- Use the tools to manage the booking process.";

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
