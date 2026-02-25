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
    private readonly string _companyName;
    private readonly CallerContext? _callerContext;
    private readonly HashSet<string> _changedSlots = new();
    private int _clarificationAttempts; // loop-breaker counter

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
        CallerContext? callerContext = null)
    {
        SessionId = sessionId;
        CallerId = callerId;
        _companyName = companyName;
        _extractionService = extractionService;
        _fareService = fareService;
        _callerContext = callerContext;

        _engine.OnLog += msg => OnLog?.Invoke(msg);
        _engine.OnStateChanged += OnEngineStateChanged;
    }

    /// <summary>
    /// Start the call — send greeting instruction to AI.
    /// </summary>
    public void Start()
    {
        Log($"Call started: {CallerId}");

        // Auto-fill name for returning callers
        if (_callerContext?.IsReturningCaller == true && !string.IsNullOrWhiteSpace(_callerContext.CallerName))
        {
            _engine.RawData.SetSlot("name", _callerContext.CallerName);
            Log($"Auto-filled name from caller history: {_callerContext.CallerName}");
        }

        _engine.BeginCollection();
        EmitCurrentInstruction();
    }

    /// <summary>
    /// Process caller's spoken response for the current slot.
    /// The SIP bridge's transcript handler calls this with the raw text.
    /// </summary>
    public async Task ProcessCallerResponseAsync(string transcript, CancellationToken ct = default)
    {
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

        // Resolve aliases ("home", "work", "the usual") for address slots
        var resolved = AliasResolver.TryResolve(currentSlot, transcript, _callerContext);
        var valueToStore = transcript;
        if (resolved != null)
        {
            Log($"Alias resolved: \"{transcript}\" → \"{resolved.ResolvedAddress}\" (alias: {resolved.AliasName})");
            valueToStore = resolved.ResolvedAddress;
        }

        // Store raw (or alias-resolved) value for current slot
        var nextSlot = _engine.AcceptSlotValue(currentSlot, valueToStore);

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

    // ─── Private ─────────────────────────────────────────────

    private async Task RunExtractionAsync(CancellationToken ct)
    {
        _engine.BeginExtraction();
        EmitCurrentInstruction(); // "Checking availability..."

        try
        {
            var request = _engine.BuildExtractionRequest(_callerContext);
            Log($"Extracting: name={request.Slots.Name}, pickup={request.Slots.Pickup}, " +
                $"dest={request.Slots.Destination}, pax={request.Slots.Passengers}, " +
                $"time={request.Slots.PickupTime}");

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
            _engine.PendingClarification);
        OnAiInstruction?.Invoke(instruction);
    }

    private void OnEngineStateChanged(CollectionState from, CollectionState to)
    {
        Log($"State transition: {from} → {to}");
    }

    private void Log(string msg) => OnLog?.Invoke($"[Session:{SessionId}] {msg}");
}
