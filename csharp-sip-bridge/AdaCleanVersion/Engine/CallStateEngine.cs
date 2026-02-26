using AdaCleanVersion.Models;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Deterministic state machine for taxi booking collection.
/// 
/// Key principle: AI NEVER controls state transitions.
/// The engine decides what to collect, when to extract, and when to dispatch.
/// AI is only used as a normalization function at the extraction phase.
/// </summary>
public class CallStateEngine
{
    public CollectionState State { get; private set; } = CollectionState.Greeting;
    public RawBookingData RawData { get; } = new();
    public StructuredBooking? StructuredResult { get; private set; }
    public FareResult? FareResult { get; private set; }

    public event Action<CollectionState, CollectionState>? OnStateChanged;
    public event Action<string>? OnLog;

    /// <summary>
    /// Advance from Greeting to first collection state.
    /// Called after AI greeting is delivered.
    /// </summary>
    public void BeginCollection()
    {
        // Skip to the first missing slot — if name is pre-filled, go straight to pickup
        var nextSlot = RawData.NextMissingSlot();
        if (nextSlot == null)
        {
            TransitionTo(CollectionState.ReadyForExtraction);
            return;
        }
        TransitionTo(SlotToState(nextSlot));
    }

    /// <summary>
    /// Store a raw slot value and advance state if appropriate.
    /// For pickup/destination, transitions to VerifyingPickup/VerifyingDestination
    /// so the session can geocode inline before asking the next question.
    /// 
    /// CRITICAL GATE: The engine will NEVER advance past collection states
    /// (to ReadyForExtraction or beyond) unless ALL required slots are filled.
    /// This mirrors AdaSdkModel's BookingSnapshot.IsComplete pattern.
    /// </summary>
    /// <returns>The next slot to collect, or null if all slots are filled.</returns>
    public string? AcceptSlotValue(string slotName, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return RawData.NextMissingSlot();

        RawData.SetSlot(slotName, rawValue.Trim());
        Log($"Slot '{slotName}' set: \"{rawValue.Trim()}\"");

        // For address slots, transition to verification state (inline geocoding)
        if (slotName == "pickup")
        {
            TransitionTo(CollectionState.VerifyingPickup);
            return RawData.NextMissingSlot();
        }
        if (slotName == "destination")
        {
            TransitionTo(CollectionState.VerifyingDestination);
            return RawData.NextMissingSlot();
        }

        // Check if all slots are now filled
        if (RawData.AllRequiredPresent)
        {
            TransitionTo(CollectionState.ReadyForExtraction);
            return null;
        }

        // Advance to next missing slot
        var next = RawData.NextMissingSlot();
        if (next != null)
        {
            TransitionTo(SlotToState(next));
        }

        return next;
    }

    /// <summary>
    /// Allow correction of any previously-set slot.
    /// Simply overwrites the raw value — no AI re-interpretation.
    /// </summary>
    public void CorrectSlot(string slotName, string newRawValue)
    {
        RawData.SetSlot(slotName, newRawValue.Trim());
        Log($"Slot '{slotName}' corrected: \"{newRawValue.Trim()}\"");

        // If we were past collection, revert to extraction-ready check
        if (State >= CollectionState.ReadyForExtraction && State < CollectionState.Dispatched)
        {
            if (RawData.AllRequiredPresent)
                TransitionTo(CollectionState.ReadyForExtraction);
        }
    }

    /// <summary>
    /// Mark extraction as started.
    /// </summary>
    public void BeginExtraction()
    {
        if (State != CollectionState.ReadyForExtraction)
        {
            Log($"Cannot extract in state {State}");
            return;
        }
        TransitionTo(CollectionState.Extracting);
    }

    /// <summary>
    /// Store the structured result from AI extraction and advance to fare presentation.
    /// </summary>
    public void CompleteExtraction(StructuredBooking booking)
    {
        StructuredResult = booking;
        TransitionTo(CollectionState.Geocoding);
    }

    /// <summary>
    /// Begin geocoding/fare calculation phase.
    /// </summary>
    public void BeginGeocoding()
    {
        if (State != CollectionState.Geocoding)
        {
            Log($"Cannot geocode in state {State}");
            return;
        }
        Log("Geocoding started");
    }

