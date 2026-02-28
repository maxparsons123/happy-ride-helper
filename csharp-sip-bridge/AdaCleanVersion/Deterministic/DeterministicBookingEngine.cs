// DeterministicBookingEngine.cs
// A deterministic “never-loops” taxi booking state machine.
// - Server owns state.
// - Only accepts ToolSync (from your sync_booking_data tool) + BackendResult events.
// - Every transition produces exactly ONE NextAction.
// - Bounded retries prevent infinite loops.
// - Verification is an ACTION (geocode) not a looping state.

using System;
using System.Collections.Generic;
using System.Globalization;
using AdaCleanVersion.Services;

namespace TaxiBot.Deterministic
{
    // ---------- Public API ----------

    public sealed class DeterministicBookingEngine
    {
        public BookingState State { get; private set; } = BookingState.New();
        private int _noOpCount = 0;

        public DeterministicBookingEngine(EngineConfig? config = null)
        {
            _cfg = config ?? EngineConfig.Default();
        }

        /// <summary>
        /// Apply an external event (tool sync from model OR backend result).
        /// Returns the next action the caller should execute (speak, geocode, dispatch, etc.).
        /// </summary>
        public NextAction Step(EngineEvent ev)
        {
            // Idempotency guard: optional, but prevents double-processing when tool calls duplicate.
            if (ev is ToolSyncEvent tse && !string.IsNullOrWhiteSpace(tse.TurnId))
            {
                if (State.LastTurnIdProcessed == tse.TurnId)
                    return NextAction.None("duplicate tool turn ignored");

                State = State with { LastTurnIdProcessed = tse.TurnId };
            }

            return ev switch
            {
                ToolSyncEvent tool => OnToolSync(tool),
                BackendResultEvent backend => OnBackendResult(backend),
                _ => NextAction.TransferToHuman("Unknown event type")
            };
        }

        /// <summary>
        /// Call once at call start to get the very first prompt.
        /// </summary>
        public NextAction Start()
        {
            State = State with { Stage = Stage.CollectPickup };
            return NextAction.Ask("Welcome to 247 Radio Cars. What is your pickup address?");
        }

        // ---------- Internals ----------

        private readonly EngineConfig _cfg;

