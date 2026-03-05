// Last updated: 2026-03-03 (v1.3 — Sprinkle caller name for personal feel)

namespace AdaSdkModel.Core.Deterministic;

/// <summary>
/// Maps DeterministicStage → exact prompt instruction.
/// The model receives ONLY this instruction — no booking state blob,
/// no compliance guard, no parity check.
/// 
/// The model becomes a speech renderer:
///   Engine decides WHAT to say → Model decides HOW to say it.
///
/// NAME PERSONALIZATION STRATEGY:
///   Use name at: greeting, destination ask, fare presentation, post-booking, goodbye.
///   Skip name at: verifications, short data collection, payment choice.
///   This gives a warm personal feel without being repetitive.
/// </summary>
public static class DeterministicPromptBuilder
{
    /// <summary>
    /// Build the single-purpose instruction for the current stage.
    /// Returns null for silent stages (e.g. FareCalculating).
    /// </summary>
    public static string? BuildInstruction(DeterministicStage stage, BookingDraft draft)
    {
        var name = draft.HasName ? draft.Name : null;
        return stage switch
        {
            DeterministicStage.Greeting when draft.HasActiveBooking =>
                name != null
                    ? $"Welcome back {name}. The caller has an active booking. Ask if they'd like to cancel it, make changes, or check the driver status."
                    : "Welcome back. The caller has an active booking. Ask if they'd like to cancel it, make changes, or check the driver status.",

            DeterministicStage.Greeting =>
                name != null
                    ? $"Greet the caller by name ({name}). Ask where they'd like to be picked up from."
                    : "Greet the caller warmly and ask for their name.",

            DeterministicStage.CollectingName =>
                "Ask for the caller's name.",

            DeterministicStage.CollectingPickup =>
                name != null
                    ? $"Thank {name} and ask where they would like to be picked up from."
                    : "Ask where they would like to be picked up from.",

            DeterministicStage.ValidatingPickup =>
                "Say ONE short sentence like 'Let me just check that address for you.' Then STOP and wait silently.",

            DeterministicStage.VerifyingPickup =>
                $"Read back the pickup address EXACTLY as written here: \"{draft.PickupVerifiedAddress ?? draft.Pickup}\". Do NOT use the caller's pronunciation or transcript — use these EXACT words. Ask: 'I've got {draft.PickupVerifiedAddress ?? draft.Pickup} as your pickup. Is that correct?'",

            DeterministicStage.CollectingPickupLocation =>
                $"The pickup is {draft.PickupVerifiedAddress ?? draft.Pickup}. Ask: 'Whereabouts at {draft.PickupVerifiedAddress ?? draft.Pickup} will you be waiting? For example, by the main entrance, car park, or any specific spot.'",

            DeterministicStage.CollectingDestination =>
                name != null
                    ? $"And where would you like to go, {name}?"
                    : "Ask where they would like to go.",

            DeterministicStage.ValidatingDestination =>
                "Say ONE short sentence like 'Let me just check that for you.' Then STOP and wait silently.",

            DeterministicStage.VerifyingDestination =>
                $"Read back the destination EXACTLY as written here: \"{draft.DestVerifiedAddress ?? draft.Destination}\". Do NOT use the caller's pronunciation or transcript — use these EXACT words. Ask: 'I've got {draft.DestVerifiedAddress ?? draft.Destination} as your destination. Is that correct?'",

            DeterministicStage.CollectingPassengers =>
                "Ask how many passengers will be travelling.",

            DeterministicStage.CollectingTime =>
                "Ask when they'd like to be picked up.",

            DeterministicStage.CollectingNotes =>
                "Ask: 'Before I get the fare, would you like to leave a pickup note for the driver? " +
                "For example, where exactly you'll be waiting, if you need wheelchair access, or any special requests. " +
                "Just say your note, or say no if you don't need one.'",

            DeterministicStage.VerifyingNotes =>
                $"Read back the driver note: \"{draft.SpecialInstructions}\". Ask the caller to confirm this is correct.",

            DeterministicStage.FareCalculating =>
                "Say ONE short sentence like 'Let me check that for you' or 'Just checking the fare now'. Then STOP. Do NOT say goodbye, do NOT confirm any booking, do NOT end the call. Wait silently for the fare result.",

            DeterministicStage.AwaitingConfirmation =>
                BuildFareConfirmation(draft, name),

            DeterministicStage.PostBooking =>
                name != null
                    ? $"[SYSTEM — MANDATORY SPEECH] You MUST say the following EXACTLY as written — do NOT shorten, summarise, or skip ANY part:\n\n\"Great {name}. We'll send you a WhatsApp confirmation message with your booking details and a secure payment link. Just tap the link to pay the fixed fare and secure your taxi. Is there anything else I can help you with?\""
                    : "[SYSTEM — MANDATORY SPEECH] You MUST say the following EXACTLY as written — do NOT shorten, summarise, or skip ANY part:\n\n\"Great. We'll send you a WhatsApp confirmation message with your booking details and a secure payment link. Just tap the link to pay the fixed fare and secure your taxi. Is there anything else I can help you with?\"",

            DeterministicStage.ManagingExisting =>
                "Ask the caller: would you like to cancel your booking, make any changes, or check on your driver?",

            DeterministicStage.AwaitingCancelConfirm =>
                "Ask the caller to confirm: 'Are you sure you'd like to cancel your booking?'",

            DeterministicStage.Disambiguating =>
                null, // Backend injects disambiguation options

            DeterministicStage.Ending =>
                name != null
                    ? $"Say goodbye to {name} warmly and professionally. Wish them a safe journey."
                    : "Say goodbye warmly and professionally.",

            DeterministicStage.Escalated =>
                "Tell the caller you're transferring them to an operator.",

            _ => null
        };
    }

