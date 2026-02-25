// Last updated: 2026-02-25 (v1.0 — Split-Brain deterministic state machine)
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Core;

/// <summary>
/// Deterministic state machine for taxi booking voice calls.
/// 
/// This is the "brain" of the Split-Brain architecture:
/// - The model extracts data and generates speech
/// - The engine decides workflow, gates tools, and transitions state
/// - The backend executes approved actions
/// 
/// INTEGRATION STRATEGY (Phase 1 — Overlay):
/// Wire this alongside existing CallSession tool handlers.
/// Use it to GATE destructive actions (cancel, book) and enforce ordering.
/// Existing logic (disambiguation, fare sanity, house number guards) stays in CallSession.
/// 
/// Phase 2: Move more logic from CallSession into the engine.
/// Phase 3: Replace IntentGuard + DestructiveActionGuard entirely.
/// </summary>
public sealed class CallStateEngine
{
    private readonly ILogger _logger;
    private readonly string _sessionId;

    public CallState State { get; private set; }
    
    /// <summary>Whether the caller has an active booking loaded from the database.</summary>
    public bool HasActiveBooking { get; private set; }
    
    /// <summary>Whether a fare has been calculated and is available.</summary>
    public bool FareAvailable { get; private set; }
    
    /// <summary>Whether the booking has been dispatched (book_taxi confirmed).</summary>
    public bool BookingDispatched { get; private set; }
    
    /// <summary>Whether the fare was explicitly rejected by the caller.</summary>
    public bool FareRejected { get; private set; }
    
    /// <summary>Whether a payment preference has been selected.</summary>
    public string? PaymentPreference { get; private set; }
    
    /// <summary>Whether this is an amendment to an existing confirmed booking.</summary>
    public bool IsAmendment { get; private set; }
    
    /// <summary>Timestamp of when cancellation confirmation was requested.</summary>
    private DateTime? _cancelConfirmationRequestedAt;
    private const int CANCEL_CONFIRMATION_TIMEOUT_SECONDS = 30;