        private NextAction OnToolSync(ToolSyncEvent ev)
        {
            // Route amend/new based on stage + content.
            if (State.Stage is Stage.End or Stage.Escalate)
                return NextAction.Hangup("This call is already complete.");

            var patch = BookingPatch.FromToolSync(ev, _cfg, State.Slots);

            // ── Fix #1: No-op tool calls must not trigger actions ──
            // If the model called the tool but provided no real slot changes and no actionable intent,
            // just reprompt the current question (bounded).
            if (!patch.HasAnySlotChanges && patch.Intent == ToolIntent.Unknown)
            {
                _noOpCount++;
                if (_noOpCount >= 3)
                {
                    State = State with { Stage = Stage.Escalate };
                    return NextAction.TransferToHuman("I'm not getting the details clearly.");
                }

                // Reprompt whatever the current stage needs
                return RepromptCurrentStage();
            }

            _noOpCount = 0; // Reset on real data

            // If we are booked and user provided any booking field, treat as amendment.
            if (State.Stage == Stage.Booked || State.Stage == Stage.AmendMenu || State.Stage == Stage.AmendConfirm ||
                State.Stage == Stage.AmendCollectPickup || State.Stage == Stage.AmendCollectDropoff ||
                State.Stage == Stage.AmendCollectPassengers || State.Stage == Stage.AmendCollectTime)
            {
                return OnAmendToolSync(ev, patch);
            }

            // ── Fix #3: ConfirmDetails must require explicit YES to dispatch ──
            if (State.Stage == Stage.ConfirmDetails)
            {
                if (patch.Intent == ToolIntent.Confirm)
                {
                    State = State with { Stage = Stage.Dispatching };
                    return NextAction.CallDispatch(State.Slots);
                }

                if (patch.Intent == ToolIntent.Decline || patch.Intent == ToolIntent.Cancel)
                {
                    State = State with { Stage = Stage.End };
                    return NextAction.Hangup("No problem. Goodbye.");
                }

                // If they changed anything while confirming, apply patch, then go to verification/next missing.
                if (patch.HasAnySlotChanges)
                {
                    State = ApplyPatch(State, patch);

                    if (patch.PickupChanged)
                        return StartGeocodePickup();

                    if (patch.DropoffChanged)
                        return StartGeocodeDropoff();

                    return GoToNextMissingOrConfirm();
                }

                // Unclear confirmation: bounded reprompt.
                State = State.WithRetry(RetryKey.Confirm);
                if (State.Retries.Confirm > _cfg.MaxConfirmRetries)
                {
                    State = State with { Stage = Stage.Escalate };
                    return NextAction.TransferToHuman("Confirmation unclear too many times.");
                }

                return NextAction.Ask("⛔ CONFIRMATION GATE: The booking is NOT confirmed. Do NOT say 'booked', 'confirmed', 'safe travels' or any closing phrase. Say ONLY: \"Just to confirm, is that correct? Please say yes or no.\" Then STOP.");
            }

            // Normal collection flow:
            State = ApplyPatch(State, patch);

            // If an address changed at any point in collection, re-verify immediately
            // before asking for downstream fields (e.g. destination correction during passengers).
            // Also reset the retry counter for the stage we're pivoting away from,
            // so when flow returns after geocoding, the counter starts fresh.
            if (patch.PickupChanged)
            {
                ResetCurrentStageRetry();
                return StartGeocodePickup();
            }

            if (patch.DropoffChanged)
            {
                ResetCurrentStageRetry();
                return StartGeocodeDropoff();
            }

            // Decide next action deterministically based on stage + slots.
            return State.Stage switch
            {
                Stage.CollectPickup => HandleCollectPickup(),
                Stage.CollectDropoff => HandleCollectDropoff(),
                Stage.CollectPassengers => HandleCollectPassengers(),
                Stage.CollectTime => HandleCollectTime(),
                Stage.Start => Start(),
                _ => GoToNextMissingOrConfirm()
            };
        }

        /// <summary>
        /// Reprompt the current stage's question without advancing or geocoding.
        /// </summary>
        private NextAction RepromptCurrentStage()
        {
            // If we have a last prompt, reuse it
            if (!string.IsNullOrWhiteSpace(State.LastPrompt))
                return NextAction.Ask(State.LastPrompt);

            return State.Stage switch
            {
                Stage.CollectPickup => NextAction.Ask("What is your pickup address?"),
                Stage.CollectDropoff => NextAction.Ask("What is your dropoff address?"),
                Stage.CollectPassengers => NextAction.Ask("How many passengers will be travelling?"),
                Stage.CollectTime => NextAction.Ask("What time would you like the pickup?"),
                Stage.ConfirmDetails => NextAction.Ask("Is that correct? Please say yes or no."),
                _ => NextAction.Ask("Could you repeat that please?")
            };
        }

        private NextAction OnBackendResult(BackendResultEvent ev)
        {
            switch (ev.Type)
            {
                case BackendResultType.GeocodePickup:
                    return HandleGeocodePickupResult(ev);

                case BackendResultType.GeocodeDropoff:
                    return HandleGeocodeDropoffResult(ev);

                case BackendResultType.Dispatch:
                    return HandleDispatchResult(ev);

                case BackendResultType.Amend:
                    return HandleAmendResult(ev);

                default:
                    State = State with { Stage = Stage.Escalate };
                    return NextAction.TransferToHuman($"Unknown backend result: {ev.Type}");
            }
        }

        // ---------- Collection handlers ----------

        private NextAction HandleCollectPickup()
        {
            if (!State.Slots.Pickup.HasValue)
            {
                return AskWithRetry(
                    RetryKey.Pickup,
                    "What is your pickup address?",
                    "I still need the pickup address. Please say the full pickup address, including town or postcode.",
                    onExhausted: "Pickup not captured.");
            }

            // We have a pickup -> geocode it (one-shot action).
            return StartGeocodePickup();
        }

