using AdaSdkModel.Ai;
using AdaSdkModel.Config;
using AdaSdkModel.Services;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Core;

/// <summary>
/// Manages a single call session lifecycle with G.711 A-law passthrough.
/// Simplified flow: sync stores data, book_taxi does quote/confirm.
/// </summary>
public sealed class CallSession : ICallSession
{
    private readonly ILogger<CallSession> _logger;
    private readonly AppSettings _settings;
    private readonly IOpenAiClient _aiClient;

    public IOpenAiClient AiClient => _aiClient;
    private readonly IFareCalculator _fareCalculator;
    private readonly IDispatcher _dispatcher;
    private readonly IcabbiBookingService? _icabbi;
    private readonly bool _icabbiEnabled;

    private readonly BookingState _booking = new();
    private readonly Audio.ALawThinningFilter? _thinningFilter;

    private int _disposed;
    private int _active;
    private int _bookTaxiCompleted;
    private long _lastAdaFinishedAt;

    public string SessionId { get; }
    public string CallerId { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public bool IsActive => Volatile.Read(ref _active) == 1;

    public event Action<ICallSession, string>? OnEnded;
    public event Action<BookingState>? OnBookingUpdated;
    public event Action<string, string>? OnTranscript;
    public event Action<byte[]>? OnAudioOut;
    public event Action? OnBargeIn;

    public CallSession(
        string sessionId,
        string callerId,
        ILogger<CallSession> logger,
        AppSettings settings,
        IOpenAiClient aiClient,
        IFareCalculator fareCalculator,
        IDispatcher dispatcher,
        IcabbiBookingService? icabbi = null,
        bool icabbiEnabled = false)
    {
        SessionId = sessionId;
        CallerId = callerId;
        _logger = logger;
        _settings = settings;
        _aiClient = aiClient;
        _fareCalculator = fareCalculator;
        _dispatcher = dispatcher;
        _icabbi = icabbi;
        _icabbiEnabled = icabbiEnabled;

        _booking.CallerPhone = callerId;

        // Init thinning filter if configured
        if (settings.Audio.ThinningAlpha > 0.01f)
            _thinningFilter = new Audio.ALawThinningFilter(settings.Audio.ThinningAlpha);

        // Wire up AI client events
        _aiClient.OnAudio += HandleAiAudio;
        _aiClient.OnToolCall += HandleToolCallAsync;
        _aiClient.OnEnded += reason => _ = EndAsync(reason);
        _aiClient.OnTranscript += (role, text) => OnTranscript?.Invoke(role, text);

        if (_aiClient is OpenAiSdkClient sdkClient)
        {
            sdkClient.OnBargeIn += () => OnBargeIn?.Invoke();
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _active, 1) == 1)
            return;

        _logger.LogInformation("[{SessionId}] Starting G.711 session for {CallerId}", SessionId, CallerId);
        await _aiClient.ConnectAsync(CallerId, ct);
    }

    public async Task EndAsync(string reason)
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
            return;

