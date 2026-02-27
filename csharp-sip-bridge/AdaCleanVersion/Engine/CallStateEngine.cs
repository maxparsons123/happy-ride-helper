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
    /// Post-extraction guard: if verified addresses exist, override any LLM drift
    /// (e.g., LLM normalizing "7 Russell Street" back to "7 Brent Russell Street"
    /// because the raw conversation context contained the garbled version).
    /// </summary>
    public void CompleteExtraction(StructuredBooking booking)
    {
        StructuredResult = booking;

        // Override LLM-normalized addresses with verified geocoded ground truth
        if (VerifiedPickup != null && !string.IsNullOrEmpty(VerifiedPickup.Address))
        {
            var overridden = BuildOverrideAddress(VerifiedPickup.Address, booking.Pickup);
            if (overridden != null)
            {
                StructuredResult = new StructuredBooking
                {
                    CallerName = booking.CallerName,
                    Pickup = overridden,
                    Destination = booking.Destination,
                    Passengers = booking.Passengers,
                    PickupTime = booking.PickupTime,
                    PickupDateTime = booking.PickupDateTime
                };
                Log($"Post-extraction pickup override: \"{booking.Pickup.DisplayName}\" → \"{overridden.DisplayName}\"");
            }
        }

        if (VerifiedDestination != null && !string.IsNullOrEmpty(VerifiedDestination.Address))
        {
            var overridden = BuildOverrideAddress(VerifiedDestination.Address, StructuredResult.Destination);
            if (overridden != null)
            {
                StructuredResult = new StructuredBooking
                {
                    CallerName = StructuredResult.CallerName,
                    Pickup = StructuredResult.Pickup,
                    Destination = overridden,
                    Passengers = StructuredResult.Passengers,
                    PickupTime = StructuredResult.PickupTime,
                    PickupDateTime = StructuredResult.PickupDateTime
                };
                Log($"Post-extraction destination override: \"{booking.Destination.DisplayName}\" → \"{overridden.DisplayName}\"");
            }
        }

        TransitionTo(CollectionState.Geocoding);
    }

    /// <summary>
    /// Build an override StructuredAddress from a verified geocoded address string.
    /// Uses AddressParser for house/street and regex for postcode extraction.
    /// Returns null if parsing yields nothing useful.
    /// </summary>
    private static StructuredAddress? BuildOverrideAddress(string verifiedAddress, StructuredAddress fallback)
    {
        var parsed = Services.AddressParser.ParseAddress(verifiedAddress);
        
        // Extract postcode from verified address (e.g., "CV1 2BW")
        var postcodeMatch = System.Text.RegularExpressions.Regex.Match(
            verifiedAddress, @"[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var postcode = postcodeMatch.Success ? postcodeMatch.Value : fallback.Postcode;

        var houseNum = parsed.HasHouseNumber ? parsed.HouseNumber : fallback.HouseNumber;
        var street = !string.IsNullOrEmpty(parsed.StreetName) ? parsed.StreetName : fallback.StreetName;
        var area = !string.IsNullOrEmpty(parsed.TownOrArea) ? parsed.TownOrArea : fallback.Area;

        return new StructuredAddress
        {
            HouseNumber = houseNum,
            StreetName = street,
            Area = fallback.Area ?? area,
            City = fallback.City ?? area,
            Postcode = postcode
        };
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

        // Update the raw slot: use Ada's readback (cleaned address) + area clarification
        // instead of endlessly appending to garbage accumulated raw strings.
        // E.g., if Ada read back "52A David Road, Coventry" and caller says "Stoke",
        // we want "52A David Road, Stoke, Coventry" — not the raw STT noise.
        if (field == "pickup")
        {
            var verified = VerifiedPickup?.Address;
            var baseAddr = !string.IsNullOrEmpty(verified) ? verified : (RawData.PickupRaw ?? "");
            RawData.SetSlot("pickup", $"{baseAddr}, {clarifiedValue}");
        }
        else if (field == "destination")
        {
            var verified = VerifiedDestination?.Address;
            var baseAddr = !string.IsNullOrEmpty(verified) ? verified : (RawData.DestinationRaw ?? "");
            RawData.SetSlot("destination", $"{baseAddr}, {clarifiedValue}");
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