        private NextAction StartGeocodePickup()
        {
            State = State with
            {
                PendingVerification = PendingVerification.Pickup,
                Stage = Stage.CollectPickup // keep stage stable; verification is action not state
            };
            return NextAction.CallGeocodePickup(State.Slots.Pickup.Raw!);
        }

        private NextAction HandleGeocodePickupResult(BackendResultEvent ev)
        {
            // Verification action completed; must either advance, reprompt (bounded), or escalate.
            if (ev.Ok)
            {
                State = State with
                {
                    PendingVerification = PendingVerification.None,
                    Slots = State.Slots with
                    {
                        Pickup = State.Slots.Pickup with
                        {
                            Normalized = ev.NormalizedAddress ?? State.Slots.Pickup.Normalized,
                            Verified = true
                        }
                    }
                };

                // Advance deterministically
                return GoToNextMissingOrConfirm();
            }

            // Fail -> reprompt pickup with bounded retries.
            State = State.WithRetry(RetryKey.PickupVerify);
            if (State.Retries.PickupVerify > _cfg.MaxPickupVerifyRetries)
            {
                State = State with { Stage = Stage.Escalate };
                return NextAction.TransferToHuman("Pickup address could not be resolved.");
            }

            // Keep raw pickup but ask for clarification
            return NextAction.Ask("I couldn’t find that pickup on the map. Please repeat the pickup address, including town or postcode.");
        }

        private NextAction HandleCollectDropoff()
        {
            if (!State.Slots.Dropoff.HasValue)
            {
                return AskWithRetry(
                    RetryKey.Dropoff,
                    "What is your dropoff address?",
                    "I still need the dropoff address. Please say the full destination, including town or postcode.",
                    onExhausted: "Destination not captured.");
            }

            return StartGeocodeDropoff();
        }

        private NextAction StartGeocodeDropoff()
        {
            State = State with
            {
                PendingVerification = PendingVerification.Dropoff,
                Stage = Stage.CollectDropoff
            };
            return NextAction.CallGeocodeDropoff(State.Slots.Dropoff.Raw!);
        }

        private NextAction HandleGeocodeDropoffResult(BackendResultEvent ev)
        {
            if (ev.Ok)
            {
                State = State with
                {
                    PendingVerification = PendingVerification.None,
                    Slots = State.Slots with
                    {
                        Dropoff = State.Slots.Dropoff with
                        {
                            Normalized = ev.NormalizedAddress ?? State.Slots.Dropoff.Normalized,
                            Verified = true
                        }
                    }
                };

                return GoToNextMissingOrConfirm();
            }

            State = State.WithRetry(RetryKey.DropoffVerify);
            if (State.Retries.DropoffVerify > _cfg.MaxDropoffVerifyRetries)
            {
                State = State with { Stage = Stage.Escalate };
                return NextAction.TransferToHuman("Destination address could not be resolved.");
            }

            return NextAction.Ask("I couldn’t find that destination on the map. Please repeat the dropoff address, including town or postcode.");
        }

        private NextAction HandleCollectPassengers()
        {
            // Guard: if destination is missing/unverified, collect & verify it first.
            // This prevents "stuck on passengers" after destination corrections.
            if (!State.Slots.Dropoff.HasValue || !State.Slots.Dropoff.Verified)
            {
                State = State with { Stage = Stage.CollectDropoff };
                return HandleCollectDropoff();
            }

            if (State.Slots.Passengers is >= 1 and <= 8)
                return GoToNextMissingOrConfirm();

            return AskWithRetry(
                RetryKey.Passengers,
                "How many passengers will be travelling?",
                "Please tell me the number of passengers, for example '2 passengers'.",
                onExhausted: "Passengers not captured.");
        }

        private NextAction HandleCollectTime()
        {
            if (State.Slots.PickupTime is not null)
                return GoToNextMissingOrConfirm();

            return AskWithRetry(
                RetryKey.Time,
                "What time would you like the pickup?",
                "Please tell me the pickup time, for example 'ASAP' or 'tomorrow at 6pm'.",
                onExhausted: "Pickup time not captured.");
        }

        // ---------- Confirm + Dispatch ----------

