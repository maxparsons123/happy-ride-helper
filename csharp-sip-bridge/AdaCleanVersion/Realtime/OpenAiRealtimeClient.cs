using System.Text.RegularExpressions;
using AdaCleanVersion.Audio;
using AdaCleanVersion.Services;
using AdaCleanVersion.Session;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using TaxiBot.Deterministic;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// v7 Orchestrator: thin top-level class that wires components and routes events.
/// 
/// Components:
///   RealtimeAudioBridge       — RTP ↔ OpenAI audio (G.711 passthrough + playout)
///   MicGateController         — deterministic mic gating (buffer-all, flush-tail)
///   DeterministicBookingEngine — single-authority state machine (no AI state)
///   RealtimeToolRouter        — tool call → engine.Step() → action execution
///   InstructionCoordinator    — session.update sequencing (reprompts only)
///   IRealtimeTransport        — raw WebSocket protocol (swappable)
///
/// Engine drives ALL state. AI is voice-only. No transcript fallback.
/// </summary>
public sealed class OpenAiRealtimeClient : IAsyncDisposable
{
    private readonly string _callId;
    private readonly string _voice;
    private readonly G711CodecType _codec;
    private readonly CleanCallSession _session;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    // ── Components ──
    private readonly IRealtimeTransport _transport;
    private readonly MicGateController _micGate;
    private readonly RealtimeAudioBridge _audio;
    private readonly RealtimeToolRouter _tools;
    private readonly InstructionCoordinator _instructions;
    private readonly DeterministicBookingEngine _engine;

    // ── Events ──
    public event Action<string>? OnLog;
    public event Action<byte[]>? OnAudioOut;
    public event Action? OnBargeIn;
    public event Action? OnMicUngated;
    public event Action<string>? OnTransfer;
    public event Action<string>? OnHangup;

    public OpenAiRealtimeClient(
        string apiKey,
        string model,
        string voice,
        string callId,
        RTPSession rtpSession,
        CleanCallSession session,
        ILogger logger,
        FareGeocodingService? fareService = null,
        IcabbiBookingService? icabbiService = null,
        G711CodecType codec = G711CodecType.PCMU,
        IRealtimeTransport? transport = null)
    {
        _callId = callId;
        _voice = voice;
        _codec = codec;
        _session = session;
        _logger = logger;

        // ── Build component graph ──
        _transport = transport ?? new WebSocketRealtimeTransport();

        _micGate = new MicGateController();

        _audio = new RealtimeAudioBridge(rtpSession, _transport, codec, _micGate, _cts.Token);
        _audio.OnLog += Log;
        _audio.OnAudioOut += frame => { try { OnAudioOut?.Invoke(frame); } catch { } };
        _audio.OnBargeIn += () => { try { OnBargeIn?.Invoke(); } catch { } };
        _audio.OnMicUngated += () =>
        {
            _session.NotifyMicUngated();
            try { OnMicUngated?.Invoke(); } catch { }
        };

        _instructions = new InstructionCoordinator(
            _transport,
            () => _micGate.IsGated,
            _cts.Token);
        _instructions.OnLog += Log;

        // ── Deterministic engine + tool router ──
        _engine = new DeterministicBookingEngine();

        // Geocode lambda: wraps FareGeocodingService → GeocodeResult
        Func<string, Task<GeocodeResult>> geocodeFn = async (rawAddress) =>
        {
            if (fareService == null)
                return new GeocodeResult(Ok: false, Error: "No geocode service configured");

            try
            {
                var geocoded = await fareService.GeocodeAddressAsync(
                    rawAddress, "address", session.CallerId, _cts.Token);

                if (geocoded == null)
                    return new GeocodeResult(Ok: false, Error: "Geocode returned null");

                if (geocoded.IsAmbiguous)
                    return new GeocodeResult(Ok: false, Error: "Address is ambiguous");

                return new GeocodeResult(
                    Ok: true,
                    NormalizedAddress: geocoded.Address);
            }
            catch (Exception ex)
            {
                return new GeocodeResult(Ok: false, Error: ex.Message);
            }
        };

        // Dispatch lambda: wraps IcabbiBookingService → DispatchResult
        Func<BookingSlots, Task<DispatchResult>> dispatchFn = async (slots) =>
        {
            if (icabbiService == null)
                return new DispatchResult(Ok: false, Error: "No dispatch service configured");

            try
            {
                // Build a minimal StructuredBooking from engine slots
                var booking = new AdaCleanVersion.Models.StructuredBooking
                {
                    Pickup = slots.Pickup.Normalized ?? slots.Pickup.Raw ?? "",
                    Destination = slots.Dropoff.Normalized ?? slots.Dropoff.Raw ?? "",
                    Passengers = slots.Passengers ?? 1,
                    PickupTime = slots.PickupTime?.Raw ?? "ASAP",
                };

                var result = await icabbiService.CreateAndDispatchAsync(
                    booking,
                    session.Engine.FareResult,
                    session.CallerId,
                    _cts.Token);

                return result.Success
                    ? new DispatchResult(Ok: true, BookingId: result.BookingRef)
                    : new DispatchResult(Ok: false, Error: result.Error);
            }
            catch (Exception ex)
            {
                return new DispatchResult(Ok: false, Error: ex.Message);
            }
        };

        _tools = new RealtimeToolRouter(_engine, _transport, geocodeFn, dispatchFn, _cts.Token);
        _tools.OnLog += Log;
        _tools.OnInstruction += instruction => Log($"📋 Engine instruction: {instruction}");
        _tools.OnTransfer += reason => { try { OnTransfer?.Invoke(reason); } catch { } };
        _tools.OnHangup += reason => { try { OnHangup?.Invoke(reason); } catch { } };

        // ── Wire transport events ──
        _transport.OnMessage += HandleServerMessageAsync;
        _transport.OnDisconnected += reason => Log($"🔌 Transport disconnected: {reason}");

        // Build connection headers (stored for ConnectAsync)
        _apiKey = apiKey;
        _model = model;
    }

