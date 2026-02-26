using AdaCleanVersion.Engine;
using AdaCleanVersion.Models;
using AdaCleanVersion.Services;

namespace AdaCleanVersion.Session;

/// <summary>
/// Clean call session — orchestrates the deterministic engine + AI voice interface.
/// 
/// Architecture:
/// 1. Engine drives flow deterministically (no AI involvement in state)
/// 2. AI is voice-only — responds to [INSTRUCTION] messages
/// 3. Caller responses stored as raw slot values
/// 4. Single AI extraction pass when all slots filled
/// 5. Validation → fare → payment → confirmation → dispatch
/// </summary>
public class CleanCallSession
{
    private readonly CallStateEngine _engine = new();
    private readonly IExtractionService _extractionService;
    private readonly FareGeocodingService? _fareService;
    private readonly LocalGeminiReconciler? _reconciler;
    private readonly string _companyName;
    private readonly CallerContext? _callerContext;
    private readonly HashSet<string> _changedSlots = new();
    private int _clarificationAttempts; // loop-breaker counter

    // ─── Ada Transcript Tracking (Source of Truth) ─────────────
    // Ada's spoken responses are accumulated for extraction context.
    private readonly List<string> _adaTranscripts = new();
    // The last instruction emitted to Ada — used as "Ada's question" for 3-way geocoding context.
    private string _lastEmittedInstruction = "";