        private NextAction GoToNextMissingOrConfirm()
        {
            // Determine next stage by missing slots.
            if (!State.Slots.Pickup.HasValue || !State.Slots.Pickup.Verified)
            {
                State = State with { Stage = Stage.CollectPickup };
                return HandleCollectPickup();
            }

            if (!State.Slots.Dropoff.HasValue || !State.Slots.Dropoff.Verified)
            {
                State = State with { Stage = Stage.CollectDropoff };
                return HandleCollectDropoff();
            }

            if (State.Slots.Passengers is null)
            {
                State = State with { Stage = Stage.CollectPassengers };
                return HandleCollectPassengers();
            }

            if (State.Slots.PickupTime is null)
            {
                State = State with { Stage = Stage.CollectTime };
                return HandleCollectTime();
            }

            // All captured -> confirm
            State = State with { Stage = Stage.ConfirmDetails };

            var pickup = State.Slots.Pickup.Raw!;
            var dropoff = State.Slots.Dropoff.Raw!;
            var pax = State.Slots.Passengers!.Value;
            var time = State.Slots.PickupTime!.Display;

            return NextAction.Ask(
                $"⛔ CONFIRMATION GATE: The booking is NOT confirmed yet. You MUST read back the details and ask the caller to confirm. " +
                $"Do NOT say 'booked', 'confirmed', 'arranged', 'on its way', 'safe travels', 'have a good day', or ANY closing phrase. " +
                $"Say ONLY: \"Just to confirm: pickup {pickup}, going to {dropoff}, {pax} passenger{(pax == 1 ? "" : "s")}, at {time}. Is that correct?\" " +
                $"Then STOP and WAIT for the caller to say yes or no. NOTHING ELSE.");
        }

        private NextAction HandleDispatchResult(BackendResultEvent ev)
        {
            if (!ev.Ok)
            {
                State = State with { Stage = Stage.Escalate };
                return NextAction.TransferToHuman("Dispatch failed.");
            }

            State = State with
            {
                Stage = Stage.Booked,
                BookingId = ev.BookingId ?? State.BookingId
            };

            var refId = State.BookingId ?? "UNKNOWN";
            return NextAction.Ask($"Booked. Your reference is {refId}. Would you like to amend anything?");
        }

        // ---------- Amend Flow ----------

        private NextAction OnAmendToolSync(ToolSyncEvent ev, BookingPatch patch)
        {
            // If no booking id, we can't amend. Move back to normal flow (or escalate).
            if (string.IsNullOrWhiteSpace(State.BookingId))
            {
                // Treat as still collecting (caller is probably in same call and not actually booked).
                return OnToolSync(ev with { }); // just reuse normal logic
            }

            if (patch.Intent == ToolIntent.Cancel)
            {
                State = State with { Stage = Stage.End };
                return NextAction.Hangup("Okay. Goodbye.");
            }

            if (!patch.HasAnySlotChanges)
            {
                // If caller says "no" or unknown with no changes, end politely.
                if (patch.Intent == ToolIntent.Decline)
                {
                    State = State with { Stage = Stage.End };
                    return NextAction.Hangup("Thanks for calling 247 Radio Cars. Goodbye.");
                }

                // Ask a simple menu once.
                State = State.WithRetry(RetryKey.AmendMenu);
                if (State.Retries.AmendMenu > 1)
                {
                    State = State with { Stage = Stage.End };
                    return NextAction.Hangup("Thanks for calling 247 Radio Cars. Goodbye.");
                }

                return NextAction.Ask("Tell me what you'd like to change: pickup, destination, passengers, or time.");
            }

            // Apply changes to slots (and re-verify addresses if changed).
            State = ApplyPatch(State, patch);

            if (patch.PickupChanged)
            {
                // Verify then confirm amendment
                State = State with { Stage = Stage.AmendCollectPickup };
                return NextAction.CallGeocodePickup(State.Slots.Pickup.Raw!);
            }

            if (patch.DropoffChanged)
            {
                State = State with { Stage = Stage.AmendCollectDropoff };
                return NextAction.CallGeocodeDropoff(State.Slots.Dropoff.Raw!);
            }

            // If only passengers/time changed, go straight to amend confirm.
            State = State with { Stage = Stage.AmendConfirm };
            return AskAmendConfirm();
        }