        _logger.LogInformation("[{SessionId}] Ending session: {Reason}", SessionId, reason);
        await _aiClient.DisconnectAsync();
        OnEnded?.Invoke(this, reason);
    }

    public void ProcessInboundAudio(byte[] alawRtp)
    {
        if (!IsActive || alawRtp.Length == 0) return;
        _aiClient.SendAudio(alawRtp);
    }

    public byte[]? GetOutboundFrame() => null;

    public void NotifyPlayoutComplete()
    {
        Volatile.Write(ref _lastAdaFinishedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (_aiClient is OpenAiSdkClient sdkClient)
            sdkClient.NotifyPlayoutComplete();
    }

    private void HandleAiAudio(byte[] alawFrame)
    {
        // 1. Volume boost
        var gain = (float)_settings.Audio.VolumeBoost;
        if (gain > 1.01f || gain < 0.99f)
            Audio.ALawVolumeBoost.ApplyInPlace(alawFrame, gain);

        // 2. High-pass thinning filter (removes bass mud, crisper telephony voice)
        _thinningFilter?.ApplyInPlace(alawFrame);

        OnAudioOut?.Invoke(alawFrame);
    }

    private async Task<object> HandleToolCallAsync(string name, Dictionary<string, object?> args)
    {
        _logger.LogDebug("[{SessionId}] Tool call: {Name} (args: {ArgCount})", SessionId, name, args.Count);

        return name switch
        {
            "sync_booking_data" => HandleSyncBookingData(args),
            "clarify_address" => HandleClarifyAddress(args),
            "book_taxi" => await HandleBookTaxiAsync(args),
            "end_call" => HandleEndCall(args),
            _ => new { error = $"Unknown tool: {name}" }
        };
    }

    // =========================
    // SYNC BOOKING DATA
    // =========================
    private static readonly HashSet<string> _rejectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "caller", "anonymous", "user", "customer", "guest", "n/a", "na", "none", ""
    };

    private int _fareAutoTriggered;
    private bool _pickupDisambiguated;
    private bool _destDisambiguated;
    private string[]? _pendingDestAlternatives;
    private string? _pendingDestClarificationMessage;

    private object HandleSyncBookingData(Dictionary<string, object?> args)
    {
        // Name validation guard — reject placeholder names
        if (args.TryGetValue("caller_name", out var n))
        {
            var nameVal = n?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(nameVal) && !_rejectedNames.Contains(nameVal))
                _booking.Name = nameVal;
            else if (_rejectedNames.Contains(nameVal ?? ""))
                _logger.LogWarning("[{SessionId}] ⛔ Rejected placeholder name: '{Name}'", SessionId, nameVal);
        }

        // ── TRANSCRIPT MISMATCH DETECTION ──
        // Compare Ada's interpreted values (tool args) with the raw Whisper STT transcript.
        // If they differ significantly, flag for clarification.
        string? mismatchWarning = null;
        if (_aiClient is OpenAiSdkClient sdkTranscript && !string.IsNullOrWhiteSpace(sdkTranscript.LastUserTranscript))
        {
            var sttText = sdkTranscript.LastUserTranscript;
            
            // Check pickup mismatch
            if (args.TryGetValue("pickup", out var pickupArg) && !string.IsNullOrWhiteSpace(pickupArg?.ToString()))
            {
                var pickupVal = pickupArg.ToString()!;
                if (IsSignificantlyDifferent(pickupVal, sttText) && !string.IsNullOrWhiteSpace(_booking.Pickup) 
                    && IsSignificantlyDifferent(pickupVal, _booking.Pickup))
                {
                    _logger.LogWarning("[{SessionId}] ⚠️ PICKUP MISMATCH: STT='{Stt}' vs Ada='{Ada}'", SessionId, sttText, pickupVal);
                    mismatchWarning = $"PICKUP address mismatch detected: the system transcribed '{sttText}' but you interpreted it as '{pickupVal}'. Ask the caller to confirm: did they say '{pickupVal}'?";
                }
            }
            
            // Check destination mismatch
            if (args.TryGetValue("destination", out var destArg) && !string.IsNullOrWhiteSpace(destArg?.ToString()))
            {
                var destVal = destArg.ToString()!;
                if (IsSignificantlyDifferent(destVal, sttText) && !string.IsNullOrWhiteSpace(_booking.Destination)
                    && IsSignificantlyDifferent(destVal, _booking.Destination))
                {
                    _logger.LogWarning("[{SessionId}] ⚠️ DEST MISMATCH: STT='{Stt}' vs Ada='{Ada}'", SessionId, sttText, destVal);
                    mismatchWarning = $"DESTINATION address mismatch detected: the system transcribed '{sttText}' but you interpreted it as '{destVal}'. Ask the caller to confirm: did they say '{destVal}'?";
                }
            }
        }

        if (args.TryGetValue("pickup", out var p))
        {
            var incoming = p?.ToString();
            if (StreetNameChanged(_booking.Pickup, incoming))
            {
                _booking.PickupLat = _booking.PickupLon = null;
                _booking.PickupStreet = _booking.PickupNumber = _booking.PickupPostalCode = _booking.PickupCity = _booking.PickupFormatted = null;
                // Reset fare auto-trigger if address changed after previous fare
                Interlocked.Exchange(ref _fareAutoTriggered, 0);
            }
            _booking.Pickup = incoming;
        }
        if (args.TryGetValue("destination", out var d))
        {
            var incoming = d?.ToString();
            if (StreetNameChanged(_booking.Destination, incoming))
            {
                _booking.DestLat = _booking.DestLon = null;
                _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
                Interlocked.Exchange(ref _fareAutoTriggered, 0);
            }
            _booking.Destination = incoming;
        }
        if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn))
            _booking.Passengers = pn;
        if (args.TryGetValue("pickup_time", out var pt))
            _booking.PickupTime = pt?.ToString();

        _logger.LogInformation("[{SessionId}] ⚡ Sync: Name={Name}, Pickup={Pickup}, Dest={Dest}, Pax={Pax}",
            SessionId, _booking.Name ?? "?", _booking.Pickup ?? "?", _booking.Destination ?? "?", _booking.Passengers);

        OnBookingUpdated?.Invoke(_booking.Clone());

        // If transcript mismatch was detected, return warning to Ada
        if (mismatchWarning != null)
            return new { success = true, warning = mismatchWarning };

        // If name is still missing, tell Ada to ask for it
        if (string.IsNullOrWhiteSpace(_booking.Name))
            return new { success = true, warning = "Name is required before booking. Ask the caller for their name." };

        // AUTO-TRIGGER: When all 5 fields are filled, automatically calculate fare
        // This matches the v3.9 prompt: "When sync_booking_data is called with all 5 fields filled,
        // the system will AUTOMATICALLY validate the addresses and calculate the fare."
        bool allFieldsFilled = !string.IsNullOrWhiteSpace(_booking.Name)
            && !string.IsNullOrWhiteSpace(_booking.Pickup)
            && !string.IsNullOrWhiteSpace(_booking.Destination)
            && _booking.Passengers > 0
            && !string.IsNullOrWhiteSpace(_booking.PickupTime);

        if (allFieldsFilled && Interlocked.CompareExchange(ref _fareAutoTriggered, 1, 0) == 0)
        {
            _logger.LogInformation("[{SessionId}] 🚀 All fields filled — auto-triggering fare calculation", SessionId);

            var pickup = _booking.Pickup!;
            var destination = _booking.Destination!;
            var callerId = CallerId;
            var sessionId = SessionId;

            _ = Task.Run(async () =>
            {
                try
                {
                    var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, destination, callerId);
                    var completed = await Task.WhenAny(aiTask, Task.Delay(10000));

                    FareResult result;
                    if (completed == aiTask)
                    {
                        result = await aiTask;
                        _logger.LogInformation("[{SessionId}] 📊 Fare result: NeedsClarification={Clarif}, Fare={Fare}, Eta={Eta}, PickupAlts={PAlts}, DestAlts={DAlts}",
                            sessionId, result.NeedsClarification, result.Fare, result.Eta,
                            result.PickupAlternatives != null ? string.Join("|", result.PickupAlternatives) : "none",
                            result.DestAlternatives != null ? string.Join("|", result.DestAlternatives) : "none");
                        if (result.NeedsClarification)
                        {
                            var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                            var destAlts = result.DestAlternatives ?? Array.Empty<string>();

                            // ADDRESS LOCK: Pickup first, stash dest for later
                            if (pickupAlts.Length > 0)
                            {
                                if (destAlts.Length > 0)
                                {
                                    _pendingDestAlternatives = destAlts;
                                    _pendingDestClarificationMessage = result.ClarificationMessage;
                                    _destDisambiguated = false;
                                }

                                _pickupDisambiguated = false;
                                _logger.LogInformation("[{SessionId}] 🔒 Address Lock: PICKUP disambiguation needed: {Alts}", sessionId, string.Join("|", pickupAlts));

                                if (_aiClient is OpenAiSdkClient sdkClarif)
                                    await sdkClarif.InjectMessageAndRespondAsync(
                                        $"[ADDRESS DISAMBIGUATION] needs_disambiguation=true, target=pickup, " +
                                        $"options=[{string.Join(", ", pickupAlts)}]. " +
                                        "Ask the caller ONLY about the PICKUP location. Do NOT mention the destination. " +
                                        "Present the options with numbers, then STOP and WAIT for their answer. " +
                                        "When they choose, call clarify_address(target=\"pickup\", selected=\"[their choice]\").");
                            }
                            else if (destAlts.Length > 0)
                            {
                                _destDisambiguated = false;
                                _logger.LogInformation("[{SessionId}] 🔒 Address Lock: DESTINATION disambiguation needed: {Alts}", sessionId, string.Join("|", destAlts));

                                if (_aiClient is OpenAiSdkClient sdkClarif)
                                    await sdkClarif.InjectMessageAndRespondAsync(
                                        $"[ADDRESS DISAMBIGUATION] needs_disambiguation=true, target=destination, " +
                                        $"options=[{string.Join(", ", destAlts)}]. " +
                                        "Pickup is confirmed. Now ask the caller ONLY about the DESTINATION. " +
                                        "Present the options with numbers, then STOP and WAIT for their answer. " +
                                        "When they choose, call clarify_address(target=\"destination\", selected=\"[their choice]\").");
                            }
                            else
                            {
                                // NeedsClarification=true but no alternatives provided — 
                                // The edge function couldn't resolve the addresses but also couldn't suggest alternatives.
                                // Instead of falling back to Nominatim (which can geocode to wrong cities and produce
                                // absurd fares like £106.50), ask the caller to specify the city/area.
                                _logger.LogWarning("[{SessionId}] ⚠️ NeedsClarification=true but no alternatives — asking caller for city/area", sessionId);

                                if (_aiClient is OpenAiSdkClient sdkAskArea)
                                {
                                    var clarMsg = !string.IsNullOrWhiteSpace(result.ClarificationMessage)
                                        ? result.ClarificationMessage
                                        : "I couldn't pinpoint those addresses. Could you tell me which city or area they're in?";

                                    await sdkAskArea.InjectMessageAndRespondAsync(
                                        $"[ADDRESS CLARIFICATION NEEDED] The addresses could not be verified. " +
                                        $"Ask the caller: \"{clarMsg}\" " +
                                        "Once they provide the city or area, call sync_booking_data again with the updated addresses including the city.");
                                }

                                Interlocked.Exchange(ref _fareAutoTriggered, 0);
                                return;
                            }

                            // Has disambiguation alternatives — already handled above, exit
                            Interlocked.Exchange(ref _fareAutoTriggered, 0);
                            return;
                        }

                        // Check if there are pending destination alternatives from a previous round
                        // (pickup was ambiguous, now resolved, but dest still needs clarification)
                        if (_pendingDestAlternatives != null && _pendingDestAlternatives.Length > 0 && !_destDisambiguated)
                        {
                            var destAltsList = string.Join(", ", _pendingDestAlternatives);
                            _logger.LogInformation("[{SessionId}] 🔄 Now resolving pending destination disambiguation: {Alts}", sessionId, destAltsList);

                            if (_aiClient is OpenAiSdkClient sdkDestClarif)
                                await sdkDestClarif.InjectMessageAndRespondAsync(
                                    $"[DESTINATION DISAMBIGUATION] Good, the pickup is confirmed. Now the DESTINATION address is ambiguous. The options are: {destAltsList}. " +
                                    "Ask the caller ONLY about the DESTINATION location. " +
                                    "Present the destination options clearly, then STOP and WAIT for their answer.");

                            _pendingDestAlternatives = null;
                            _pendingDestClarificationMessage = null;
                            Interlocked.Exchange(ref _fareAutoTriggered, 0);
                            return;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[{SessionId}] ⚠️ Edge function timed out, using fallback", sessionId);
                        result = await _fareCalculator.CalculateAsync(pickup, destination, callerId);
                    }

                    // Clear any pending disambiguation state
                    _pendingDestAlternatives = null;
                    _pendingDestClarificationMessage = null;
                    _pickupDisambiguated = true;
                    _destDisambiguated = true;

                    // FARE SANITY CHECK: If fare is absurdly high, the destination is likely wrong (STT error)
                    if (!IsFareSane(result))
                    {
                        _logger.LogWarning("[{SessionId}] 🚨 Fare sanity check FAILED — asking user to verify destination", sessionId);
                        Interlocked.Exchange(ref _fareAutoTriggered, 0);

                        if (_aiClient is OpenAiSdkClient sdkSanity)
                            await sdkSanity.InjectMessageAndRespondAsync(
                                "[FARE SANITY ALERT] The calculated fare seems unusually high, which suggests the destination may have been misheard. " +
                                "Ask the caller to confirm or repeat their DESTINATION address. " +
                                "Say something like: \"I want to make sure I have the right destination — could you repeat where you're going?\" " +
                                "When they respond, call sync_booking_data with the corrected destination.");
                        return;
                    }

                    ApplyFareResult(result);

                    if (_aiClient is OpenAiSdkClient sdk)
                        sdk.SetAwaitingConfirmation(true);

                    OnBookingUpdated?.Invoke(_booking.Clone());

                    var spokenFare = FormatFareForSpeech(_booking.Fare);
                    _logger.LogInformation("[{SessionId}] 💰 Auto fare ready: {Fare} ({Spoken}), ETA: {Eta}",
                        sessionId, _booking.Fare, spokenFare, _booking.Eta);

                    var pickupAddr = FormatAddressForReadback(result.PickupNumber, result.PickupStreet, result.PickupPostalCode, result.PickupCity);
                    var destAddr = FormatAddressForReadback(result.DestNumber, result.DestStreet, result.DestPostalCode, result.DestCity);

                    if (_aiClient is OpenAiSdkClient sdkInject)
                        await sdkInject.InjectMessageAndRespondAsync(
                            $"[FARE RESULT] The fare from {pickupAddr} to {destAddr} is {spokenFare}, " +
                            $"estimated time of arrival is {_booking.Eta}. " +
                            $"Read back the VERIFIED addresses and fare to the caller and ask them to confirm the booking.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Auto fare calculation failed", sessionId);
                    _booking.Fare = "£8.00";
                    _booking.Eta = "8 minutes";
                    OnBookingUpdated?.Invoke(_booking.Clone());

                    if (_aiClient is OpenAiSdkClient sdkFallback)
                        await sdkFallback.InjectMessageAndRespondAsync(
                            "[FARE RESULT] The estimated fare is 8 pounds, estimated time of arrival is 8 minutes. " +
                            "Read back the details to the caller and ask them to confirm.");
                }
            });

            return new { success = true, fare_calculating = true, message = "Fare is being calculated. Do NOT repeat any interjection—the system will inject the next step once address validation is complete." };
        }

        return new { success = true };
    }

    // =========================
    // CLARIFY ADDRESS
    // =========================
    private object HandleClarifyAddress(Dictionary<string, object?> args)
    {
        var target = args.TryGetValue("target", out var t) ? t?.ToString() : null;
        var selected = args.TryGetValue("selected", out var s) ? s?.ToString() : null;

        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(selected))
            return new { success = false, needs_disambiguation = false, error = "Missing target or selected address." };

        if (target == "pickup")
        {
            _booking.Pickup = selected;
            _booking.PickupLat = _booking.PickupLon = null;
            _booking.PickupStreet = _booking.PickupNumber = _booking.PickupPostalCode = _booking.PickupCity = _booking.PickupFormatted = null;
            _pickupDisambiguated = true;
            Interlocked.Exchange(ref _fareAutoTriggered, 0);

            _logger.LogInformation("[{SessionId}] 🔒 Pickup LOCKED: {Pickup}", SessionId, selected);

            // Check if dest still needs disambiguation
            if (_pendingDestAlternatives != null && _pendingDestAlternatives.Length > 0)
            {
                var options = _pendingDestAlternatives;
                _pendingDestAlternatives = null;
                _pendingDestClarificationMessage = null;

                OnBookingUpdated?.Invoke(_booking.Clone());

                // Return structured disambiguation for destination
                return new
                {
                    success = false,
                    needs_disambiguation = true,
                    target = "destination",
                    options,
                    instructions = "Pickup is confirmed. Now ask the user to clarify the destination from these options."
                };
            }

            // Both locked — re-trigger fare
            OnBookingUpdated?.Invoke(_booking.Clone());
            _ = TriggerFareCalculationAsync();
            return new { success = true, needs_disambiguation = false, message = "Pickup locked. Fare calculation in progress — wait for [FARE RESULT]." };
        }

        if (target == "destination")
        {
            _booking.Destination = selected;
            _booking.DestLat = _booking.DestLon = null;
            _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
            _destDisambiguated = true;
            Interlocked.Exchange(ref _fareAutoTriggered, 0);

            _logger.LogInformation("[{SessionId}] 🔒 Destination LOCKED: {Destination}", SessionId, selected);

            OnBookingUpdated?.Invoke(_booking.Clone());
            _ = TriggerFareCalculationAsync();
            return new { success = true, needs_disambiguation = false, message = "Destination locked. Fare calculation in progress — wait for [FARE RESULT]." };
        }

        return new { success = false, needs_disambiguation = false, error = $"Unknown target: {target}" };
    }

    private async Task TriggerFareCalculationAsync()
    {
        if (_booking.Pickup == null || _booking.Destination == null)
            return;

        var pickup = _booking.Pickup;
        var destination = _booking.Destination;
        var callerId = CallerId;
        var sessionId = SessionId;

        try
        {
            _logger.LogInformation("[{SessionId}] 🔄 Fare re-calculation after Address Lock resolution", sessionId);
            var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, destination, callerId);

            // Check if re-disambiguation is needed (e.g. clarified address still ambiguous in a different way)
            if (result.NeedsClarification)
            {
                var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                var destAlts = result.DestAlternatives ?? Array.Empty<string>();

                if (pickupAlts.Length > 0)
                {
                    Interlocked.Exchange(ref _fareAutoTriggered, 0);
                    if (_aiClient is OpenAiSdkClient sdk)
                        await sdk.InjectMessageAndRespondAsync(
                            $"[ADDRESS DISAMBIGUATION] needs_disambiguation=true, target=pickup, " +
                            $"options=[{string.Join(", ", pickupAlts)}]. " +
                            "Ask the caller to clarify the PICKUP. Present options with numbers, then WAIT.");
                    return;
                }
                if (destAlts.Length > 0)
                {
                    Interlocked.Exchange(ref _fareAutoTriggered, 0);
                    if (_aiClient is OpenAiSdkClient sdk)
                        await sdk.InjectMessageAndRespondAsync(
                            $"[ADDRESS DISAMBIGUATION] needs_disambiguation=true, target=destination, " +
                            $"options=[{string.Join(", ", destAlts)}]. " +
                            "Ask the caller to clarify the DESTINATION. Present options with numbers, then WAIT.");
                    return;
                }
            }

            // Both addresses resolved — sanity check before applying
            if (!IsFareSane(result))
            {
                _logger.LogWarning("[{SessionId}] 🚨 Fare sanity check FAILED after clarification — asking user to verify destination", sessionId);
                Interlocked.Exchange(ref _fareAutoTriggered, 0);

                if (_aiClient is OpenAiSdkClient sdkSanity)
                    await sdkSanity.InjectMessageAndRespondAsync(
                        "[FARE SANITY ALERT] The calculated fare seems unusually high, which suggests the destination may have been misheard. " +
                        "Ask the caller to confirm or repeat their DESTINATION address. " +
                        "Say something like: \"I want to make sure I have the right destination — could you repeat where you're going?\" " +
                        "When they respond, call sync_booking_data with the corrected destination.");
                return;
            }

            ApplyFareResult(result);

            if (_aiClient is OpenAiSdkClient sdkConf)
                sdkConf.SetAwaitingConfirmation(true);

            OnBookingUpdated?.Invoke(_booking.Clone());

            var spokenFare = FormatFareForSpeech(_booking.Fare);
            var pickupAddr = FormatAddressForReadback(result.PickupNumber, result.PickupStreet, result.PickupPostalCode, result.PickupCity);
            var destAddr = FormatAddressForReadback(result.DestNumber, result.DestStreet, result.DestPostalCode, result.DestCity);

            _logger.LogInformation("[{SessionId}] 💰 Fare ready after clarification: {Fare}, ETA: {Eta}",
                sessionId, _booking.Fare, _booking.Eta);

            if (_aiClient is OpenAiSdkClient sdkInject)
                await sdkInject.InjectMessageAndRespondAsync(
                    $"[FARE RESULT] The fare from {pickupAddr} to {destAddr} is {spokenFare}, " +
                    $"estimated time of arrival is {_booking.Eta}. " +
                    "Read back the VERIFIED addresses and fare to the caller and ask them to confirm.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Fare re-calculation failed after clarification", sessionId);
            _booking.Fare = "£8.00";
            _booking.Eta = "8 minutes";
            OnBookingUpdated?.Invoke(_booking.Clone());

            if (_aiClient is OpenAiSdkClient sdkFallback)
                await sdkFallback.InjectMessageAndRespondAsync(
                    "[FARE RESULT] The estimated fare is 8 pounds, estimated time of arrival is 8 minutes. " +
                    "Read back the details to the caller and ask them to confirm.");
        }
    }

    private async Task<object> HandleBookTaxiAsync(Dictionary<string, object?> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

        // SAFETY NET: populate _booking from book_taxi args (with name validation)
        if (args.TryGetValue("caller_name", out var bn) && !string.IsNullOrWhiteSpace(bn?.ToString())
            && !_rejectedNames.Contains(bn.ToString()!.Trim()))
            _booking.Name = bn.ToString()!.Trim();

        // Name guard — reject booking without a real name
        if (string.IsNullOrWhiteSpace(_booking.Name) || _rejectedNames.Contains(_booking.Name))
            return new { success = false, error = "Caller name is required. Ask the caller for their name before booking." };
        if (args.TryGetValue("pickup", out var bp) && !string.IsNullOrWhiteSpace(bp?.ToString()))
            _booking.Pickup = bp.ToString();
        if (args.TryGetValue("destination", out var bd) && !string.IsNullOrWhiteSpace(bd?.ToString()))
            _booking.Destination = bd.ToString();
        if (args.TryGetValue("passengers", out var bpax) && int.TryParse(bpax?.ToString(), out var bpn))
            _booking.Passengers = bpn;
        if (args.TryGetValue("pickup_time", out var bpt) && !string.IsNullOrWhiteSpace(bpt?.ToString()))
            _booking.PickupTime = bpt.ToString();

        if (action == "request_quote")
        {
            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                return new { success = false, error = "Missing pickup or destination." };

            // NON-BLOCKING: return immediately so Ada speaks an interjection,
            // then inject the fare result asynchronously when ready.
            var pickup = _booking.Pickup;
            var destination = _booking.Destination;
            var callerId = CallerId;
            var sessionId = SessionId;

            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("[{SessionId}] 🔄 Background fare calculation starting for {Pickup} → {Dest}",
                        sessionId, pickup, destination);

                    var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, destination, callerId);
                    var completed = await Task.WhenAny(aiTask, Task.Delay(10000));

                    FareResult result;
                    if (completed == aiTask)
                    {
                        result = await aiTask;
                        if (result.NeedsClarification)
                        {
                            var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                            var destAlts = result.DestAlternatives ?? Array.Empty<string>();

                            // Sequential disambiguation: pickup FIRST, then dropoff
                            if (pickupAlts.Length > 0)
                            {
                                if (destAlts.Length > 0)
                                {
                                    _pendingDestAlternatives = destAlts;
                                    _pendingDestClarificationMessage = result.ClarificationMessage;
                                    _destDisambiguated = false;
                                }

                                var altsList = string.Join(", ", pickupAlts);
                                _pickupDisambiguated = false;

                                if (_aiClient is OpenAiSdkClient sdkClarif)
                                    await sdkClarif.InjectMessageAndRespondAsync(
                                        $"[PICKUP DISAMBIGUATION] The PICKUP address is ambiguous. The options are: {altsList}. " +
                                        "Ask the caller ONLY about the PICKUP location. Do NOT mention the destination yet. " +
                                        "Present the pickup options clearly, then STOP and WAIT for their answer.");
                            }
                            else if (destAlts.Length > 0)
                            {
                                var altsList = string.Join(", ", destAlts);
                                _destDisambiguated = false;

                                if (_aiClient is OpenAiSdkClient sdkClarif)
                                    await sdkClarif.InjectMessageAndRespondAsync(
                                        $"[DESTINATION DISAMBIGUATION] The DESTINATION address is ambiguous. The options are: {altsList}. " +
                                        "Ask the caller ONLY about the DESTINATION location. " +
                                        "Present the destination options clearly, then STOP and WAIT for their answer.");
                            }

                            return;
                        }

                        // Check for pending destination alternatives (pickup was resolved, dest still needs it)
                        if (_pendingDestAlternatives != null && _pendingDestAlternatives.Length > 0 && !_destDisambiguated)
                        {
                            var destAltsList = string.Join(", ", _pendingDestAlternatives);
                            _logger.LogInformation("[{SessionId}] 🔄 Now resolving pending destination disambiguation: {Alts}", sessionId, destAltsList);

                            if (_aiClient is OpenAiSdkClient sdkDestClarif)
                                await sdkDestClarif.InjectMessageAndRespondAsync(
                                    $"[DESTINATION DISAMBIGUATION] Good, the pickup is confirmed. Now the DESTINATION address is ambiguous. The options are: {destAltsList}. " +
                                    "Ask the caller ONLY about the DESTINATION location. " +
                                    "Present the destination options clearly, then STOP and WAIT for their answer.");

                            _pendingDestAlternatives = null;
                            _pendingDestClarificationMessage = null;
                            return;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[{SessionId}] ⚠️ Edge function timed out, using fallback", sessionId);
                        result = await _fareCalculator.CalculateAsync(pickup, destination, callerId);
                    }

                    // Clear pending disambiguation state
                    _pendingDestAlternatives = null;
                    _pendingDestClarificationMessage = null;

                    // FARE SANITY CHECK
                    if (!IsFareSane(result))
                    {
                        _logger.LogWarning("[{SessionId}] 🚨 Fare sanity check FAILED (book_taxi path) — asking user to verify destination", sessionId);

                        if (_aiClient is OpenAiSdkClient sdkSanity)
                            await sdkSanity.InjectMessageAndRespondAsync(
                                "[FARE SANITY ALERT] The calculated fare seems unusually high, which suggests the destination may have been misheard. " +
                                "Ask the caller to confirm or repeat their DESTINATION address. " +
                                "Say something like: \"I want to make sure I have the right destination — could you repeat where you're going?\" " +
                                "When they respond, call sync_booking_data with the corrected destination.");
                        return;
                    }

                    ApplyFareResult(result);

                    if (_aiClient is OpenAiSdkClient sdk)
                        sdk.SetAwaitingConfirmation(true);

                    OnBookingUpdated?.Invoke(_booking.Clone());

                    var spokenFare = FormatFareForSpeech(_booking.Fare);
                    _logger.LogInformation("[{SessionId}] 💰 Fare ready: {Fare} ({Spoken}), ETA: {Eta}",
                        sessionId, _booking.Fare, spokenFare, _booking.Eta);

                    var pickupAddr = FormatAddressForReadback(result.PickupNumber, result.PickupStreet, result.PickupPostalCode, result.PickupCity);
                    var destAddr = FormatAddressForReadback(result.DestNumber, result.DestStreet, result.DestPostalCode, result.DestCity);

                    // Inject fare result into conversation — Ada will read it back
                    if (_aiClient is OpenAiSdkClient sdkInject)
                        await sdkInject.InjectMessageAndRespondAsync(
                            $"[FARE RESULT] The fare from {pickupAddr} to {destAddr} is {spokenFare}, " +
                            $"estimated time of arrival is {_booking.Eta}. " +
                            $"Read back these details to the caller and ask them to confirm the booking.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Background fare calculation failed", sessionId);
                    _booking.Fare = "£8.00";
                    _booking.Eta = "8 minutes";
                    OnBookingUpdated?.Invoke(_booking.Clone());

                    if (_aiClient is OpenAiSdkClient sdkFallback)
                        await sdkFallback.InjectMessageAndRespondAsync(
                            "[FARE RESULT] The estimated fare is 8 pounds, estimated time of arrival is 8 minutes. " +
                            "Read back these details to the caller and ask them to confirm.");
                }
            });

            // Return immediately — Ada will speak "let me check that" while fare calculates
            return new
            {
                success = true,
                status = "calculating",
                message = "I'm checking the fare now. Tell the caller you're just looking up the fare and it will only take a moment."
            };
        }

        if (action == "confirmed")
        {
            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                return new { success = false, error = "Cannot confirm: missing pickup or destination." };

            if (Interlocked.CompareExchange(ref _bookTaxiCompleted, 1, 0) == 1)
                return new { success = true, booking_ref = _booking.BookingRef ?? "already-booked", message = "Already confirmed." };

            // Geocode if needed
            bool needsGeocode = string.IsNullOrWhiteSpace(_booking.PickupStreet)
                || (_booking.PickupLat == 0 && _booking.PickupLon == 0)
                || (_booking.DestLat == 0 && _booking.DestLon == 0);

            if (needsGeocode)
            {
                try
                {
                    var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(_booking.Pickup, _booking.Destination, CallerId);
                    ApplyFareResultNullSafe(result);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Pre-dispatch geocode failed", SessionId);
                }
            }

            _booking.Confirmed = true;
            _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";

            if (_aiClient is OpenAiSdkClient sdk)
                sdk.SetAwaitingConfirmation(false);

            OnBookingUpdated?.Invoke(_booking.Clone());
            _logger.LogInformation("[{SessionId}] ✅ Booked: {Ref}", SessionId, _booking.BookingRef);

            var bookingSnapshot = _booking.Clone();
            var callerId = CallerId;
            var sessionId = SessionId;

            _ = Task.Run(async () =>
            {
                if (_aiClient is OpenAiSdkClient sdkWait)
                {
                    for (int i = 0; i < 50 && sdkWait.IsResponseActive; i++)
                        await Task.Delay(100);
                }

                await _dispatcher.DispatchAsync(bookingSnapshot, callerId);
                await _dispatcher.SendWhatsAppAsync(callerId);

                // iCabbi dispatch (fire-and-forget)
                if (_icabbiEnabled && _icabbi != null)
                {
                    try
                    {
                        var icabbiResult = await _icabbi.CreateAndDispatchAsync(bookingSnapshot);
                        if (icabbiResult.Success)
                            _logger.LogInformation("[{SessionId}] 🚕 iCabbi booking created: {JourneyId}", sessionId, icabbiResult.JourneyId);
                        else
                            _logger.LogWarning("[{SessionId}] ⚠️ iCabbi booking failed: {Msg}", sessionId, icabbiResult.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{SessionId}] iCabbi dispatch error", sessionId);
                    }
                }
            });

            return new { success = true, booking_ref = _booking.BookingRef, message = $"Taxi booked successfully. Tell the caller: Your booking reference is {_booking.BookingRef}. Then ask if they need anything else. If not, say the FINAL CLOSING script verbatim and call end_call." };
        }

        return new { error = "Invalid action" };
    }

    // =========================
    // END CALL
    // =========================
    private object HandleEndCall(Dictionary<string, object?> args)
    {
        // ── END-CALL GUARD: Block premature hangup if booking flow was started but never completed ──
        bool fareWasCalculated = !string.IsNullOrWhiteSpace(_booking.Fare);
        bool bookingCompleted = Volatile.Read(ref _bookTaxiCompleted) == 1;

        if (fareWasCalculated && !bookingCompleted)
        {
            _logger.LogWarning("[{SessionId}] ⛔ END_CALL BLOCKED: fare was quoted but book_taxi(confirmed) never called", SessionId);
            return new
            {
                success = false,
                error = "Cannot end call yet — a fare was quoted but the booking was never confirmed. " +
                        "You MUST read back the fare and ask the caller to confirm before ending. " +
                        "If they already said yes, call book_taxi(action: 'confirmed') first."
            };
        }

        _ = Task.Run(async () =>
        {
            if (_aiClient is OpenAiSdkClient sdk)
            {
                var streamStart = Environment.TickCount64;
                while (sdk.IsResponseActive && Environment.TickCount64 - streamStart < 15000)
                    await Task.Delay(200);

                var enqueueStart = Environment.TickCount64;
                while ((sdk.GetQueuedFrames?.Invoke() ?? 0) == 0 && Environment.TickCount64 - enqueueStart < 5000)
                    await Task.Delay(100);

                await Task.Delay(2000);

                var drainStart = Environment.TickCount64;
                while (Environment.TickCount64 - drainStart < 20000)
                {
                    if ((sdk.GetQueuedFrames?.Invoke() ?? 0) == 0) break;
                    await Task.Delay(100);
                }

                await Task.Delay(1000);
            }
            else
            {
                await Task.Delay(5000);
            }

            await EndAsync("end_call tool");
        });

        return new { success = true };
    }

    // =========================
    // HELPERS
    // =========================
    private static string FormatFareForSpeech(string? fare)
    {
        if (string.IsNullOrEmpty(fare)) return "unknown";
        var currencyWord = fare.Contains("£") ? "pounds" : "euros";
        var clean = fare.Replace("€", "").Replace("£", "").Replace("$", "").Trim();
        if (decimal.TryParse(clean, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            var whole = (int)amount;
            var pence = (int)((amount - whole) * 100);
            return pence > 0 ? $"{whole} {currencyWord} {pence}" : $"{whole} {currencyWord}";
        }
        return fare;
    }

    private static string FormatAddressForReadback(string? number, string? street, string? postalCode, string? city)
    {
        var parts = new List<string>();
        
        if (!string.IsNullOrWhiteSpace(number))
            parts.Add(number);
        if (!string.IsNullOrWhiteSpace(street))
            parts.Add(street);
        if (!string.IsNullOrWhiteSpace(postalCode))
            parts.Add(postalCode);
        if (!string.IsNullOrWhiteSpace(city))
            parts.Add(city);
        
        return parts.Count > 0 ? string.Join(", ", parts) : "the address";
    }

    // =========================
    // FARE SANITY CHECK
    // =========================
    private const decimal MAX_SANE_FARE = 100m;
    private const int MAX_SANE_ETA_MINUTES = 120;

    /// <summary>
    /// Returns true if the fare and ETA are within reasonable bounds.
    /// Catches cases where STT mishears a destination (e.g. "Kochi" instead of "Coventry")
    /// resulting in absurd cross-country/international fares.
    /// </summary>
    private bool IsFareSane(FareResult result)
    {
        // Parse fare amount
        var fareStr = result.Fare?.Replace("£", "").Replace("€", "").Replace("$", "").Trim();
        if (decimal.TryParse(fareStr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var fareAmount))
        {
            if (fareAmount > MAX_SANE_FARE)
            {
                _logger.LogWarning("[{SessionId}] 🚨 INSANE FARE detected: {Fare} (max={Max})", SessionId, result.Fare, MAX_SANE_FARE);
                return false;
            }
        }

        // Parse ETA minutes
        var etaStr = result.Eta?.Replace("minutes", "").Replace("minute", "").Trim();
        if (int.TryParse(etaStr, out var etaMinutes))
        {
            if (etaMinutes > MAX_SANE_ETA_MINUTES)
            {
                _logger.LogWarning("[{SessionId}] 🚨 INSANE ETA detected: {Eta} (max={Max} min)", SessionId, result.Eta, MAX_SANE_ETA_MINUTES);
                return false;
            }
        }

        return true;
    }

    private void ApplyFareResult(FareResult result)
    {
        _booking.Fare = result.Fare;
        _booking.Eta = result.Eta;
        _booking.PickupLat = result.PickupLat;
        _booking.PickupLon = result.PickupLon;
        _booking.PickupStreet = result.PickupStreet;
        _booking.PickupNumber = result.PickupNumber;
        _booking.PickupPostalCode = result.PickupPostalCode;
        _booking.PickupCity = result.PickupCity;
        _booking.PickupFormatted = result.PickupFormatted;
        _booking.DestLat = result.DestLat;
        _booking.DestLon = result.DestLon;
        _booking.DestStreet = result.DestStreet;
        _booking.DestNumber = result.DestNumber;
        _booking.DestPostalCode = result.DestPostalCode;
        _booking.DestCity = result.DestCity;
        _booking.DestFormatted = result.DestFormatted;
    }

    private void ApplyFareResultNullSafe(FareResult result)
    {
        _booking.PickupLat ??= result.PickupLat;
        _booking.PickupLon ??= result.PickupLon;
        _booking.PickupStreet ??= result.PickupStreet;
        _booking.PickupNumber ??= result.PickupNumber;
        _booking.PickupPostalCode ??= result.PickupPostalCode;
        _booking.PickupCity ??= result.PickupCity;
        _booking.PickupFormatted ??= result.PickupFormatted;
        _booking.DestLat ??= result.DestLat;
        _booking.DestLon ??= result.DestLon;
        _booking.DestStreet ??= result.DestStreet;
        _booking.DestNumber ??= result.DestNumber;
        _booking.DestPostalCode ??= result.DestPostalCode;
        _booking.DestCity ??= result.DestCity;
        _booking.DestFormatted ??= result.DestFormatted;
        _booking.Fare ??= result.Fare;
        _booking.Eta ??= result.Eta;

        if (_booking.PickupLat == 0 && result.PickupLat != 0) _booking.PickupLat = result.PickupLat;
        if (_booking.PickupLon == 0 && result.PickupLon != 0) _booking.PickupLon = result.PickupLon;
        if (_booking.DestLat == 0 && result.DestLat != 0) _booking.DestLat = result.DestLat;
        if (_booking.DestLon == 0 && result.DestLon != 0) _booking.DestLon = result.DestLon;
    }

    private static bool StreetNameChanged(string? oldAddress, string? newAddress)
    {
        if (string.IsNullOrWhiteSpace(oldAddress) || string.IsNullOrWhiteSpace(newAddress))
            return false;
        string Normalize(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"\d|[^a-z ]", "").Trim();
        return Normalize(oldAddress) != Normalize(newAddress);
    }

    /// <summary>
    /// Compares two strings for significant differences using word-level similarity.
    /// Returns true if the strings differ enough to warrant clarification.
    /// Used for transcript mismatch detection (Whisper STT vs Ada's interpretation).
    /// </summary>
    private static bool IsSignificantlyDifferent(string adaValue, string sttText)
    {
        if (string.IsNullOrWhiteSpace(adaValue) || string.IsNullOrWhiteSpace(sttText))
            return false;

        // Extract the street-name portion (remove numbers, punctuation)
        static string NormalizeForComparison(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^a-z ]", " ").Trim();

        var adaNorm = NormalizeForComparison(adaValue);
        var sttNorm = NormalizeForComparison(sttText);

        // If Ada's value is fully contained in the STT, no mismatch
        if (sttNorm.Contains(adaNorm) || adaNorm.Contains(sttNorm))
            return false;

        // Word-level check: if Ada's street words appear in STT, it's fine
        var adaWords = adaNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToArray(); // Skip tiny words like "a", "to"
        var sttWords = new HashSet<string>(sttNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (adaWords.Length == 0) return false;

        // If fewer than half of Ada's significant words appear in STT, flag it
        var matchCount = adaWords.Count(w => sttWords.Contains(w));
        return matchCount < adaWords.Length / 2.0;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await EndAsync("disposed");

        _aiClient.OnAudio -= HandleAiAudio;
        _aiClient.OnToolCall -= HandleToolCallAsync;

        OnEnded = null;
        OnBookingUpdated = null;
        OnTranscript = null;
        OnAudioOut = null;
        OnBargeIn = null;

        _booking.Reset();
        Interlocked.Exchange(ref _bookTaxiCompleted, 0);

        if (_aiClient is IAsyncDisposable disposableAi)
        {
            try { await disposableAi.DisposeAsync(); }
            catch { }
        }

        _logger.LogInformation("[{SessionId}] 🧹 CallSession fully disposed", SessionId);
    }
}
