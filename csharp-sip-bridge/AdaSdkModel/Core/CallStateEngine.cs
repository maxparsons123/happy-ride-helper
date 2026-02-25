// Last updated: 2026-02-25 (v2.0 — 10/10 production-solid)
// Fixes: deterministic AdvanceToNextMissing via BookingSnapshot, silent FareCalculating,
//        no cancel auto-approve, payment guard on dispatch, AnythingElse reject differentiation.
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Core;

/// <summary>
/// Deterministic state machine for taxi booking voice calls.
/// 
/// This is the "brain" of the Split-Brain architecture:
/// - The model extracts data and generates speech
/// - The engine decides workflow, gates tools, and transitions state
/// - The backend executes approved actions
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
    /// 
    /// The BookingSnapshot allows the engine to deterministically compute
    /// the next collection state without sentinels or callbacks.
    /// </summary>
    public EngineAction HandleInput(ExtractionResult input, BookingSnapshot snapshot)
    {
        var previousState = State;
        var action = ResolveAction(input, snapshot);
        
        if (action.NewState != previousState)
        {
            _logger.LogInformation("[{SessionId}] 🔄 STATE: {From} → {To} (intent={Intent})",
                _sessionId, previousState, action.NewState, input.Intent);
        }
        
        State = action.NewState;
        return action;
    }

    private EngineAction ResolveAction(ExtractionResult input, BookingSnapshot snapshot)
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
            CallState.CollectingPickup => HandleCollectingPickup(input, snapshot),
            CallState.CollectingDestination => HandleCollectingDestination(input, snapshot),
            CallState.CollectingPassengers => HandleCollectingPassengers(input, snapshot),
            CallState.CollectingTime => HandleCollectingTime(input),
            CallState.FareCalculating => HandleFareCalculating(),
            CallState.AwaitingPaymentChoice => HandleAwaitingPaymentChoice(input),
            CallState.AwaitingBookingConfirmation => HandleAwaitingBookingConfirmation(input),
            CallState.BookingConfirmed or CallState.AnythingElse => HandleAnythingElse(input),
            CallState.ManagingExistingBooking => HandleManagingExistingBooking(input),
            CallState.AwaitingCancelConfirmation => HandleAwaitingCancelConfirmation(input),
            CallState.AwaitingAmendmentField => HandleAwaitingAmendment(input, snapshot),
            CallState.DisambiguatingPickup or CallState.DisambiguatingDestination => HandleDisambiguation(input),
            CallState.AirportIntercept => HandleAirportIntercept(),
            CallState.FareSanityCheck or CallState.AddressDiscrepancy => HandleFareSanityOrDiscrepancy(input),
            CallState.AwaitingPickupHouseNumber or CallState.AwaitingDestHouseNumber => HandleAwaitingHouseNumber(input, snapshot),
            CallState.AwaitingDestCity => HandleAwaitingDestCity(input, snapshot),
            _ => EngineAction.Silent(State)
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STATE HANDLERS
    // ═══════════════════════════════════════════════════════════════════════

    private EngineAction HandleGreeting(ExtractionResult input)
    {
        if (!string.IsNullOrWhiteSpace(input.Name))
            return new EngineAction { NewState = CallState.CollectingPickup, PromptInstruction = "Greet the caller by name and ask for pickup address.", UseSemanticVad = true, VadEagerness = 0.2f };
        
        return new EngineAction { NewState = CallState.CollectingName, PromptInstruction = "Ask for the caller's name.", UseSemanticVad = false };
    }

    private EngineAction HandleCollectingName(ExtractionResult input)
    {
        if (!string.IsNullOrWhiteSpace(input.Name))
            return new EngineAction { NewState = CallState.CollectingPickup, PromptInstruction = "Ask for pickup address.", UseSemanticVad = true, VadEagerness = 0.2f };
        
        return EngineAction.Speak(CallState.CollectingName, "Ask for their name again.");
    }

    private EngineAction HandleCollectingPickup(ExtractionResult input, BookingSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(input.Pickup))
            return AdvanceToNextMissing(snapshot.WithMerged(input));
        
        // Compound input: user provided destination but not pickup
        if (!string.IsNullOrWhiteSpace(input.Destination))
            return AdvanceToNextMissing(snapshot.WithMerged(input));
        
        return EngineAction.Silent(CallState.CollectingPickup);
    }

    private EngineAction HandleCollectingDestination(ExtractionResult input, BookingSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(input.Destination))
            return AdvanceToNextMissing(snapshot.WithMerged(input));
        
        return EngineAction.Silent(CallState.CollectingDestination);
    }

    private EngineAction HandleCollectingPassengers(ExtractionResult input, BookingSnapshot snapshot)
    {
        if (input.Passengers.HasValue)
            return AdvanceToNextMissing(snapshot.WithMerged(input));
        
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

    /// <summary>
    /// FIX #2: FareCalculating is SILENT.
    /// Never let the model fill silence during calculation — backend injects [FARE RESULT].
    /// Speaking here risks double responses or interrupting the fare injection.
    /// </summary>
    private EngineAction HandleFareCalculating()
    {
        return EngineAction.Silent(CallState.FareCalculating);
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

    /// <summary>
    /// FIX #5: Differentiate fare-stage reject from general reject.
    /// Only end call on Goodbye. Reject after booking = "what to change?".
    /// </summary>
    private EngineAction HandleAnythingElse(ExtractionResult input)
    {
        if (input.Intent == ExtractedIntent.Goodbye)
            return new EngineAction { EndCall = true, NewState = CallState.Ending,
                PromptInstruction = "Say the final closing script and end the call." };
        
        // "No" after "anything else?" means they're done — end call gracefully
        if (input.Intent == ExtractedIntent.Reject)
        {
            // Only end if booking is already dispatched; otherwise ask what to change
            if (BookingDispatched)
                return new EngineAction { EndCall = true, NewState = CallState.Ending,
                    PromptInstruction = "The caller has no more requests. Say goodbye warmly." };
            
            return EngineAction.Speak(CallState.AnythingElse,
                "Ask what the caller would like to change or help with.");
        }
        
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
                return new EngineAction { NewState = CallState.AwaitingCancelConfirmation, PromptInstruction = "Ask the caller to confirm they want to cancel their booking.", UseSemanticVad = false };
            
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
                return new EngineAction { NewState = CallState.CollectingPickup, PromptInstruction = "Start a fresh booking. Ask for pickup address.", UseSemanticVad = true, VadEagerness = 0.2f };
            
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

    private EngineAction HandleAwaitingAmendment(ExtractionResult input, BookingSnapshot snapshot)
    {
        IsAmendment = true;
        
        if (!string.IsNullOrWhiteSpace(input.Pickup) || !string.IsNullOrWhiteSpace(input.Destination))
        {
            // Address changed — need to recalculate fare
            FareAvailable = false;
            BookingDispatched = false;
            return AdvanceToNextMissing(snapshot.WithMerged(input));
        }
        
        if (input.Passengers.HasValue || !string.IsNullOrWhiteSpace(input.PickupTime))
            return AdvanceToNextMissing(snapshot.WithMerged(input));
        
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

    private EngineAction HandleAwaitingHouseNumber(ExtractionResult input, BookingSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(input.Pickup) || !string.IsNullOrWhiteSpace(input.Destination))
            return AdvanceToNextMissing(snapshot.WithMerged(input));
        
        var target = State == CallState.AwaitingPickupHouseNumber ? "pickup" : "destination";
        return EngineAction.Speak(State, $"Ask for the house number on the {target} street.");
    }

    private EngineAction HandleAwaitingDestCity(ExtractionResult input, BookingSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(input.Destination))
            return AdvanceToNextMissing(snapshot.WithMerged(input));
        
        return EngineAction.Speak(CallState.AwaitingDestCity,
            "Ask which city or area the destination is in.");
    }

    private EngineAction HandleAirportIntercept()
    {
        return new EngineAction
        {
            SendBookingLink = true,
            NewState = CallState.AnythingElse,
            PromptInstruction = null // Backend injects the message after link is created
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TOOL CALL GATING
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Gate a cancel_booking tool call.
    /// FIX #3: No auto-approve fallback. If state is wrong, BLOCK unconditionally.
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
        
        // Model says confirmed=true — validate state strictly
        if (State != CallState.AwaitingCancelConfirmation)
        {
            _logger.LogWarning("[{SessionId}] ⛔ cancel_booking(confirmed=true) BLOCKED — state is {State}, not AwaitingCancelConfirmation",
                _sessionId, State);
            return EngineAction.Block(State,
                "Cannot execute cancel — confirmation was never properly requested. " +
                "Call cancel_booking with confirmed=false first to ask the caller.");
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
    /// FIX #4: Added payment guard — booking cannot execute without payment decision.
    /// </summary>
    public EngineAction GateBookTaxiConfirmed()
    {
        if (!FareAvailable)
        {
            _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — no fare available", _sessionId);
            return EngineAction.Block(State, "Cannot confirm booking without a fare. Calculate fare first.");
        }
        
        if (string.IsNullOrEmpty(PaymentPreference))
        {
            _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — no payment preference", _sessionId);
            return EngineAction.Block(State, "Cannot confirm booking without a payment choice. Ask card or meter first.");
        }
        
        if (FareRejected)
        {
            _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — fare was rejected", _sessionId);
            return EngineAction.Block(State, "Cannot confirm booking — the fare was rejected. Recalculate first.");
        }
        
        if (BookingDispatched)
        {
            _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — already dispatched", _sessionId);
            return EngineAction.Block(State, "Booking already confirmed and dispatched.");
        }
        
        if (IsAmendment && !FareAvailable)
        {
            _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — amendment without recalculated fare", _sessionId);
            return EngineAction.Block(State, "Amendment requires fare recalculation before dispatch.");
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
        FareRejected = false; // Clear any previous rejection since we have a fresh fare
        State = CallState.AwaitingPaymentChoice;
        _logger.LogInformation("[{SessionId}] 💰 Engine: fare received → AwaitingPaymentChoice", _sessionId);
    }

    /// <summary>Called when caller selects card/meter after fare presentation.</summary>
    public void NotifyPaymentPreferenceSelected(string preference)
    {
        var normalized = preference.Trim().ToLowerInvariant() switch
        {
            "card" or "fixed" or "link" => "card",
            _ => "meter"
        };

        PaymentPreference = normalized;
        if (State == CallState.AwaitingPaymentChoice)
            State = CallState.AwaitingBookingConfirmation;

        _logger.LogInformation("[{SessionId}] 💳 Engine: payment preference set to {Preference} (state={State})",
            _sessionId, PaymentPreference, State);
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
        PaymentPreference = null; // Clear stale payment choice
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
    /// FIX #1: Deterministic next-state resolution via BookingSnapshot.
    /// No sentinels. No placeholders. No leakage to CallSession.
    /// The engine has full authority over the next state.
    /// </summary>
    private EngineAction AdvanceToNextMissing(BookingSnapshot snapshot)
    {
        var nextState = DetermineNextCollectionState(
            snapshot.HasName, snapshot.HasPickup, snapshot.HasDestination,
            snapshot.HasPassengers, snapshot.HasPickupTime);

        if (nextState == CallState.FareCalculating)
        {
            // All fields complete — trigger fare calculation
            return new EngineAction
            {
                RequestFareQuote = true,
                NewState = CallState.FareCalculating,
                PromptInstruction = "All details collected. Checking the fare now.",
                UseSemanticVad = false
            };
        }

        // Generate the appropriate prompt for the next missing field
        var instruction = nextState switch
        {
            CallState.CollectingName => "Ask for the caller's name.",
            CallState.CollectingPickup => "Ask for the pickup address.",
            CallState.CollectingDestination => "Ask where they'd like to go.",
            CallState.CollectingPassengers => "Ask how many passengers.",
            CallState.CollectingTime => "Ask when they'd like to be picked up.",
            _ => null
        };

        var useSemanticVad = nextState is CallState.CollectingPickup or CallState.CollectingDestination;

        return new EngineAction
        {
            NewState = nextState,
            PromptInstruction = instruction,
            UseSemanticVad = useSemanticVad ? true : false,
            VadEagerness = useSemanticVad ? 0.2f : 0.5f
        };
    }

    /// <summary>
    /// Determine the next collection state based on what's still missing.
    /// Pure function — no side effects.
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