    public string SessionId { get; }
    public string CallerId { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public CallStateEngine Engine => _engine;

    public event Action<string>? OnLog;
    public event Action<string>? OnAiInstruction;
    public event Action<StructuredBooking>? OnBookingReady;
    public event Action<FareResult>? OnFareReady;

    public CleanCallSession(
        string sessionId,
        string callerId,
        string companyName,
        IExtractionService extractionService,
        FareGeocodingService? fareService = null,
        CallerContext? callerContext = null,
        LocalGeminiReconciler? reconciler = null)
    {
        SessionId = sessionId;
        CallerId = callerId;
        _companyName = companyName;
        _extractionService = extractionService;
        _fareService = fareService;
        _callerContext = callerContext;
        _reconciler = reconciler;

        _engine.OnLog += msg => OnLog?.Invoke(msg);
        _engine.OnStateChanged += OnEngineStateChanged;
    }

    /// <summary>
    /// Start the call — prepare engine state. Greeting is sent separately via SendGreeting.
    /// Matches AdaSdkModel sequence: history → connect → inject context → greeting.
    /// </summary>
    public void Start()
    {
        Log($"Call started: {CallerId}");

        // Auto-fill name for returning callers (so BeginCollection skips to pickup)
        if (_callerContext?.IsReturningCaller == true && !string.IsNullOrWhiteSpace(_callerContext.CallerName))
        {
            _engine.RawData.SetSlot("name", _callerContext.CallerName);
            Log($"Auto-filled name from caller history: {_callerContext.CallerName}");
        }

        // Advance engine to first missing slot (skips name if pre-filled)
        _engine.BeginCollection();
        // NOTE: Do NOT emit instruction here — the greeting will be sent by OpenAiRealtimeClient
        // after the session is configured, matching AdaSdkModel's flow.
    }

    /// <summary>
    /// Build the greeting message to inject as a conversation item.
    /// Matches AdaSdkModel's explicit greeting style.
    /// </summary>
    public string BuildGreetingMessage()
    {
        return PromptBuilder.BuildGreetingMessage(_companyName, _callerContext, _engine.State);
    }

    /// <summary>
    /// Process caller's spoken response for the current slot.
    /// The SIP bridge's transcript handler calls this with the raw text.
    /// </summary>
    public async Task ProcessCallerResponseAsync(string transcript, CancellationToken ct = default)
    {
        // ── Priority 0: If awaiting clarification, route directly — do NOT treat as a new slot ──
        if (_engine.State == CollectionState.AwaitingClarification)
        {
            await HandlePostCollectionInput(transcript, ct);
            return;
        }

        // Step 1: Check for correction intent BEFORE normal slot processing
        var correction = CorrectionDetector.Detect(
            transcript,
            _engine.RawData.NextMissingSlot(),
            _engine.RawData.FilledSlots);

        if (correction != null)
        {
            Log($"Correction detected: {correction.SlotName} → \"{correction.NewValue}\"");
            await CorrectSlotAsync(correction.SlotName, correction.NewValue, ct);
            return;
        }

        var currentSlot = _engine.RawData.NextMissingSlot();
        if (currentSlot == null)
        {
            // All slots filled — might be a correction or confirmation
            await HandlePostCollectionInput(transcript, ct);
            return;
        }

        // Validate the input before accepting it as a slot value
        var validationError = SlotValidator.Validate(currentSlot, transcript);
        if (validationError != null)
        {
            Log($"Slot '{currentSlot}' rejected: \"{transcript}\" (reason: {validationError})");
            // Re-emit the current instruction so AI re-asks the same question
            EmitCurrentInstruction();
            return;
        }

        var valueToStore = transcript;

        // Strip common name prefixes ("It's Max" → "Max", "My name is John" → "John")
        if (currentSlot == "name")
        {
            valueToStore = System.Text.RegularExpressions.Regex.Replace(
                valueToStore,
                @"^(it'?s\s+|that'?s\s+|i'?m\s+|my\s+name\s+is\s+|i\s+am\s+|they\s+call\s+me\s+|call\s+me\s+|this\s+is\s+)",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim().TrimEnd('.', '!', ',');
            Log($"Name cleaned: \"{transcript}\" → \"{valueToStore}\"");
        }

        // Resolve aliases ("home", "work", "the usual") for address slots
        var resolved = AliasResolver.TryResolve(currentSlot, valueToStore, _callerContext);
        if (resolved != null)
        {
            Log($"Alias resolved: \"{valueToStore}\" → \"{resolved.ResolvedAddress}\" (alias: {resolved.AliasName})");
            valueToStore = resolved.ResolvedAddress;
        }

        // Store raw (or alias-resolved) value for current slot
        var nextSlot = _engine.AcceptSlotValue(currentSlot, valueToStore);

        // For address slots, AcceptSlotValue transitions to VerifyingPickup/VerifyingDestination.
        // Emit instruction telling Ada to read back the address — geocoding is triggered
        // AFTER Ada's readback arrives via ProcessAdaTranscript, giving us both the raw
        // caller STT and Ada's interpretation to send to address-dispatch.
        if (_engine.State == CollectionState.VerifyingPickup ||
            _engine.State == CollectionState.VerifyingDestination)
        {
            EmitCurrentInstruction(); // "Read back the address... let me confirm that"
            return; // Geocoding will be triggered when Ada's readback arrives
        }

        if (nextSlot == null && _engine.State == CollectionState.ReadyForExtraction)
        {
            // All slots collected — trigger extraction
            await RunExtractionAsync(ct);
        }
        else
        {
            EmitCurrentInstruction();
        }
    }

    /// <summary>
    /// Process Ada's spoken response transcript.
    /// Ada's interpretation is stored for extraction context only.
    /// Called from OpenAiRealtimeClient on response.audio_transcript.done.
    /// 
    /// NOTE: We no longer use AdaSlotRefiner to overwrite raw slot values.
    /// Instead, Ada's transcripts are accumulated and fed to StructureOnlyEngine
    /// as extraction context, which does the proper semantic parsing.
    /// 
    /// ADDITIONALLY: When we're in VerifyingPickup/VerifyingDestination, Ada's readback
    /// triggers inline geocoding with BOTH the raw caller STT and Ada's interpretation.
    /// This gives the geocoder two signals to reconcile for maximum accuracy.
    /// </summary>
    public async Task ProcessAdaTranscriptAsync(string adaText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adaText)) return;

        // Store for extraction context
        _adaTranscripts.Add(adaText);

        // If we're in a verification state, Ada just read back the address.
        // Now trigger geocoding with both the raw caller STT and Ada's readback.
        if (_engine.State == CollectionState.VerifyingPickup)
        {
            var rawAddress = _engine.RawData.PickupRaw ?? "";
            Log($"Ada readback for pickup: \"{adaText}\" (raw STT: \"{rawAddress}\")");
            await RunInlineGeocodeAsync("pickup", rawAddress, ct, adaReadback: adaText);
            return;
        }
        if (_engine.State == CollectionState.VerifyingDestination)
        {
            var rawAddress = _engine.RawData.DestinationRaw ?? "";
            Log($"Ada readback for destination: \"{adaText}\" (raw STT: \"{rawAddress}\")");
            await RunInlineGeocodeAsync("destination", rawAddress, ct, adaReadback: adaText);
            return;
        }
    }

    /// <summary>
    /// Synchronous version for backward compatibility — fire-and-forget the async version.
    /// </summary>
    public void ProcessAdaTranscript(string adaText)
    {
        if (string.IsNullOrWhiteSpace(adaText)) return;
        _ = ProcessAdaTranscriptAsync(adaText);
    }

    /// <summary>
    /// Get Ada's accumulated transcript for extraction context.
    /// </summary>
    public string GetAdaTranscriptContext()
    {
        return string.Join("\n", _adaTranscripts.TakeLast(10)); // last 10 responses
    }

