using AdaCleanVersion.Models;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Deterministic state machine for taxi booking collection.
/// 
/// Key principle: AI NEVER controls state transitions.
/// The engine decides what to collect, when to extract, and when to dispatch.
/// AI is only used as a normalization function at the extraction phase.
/// 
/// FSM transitions are enforced via AllowedTransitions map.
/// ForceState() bypasses the map for legitimate correction jumps (logged as FORCE).
/// </summary>
public class CallStateEngine
{
    public CollectionState State { get; private set; } = CollectionState.Greeting;
    public RawBookingData RawData { get; } = new();
    public StructuredBooking? StructuredResult { get; private set; }
    public FareResult? FareResult { get; private set; }

    /// <summary>
    /// Hard confirmation lock — set ONLY when state == AwaitingConfirmation
    /// and caller explicitly says yes. Engine-level authority, not AI.
    /// </summary>
    public bool ConfirmationReceived { get; private set; }

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

    // ─── TRANSITION MAP ─────────────────────────────────────────
    // Defines all legal state transitions. Any transition not in this map
    // is blocked by TransitionTo() (ForceState bypasses with audit log).

    private static readonly Dictionary<CollectionState, HashSet<CollectionState>> AllowedTransitions = new()
    {
        [CollectionState.Greeting] = new()
        {
            CollectionState.CollectingName, CollectionState.CollectingPickup,
            CollectionState.CollectingDestination, CollectionState.CollectingPassengers,
            CollectionState.CollectingPickupTime, CollectionState.ReadyForExtraction,
            CollectionState.ManagingExistingBooking, CollectionState.Ending
        },
        [CollectionState.ManagingExistingBooking] = new()
        {
            CollectionState.AwaitingCancelConfirmation, CollectionState.CollectingName,
            CollectionState.CollectingPickup, CollectionState.Ending
        },
        [CollectionState.AwaitingCancelConfirmation] = new()
        {
            CollectionState.ManagingExistingBooking, CollectionState.Ending
        },
        [CollectionState.CollectingName] = new()
        {
            CollectionState.CollectingPickup, CollectionState.CollectingDestination,
            CollectionState.CollectingPassengers, CollectionState.CollectingPickupTime,
            CollectionState.ReadyForExtraction, CollectionState.Ending
        },
        [CollectionState.CollectingPickup] = new()
        {
            CollectionState.VerifyingPickup, CollectionState.CollectingDestination,
            CollectionState.ReadyForExtraction, CollectionState.Ending
        },
        [CollectionState.VerifyingPickup] = new()
        {
            CollectionState.CollectingDestination, CollectionState.CollectingPassengers,
            CollectionState.CollectingPickupTime, CollectionState.VerifyingDestination,
            CollectionState.ReadyForExtraction, CollectionState.AwaitingClarification,
            CollectionState.Ending
        },
        [CollectionState.CollectingDestination] = new()
        {
            CollectionState.VerifyingDestination, CollectionState.CollectingPassengers,
            CollectionState.ReadyForExtraction, CollectionState.Ending
        },
        [CollectionState.VerifyingDestination] = new()
        {
            CollectionState.CollectingPassengers, CollectionState.CollectingPickupTime,
            CollectionState.ReadyForExtraction, CollectionState.AwaitingClarification,
            CollectionState.Ending
        },
        [CollectionState.CollectingPassengers] = new()
        {
            CollectionState.CollectingPickupTime, CollectionState.ReadyForExtraction,
            CollectionState.Ending
        },
        [CollectionState.CollectingPickupTime] = new()
        {
            CollectionState.ReadyForExtraction, CollectionState.Ending
        },
        [CollectionState.ReadyForExtraction] = new()
        {
            CollectionState.Extracting, CollectionState.CollectingName,
            CollectionState.CollectingPickup, CollectionState.CollectingDestination,
            CollectionState.CollectingPassengers, CollectionState.CollectingPickupTime,
            CollectionState.Ending
        },
        [CollectionState.Extracting] = new()
        {
            CollectionState.Geocoding, CollectionState.CollectingPickup,
            CollectionState.CollectingDestination, CollectionState.ReadyForExtraction,
            CollectionState.Ending
        },
        [CollectionState.Geocoding] = new()
        {
            CollectionState.PresentingFare, CollectionState.AwaitingClarification,
            CollectionState.ReadyForExtraction, CollectionState.Ending
        },
        [CollectionState.AwaitingClarification] = new()
        {
            CollectionState.VerifyingPickup, CollectionState.VerifyingDestination,
            CollectionState.CollectingPickup, CollectionState.CollectingDestination,
            CollectionState.Ending
        },
        [CollectionState.PresentingFare] = new()
        {
            CollectionState.AwaitingPaymentChoice, CollectionState.AwaitingConfirmation,
            CollectionState.ReadyForExtraction, CollectionState.CollectingPickup,
            CollectionState.CollectingDestination, CollectionState.CollectingPassengers,
            CollectionState.CollectingPickupTime, CollectionState.Ending
        },
        [CollectionState.AwaitingPaymentChoice] = new()
        {
            CollectionState.AwaitingConfirmation, CollectionState.ReadyForExtraction,
            CollectionState.CollectingPickup, CollectionState.CollectingDestination,
            CollectionState.Ending
        },
        [CollectionState.AwaitingConfirmation] = new()
        {
            CollectionState.Dispatched, CollectionState.CollectingPickup,
            CollectionState.CollectingDestination, CollectionState.CollectingPassengers,
            CollectionState.CollectingPickupTime, CollectionState.ReadyForExtraction,
            CollectionState.Ending
        },
        [CollectionState.Dispatched] = new()
        {
            CollectionState.Ending
        },
        [CollectionState.Ending] = new()
        {
            CollectionState.Ended
        },
        [CollectionState.Ended] = new() // Terminal — no transitions allowed
        {
        },
    };