    /// <summary>
    /// Build a concise fare presentation prompt.
    /// Includes only verified address names and the fare — no booking state blob.
    /// Uses full street addresses / store names (not just city names) so the caller
    /// knows exactly what they booked (e.g. "52A David Road" → "Morrison's, Holyhead Road").
    /// </summary>
    private static string BuildFareConfirmation(BookingDraft draft, string? name)
    {
        // Prefer the verified geocoded address, then the raw caller input, never just a city
        var pickup = draft.PickupVerifiedAddress ?? draft.Pickup ?? "your pickup";
        var dest = draft.DestVerifiedAddress ?? draft.Destination ?? "your destination";
        var fare = draft.Fare ?? "the quoted fare";
        var eta = draft.Eta;
        var etaPart = !string.IsNullOrWhiteSpace(eta) ? $" The driver should arrive in about {eta}." : "";
        var notesPart = !string.IsNullOrWhiteSpace(draft.SpecialInstructions) ? $" Driver note: '{draft.SpecialInstructions}'." : "";
        var namePrefix = name != null ? $"Okay {name}, here are your booking details. " : "Here are your booking details. ";

        return $"[FARE RESULT] You MUST say ALL of the following out loud — do NOT skip any part. " +
               $"Say EXACTLY: '{namePrefix}Picking up from {pickup} going to {dest}. The fixed fare is {fare}.{etaPart}{notesPart} " +
               $"Are you happy to go ahead with this booking?' " +
               $"Do NOT shorten the addresses to just the city name. Do NOT skip the fare or ETA. Read EVERY detail.";
    }

    /// <summary>
    /// Build a no-reply re-prompt for the current stage.
    /// Used by the watchdog when the caller goes silent.
    /// </summary>
    public static string? BuildNoReplyPrompt(DeterministicStage stage, BookingDraft draft)
    {
        return stage switch
        {
            DeterministicStage.CollectingName => "I just need your name to get started.",
            DeterministicStage.CollectingPickup => "I just need your pickup address.",
            DeterministicStage.VerifyingPickup => $"Was that {draft.Pickup}? Just a quick yes or no.",
            DeterministicStage.CollectingDestination => "And where would you like to go?",
            DeterministicStage.VerifyingDestination => $"Was that {draft.Destination}? Just a quick yes or no.",
            DeterministicStage.CollectingPickupLocation => $"Whereabouts at {draft.PickupVerifiedAddress ?? draft.Pickup} will you be?",
            DeterministicStage.ValidatingPickup => "Just checking that address for you.",
            DeterministicStage.ValidatingDestination => "Just checking that for you.",
            DeterministicStage.CollectingPassengers => "How many passengers will be travelling?",
            DeterministicStage.CollectingTime => "When would you like to be picked up?",
            DeterministicStage.CollectingNotes => "Would you like to leave a note for the driver?",
            DeterministicStage.VerifyingNotes => $"I've got your note as: '{draft.SpecialInstructions}'. Is that right?",
            DeterministicStage.AwaitingConfirmation => "Are you happy to go ahead with this booking?",
            DeterministicStage.FareCalculating => "I'm just working on the fare for you — won't be a moment.",
            DeterministicStage.PostBooking => "Is there anything else I can help with?",
            DeterministicStage.ManagingExisting => "Would you like to cancel, make changes, or check on your driver?",
            DeterministicStage.AwaitingCancelConfirm => "Are you sure you'd like to cancel?",
            _ => null
        };
    }

    /// <summary>
    /// Get VAD configuration for the current stage.
    /// Returns (useSemanticVad, eagerness).
    /// </summary>
    public static (bool Semantic, float Eagerness) GetVadConfig(DeterministicStage stage)
    {
        return stage switch
        {
            // Address collection: patient, semantic VAD
            DeterministicStage.CollectingPickup or
            DeterministicStage.CollectingDestination or
            DeterministicStage.CollectingPickupLocation =>
                (true, 0.2f),

            // Confirmation gates: semantic but moderate
            DeterministicStage.AwaitingConfirmation or
            DeterministicStage.VerifyingPickup or
            DeterministicStage.VerifyingDestination or
            DeterministicStage.VerifyingNotes =>
                (true, 0.3f),

            // Short answers: fast server VAD
            _ => (false, 0.5f)
        };
    }
}