    /// <summary>
    /// Correct a specific slot by name.
    /// </summary>
    public async Task CorrectSlotAsync(string slotName, string newValue, CancellationToken ct = default)
    {
        var oldValue = _engine.RawData.GetSlot(slotName);
        _engine.CorrectSlot(slotName, newValue);

        // Track which slot changed for update extraction
        _changedSlots.Add(slotName);

        var instruction = PromptBuilder.BuildCorrectionInstruction(
            slotName, oldValue ?? "", newValue);
        OnAiInstruction?.Invoke(instruction);

        // Re-check if ready for extraction after correction
        if (_engine.RawData.AllRequiredPresent &&
            _engine.State < CollectionState.ReadyForExtraction)
        {
            // If we already have a structured result, use update extraction
            if (_engine.StructuredResult != null && _changedSlots.Count > 0)
                await RunUpdateExtractionAsync(ct);
            else
                await RunExtractionAsync(ct);
        }
    }

    /// <summary>
    /// Accept payment choice and advance.
    /// </summary>
    public void AcceptPayment(string method)
    {
        _engine.AcceptPaymentChoice(method);
        EmitCurrentInstruction();
    }

    /// <summary>
    /// Confirm the booking and dispatch.
    /// </summary>
    public void ConfirmBooking()
    {
        _engine.ConfirmBooking();
        EmitCurrentInstruction();
    }

    /// <summary>
    /// End the call.
    /// </summary>
    public void EndCall()
    {
        _engine.EndCall();
        EmitCurrentInstruction();
    }

    /// <summary>
    /// Get the system prompt for the AI voice interface.
    /// </summary>
    public string GetSystemPrompt() => PromptBuilder.BuildSystemPrompt(_companyName, _callerContext);

    // ─── Inline Address Verification ────────────────────────