        private NextAction HandleAmendResult(BackendResultEvent ev)
        {
            if (!ev.Ok)
            {
                State = State with { Stage = Stage.Escalate };
                return NextAction.TransferToHuman("Amendment failed.");
            }

            State = State with { Stage = Stage.Booked };
            var refId = State.BookingId ?? "UNKNOWN";
            return NextAction.Ask($"Updated. Your reference is {refId}. Would you like to amend anything else?");
        }

        private NextAction AskAmendConfirm()
        {
            var pickup = State.Slots.Pickup.Raw!;
            var dropoff = State.Slots.Dropoff.Raw!;
            var pax = State.Slots.Passengers ?? 1;
            var time = State.Slots.PickupTime?.Display ?? "ASAP";

            return NextAction.Ask($"Confirm the changes: pickup {pickup}, going to {dropoff}, {pax} passenger{(pax == 1 ? "" : "s")}, at {time}. Is that correct?");
        }

        /// <summary>
        /// Reset the retry counter for the current collection stage.
        /// Called before a correction pivot so that when flow returns
        /// to this stage after geocoding, the counter starts fresh.
        /// </summary>
        private void ResetCurrentStageRetry()
        {
            var key = State.Stage switch
            {
                Stage.CollectPickup => RetryKey.Pickup,
                Stage.CollectDropoff => RetryKey.Dropoff,
                Stage.CollectPassengers => RetryKey.Passengers,
                Stage.CollectTime => RetryKey.Time,
                Stage.ConfirmDetails => RetryKey.Confirm,
                _ => (RetryKey?)null
            };
            if (key.HasValue)
                State = State.ResetRetry(key.Value);
        }

        // ---------- Utilities ----------

        private BookingState ApplyPatch(BookingState s, BookingPatch patch)
        {
            var slots = s.Slots;

            if (patch.PickupChanged && patch.PickupRaw is not null)
            {
                // Only reset verification if the raw address actually changed
                bool isSamePickup = string.Equals(slots.Pickup.Raw, patch.PickupRaw, StringComparison.OrdinalIgnoreCase);
                if (!isSamePickup)
                {
                    slots = slots with
                    {
                        Pickup = new AddressSlot(patch.PickupRaw, Normalized: null, Verified: false)
                    };
                    s = s.ResetRetry(RetryKey.PickupVerify);
                }
            }

            if (patch.DropoffChanged && patch.DropoffRaw is not null)
            {
                bool isSameDropoff = string.Equals(slots.Dropoff.Raw, patch.DropoffRaw, StringComparison.OrdinalIgnoreCase);
                if (!isSameDropoff)
                {
                    slots = slots with
                    {
                        Dropoff = new AddressSlot(patch.DropoffRaw, Normalized: null, Verified: false)
                    };
                    s = s.ResetRetry(RetryKey.DropoffVerify);
                }
            }

            if (patch.PassengersChanged)
                slots = slots with { Passengers = patch.Passengers };

            if (patch.PickupTimeChanged)
                slots = slots with { PickupTime = patch.PickupTime };

            if (!string.IsNullOrWhiteSpace(patch.SpecialInstructions))
                slots = slots with { SpecialInstructions = patch.SpecialInstructions };

            return s with { Slots = slots };
        }

        private NextAction AskWithRetry(RetryKey key, string firstAsk, string reprompt, string onExhausted)
        {
            var current = State.Retries.Get(key);
            if (current == 0)
            {
                State = State.WithRetry(key);
                State = State with { LastPrompt = firstAsk };
                return NextAction.Ask(firstAsk);
            }

            State = State.WithRetry(key);
            var newCount = State.Retries.Get(key);

            var max = _cfg.MaxByKey(key);
            if (newCount > max)
            {
                State = State with { Stage = Stage.Escalate };
                return NextAction.TransferToHuman(onExhausted);
            }

            State = State with { LastPrompt = reprompt };
            return NextAction.Ask(reprompt);
        }
    }

    // ---------- Configuration ----------

