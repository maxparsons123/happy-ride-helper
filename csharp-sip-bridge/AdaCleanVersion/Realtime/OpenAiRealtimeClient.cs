using AdaCleanVersion.Audio;
using AdaCleanVersion.Services;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using TaxiBot.Deterministic;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// v9 — Clean transport bridge. Zero orchestration logic.
/// 
/// This class does exactly 4 things:
///   1. Audio: RTP ↔ OpenAI (G.711 passthrough via RealtimeAudioBridge)
///   2. Mic gate: arm on audio start, ungate when playout drains, barge-in via response.cancel
///   3. Tool passthrough: sync_booking_data → engine.Step() → execute action
///   4. Log transcripts (no processing, no fallback, no state changes)
///
/// The DeterministicBookingEngine owns ALL state.
/// The AI model is voice-only — it speaks what the engine tells it to.
/// AudioBridge never touches state. ToolRouter never touches audio.
/// </summary>
public sealed class OpenAiRealtimeClient : IAsyncDisposable
{
    private readonly string _callId;
    private readonly string _voice;
    private readonly G711CodecType _codec;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _systemPrompt;

    // ── Components (only 4) ──
    private readonly IRealtimeTransport _transport;
    private readonly MicGateController _micGate;
    private readonly RealtimeAudioBridge _audio;
    private readonly RealtimeToolRouter _tools;

    // ── Events ──
    public event Action<string>? OnLog;
    public event Action<byte[]>? OnAudioOut;
    public event Action? OnBargeIn;
    public event Action? OnMicUngated;
    public event Action<string>? OnTransfer;
    public event Action<string>? OnHangup;
    public event Action<Stage>? OnStageChanged;

    public OpenAiRealtimeClient(
        string apiKey,
        string model,
        string voice,
        string callId,
        string systemPrompt,
        RTPSession rtpSession,
        ILogger logger,
        FareGeocodingService? fareService = null,
        IcabbiBookingService? icabbiService = null,
        string? callerPhone = null,
        G711CodecType codec = G711CodecType.PCMU,
        IRealtimeTransport? transport = null,
        VoIPMediaSession? mediaSession = null,
        DeterministicBookingEngine? engine = null)
    {
        _callId = callId;
        _voice = voice;
        _codec = codec;
        _logger = logger;
        _apiKey = apiKey;
        _model = model;
        _systemPrompt = systemPrompt;

        // ── Transport ──
        _transport = transport ?? new WebSocketRealtimeTransport();
        _transport.OnMessage += HandleServerMessageAsync;
        _transport.OnDisconnected += reason => Log($"🔌 Transport disconnected: {reason}");

        // ── Mic gate (simple energy-based) ──
        _micGate = new MicGateController();

        // ── Audio bridge (deterministic 20ms pacing, tiny jitter buffer) ──
        _audio = new RealtimeAudioBridge(rtpSession, _transport, codec, _micGate, _cts.Token, mediaSession);
        _audio.OnLog += Log;
        _audio.OnAudioOut += frame => { try { OnAudioOut?.Invoke(frame); } catch { } };
        _audio.OnBargeIn += () => { try { OnBargeIn?.Invoke(); } catch { } };
        _audio.OnMicUngated += () => { try { OnMicUngated?.Invoke(); } catch { } };

        // ── Deterministic engine (shared or new) ──
        var eng = engine ?? new DeterministicBookingEngine();

        // Geocode lambda
        Func<string, Task<GeocodeResult>> geocodeFn = async rawAddress =>
        {
            if (fareService == null)
                return new GeocodeResult(false, Error: "No geocode service");
            try
            {
                var result = await fareService.GeocodeAddressAsync(
                    rawAddress, "address", callerPhone, _cts.Token);
                if (result == null) return new GeocodeResult(false, Error: "Null result");
                if (result.IsAmbiguous) return new GeocodeResult(false, Error: "Ambiguous");
                return new GeocodeResult(true, NormalizedAddress: result.Address);
            }
            catch (Exception ex) { return new GeocodeResult(false, Error: ex.Message); }
        };

        // Dispatch lambda
        Func<BookingSlots, Task<DispatchResult>> dispatchFn = async slots =>
        {
            if (icabbiService == null)
                return new DispatchResult(false, Error: "No dispatch service");
            try
            {
                var booking = new AdaCleanVersion.Models.StructuredBooking
                {
                    CallerName = "Caller",
                    Pickup = new AdaCleanVersion.Models.StructuredAddress
                    {
                        RawDisplayName = slots.Pickup.Normalized ?? slots.Pickup.Raw ?? ""
                    },
                    Destination = new AdaCleanVersion.Models.StructuredAddress
                    {
                        RawDisplayName = slots.Dropoff.Normalized ?? slots.Dropoff.Raw ?? ""
                    },
                    Passengers = slots.Passengers ?? 1,
                    PickupTime = slots.PickupTime?.Raw ?? "ASAP",
                };
                var result = await icabbiService.CreateAndDispatchAsync(
                    booking, null!, callerPhone ?? "", callerName: null, icabbiDriverId: null, icabbiVehicleId: null, ct: _cts.Token);
                return result.Success
                    ? new DispatchResult(true, BookingId: result.JourneyId)
                    : new DispatchResult(false, Error: result.Message);
            }
            catch (Exception ex) { return new DispatchResult(false, Error: ex.Message); }
        };

        // ── Tool router (engine + backend lambdas) ──
        _tools = new RealtimeToolRouter(eng, _transport, geocodeFn, dispatchFn, _cts.Token);
        _tools.OnLog += Log;
        _tools.OnInstruction += instruction => Log($"📋 Instruction: {instruction}");
        _tools.OnTransfer += reason => { try { OnTransfer?.Invoke(reason); } catch { } };
        _tools.OnHangup += reason => { try { OnHangup?.Invoke(reason); } catch { } };
        _tools.OnStageChanged += stage => { try { OnStageChanged?.Invoke(stage); } catch { } };
    }