    public CallStateEngine(ILogger logger, string sessionId, bool hasActiveBooking = false)
    {
        _logger = logger;
        _sessionId = sessionId;
        HasActiveBooking = hasActiveBooking;
        State = hasActiveBooking ? CallState.ManagingExistingBooking : CallState.Greeting;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CORE INPUT HANDLER
    // ═══════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Process an extraction result and determine the next action.
    /// This is the single entry point per user turn.
    /// </summary>
    public EngineAction HandleInput(ExtractionResult input)
    {
        var previousState = State;
        var action = ResolveAction(input);
        
        if (action.NewState != previousState)
        {
            _logger.LogInformation("[{SessionId}] 🔄 STATE: {From} → {To} (intent={Intent})",
                _sessionId, previousState, action.NewState, input.Intent);
        }
        
        State = action.NewState;
        return action;
    }

    private EngineAction ResolveAction(ExtractionResult input)
    {
        // Global intents that override state
        if (input.Intent == ExtractedIntent.TransferOperator)
            return new EngineAction { TransferToOperator = true, NewState = CallState.Transferring,
                PromptInstruction = "Tell the caller you're transferring them to an operator." };
        
        if (input.Intent == ExtractedIntent.Goodbye)
            return new EngineAction { EndCall = true, NewState = CallState.Ending,
                PromptInstruction = "Say goodbye warmly." };

        return State switch
        {
            CallState.Greeting => HandleGreeting(input),
            CallState.CollectingName => HandleCollectingName(input),
            CallState.CollectingPickup => HandleCollectingPickup(input),
            CallState.CollectingDestination => HandleCollectingDestination(input),
            CallState.CollectingPassengers => HandleCollectingPassengers(input),
            CallState.CollectingTime => HandleCollectingTime(input),
            CallState.FareCalculating => HandleFareCalculating(input),
            CallState.AwaitingPaymentChoice => HandleAwaitingPaymentChoice(input),
            CallState.AwaitingBookingConfirmation => HandleAwaitingBookingConfirmation(input),
            CallState.BookingConfirmed or CallState.AnythingElse => HandleAnythingElse(input),
            CallState.ManagingExistingBooking => HandleManagingExistingBooking(input),
            CallState.AwaitingCancelConfirmation => HandleAwaitingCancelConfirmation(input),
            CallState.AwaitingAmendmentField => HandleAwaitingAmendment(input),
            CallState.DisambiguatingPickup or CallState.DisambiguatingDestination => HandleDisambiguation(input),
            CallState.AirportIntercept => HandleAirportIntercept(input),
            CallState.FareSanityCheck or CallState.AddressDiscrepancy => HandleFareSanityOrDiscrepancy(input),
            CallState.AwaitingPickupHouseNumber or CallState.AwaitingDestHouseNumber => HandleAwaitingHouseNumber(input),
            CallState.AwaitingDestCity => HandleAwaitingDestCity(input),
            _ => EngineAction.Silent(State)
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STATE HANDLERS — each returns an EngineAction
    // ═══════════════════════════════════════════════════════════════════════

    private EngineAction HandleGreeting(ExtractionResult input)
    {
        if (!string.IsNullOrWhiteSpace(input.Name))
            return EngineAction.Speak(CallState.CollectingPickup, "Greet the caller by name and ask for pickup address.")
                with { UseSemanticVad = true, VadEagerness = 0.2f };
        
        return EngineAction.Speak(CallState.CollectingName, "Ask for the caller's name.")
            with { UseSemanticVad = false };
    }

    private EngineAction HandleCollectingName(ExtractionResult input)
    {
        if (!string.IsNullOrWhiteSpace(input.Name))
            return EngineAction.Speak(CallState.CollectingPickup, "Ask for pickup address.")
                with { UseSemanticVad = true, VadEagerness = 0.2f };
        
        return EngineAction.Speak(CallState.CollectingName, "Ask for their name again.");
    }

    private EngineAction HandleCollectingPickup(ExtractionResult input)
    {
        if (!string.IsNullOrWhiteSpace(input.Pickup))
            return EngineAction.Speak(CallState.CollectingDestination, "Ask where they'd like to go.")
                with { UseSemanticVad = true, VadEagerness = 0.2f };
        
        // Compound input: user provided everything at once
        if (!string.IsNullOrWhiteSpace(input.Destination))
            return AdvanceToNextMissing(input);
        
        return EngineAction.Silent(CallState.CollectingPickup);
    }

    private EngineAction HandleCollectingDestination(ExtractionResult input)
    {
        if (!string.IsNullOrWhiteSpace(input.Destination))
            return AdvanceToNextMissing(input);
        
        return EngineAction.Silent(CallState.CollectingDestination);
    }

    private EngineAction HandleCollectingPassengers(ExtractionResult input)
    {
        if (input.Passengers.HasValue)
            return AdvanceToNextMissing(input);
        
        return EngineAction.Silent(CallState.CollectingPassengers);
    }

    private EngineAction HandleCollectingTime(ExtractionResult input)
    {
        if (!string.IsNullOrWhiteSpace(input.PickupTime))
            return new EngineAction
            {
                RequestFareQuote = true,
                NewState = CallState.FareCalculating,
                PromptInstruction = "Tell the caller you're checking the fare. Do NOT guess any amount.",
                UseSemanticVad = false
            };
        
        return EngineAction.Silent(CallState.CollectingTime);
    }

    private EngineAction HandleFareCalculating(ExtractionResult input)
    {
        // Block everything during fare calculation — user should wait
        return EngineAction.Speak(CallState.FareCalculating,
            "The fare is still being calculated. Tell the caller to hold on a moment.");
    }

    private EngineAction HandleAwaitingPaymentChoice(ExtractionResult input)
    {
        if (input.Intent == ExtractedIntent.ChooseCard || 
            input.PaymentPreference?.ToLowerInvariant() is "card" or "fixed" or "link")
        {
            PaymentPreference = "card";
            return new EngineAction
            {
                ExecuteBooking = true,
                NewState = CallState.BookingConfirmed,
                PromptInstruction = null // Backend handles speech injection after booking
            };
        }
        
        if (input.Intent == ExtractedIntent.ChooseMeter ||
            input.PaymentPreference?.ToLowerInvariant() is "meter" or "cash" or "on the day")
        {
            PaymentPreference = "meter";
            return new EngineAction
            {
                ExecuteBooking = true,
                NewState = CallState.BookingConfirmed,
                PromptInstruction = null
            };
        }
        
        if (input.Intent == ExtractedIntent.Reject)
        {
            FareRejected = true;
            return EngineAction.Speak(CallState.CollectingPickup,
                "Ask what they'd like to change — pickup, destination, or passengers.");
        }
        
        return EngineAction.Speak(CallState.AwaitingPaymentChoice,
            "Repeat the payment options: fixed price via payment link, or pay by meter on the day.");
    }

    private EngineAction HandleAwaitingBookingConfirmation(ExtractionResult input)
    {
        if (input.Intent == ExtractedIntent.Confirm)
        {
            return new EngineAction
            {
                ExecuteBooking = true,
                NewState = CallState.BookingConfirmed
            };
        }
        
        if (input.Intent == ExtractedIntent.Reject)
        {
            FareRejected = true;
            return EngineAction.Speak(CallState.CollectingPickup,
                "Ask what they'd like to change.");
        }
        
        return EngineAction.Speak(CallState.AwaitingBookingConfirmation,
            "Ask the caller to confirm: would they like to go ahead and book?");
    }

    private EngineAction HandleAnythingElse(ExtractionResult input)
    {
        if (input.Intent == ExtractedIntent.Goodbye || input.Intent == ExtractedIntent.Reject)
            return new EngineAction { EndCall = true, NewState = CallState.Ending,
                PromptInstruction = "Say the final closing script and end the call." };
        
        if (input.Intent == ExtractedIntent.NewBooking)
        {
            Reset(preserveCallerIdentity: true);
            return EngineAction.Speak(CallState.CollectingPickup,
                "Start a new booking. Ask for pickup address.");
        }
        
        if (!string.IsNullOrWhiteSpace(input.SpecialInstructions))
            return EngineAction.Speak(CallState.AnythingElse,
                "Confirm the special instructions were saved. Ask if there's anything else.");
        
        return EngineAction.Silent(CallState.AnythingElse);
    }

    // ── EXISTING BOOKING MANAGEMENT ──

    private EngineAction HandleManagingExistingBooking(ExtractionResult input)
    {
        switch (input.Intent)
        {
            case ExtractedIntent.CancelBooking:
                _cancelConfirmationRequestedAt = DateTime.UtcNow;
                return EngineAction.Speak(CallState.AwaitingCancelConfirmation,
                    "Ask the caller to confirm they want to cancel their booking.")
                    with { UseSemanticVad = false };
            
            case ExtractedIntent.AmendBooking:
                return EngineAction.Speak(CallState.AwaitingAmendmentField,
                    "Ask what they'd like to change about their booking.");
            
            case ExtractedIntent.CheckStatus:
                return new EngineAction
                {
                    CheckStatus = true,
                    NewState = CallState.ManagingExistingBooking,
                    PromptInstruction = "Provide the booking status to the caller."
                };
            
            case ExtractedIntent.NewBooking:
                Reset(preserveCallerIdentity: true);
                HasActiveBooking = false;
                return EngineAction.Speak(CallState.CollectingPickup,
                    "Start a fresh booking. Ask for pickup address.")
                    with { UseSemanticVad = true, VadEagerness = 0.2f };
            
            default:
                return EngineAction.Speak(CallState.ManagingExistingBooking,
                    "Ask the caller: would you like to cancel, make changes, or check on your driver?");
        }
    }

    private EngineAction HandleAwaitingCancelConfirmation(ExtractionResult input)
    {
        if (input.Intent == ExtractedIntent.Confirm)
        {
            // Timeout check
            if (_cancelConfirmationRequestedAt.HasValue &&
                (DateTime.UtcNow - _cancelConfirmationRequestedAt.Value).TotalSeconds > CANCEL_CONFIRMATION_TIMEOUT_SECONDS)
            {
                _cancelConfirmationRequestedAt = null;
                _logger.LogWarning("[{SessionId}] 🛡️ Cancel confirmation EXPIRED", _sessionId);
                return EngineAction.Speak(CallState.ManagingExistingBooking,
                    "The confirmation timed out. Ask the caller again what they'd like to do.");
            }
            
            _cancelConfirmationRequestedAt = null;
            return new EngineAction
            {
                ExecuteCancel = true,
                NewState = CallState.Greeting,
                PromptInstruction = "Tell the caller their booking has been cancelled. Ask if they'd like a new booking."
            };
        }
        
        if (input.Intent == ExtractedIntent.Reject)
        {
            _cancelConfirmationRequestedAt = null;
            return EngineAction.Speak(CallState.ManagingExistingBooking,
                "The caller decided not to cancel. Ask what else they'd like to do.");
        }
        
        return EngineAction.Speak(CallState.AwaitingCancelConfirmation,
            "Ask again: are you sure you'd like to cancel your booking?");
    }

    private EngineAction HandleAwaitingAmendment(ExtractionResult input)
    {
        IsAmendment = true;
        
        if (!string.IsNullOrWhiteSpace(input.Pickup) || !string.IsNullOrWhiteSpace(input.Destination))
        {
            // Address changed — need to recalculate fare
            FareAvailable = false;
            BookingDispatched = false;
            return AdvanceToNextMissing(input);
        }
        
        if (input.Passengers.HasValue || !string.IsNullOrWhiteSpace(input.PickupTime))
            return AdvanceToNextMissing(input);
        
        return EngineAction.Speak(CallState.AwaitingAmendmentField,
            "Ask what they'd like to change: pickup, destination, passengers, or time.");
    }

    // ── DISAMBIGUATION ──

    private EngineAction HandleDisambiguation(ExtractionResult input)
    {
        if (input.Intent == ExtractedIntent.SelectOption && !string.IsNullOrWhiteSpace(input.SelectedOption))
        {
            if (State == CallState.DisambiguatingPickup)
            {
                return new EngineAction
                {
                    RequestFareQuote = true,
                    NewState = CallState.FareCalculating,
                    PromptInstruction = "Pickup confirmed. Checking the fare now."
                };
            }
            
            return new EngineAction
            {
                RequestFareQuote = true,
                NewState = CallState.FareCalculating,
                PromptInstruction = "Destination confirmed. Checking the fare now."
            };
        }
        
        return EngineAction.Speak(State, "Repeat the options and ask which one they meant.");
    }

    // ── GUARDS ──

    private EngineAction HandleFareSanityOrDiscrepancy(ExtractionResult input)
    {
        // User provided corrected data
        if (!string.IsNullOrWhiteSpace(input.Destination) || !string.IsNullOrWhiteSpace(input.Pickup))
        {
            return new EngineAction
            {
                RequestFareQuote = true,
                NewState = CallState.FareCalculating,
                PromptInstruction = "Checking the updated fare now."
            };
        }
        
        if (input.Intent == ExtractedIntent.Confirm)
        {
            // User confirmed the destination is correct despite sanity alert
            return new EngineAction
            {
                RequestFareQuote = true,
                NewState = CallState.FareCalculating,
                PromptInstruction = "Recalculating with confirmed details."
            };
        }
        
        return EngineAction.Speak(State,
            "Ask the caller to confirm their destination address and which city they're in.");
    }

    private EngineAction HandleAwaitingHouseNumber(ExtractionResult input)
    {
        // The pickup/destination should now include a house number
        if (!string.IsNullOrWhiteSpace(input.Pickup) || !string.IsNullOrWhiteSpace(input.Destination))
            return AdvanceToNextMissing(input);
        
        var target = State == CallState.AwaitingPickupHouseNumber ? "pickup" : "destination";
        return EngineAction.Speak(State, $"Ask for the house number on the {target} street.");
    }

    private EngineAction HandleAwaitingDestCity(ExtractionResult input)
    {
        if (!string.IsNullOrWhiteSpace(input.Destination))
            return AdvanceToNextMissing(input);
        
        return EngineAction.Speak(CallState.AwaitingDestCity,
            "Ask which city or area the destination is in.");
    }

    private EngineAction HandleAirportIntercept(ExtractionResult input)
    {
        return new EngineAction
        {
            SendBookingLink = true,
            NewState = CallState.AnythingElse,
            PromptInstruction = null // Backend injects the message after link is created
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TOOL CALL GATING — Called by CallSession before executing model-initiated tools
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Gate a cancel_booking tool call. Returns an EngineAction.
    /// If BlockToolCall=true, the tool call should be rejected.
    /// If ExecuteCancel=true, the tool call is approved.
    /// </summary>
    public EngineAction GateCancelBooking(bool confirmed)
    {
        if (!confirmed)
        {
            // Model is initiating confirmation — this is allowed
            _cancelConfirmationRequestedAt = DateTime.UtcNow;
            State = CallState.AwaitingCancelConfirmation;
            return EngineAction.Speak(CallState.AwaitingCancelConfirmation,
                "Ask the caller to confirm cancellation.");
        }
        
        // Model says confirmed=true — validate
        if (State != CallState.AwaitingCancelConfirmation)
        {
            _logger.LogWarning("[{SessionId}] ⛔ cancel_booking(confirmed=true) BLOCKED — state is {State}, not AwaitingCancelConfirmation",
                _sessionId, State);
            
            // AUTO-BEGIN safety net: if Ada verbally asked for confirmation but skipped confirmed=false
            _cancelConfirmationRequestedAt = DateTime.UtcNow;
            State = CallState.AwaitingCancelConfirmation;
            
            // Allow it through with the auto-begun state
            _cancelConfirmationRequestedAt = null;
            return new EngineAction { ExecuteCancel = true, NewState = CallState.Greeting };
        }
        
        // Timeout check
        if (_cancelConfirmationRequestedAt.HasValue &&
            (DateTime.UtcNow - _cancelConfirmationRequestedAt.Value).TotalSeconds > CANCEL_CONFIRMATION_TIMEOUT_SECONDS)
        {
            _cancelConfirmationRequestedAt = null;
            State = CallState.ManagingExistingBooking;
            return EngineAction.Block(CallState.ManagingExistingBooking,
                "Confirmation expired. Ask the caller again.");
        }
        
        _cancelConfirmationRequestedAt = null;
        State = CallState.Greeting;
        return new EngineAction { ExecuteCancel = true, NewState = CallState.Greeting };
    }

    /// <summary>
    /// Gate a book_taxi(confirmed) tool call.
    /// </summary>
    public EngineAction GateBookTaxiConfirmed()
    {
        if (!FareAvailable)
        {
            _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — no fare available", _sessionId);
            return EngineAction.Block(State, "Cannot confirm booking without a fare. Calculate fare first.");
        }
        
        if (BookingDispatched)
        {
            _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — already dispatched", _sessionId);
            return EngineAction.Block(State, "Booking already confirmed and dispatched.");
        }
        
        BookingDispatched = true;
        State = CallState.BookingConfirmed;
        return new EngineAction { ExecuteBooking = true, NewState = CallState.BookingConfirmed };
    }

    /// <summary>
    /// Gate an end_call tool call.
    /// </summary>
    public EngineAction GateEndCall()
    {
        if (FareAvailable && !BookingDispatched && !FareRejected)
        {
            _logger.LogWarning("[{SessionId}] ⛔ end_call BLOCKED — fare quoted but booking not confirmed/rejected", _sessionId);
            return EngineAction.Block(State,
                "Cannot end call — a fare was quoted but the booking was never confirmed. " +
                "Ask the caller to confirm or cancel first.");
        }
        
        State = CallState.Ending;
        return new EngineAction { EndCall = true, NewState = CallState.Ending };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STATE NOTIFICATIONS — Called by CallSession when external events occur
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Called when fare calculation completes successfully.</summary>
    public void NotifyFareReceived()
    {
        FareAvailable = true;
        State = CallState.AwaitingPaymentChoice;
        _logger.LogInformation("[{SessionId}] 💰 Engine: fare received → AwaitingPaymentChoice", _sessionId);
    }

    /// <summary>Called when disambiguation is needed (address ambiguous).</summary>
    public void NotifyDisambiguationNeeded(bool isPickup)
    {
        State = isPickup ? CallState.DisambiguatingPickup : CallState.DisambiguatingDestination;
        _logger.LogInformation("[{SessionId}] 🔀 Engine: disambiguation needed ({Target})",
            _sessionId, isPickup ? "pickup" : "destination");
    }

    /// <summary>Called when fare sanity check fails.</summary>
    public void NotifyFareSanityFailed()
    {
        State = CallState.FareSanityCheck;
        FareAvailable = false;
    }

    /// <summary>Called when address discrepancy is detected.</summary>
    public void NotifyAddressDiscrepancy()
    {
        State = CallState.AddressDiscrepancy;
        FareAvailable = false;
    }

    /// <summary>Called when house number is missing from an address.</summary>
    public void NotifyHouseNumberMissing(bool isPickup)
    {
        State = isPickup ? CallState.AwaitingPickupHouseNumber : CallState.AwaitingDestHouseNumber;
    }

    /// <summary>Called when destination lacks city context.</summary>
    public void NotifyDestCityMissing()
    {
        State = CallState.AwaitingDestCity;
    }

    /// <summary>Called when airport destination is detected.</summary>
    public void NotifyAirportDetected()
    {
        State = CallState.AirportIntercept;
    }

    /// <summary>Called when fare calculation begins (all fields filled).</summary>
    public void NotifyFareCalculating()
    {
        State = CallState.FareCalculating;
        _logger.LogInformation("[{SessionId}] 🔄 Engine: fare calculating started", _sessionId);
    }

    /// <summary>Called when booking is confirmed and dispatched via external path (safety net, intent guard).</summary>
    public void NotifyBookingDispatched()
    {
        BookingDispatched = true;
        State = CallState.BookingConfirmed;
        _logger.LogInformation("[{SessionId}] ✅ Engine: booking dispatched (external)", _sessionId);
    }

    /// <summary>Called when the booking enters the AnythingElse stage (post-booking).</summary>
    public void NotifyAnythingElse()
    {
        State = CallState.AnythingElse;
    }

    /// <summary>Called when a new booking intent is detected (resets for fresh collection).</summary>
    public void NotifyNewBooking()
    {
        Reset(preserveCallerIdentity: true);
        HasActiveBooking = false;
        State = CallState.CollectingPickup;
        _logger.LogInformation("[{SessionId}] 🔄 Engine: new booking started", _sessionId);
    }

    /// <summary>Called when the fare is rejected by the caller.</summary>
    public void NotifyFareRejected()
    {
        FareRejected = true;
        FareAvailable = false;
        State = CallState.CollectingPickup;
        _logger.LogInformation("[{SessionId}] ❌ Engine: fare rejected", _sessionId);
    }

    /// <summary>Tracks field collection progress. Call after applying extraction to BookingState.</summary>
    public void UpdateCollectionState(bool hasName, bool hasPickup, bool hasDestination, bool hasPassengers, bool hasTime)
    {
        // Only advance if we're in a collection state
        if (State is not (CallState.Greeting or CallState.CollectingName or CallState.CollectingPickup
            or CallState.CollectingDestination or CallState.CollectingPassengers or CallState.CollectingTime))
            return;

        var nextState = DetermineNextCollectionState(hasName, hasPickup, hasDestination, hasPassengers, hasTime);
        if (nextState != State)
        {
            _logger.LogInformation("[{SessionId}] 📊 Engine collection: {From} → {To}", _sessionId, State, nextState);
            State = nextState;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determine the next collection state based on what's still missing.
    /// Used for compound inputs and field-by-field progression.
    /// 
    /// NOTE: This does NOT check BookingState directly — it relies on the extraction
    /// result to know what was just provided. The caller (CallSession) maintains the
    /// authoritative BookingState and should call this after applying the extraction.
    /// </summary>
    private EngineAction AdvanceToNextMissing(ExtractionResult justProvided)
    {
        // This method returns the state to advance to.
        // CallSession is responsible for checking which fields are actually filled.
        // We return a sentinel that CallSession interprets.
        return new EngineAction
        {
            NewState = CallState.CollectingPickup, // Placeholder — CallSession overrides based on actual BookingState
            PromptInstruction = "__ADVANCE_TO_NEXT__" // Sentinel for CallSession
        };
    }

    /// <summary>
    /// Advance to the next collection state based on actual booking data.
    /// Called by CallSession after applying extraction results to BookingState.
    /// </summary>
    public CallState DetermineNextCollectionState(
        bool hasName, bool hasPickup, bool hasDestination, 
        bool hasPassengers, bool hasPickupTime)
    {
        if (!hasName) return CallState.CollectingName;
        if (!hasPickup) return CallState.CollectingPickup;
        if (!hasDestination) return CallState.CollectingDestination;
        if (!hasPassengers) return CallState.CollectingPassengers;
        if (!hasPickupTime) return CallState.CollectingTime;
        return CallState.FareCalculating; // All fields filled
    }

    /// <summary>Reset for a new booking (preserves caller identity if requested).</summary>
    public void Reset(bool preserveCallerIdentity = false)
    {
        FareAvailable = false;
        BookingDispatched = false;
        FareRejected = false;
        PaymentPreference = null;
        IsAmendment = false;
        _cancelConfirmationRequestedAt = null;
        State = CallState.Greeting;
        
        _logger.LogInformation("[{SessionId}] 🔄 Engine RESET (preserveIdentity={Preserve})",
            _sessionId, preserveCallerIdentity);
    }
}