    public sealed record EngineConfig(
        int MaxPickupRetries,
        int MaxDropoffRetries,
        int MaxPassengersRetries,
        int MaxTimeRetries,
        int MaxConfirmRetries,
        int MaxPickupVerifyRetries,
        int MaxDropoffVerifyRetries)
    {
        public static EngineConfig Default() => new(
            MaxPickupRetries: 3,
            MaxDropoffRetries: 3,
            MaxPassengersRetries: 2,
            MaxTimeRetries: 2,
            MaxConfirmRetries: 2,
            MaxPickupVerifyRetries: 3,
            MaxDropoffVerifyRetries: 3
        );

        public int MaxByKey(RetryKey key) => key switch
        {
            RetryKey.Pickup => MaxPickupRetries,
            RetryKey.Dropoff => MaxDropoffRetries,
            RetryKey.Passengers => MaxPassengersRetries,
            RetryKey.Time => MaxTimeRetries,
            RetryKey.Confirm => MaxConfirmRetries,
            RetryKey.PickupVerify => MaxPickupVerifyRetries,
            RetryKey.DropoffVerify => MaxDropoffVerifyRetries,
            RetryKey.AmendMenu => 1,
            _ => 2
        };
    }

    // ---------- State ----------

    public enum Stage
    {
        Start,
        CollectPickup,
        CollectDropoff,
        CollectPassengers,
        CollectTime,
        ConfirmDetails,
        Dispatching,
        Booked,
        AmendMenu,
        AmendCollectPickup,
        AmendCollectDropoff,
        AmendCollectPassengers,
        AmendCollectTime,
        AmendConfirm,
        End,
        Escalate
    }

    public enum PendingVerification { None, Pickup, Dropoff }

    public sealed record BookingState(
        Stage Stage,
        BookingSlots Slots,
        RetryCounters Retries,
        PendingVerification PendingVerification,
        string? BookingId,
        string? LastPrompt,
        string? LastTurnIdProcessed)
    {
        public static BookingState New() => new(
            Stage: Stage.Start,
            Slots: BookingSlots.Empty(),
            Retries: new RetryCounters(),
            PendingVerification: PendingVerification.None,
            BookingId: null,
            LastPrompt: null,
            LastTurnIdProcessed: null
        );

        public BookingState WithRetry(RetryKey key) => this with { Retries = Retries.Increment(key) };
        public BookingState ResetRetry(RetryKey key) => this with { Retries = Retries.Reset(key) };
    }

    public sealed record BookingSlots(
        AddressSlot Pickup,
        AddressSlot Dropoff,
        int? Passengers,
        PickupTime? PickupTime,
        string? SpecialInstructions)
    {
        public static BookingSlots Empty() => new(
            Pickup: AddressSlot.Empty(),
            Dropoff: AddressSlot.Empty(),
            Passengers: null,
            PickupTime: null,
            SpecialInstructions: null
        );
    }

    public sealed record AddressSlot(string? Raw, string? Normalized, bool Verified)
    {
        public bool HasValue => !string.IsNullOrWhiteSpace(Raw);
        public static AddressSlot Empty() => new(null, null, Verified: false);
    }

    public sealed record PickupTime(string Raw, DateTime? WhenUtc, bool IsAsap)
    {
        public string Display => IsAsap ? "ASAP" : Raw;
    }

    // ---------- Retries ----------

    public enum RetryKey
    {
        Pickup,
        Dropoff,
        Passengers,
        Time,
        Confirm,
        PickupVerify,
        DropoffVerify,
        AmendMenu
    }

    public sealed class RetryCounters
    {
        private readonly Dictionary<RetryKey, int> _map = new();

        public int Get(RetryKey key) => _map.TryGetValue(key, out var v) ? v : 0;

        public RetryCounters Increment(RetryKey key)
        {
            _map[key] = Get(key) + 1;
            return this;
        }

        public RetryCounters Reset(RetryKey key)
        {
            _map.Remove(key);
            return this;
        }

        public int Pickup => Get(RetryKey.Pickup);
        public int Dropoff => Get(RetryKey.Dropoff);
        public int Passengers => Get(RetryKey.Passengers);
        public int Time => Get(RetryKey.Time);
        public int Confirm => Get(RetryKey.Confirm);
        public int PickupVerify => Get(RetryKey.PickupVerify);
        public int DropoffVerify => Get(RetryKey.DropoffVerify);
        public int AmendMenu => Get(RetryKey.AmendMenu);
    }