    private readonly TaskCompletionSource _sessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        // Single session config — static VAD, no dynamic switching
        var sessionConfig = RealtimeSessionConfig.Build(_systemPrompt, _voice, _codec);
        await _transport.SendAsync(sessionConfig, _cts.Token);

        var fmt = _codec == G711CodecType.PCMU ? "g711_ulaw" : "g711_alaw";
        Log($"📋 Session configured: {fmt}, static VAD, sync_booking_data tool");

        _audio.Start();
        Log("✅ Audio bridge active");

        // Wait for OpenAI to confirm session is ready before greeting
        Log("⏳ Waiting for session.updated before greeting...");
        var timeout = Task.Delay(5000, _cts.Token);
        var ready = await Task.WhenAny(_sessionReady.Task, timeout);
        if (ready == timeout)
            Log("⚠ session.updated timeout — sending greeting anyway");

        await _tools.StartAsync();
        Log("📢 Engine started");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _audio.Dispose();
        await _transport.DisposeAsync();
        _cts.Dispose();
        Log("🔌 Disconnected");
    }

    // ─── Event Routing (pure passthrough) ───────────────────

    private async Task HandleServerMessageAsync(string json)
    {
        var evt = RealtimeEventParser.Parse(json);

        switch (evt.Type)
        {
            case RealtimeEventType.AudioDelta:
                _audio.HandleAudioDelta(evt.AudioBase64);
                break;

            case RealtimeEventType.ResponseCreated:
                break; // no-op — wait for actual audio

            case RealtimeEventType.AudioStarted:
                _micGate.Arm();
                Log("🔇 Mic gated (audio started)");
                break;

            case RealtimeEventType.AudioDone:
                _audio.HandleResponseAudioDone();
                break;

            case RealtimeEventType.SpeechStarted:
                _tools.ResetTurn();
                if (_micGate.IsGated)
                {
                    if (!_audio.HandleBargeIn())
                        Log("🎤 Barge-in debounced");
                }
                break;

            case RealtimeEventType.SpeechStopped:
                break; // no-op — model auto-responds

            case RealtimeEventType.CallerTranscript:
                if (!string.IsNullOrWhiteSpace(evt.Transcript))
                    Log($"👤 Caller: {evt.Transcript}");
                break;

            case RealtimeEventType.AdaTranscriptDone:
                if (!string.IsNullOrWhiteSpace(evt.Transcript))
                    Log($"🤖 AI: {evt.Transcript}");
                break;

            case RealtimeEventType.ToolCallDone:
                // CRITICAL: never block receive loop on tool execution.
                // Audio deltas must remain real-time even during geocode/dispatch.
                _ = Task.Run(async () =>
                {
                    try { await _tools.HandleToolCallAsync(evt); }
                    catch (Exception ex) { Log($"⚠ Tool routing error: {ex.Message}"); }
                }, _cts.Token);
                break;

            case RealtimeEventType.ResponseCanceled:
                break; // barge-in artifact, ignore

            case RealtimeEventType.SessionCreated:
                Log("📡 Session created");
                break;

            case RealtimeEventType.SessionUpdated:
                Log("📋 Session config accepted — ready");
                _sessionReady.TrySetResult();
                break;

            case RealtimeEventType.Error:
                if (evt.ErrorMessage != null &&
                    !evt.ErrorMessage.Contains("no active response found") &&
                    !evt.ErrorMessage.Contains("buffer too small"))
                    Log($"⚠ Error: {evt.ErrorMessage}");
                break;
        }
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