    /// <summary>
    /// Geocode a single address inline after Ada reads it back.
    /// Uses the full 3-way context (Ada's question + raw STT + Ada's readback)
    /// for "Level 5" dispatch accuracy.
    /// 
    /// Strategy:
    ///   1. Primary: address-dispatch edge function with 3-way context
    ///   2. Fallback: local Gemini reconciler (if edge function fails)
    /// </summary>
    private async Task RunInlineGeocodeAsync(string field, string rawAddress, CancellationToken ct, string? adaReadback = null)
    {
        if (_fareService == null && _reconciler == null)
        {
            Log($"No fare service or reconciler — skipping inline geocode for {field}");
            _engine.SkipVerification(field, "no fare/reconciler service");
            EmitCurrentInstruction();
            return;
        }

        try
        {
            // Get Ada's question that prompted this address (the instruction before the verifying state)
            var adaQuestion = _lastEmittedInstruction;

            Log($"Inline geocoding {field}: raw=\"{rawAddress}\", adaReadback=\"{adaReadback ?? "none"}\", adaQuestion present={!string.IsNullOrEmpty(adaQuestion)}");

            GeocodedAddress? geocoded = null;

            // Primary path: address-dispatch edge function with 3-way context
            if (_fareService != null)
            {
                geocoded = await _fareService.GeocodeAddressAsync(
                    rawAddress, field, CallerId, ct,
                    adaReadback: adaReadback,
                    adaQuestion: adaQuestion);
            }

            // Fallback: local Gemini reconciler if primary failed
            if (geocoded == null && _reconciler != null)
            {
                Log($"Primary geocode failed — trying local Gemini reconciler for {field}");
                var reconcileRequest = new ReconcileContextRequest
                {
                    AdaQuestion = adaQuestion,
                    UserRawSpeech = rawAddress,
                    AdaReadback = adaReadback,
                    CurrentCity = _callerContext?.LastPickup?.Split(',').LastOrDefault()?.Trim(),
                    Context = field,
                    CallerPhone = CallerId
                };

                var reconciled = await _reconciler.ReconcileAsync(reconcileRequest, ct);
                if (reconciled != null && reconciled.Confidence >= 0.4)
                {
                    geocoded = reconciled.ToGeocodedAddress();
                    Log($"Local reconciler resolved {field}: \"{geocoded.Address}\" (conf={reconciled.Confidence:F2})");
                }
                else if (reconciled != null)
                {
                    Log($"Local reconciler low confidence ({reconciled.Confidence:F2}) for {field} — treating as ambiguous");
                    geocoded = new GeocodedAddress
                    {
                        Address = reconciled.ReconciledAddress,
                        Lat = reconciled.Lat,
                        Lon = reconciled.Lon,
                        IsAmbiguous = true,
                        Alternatives = reconciled.Alternatives,
                    };
                }
            }

            if (geocoded == null)
            {
                Log($"All geocoding paths returned null for {field} — skipping");
                _engine.SkipVerification(field, "geocode failed");
                EmitCurrentInstruction();
                return;
            }

            if (geocoded.IsAmbiguous)
            {
                Log($"Inline geocode: {field} is ambiguous — entering clarification");
                _engine.EnterClarification(new ClarificationInfo
                {
                    AmbiguousField = field,
                    Message = "Which area is that in?",
                    Alternatives = geocoded.Alternatives,
                    Attempt = 0
                });
                EmitCurrentInstruction();
                return;
            }

            // Success — store verified address and advance
            if (field == "pickup")
            {
                _engine.CompletePickupVerification(geocoded);
                Log($"✅ Pickup verified: \"{geocoded.Address}\"");
            }
            else
            {
                _engine.CompleteDestinationVerification(geocoded);
                Log($"✅ Destination verified: \"{geocoded.Address}\"");
            }

            // Emit instruction with verified address in the prompt
            EmitCurrentInstruction();

            // Check if all slots are now filled after verification
            if (_engine.State == CollectionState.ReadyForExtraction)
            {
                await RunExtractionAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Log($"Inline geocode error for {field}: {ex.Message}");
            _engine.SkipVerification(field, ex.Message);
            EmitCurrentInstruction();
        }
    }

    // ─── Private ─────────────────────────────────────────────

    private async Task RunExtractionAsync(CancellationToken ct)
    {
        _engine.BeginExtraction();
        EmitCurrentInstruction(); // "Checking availability..."

        try
        {
            var request = _engine.BuildExtractionRequest(_callerContext);

            // Include Ada's transcript as additional context — Ada is source of truth
            request.AdaTranscriptContext = GetAdaTranscriptContext();

            Log($"Extracting: name={request.Slots.Name}, pickup={request.Slots.Pickup}, " +
                $"dest={request.Slots.Destination}, pax={request.Slots.Passengers}, " +
                $"time={request.Slots.PickupTime}");
            if (!string.IsNullOrEmpty(request.AdaTranscriptContext))
                Log($"Ada context: {request.AdaTranscriptContext.Length} chars");

            var result = await _extractionService.ExtractAsync(request, ct);

            if (result.Success && result.Booking != null)
            {
                _engine.CompleteExtraction(result.Booking);
                OnBookingReady?.Invoke(result.Booking);

                // Chain into fare/geocoding pipeline
                await RunFarePipelineAsync(result.Booking, ct);
            }
            else
            {
                _engine.ExtractionFailed(result.Error ?? "Unknown extraction error");
                EmitCurrentInstruction();
            }
        }
        catch (Exception ex)
        {
            Log($"Extraction error: {ex.Message}");
            _engine.ExtractionFailed(ex.Message);
            EmitCurrentInstruction();
        }
    }

    private async Task RunUpdateExtractionAsync(CancellationToken ct)
    {
        var previousBooking = _engine.StructuredResult!;
        _engine.BeginExtraction();
        EmitCurrentInstruction();

        try
        {
            var request = _engine.BuildExtractionRequest(_callerContext);
            Log($"Update extraction for changed slots: {string.Join(", ", _changedSlots)}");

            var result = await _extractionService.ExtractUpdateAsync(
                request, previousBooking, _changedSlots, ct);

            _changedSlots.Clear();

            if (result.Success && result.Booking != null)
            {
                _engine.CompleteExtraction(result.Booking);
                OnBookingReady?.Invoke(result.Booking);

                // Chain into fare/geocoding pipeline
                await RunFarePipelineAsync(result.Booking, ct);
            }
            else
            {
                _engine.ExtractionFailed(result.Error ?? "Unknown update extraction error");
                EmitCurrentInstruction();
            }
        }
        catch (Exception ex)
        {
            Log($"Update extraction error: {ex.Message}");
            _engine.ExtractionFailed(ex.Message);
            EmitCurrentInstruction();
        }
    }

    /// <summary>
    /// Run geocoding + fare calculation after extraction.
    /// State flow: Extracting → Geocoding → PresentingFare
    /// </summary>
    private async Task RunFarePipelineAsync(StructuredBooking booking, CancellationToken ct)
    {
        if (_fareService == null)
        {
            // No fare service configured — skip to fare presentation without fare data
            Log("No fare service configured — skipping geocoding");
            _engine.GeocodingFailed("No fare service");
            EmitCurrentInstruction();
            return;
        }

        _engine.BeginGeocoding();
        EmitCurrentInstruction(); // Silent instruction during geocoding

        try
        {
            Log($"Geocoding: pickup=\"{booking.Pickup.DisplayName}\", dest=\"{booking.Destination.DisplayName}\"");

            var fareResult = await _fareService.CalculateAsync(
                booking, CallerId, ct);

            if (fareResult == null)
            {
                Log("Fare pipeline returned null");
                _engine.GeocodingFailed("Fare calculation failed");
                EmitCurrentInstruction();
                return;
            }

            if (fareResult.NeedsClarification)
            {
                _clarificationAttempts++;

                // Loop breaker: after 2 consecutive clarification failures, fall through
                if (_clarificationAttempts >= 2)
                {
                    Log($"Clarification loop breaker: {_clarificationAttempts} attempts — falling through");
                    _clarificationAttempts = 0;
                    _engine.GeocodingFailed("Address could not be resolved after multiple attempts");
                    EmitCurrentInstruction();
                    return;
                }

                var ambiguousField = fareResult.Pickup.IsAmbiguous ? "pickup" : "destination";
                var ambiguousAddr = fareResult.Pickup.IsAmbiguous ? fareResult.Pickup : fareResult.Destination;

                Log($"Address clarification needed ({ambiguousField}): {fareResult.ClarificationMessage}");

                _engine.EnterClarification(new ClarificationInfo
                {
                    AmbiguousField = ambiguousField,
                    Message = fareResult.ClarificationMessage ?? "Which area is that in?",
                    Alternatives = ambiguousAddr.Alternatives,
                    Attempt = _clarificationAttempts
                });

                EmitCurrentInstruction();
                return;
            }

            // Success — reset clarification counter
            _clarificationAttempts = 0;

            _engine.CompleteGeocoding(fareResult);
            OnFareReady?.Invoke(fareResult);

            Log($"🚕 Fare ready: {fareResult.Fare} ({fareResult.DistanceMiles:F1}mi), " +
                $"ETA: {fareResult.DriverEta}, zone: {fareResult.ZoneName ?? "none"}");

            EmitCurrentInstruction();
        }
        catch (Exception ex)
        {
            Log($"Fare pipeline error: {ex.Message}");
            _engine.GeocodingFailed(ex.Message);
            EmitCurrentInstruction();
        }
    }

    private async Task HandlePostCollectionInput(string transcript, CancellationToken ct)
    {
        Log($"Post-collection input: \"{transcript}\" (state: {_engine.State})");

        // Check for correction intent first — even post-collection
        var correction = CorrectionDetector.Detect(
            transcript, null, _engine.RawData.FilledSlots);

        if (correction != null)
        {
            Log($"Post-collection correction: {correction.SlotName} → \"{correction.NewValue}\"");
            await CorrectSlotAsync(correction.SlotName, correction.NewValue, ct);
            return;
        }

        var lower = transcript.ToLowerInvariant().Trim();

        switch (_engine.State)
        {
            case CollectionState.AwaitingClarification:
                // Caller responded to "which area?" — accept and re-run pipeline
                Log($"Clarification response: \"{transcript}\"");
                _engine.AcceptClarification(transcript);

                // Re-run extraction + geocoding with the clarified address
                if (_engine.State == CollectionState.ReadyForExtraction)
                    await RunExtractionAsync(ct);
                else
                    EmitCurrentInstruction();
                break;

            case CollectionState.AwaitingPaymentChoice:
                if (lower.Contains("card")) AcceptPayment("card");
                else if (lower.Contains("cash") || lower.Contains("meter")) AcceptPayment("meter");
                else EmitCurrentInstruction();
                break;

            case CollectionState.AwaitingConfirmation:
                if (lower.Contains("yes") || lower.Contains("confirm") || lower.Contains("book"))
                    ConfirmBooking();
                else if (lower.Contains("no") || lower.Contains("cancel"))
                    EndCall();
                else
                    EmitCurrentInstruction();
                break;

            default:
                EmitCurrentInstruction();
                break;
        }
    }

    private void EmitCurrentInstruction()
    {
        var instruction = PromptBuilder.BuildInstruction(
            _engine.State, _engine.RawData, _callerContext,
            _engine.StructuredResult, _engine.FareResult,
            _engine.PendingClarification,
            _engine.VerifiedPickup, _engine.VerifiedDestination);
        _lastEmittedInstruction = instruction; // Track for 3-way geocoding context
        OnAiInstruction?.Invoke(instruction);
    }

    private void OnEngineStateChanged(CollectionState from, CollectionState to)
    {
        Log($"State transition: {from} → {to}");
    }

    private void Log(string msg) => OnLog?.Invoke($"[Session:{SessionId}] {msg}");
}