    // ---------- Events ----------

    public abstract record EngineEvent;

    /// <summary>
    /// Event produced from the model’s sync_booking_data tool call.
    /// TurnId optional but recommended (call_id or your own per-turn ID) for idempotency.
    /// </summary>
    public sealed record ToolSyncEvent(
        string? TurnId,
        string? Pickup,
        string? Destination,
        int? Passengers,
        string? PickupTime,
        string? Intent,
        string? SpecialInstructions) : EngineEvent;

    public enum BackendResultType
    {
        GeocodePickup,
        GeocodeDropoff,
        Dispatch,
        Amend
    }

    /// <summary>
    /// Event produced by your backend after geocode/dispatch/amend calls complete.
    /// </summary>
    public sealed record BackendResultEvent(
        BackendResultType Type,
        bool Ok,
        string? NormalizedAddress = null,
        string? BookingId = null,
        string? Error = null) : EngineEvent;

    // ---------- Actions ----------

    public abstract record NextAction(string Kind, string? Reason = null)
    {
        public static NextAction Ask(string text) => new AskAction(text);
        public static NextAction Silence(string reason) => new SilenceAction(reason);
        public static NextAction CallGeocodePickup(string rawAddress) => new GeocodePickupAction(rawAddress);
        public static NextAction CallGeocodeDropoff(string rawAddress) => new GeocodeDropoffAction(rawAddress);
        public static NextAction CallDispatch(BookingSlots slots) => new DispatchAction(slots);
        public static NextAction CallAmend(string bookingId, BookingSlots patch) => new AmendAction(bookingId, patch);
        public static NextAction TransferToHuman(string reason) => new TransferAction(reason);
        public static NextAction Hangup(string text) => new HangupAction(text);
        public static NextAction None(string reason) => new NoneAction(reason);
    }

    public sealed record AskAction(string Text) : NextAction("ask");
    public sealed record SilenceAction(string Why) : NextAction("silence", Why);
    public sealed record GeocodePickupAction(string RawAddress) : NextAction("geocode_pickup");
    public sealed record GeocodeDropoffAction(string RawAddress) : NextAction("geocode_dropoff");
    public sealed record DispatchAction(BookingSlots Slots) : NextAction("dispatch");
    public sealed record AmendAction(string BookingId, BookingSlots Patch) : NextAction("amend");
    public sealed record TransferAction(string Why) : NextAction("transfer", Why);
    public sealed record HangupAction(string Text) : NextAction("hangup");
    public sealed record NoneAction(string Why) : NextAction("none", Why);

    // ---------- Patch extraction from tool call ----------

    public enum ToolIntent { Unknown, Confirm, Decline, Cancel, Amend, NewBooking }