    /// <summary>
    /// Geocoded addresses stored per-slot for inline verification.
    /// </summary>
    public GeocodedAddress? VerifiedPickup { get; private set; }
    public GeocodedAddress? VerifiedDestination { get; private set; }

    /// <summary>
    /// Store verified pickup address from inline geocoding and advance to next slot.
    /// </summary>
    public void CompletePickupVerification(GeocodedAddress geocoded)
    {
        VerifiedPickup = geocoded;
        Log($"Pickup verified: \"{geocoded.Address}\" ({geocoded.Lat:F5},{geocoded.Lon:F5})");

        // Advance to next missing slot
        if (RawData.AllRequiredPresent)
        {
            TransitionTo(CollectionState.ReadyForExtraction);
        }
        else
        {
            var next = RawData.NextMissingSlot();
            TransitionTo(next != null ? SlotToState(next) : CollectionState.ReadyForExtraction);
        }
    }

    /// <summary>
    /// Store verified destination address from inline geocoding and advance.
    /// </summary>
    public void CompleteDestinationVerification(GeocodedAddress geocoded)
    {
        VerifiedDestination = geocoded;
        Log($"Destination verified: \"{geocoded.Address}\" ({geocoded.Lat:F5},{geocoded.Lon:F5})");

        if (RawData.AllRequiredPresent)
        {
            TransitionTo(CollectionState.ReadyForExtraction);
        }
        else
        {
            var next = RawData.NextMissingSlot();
            TransitionTo(next != null ? SlotToState(next) : CollectionState.ReadyForExtraction);
        }
    }

    /// <summary>
    /// Inline geocoding failed — skip verification, proceed to next slot.
    /// The full geocoding pipeline will run later.
    /// </summary>
    public void SkipVerification(string field, string reason)
    {
        Log($"Verification skipped for {field}: {reason}");
        if (RawData.AllRequiredPresent)
        {
            TransitionTo(CollectionState.ReadyForExtraction);
        }
        else
        {
            var next = RawData.NextMissingSlot();
            TransitionTo(next != null ? SlotToState(next) : CollectionState.ReadyForExtraction);
        }
    }

    /// <summary>
    /// Store the fare result and advance to fare presentation.
    /// </summary>
    public void CompleteGeocoding(FareResult fareResult)
    {
        FareResult = fareResult;
        TransitionTo(CollectionState.PresentingFare);
    }

    /// <summary>
    /// Geocoding/fare calculation failed — present what we have or retry.
    /// </summary>
    public void GeocodingFailed(string error)
    {
        Log($"Geocoding failed: {error}");
        // Fall through to PresentingFare without fare data —
        // session can still present booking and ask for confirmation
        TransitionTo(CollectionState.PresentingFare);
    }

    /// <summary>
    /// Address needs clarification — enter clarification state.
    /// Stores the pending clarification info for the session to route back to caller.
    /// </summary>
    public ClarificationInfo? PendingClarification { get; private set; }

    public void EnterClarification(ClarificationInfo info)
    {
        PendingClarification = info;
        Log($"Clarification needed: {info.AmbiguousField} — \"{info.Message}\"");
        TransitionTo(CollectionState.AwaitingClarification);
    }

    /// <summary>
    /// Caller provided clarification — update the affected slot and re-geocode (NOT re-extract).
    /// </summary>
    public void AcceptClarification(string clarifiedValue)
    {
        if (State != CollectionState.AwaitingClarification || PendingClarification == null)
        {
            Log($"Clarification ignored in state {State}");
            return;
        }

        var field = PendingClarification.AmbiguousField;
        Log($"Clarification accepted for {field}: \"{clarifiedValue}\"");

        // Update the raw slot with the clarified value
        if (field == "pickup")
        {
            var existing = RawData.PickupRaw ?? "";
            RawData.SetSlot("pickup", $"{existing}, {clarifiedValue}");
        }
        else if (field == "destination")
        {
            var existing = RawData.DestinationRaw ?? "";
            RawData.SetSlot("destination", $"{existing}, {clarifiedValue}");
        }

        // Transition back to re-verify (re-geocode) the clarified field — NOT to ReadyForExtraction
        var targetState = field == "pickup"
            ? CollectionState.VerifyingPickup
            : CollectionState.VerifyingDestination;

        PendingClarification = null;
        TransitionTo(targetState);
    }