    private readonly string _apiKey;
    private readonly string _model;

    // ─── Lifecycle ──────────────────────────────────────────

    public async Task ConnectAsync()
    {
        var url = $"wss://api.openai.com/v1/realtime?model={_model}";
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {_apiKey}",
            ["OpenAI-Beta"] = "realtime=v1"
        };

        await _transport.ConnectAsync(url, headers, _cts.Token);
        Log("🔌 Connected to OpenAI Realtime");

        // Send session config
        var sessionConfig = RealtimeSessionConfig.Build(
            _session.GetSystemPrompt(), _voice, _codec);
        await _transport.SendAsync(sessionConfig, _cts.Token);

        var audioFormat = _codec == G711CodecType.PCMU ? "g711_ulaw" : "g711_alaw";
        Log($"📋 Session configured: {audioFormat} passthrough, sync_booking_data tool");

        // Start audio bridge
        _audio.Start();

        Log("✅ Bidirectional audio bridge active (v7 deterministic engine)");

        // Start deterministic engine — sends greeting via tool router
        await _tools.StartAsync();
        Log("📢 Engine started — greeting sent");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        _audio.Dispose();
        await _transport.DisposeAsync();
        _cts.Dispose();

        Log("🔌 OpenAI Realtime disconnected");
    }

    // ─── Event Routing ──────────────────────────────────────

    private async Task HandleServerMessageAsync(string json)
    {
        var evt = RealtimeEventParser.Parse(json);

        switch (evt.Type)
        {
            // ── FAST PATH: Audio deltas — must never be blocked ──
            case RealtimeEventType.AudioDelta:
                _audio.HandleAudioDelta(evt.AudioBase64);
                break;

            // ── AI response starting → arm mic gate ──
            case RealtimeEventType.ResponseCreated:
                _micGate.Arm();
                break;

            // ── AI finished sending audio → ungate mic ──
            case RealtimeEventType.AudioDone:
                _audio.HandleResponseAudioDone();
                break;

            // ── Barge-in ──
            case RealtimeEventType.SpeechStarted:
                _tools.ResetTurn();
                HandleSpeechStarted();
                break;

            // ── Speech ended (no-op — let AI auto-respond for tool calls) ──
            case RealtimeEventType.SpeechStopped:
                break;

            // ── Caller transcript ──
            case RealtimeEventType.CallerTranscript:
                HandleCallerTranscript(evt.Transcript);
                break;

            // ── Ada's spoken transcript ──
            case RealtimeEventType.AdaTranscriptDone:
                HandleAdaTranscript(evt.Transcript);
                break;

            // ── Tool call ──
            case RealtimeEventType.ToolCallDone:
                await _tools.HandleToolCallAsync(evt);
                break;

            // ── Response canceled (barge-in or truncation) ──
            case RealtimeEventType.ResponseCanceled:
                Log("🛑 Response canceled");
                break;

            // ── Session lifecycle ──
            case RealtimeEventType.SessionCreated:
                Log("📡 Session created by server");
                break;

            case RealtimeEventType.SessionUpdated:
                Log("📋 Session config accepted");
                break;

            // ── Errors ──
            case RealtimeEventType.Error:
                HandleError(evt.ErrorMessage);
                break;
        }
    }

    // ─── Event Handlers ─────────────────────────────────────

    private void HandleSpeechStarted()
    {
        if (!_micGate.IsGated)
        {
            Log("🎤 Barge-in — mic already ungated, skipping re-flush");
            return;
        }

        if (_audio.HandleBargeIn())
        {
            // Barge-in processed successfully
        }
        else
        {
            Log("🎤 Barge-in debounced");
        }
    }

    private void HandleCallerTranscript(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return;
        Log($"👤 Caller: {transcript}");
        // Tool call is the single authority. No transcript fallback.
    }

    private void HandleAdaTranscript(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return;
        var cleanText = Regex.Replace(rawText, @"^\[CORRECTION:\w+\]\s*", "").Trim();
        Log($"🤖 AI: {cleanText}");
        // No session processing — engine drives all state transitions via tool calls.
    }

    private void HandleError(string? errMsg)
    {
        if (errMsg != null && (
            errMsg.Contains("no active response found") ||
            errMsg.Contains("buffer too small")))
            return;

        Log($"⚠ OpenAI error: {errMsg}");
    }

    // ─── Logging ────────────────────────────────────────────

    private void Log(string msg)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _logger.LogInformation(msg); } catch { }
        });
        OnLog?.Invoke($"[RT:{_callId}] {msg}");
    }
}