    // ─── COLLECTION ─────────────────────────────────────────────

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
                ForceState(CollectionState.ReadyForExtraction);
        }
    }

    // ─── EXTRACTION ─────────────────────────────────────────────

    /// <summary>
    /// Mark extraction as started.
    /// Idempotency guard: blocks if already extracting or beyond.
    /// </summary>
    public void BeginExtraction()
    {
        if (State >= CollectionState.Extracting && State != CollectionState.ReadyForExtraction)
        {
            Log($"Extraction already in progress or complete (state={State}) — ignoring");
            return;
        }
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

    // ─── VERIFIED ADDRESSES ─────────────────────────────────────
    // Ground truth from geocoding. Uses reverification pattern:
    // ClearVerifiedAddress flags for re-verify but preserves last known value
    // until overwritten by a new verified result.

    /// <summary>Geocoded addresses stored per-slot for inline verification.</summary>
    public GeocodedAddress? VerifiedPickup { get; private set; }
    public GeocodedAddress? VerifiedDestination { get; private set; }

    /// <summary>True when pickup needs re-geocoding (address was corrected).</summary>
    public bool PickupNeedsReverification { get; private set; }
    /// <summary>True when destination needs re-geocoding (address was corrected).</summary>
    public bool DestinationNeedsReverification { get; private set; }

    /// <summary>
    /// Store verified pickup address from inline geocoding and advance to next slot.
    /// </summary>
    public void CompletePickupVerification(GeocodedAddress geocoded)
    {
        VerifiedPickup = geocoded;
        PickupNeedsReverification = false;
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
        DestinationNeedsReverification = false;
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
        ForceState(SlotToState(field));
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

    // ─── CLARIFICATION ──────────────────────────────────────────

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
        ForceState(targetState);
    }

    /// <summary>Clears clarification state without transitioning — used when caller rejects options.</summary>
    public void ClearPendingClarification()
    {
        PendingClarification = null;
    }

    /// <summary>
    /// Extraction failed — go back to collection to fix issues.
    /// </summary>
    public void ExtractionFailed(string error)
    {
        Log($"Extraction failed: {error}");
        // Determine which slot needs fixing based on error, or go back to first
        var next = RawData.NextMissingSlot() ?? "pickup";
        ForceState(SlotToState(next));
    }

    // ─── PAYMENT & CONFIRMATION ─────────────────────────────────

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
            Log($"⛔ Confirmation BLOCKED — state is {State}, not AwaitingConfirmation");
            return;
        }
        if (!RawData.AllRequiredPresent)
        {
            Log($"⛔ Confirmation BLOCKED — slots still missing. Next: {RawData.NextMissingSlot()}");
            var next = RawData.NextMissingSlot()!;
            ForceState(SlotToState(next));
            return;
        }
        ConfirmationReceived = true;
        Log("✅ ConfirmationReceived = true — caller explicitly confirmed");
        TransitionTo(CollectionState.Dispatched);
    }

    /// <summary>
    /// Caller rejected fare or wants to change something.
    /// Go back to the slot they want to correct.
    /// </summary>
    public void RejectAndCorrect(string slotName)
    {
        ForceState(SlotToState(slotName));
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

    // ─── EXTRACTION REQUEST ─────────────────────────────────────

    /// <summary>
    /// Build the ExtractionRequest payload for the AI service.
    /// </summary>
    public ExtractionRequest BuildExtractionRequest(CallerContext? context = null)
    {
        // Prefer verified/geocoded addresses over raw STT when available.
        // Raw slots can contain garbled STT (e.g., "3,000 years." instead of "7 Russell Street"),
        // but verified addresses have been confirmed via geocoding.
        // REVERIFICATION GUARD: If flagged for re-verify, use raw data (new correction) not stale verified.
        var pickupForExtraction = (!PickupNeedsReverification && VerifiedPickup != null)
            ? VerifiedPickup.Address : (RawData.PickupRaw ?? "");
        var destinationForExtraction = (!DestinationNeedsReverification && VerifiedDestination != null)
            ? VerifiedDestination.Address : (RawData.DestinationRaw ?? "");

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

    // ─── STATE CONTROL ──────────────────────────────────────────

    /// <summary>
    /// Force engine into a specific state — bypasses transition map.
    /// Used for address correction re-verification and error recovery.
    /// Logged as FORCE for audit trail.
    /// </summary>
    public void ForceState(CollectionState newState)
    {
        var old = State;
        Log($"⚡ FORCE state: {old} → {newState}");
        State = newState;
        OnStateChanged?.Invoke(old, newState);
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
    /// Flag a verified address for re-verification after correction.
    /// Preserves the last known verified value until overwritten by a new geocode result.
    /// Ground truth should never disappear — only be replaced.
    /// </summary>
    public void ClearVerifiedAddress(string field)
    {
        if (field == "pickup")
        {
            PickupNeedsReverification = true;
            Log($"Pickup flagged for re-verification (preserving last verified: \"{VerifiedPickup?.Address ?? "none"}\")");
        }
        else if (field == "destination")
        {
            DestinationNeedsReverification = true;
            Log($"Destination flagged for re-verification (preserving last verified: \"{VerifiedDestination?.Address ?? "none"}\")");
        }
    }

    /// <summary>
    /// Hard-clear a verified address (sets to null). Use only when the address is
    /// completely invalid and no fallback is acceptable (e.g., same-address guard).
    /// </summary>
    public void HardClearVerifiedAddress(string field)
    {
        if (field == "pickup")
        {
            VerifiedPickup = null;
            PickupNeedsReverification = false;
            Log("Verified pickup HARD CLEARED (no fallback)");
        }
        else if (field == "destination")
        {
            VerifiedDestination = null;
            DestinationNeedsReverification = false;
            Log("Verified destination HARD CLEARED (no fallback)");
        }
    }

    // ─── EXISTING BOOKING MANAGEMENT ────────────────────────────

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
    /// Auto-reverts to ManagingExistingBooking if expired — prevents soft-lock.
    /// </summary>
    public bool IsCancelConfirmationExpired()
    {
        if (!_cancelConfirmationRequestedAt.HasValue) return false;
        if ((DateTime.UtcNow - _cancelConfirmationRequestedAt.Value).TotalSeconds <= CANCEL_CONFIRMATION_TIMEOUT_SECONDS)
            return false;

        // Auto-reset: revert to ManagingExistingBooking to prevent soft-lock
        Log("⏰ Cancel confirmation EXPIRED — auto-reverting to ManagingExistingBooking");
        ClearCancelConfirmation();
        ForceState(CollectionState.ManagingExistingBooking);
        return true;
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

    // ─── CORE TRANSITION ────────────────────────────────────────

    /// <summary>
    /// Guarded state transition — validates against AllowedTransitions map.
    /// Illegal transitions are blocked and logged.
    /// </summary>
    private void TransitionTo(CollectionState newState)
    {
        var old = State;

        // Self-transition is always allowed (no-op)
        if (old == newState) return;

        // ── DISPATCH HARD BLOCK ──
        // Dispatched is ONLY reachable from AwaitingConfirmation with ConfirmationReceived = true.
        // Even if a bug or future code tries to dispatch, this gate prevents it.
        if (newState == CollectionState.Dispatched)
        {
            if (!ConfirmationReceived || old != CollectionState.AwaitingConfirmation)
            {
                Log($"⛔ DISPATCH BLOCKED: ConfirmationReceived={ConfirmationReceived}, state={old} — cannot dispatch");
                return;
            }
        }

        // Validate against transition map
        if (!AllowedTransitions.TryGetValue(old, out var allowed) || !allowed.Contains(newState))
        {
            Log($"⛔ ILLEGAL transition blocked: {old} → {newState}");
            return;
        }

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
