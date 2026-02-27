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

    /// <summary>Whether the caller has an active booking loaded from the database.</summary>
    public bool HasActiveBooking { get; set; }

    /// <summary>UUID of existing active booking (for cancel/status operations).</summary>
    public string? ExistingBookingId { get; set; }

    /// <summary>iCabbi journey ID from the existing booking (for cancel operations).</summary>
    public string? ExistingIcabbiJourneyId { get; set; }

    /// <summary>Timestamp when cancel confirmation was requested (for timeout guard).</summary>
    private DateTime? _cancelConfirmationRequestedAt;
    private const int CANCEL_CONFIRMATION_TIMEOUT_SECONDS = 30;

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
    /// Store the structured result from extraction and advance to fare presentation.
    /// No LLM drift guard needed — DirectBookingBuilder uses verified addresses directly.
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

        // Don't resurrect a dead/ending call
        if (State >= CollectionState.Ending)
        {
            Log("Pickup verification completed after call ended — ignoring state transition");
            return;
        }

        // If destination is pre-filled (burst) but unverified, go verify it next
        if (!string.IsNullOrWhiteSpace(RawData.DestinationRaw) && VerifiedDestination == null)
        {
            TransitionTo(CollectionState.VerifyingDestination);
            return;
        }

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

        // Don't resurrect a dead/ending call
        if (State >= CollectionState.Ending)
        {
            Log("Destination verification completed after call ended — ignoring state transition");
            return;
        }

        // Clear burst flag — both addresses verified, resume normal flow
        if (RawData.IsMultiSlotBurst)
        {
            RawData.IsMultiSlotBurst = false;
            Log("Multi-slot burst complete — resuming normal flow");
        }

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
    /// Inline geocoding failed — ask for clarification (area/city) instead of clearing the slot.
    /// This avoids frustrating "repeat your address" loops when geocoding times out or fails.
    /// Only clears and re-collects if the slot is truly empty or after clarification exhaustion.
    /// </summary>
    public void SkipVerification(string field, string reason, bool forceRecollect = false)
    {
        Log($"Verification skipped for {field}: {reason}");

        var rawValue = field == "pickup" ? RawData.PickupRaw : RawData.DestinationRaw;

        // If we have a raw value and haven't exhausted clarification, ask for area/city context
        if (!forceRecollect && !string.IsNullOrWhiteSpace(rawValue))
        {
            Log($"Keeping raw slot '{field}' (\"{rawValue}\") — asking caller to confirm or provide more detail");
            EnterClarification(new ClarificationInfo
            {
                AmbiguousField = field,
                Message = $"GEOCODE_FAILED:{rawValue}",
            });
            return;
        }

        // Fallback: clear and re-collect (e.g., slot was empty or clarification exhausted)
        RawData.SetSlot(field, "");
        Log($"Cleared raw slot '{field}' — forcing re-collection");
        TransitionTo(SlotToState(field));
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

        // Strip noise responses ("okay", "yes", "yeah", "sure", "right") that are just
        // confirmations of Ada's suggestion, not actual area/address clarification data.
        var cleaned = clarifiedValue.Trim().TrimEnd('.', '!', '?');
        var noiseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "okay", "ok", "yes", "yeah", "yep", "sure", "right", "correct", 
            "that's right", "that's correct", "yes please", "yea"
        };
        
        bool isNoiseOnly = noiseWords.Contains(cleaned);

        // Update the raw slot: use Ada's readback (cleaned address) + area clarification
        // instead of endlessly appending to garbage accumulated raw strings.
        if (field == "pickup")
        {
            var verified = VerifiedPickup?.Address;
            var baseAddr = !string.IsNullOrEmpty(verified) ? verified : (RawData.PickupRaw ?? "");
            // If caller just said "okay" confirming Ada's suggestion, don't append — use base as-is
            RawData.SetSlot("pickup", isNoiseOnly ? baseAddr : $"{baseAddr}, {cleaned}");
        }
        else if (field == "destination")
        {
            var verified = VerifiedDestination?.Address;
            var baseAddr = !string.IsNullOrEmpty(verified) ? verified : (RawData.DestinationRaw ?? "");
            RawData.SetSlot("destination", isNoiseOnly ? baseAddr : $"{baseAddr}, {cleaned}");
        }
        
        if (isNoiseOnly)
            Log($"Clarification was noise-only (\"{clarifiedValue}\") — using base address without appending");

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
        // Prefer verified/geocoded addresses over raw STT when available.
        // Raw slots can contain garbled STT (e.g., "3,000 years." instead of "7 Russell Street"),
        // but verified addresses have been confirmed via geocoding.
        var pickupForExtraction = VerifiedPickup?.Address ?? RawData.PickupRaw ?? "";
        var destinationForExtraction = VerifiedDestination?.Address ?? RawData.DestinationRaw ?? "";

        return new ExtractionRequest
        {
            Context = context,
            Slots = new RawSlots
            {
                Name = RawData.NameRaw ?? "",
                Pickup = pickupForExtraction,
                Destination = destinationForExtraction,
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

    /// <summary>
    /// Advance to the collection state for a named slot (e.g. "passengers" → CollectingPassengers).
    /// Used by tool call handler to progress after populating partial slots.
    /// </summary>
    public void AdvanceToSlot(string slotName)
    {
        var target = SlotToState(slotName);
        Log($"AdvanceToSlot: {slotName} → {target}");
        TransitionTo(target);
    }

    /// <summary>
    /// Clear fare result for recalculation after address correction.
    /// </summary>
    public void ClearFareResult()
    {
        FareResult = null;
        Log("Fare result cleared for recalculation");
    }

    /// <summary>
    /// Clear a verified address so it can be re-verified after correction.
    /// </summary>
    public void ClearVerifiedAddress(string field)
    {
        if (field == "pickup")
        {
            VerifiedPickup = null;
            Log("Verified pickup cleared for re-verification");
        }
        else if (field == "destination")
        {
            VerifiedDestination = null;
            Log("Verified destination cleared for re-verification");
        }
    }

    // ── EXISTING BOOKING MANAGEMENT ──

    /// <summary>
    /// Start cancel confirmation flow — sets timeout guard.
    /// </summary>
    public void RequestCancelConfirmation()
    {
        _cancelConfirmationRequestedAt = DateTime.UtcNow;
        TransitionTo(CollectionState.AwaitingCancelConfirmation);
        Log("Cancel confirmation requested — awaiting caller confirmation");
    }

    /// <summary>
    /// Check if cancel confirmation has expired (caller took too long).
    /// </summary>
    public bool IsCancelConfirmationExpired()
    {
        if (!_cancelConfirmationRequestedAt.HasValue) return false;
        return (DateTime.UtcNow - _cancelConfirmationRequestedAt.Value).TotalSeconds > CANCEL_CONFIRMATION_TIMEOUT_SECONDS;
    }

    /// <summary>
    /// Clear cancel confirmation state (after confirmed or rejected).
    /// </summary>
    public void ClearCancelConfirmation()
    {
        _cancelConfirmationRequestedAt = null;
    }

    /// <summary>
    /// Start new booking after managing existing (preserves caller identity).
    /// </summary>
    public void StartNewBookingFromManaging()
    {
        HasActiveBooking = false;
        ExistingBookingId = null;
        ExistingIcabbiJourneyId = null;
        TransitionTo(CollectionState.CollectingPickup);
        Log("New booking started — cleared active booking");
    }

    /// <summary>
    /// Whether we're in a recalculation flow (address corrected after fare was presented).
    /// </summary>
    public bool IsRecalculating { get; set; }

    private void TransitionTo(CollectionState newState)
    {
        var old = State;
        State = newState;
        Log($"State: {old} → {newState}");
        OnStateChanged?.Invoke(old, newState);
    }

    public static CollectionState SlotToState(string slotName) => slotName.ToLowerInvariant() switch
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