    internal sealed record BookingPatch(
        ToolIntent Intent,
        bool PickupChanged,
        string? PickupRaw,
        bool DropoffChanged,
        string? DropoffRaw,
        bool PassengersChanged,
        int? Passengers,
        bool PickupTimeChanged,
        PickupTime? PickupTime,
        string? SpecialInstructions)
    {
        public bool HasAnySlotChanges =>
            PickupChanged || DropoffChanged || PassengersChanged || PickupTimeChanged ||
            !string.IsNullOrWhiteSpace(SpecialInstructions);

        /// <summary>
        /// Build a patch from tool args. Uses current slots to detect actual changes
        /// vs the model re-sending the same data (dirty-flag logic).
        /// </summary>
        public static BookingPatch FromToolSync(ToolSyncEvent ev, EngineConfig cfg, BookingSlots currentSlots)
        {
            var intent = ParseIntent(ev.Intent);

            // ── Fix #2: Only flag as "changed" if the value is actually different ──
            var pickupChanged = !string.IsNullOrWhiteSpace(ev.Pickup)
                && !string.Equals(ev.Pickup, currentSlots.Pickup.Raw, StringComparison.OrdinalIgnoreCase);
            var dropoffChanged = !string.IsNullOrWhiteSpace(ev.Destination)
                && !string.Equals(ev.Destination, currentSlots.Dropoff.Raw, StringComparison.OrdinalIgnoreCase);
            var paxChanged = ev.Passengers.HasValue
                && ev.Passengers != currentSlots.Passengers;
            var timeChanged = !string.IsNullOrWhiteSpace(ev.PickupTime)
                && (currentSlots.PickupTime is null || !string.Equals(ev.PickupTime, currentSlots.PickupTime.Raw, StringComparison.OrdinalIgnoreCase));

            var pax = ev.Passengers;
            if (paxChanged && (pax is < 1 or > 8))
            {
                paxChanged = false;
                pax = null;
            }

            var time = timeChanged ? ParsePickupTime(ev.PickupTime!) : null;

            return new BookingPatch(
                Intent: intent,
                PickupChanged: pickupChanged,
                PickupRaw: pickupChanged ? ev.Pickup : null,
                DropoffChanged: dropoffChanged,
                DropoffRaw: dropoffChanged ? ev.Destination : null,
                PassengersChanged: paxChanged,
                Passengers: pax,
                PickupTimeChanged: timeChanged && time is not null,
                PickupTime: time,
                SpecialInstructions: ev.SpecialInstructions
            );
        }

        private static ToolIntent ParseIntent(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return ToolIntent.Unknown;
            s = s.Trim().ToLowerInvariant();

            return s switch
            {
                "confirm" or "yes" or "y" => ToolIntent.Confirm,
                "no" or "decline" => ToolIntent.Decline,
                "cancel" => ToolIntent.Cancel,
                "amend" => ToolIntent.Amend,
                "new_booking" or "new" => ToolIntent.NewBooking,
                _ => ToolIntent.Unknown
            };
        }

        private static PickupTime? ParsePickupTime(string raw)
        {
            raw = raw.Trim();

            // Use the full UkTimeParser which handles ASAP variants, relative offsets,
            // UK colloquialisms ("half seven"), named periods, day-of-week, explicit times, etc.
            var result = UkTimeParser.Parse(raw);
            if (result != null)
            {
                if (result.IsAsap)
                    return new PickupTime("ASAP", WhenUtc: null, IsAsap: true);

                // Convert London local time to UTC for scheduling
                DateTime? whenUtc = null;
                if (result.Resolved.HasValue)
                {
                    var londonTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
                    whenUtc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(result.Resolved.Value, DateTimeKind.Unspecified), londonTz);
                }

                return new PickupTime(result.Normalized, whenUtc, IsAsap: false);
            }

            // Legacy fallback: exact "asap" match (should be caught by UkTimeParser above)
            if (string.Equals(raw, "asap", StringComparison.OrdinalIgnoreCase))
                return new PickupTime("ASAP", WhenUtc: null, IsAsap: true);

            // Last resort: try ISO format
            if (DateTime.TryParseExact(
                raw,
                "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
            {
                return new PickupTime(raw, dt, IsAsap: false);
            }

            return null;
        }
    }

    // ---------- Example: mapping from your sync_booking_data tool args ----------

    public static class ToolSyncMapper
    {
        /// <summary>
        /// Convert your tool args dictionary into ToolSyncEvent.
        /// You can pass call_id into turnId for idempotency.
        /// </summary>
        public static ToolSyncEvent FromToolArgs(string? callIdOrTurnId, IDictionary<string, object?> args)
        {
            string? Str(string key) => args.TryGetValue(key, out var v) ? v?.ToString() : null;

            int? Int(string key)
            {
                if (!args.TryGetValue(key, out var v) || v is null) return null;
                if (v is int i) return i;
                if (int.TryParse(v.ToString(), out var j)) return j;
                return null;
            }

            return new ToolSyncEvent(
                TurnId: callIdOrTurnId,
                Pickup: Str("pickup"),
                Destination: Str("destination"),
                Passengers: Int("passengers"),
                PickupTime: Str("pickup_time"),
                Intent: Str("intent"), // optional if you add it to tool schema
                SpecialInstructions: Str("special_instructions")
            );
        }
    }
}