    /// <summary>
    /// Extraction failed — go back to collection to fix issues.
    /// </summary>
    public void ExtractionFailed(string error)
    {
        Log($"Extraction failed: {error}");
        // Determine which slot needs fixing based on error, or go back to first
        var next = RawData.NextMissingSlot() ?? "pickup";
        TransitionTo(SlotToState(next));
    }

    /// <summary>
    /// Caller selected payment method. Advance to confirmation.
    /// </summary>
    public void AcceptPaymentChoice(string method)
    {
        if (State != CollectionState.AwaitingPaymentChoice && State != CollectionState.PresentingFare)
        {
            Log($"Payment choice ignored in state {State}");
            return;
        }
        Log($"Payment method: {method}");
        TransitionTo(CollectionState.AwaitingConfirmation);
    }

    /// <summary>
    /// Caller confirmed booking. Dispatch.
    /// CRITICAL GATE: Also verifies all slots are filled before dispatching.
    /// </summary>
    public void ConfirmBooking()
    {
        if (State != CollectionState.AwaitingConfirmation)
        {
            Log($"Confirmation ignored in state {State}");
            return;
        }
        if (!RawData.AllRequiredPresent)
        {
            Log($"⛔ Confirmation BLOCKED — slots still missing. Next: {RawData.NextMissingSlot()}");
            var next = RawData.NextMissingSlot()!;
            TransitionTo(SlotToState(next));
            return;
        }
        TransitionTo(CollectionState.Dispatched);
    }

    /// <summary>
    /// Caller rejected fare or wants to change something.
    /// Go back to the slot they want to correct.
    /// </summary>
    public void RejectAndCorrect(string slotName)
    {
        TransitionTo(SlotToState(slotName));
    }

    /// <summary>
    /// End the call gracefully.
    /// CRITICAL GATE: Refuses to end the call if slots are still missing
    /// (unless force=true for hangup/disconnect scenarios).
    /// </summary>
    public void EndCall(bool force = false)
    {
        if (!force && !RawData.AllRequiredPresent && State < CollectionState.ReadyForExtraction)
        {
            Log($"EndCall BLOCKED — slots still missing. Next: {RawData.NextMissingSlot()}");
            // Stay in current collection state, don't transition to Ending
            return;
        }
        TransitionTo(CollectionState.Ending);
    }

    /// <summary>
    /// Build the ExtractionRequest payload for the AI service.
    /// </summary>
    public ExtractionRequest BuildExtractionRequest(CallerContext? context = null)
    {
        return new ExtractionRequest
        {
            Context = context,
            Slots = new RawSlots
            {
                Name = RawData.NameRaw ?? "",
                Pickup = RawData.PickupRaw ?? "",
                Destination = RawData.DestinationRaw ?? "",
                Passengers = RawData.PassengersRaw ?? "",
                PickupTime = RawData.PickupTimeRaw ?? ""
            }
        };
    }

    /// <summary>
    /// Force engine into a specific state — used for address correction re-verification.
    /// </summary>
    public void ForceState(CollectionState newState)
    {
        Log($"Force state: {State} → {newState}");
        TransitionTo(newState);
    }

    private void TransitionTo(CollectionState newState)
    {
        var old = State;
        State = newState;
        Log($"State: {old} → {newState}");
        OnStateChanged?.Invoke(old, newState);
    }

    private static CollectionState SlotToState(string slotName) => slotName.ToLowerInvariant() switch
    {
        "name" => CollectionState.CollectingName,
        "pickup" => CollectionState.CollectingPickup,
        "destination" => CollectionState.CollectingDestination,
        "passengers" => CollectionState.CollectingPassengers,
        "pickup_time" => CollectionState.CollectingPickupTime,
        _ => CollectionState.CollectingPickup
    };

    private void Log(string msg) => OnLog?.Invoke($"[Engine] {msg}");
}
