using AdaCleanVersion.Models;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Builds AI conversation prompts based on current engine state.
/// AI is instructed to collect specific information — no tool calls, no state mutation.
/// The AI is a voice interface only.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// Build the system prompt for the conversation AI.
    /// This AI only speaks — it never calls tools or mutates booking state.
    /// </summary>
    public static string BuildSystemPrompt(string companyName, CallerContext? context = null)
    {
        var callerInfo = "";
        if (context?.IsReturningCaller == true)
        {
            callerInfo = $"""

                CALLER CONTEXT (use naturally, don't read out verbatim):
                - Name: {context.CallerName ?? "unknown"}
                - Total bookings: {context.TotalBookings}
                - Last pickup: {context.LastPickup ?? "none"}
                - Last destination: {context.LastDestination ?? "none"}
                - Language: {context.PreferredLanguage ?? "en"}
                {(context.AddressAliases?.Count > 0 ? $"- Known aliases: {string.Join(", ", context.AddressAliases.Keys)}" : "")}

                This is a RETURNING caller. Greet them by name if known.
                You may reference their previous addresses naturally (e.g. "same pickup as last time?").
                """;
        }

        return $"""
            You are Ada, a friendly and professional AI taxi booking assistant for {companyName}.
            You speak naturally with a warm tone. Respond in the caller's language.

            IMPORTANT — YOUR ROLE:
            - You are a VOICE INTERFACE only
            - You collect information by asking questions
            - You do NOT have any tools or functions
            - You do NOT make decisions about booking
            - You do NOT confirm, dispatch, or end a booking unless explicitly instructed
            - You do NOT normalize, shorten, or alter house numbers/addresses

            SCRIPT ENFORCEMENT (HARD RULES):
            - The system will send [INSTRUCTION] messages.
            - You MUST follow the latest [INSTRUCTION] exactly.
            - Ask ONLY for the current required field.
            - Never skip to later fields (e.g. passengers/time) unless instructed.
            - ⚠️ NEVER say "booking arranged", "taxi scheduled", "taxi is on its way", "safe travels", or "goodbye" UNLESS the [INSTRUCTION] explicitly tells you to.
            - ⚠️ If the [INSTRUCTION] says SILENCE or says not to speak, you MUST NOT output any speech at all.
            - If caller gives multiple details in one turn, acknowledge briefly and still follow the latest [INSTRUCTION].
            - Keep responses concise — this is a phone call, not a chat.
            - NEVER repeat the greeting. You only greet ONCE at the start of the call.
            {callerInfo}
            """;
    }

    /// <summary>
    /// Build instruction for the current collection state.
    /// Sent as a system message to guide AI's next response.
    /// </summary>
    public static string BuildInstruction(
        CollectionState state, RawBookingData rawData,
        CallerContext? context = null,
        StructuredBooking? booking = null,
        FareResult? fareResult = null,
        ClarificationInfo? clarification = null,
        GeocodedAddress? verifiedPickup = null,
        GeocodedAddress? verifiedDestination = null)
    {
        return state switch
        {
            // Greeting states are now handled via BuildGreetingMessage — these are fallbacks
            CollectionState.Greeting when context?.IsReturningCaller == true =>
                $"[INSTRUCTION] Greet {context.CallerName ?? "the caller"} warmly by name — they're a returning customer. " +
                "Confirm their name and ask whereabouts they are.",

            CollectionState.Greeting =>
                "[INSTRUCTION] Greet the caller warmly and ask for their name.",

            CollectionState.CollectingName =>
                "[INSTRUCTION] Ask the caller for their name.",

            CollectionState.CollectingPickup when context?.IsReturningCaller == true && context.LastPickup != null =>
                $"[INSTRUCTION] Ask for their PICKUP address. " +
                $"Their last pickup was \"{context.LastPickup}\" — you can offer it as a suggestion. " +
                "They must include a house number if it's a street address.",

            CollectionState.CollectingPickup =>
                $"[INSTRUCTION] {NameAck(rawData)} Ask for their PICKUP address. " +
                "They must include a house number if it's a street address.",

            CollectionState.VerifyingPickup =>
                $"[INSTRUCTION] Read back the pickup address as \"{rawData.PickupRaw}\" and say " +
                "\"let me just confirm that for you\". Then STOP and wait silently. " +
                "Do NOT alter or normalize the address — read it back exactly as shown.",

            CollectionState.CollectingDestination when context?.LastDestination != null =>
                $"[INSTRUCTION] Pickup confirmed as \"{verifiedPickup?.Address ?? rawData.PickupRaw}\". " +
                $"Their last destination was \"{context.LastDestination}\" — you can offer it. " +
                "Now ask for their DESTINATION address.",

            CollectionState.CollectingDestination =>
                $"[INSTRUCTION] Pickup confirmed as \"{verifiedPickup?.Address ?? rawData.PickupRaw}\". " +
                "Now ask for their DESTINATION address.",

            CollectionState.VerifyingDestination =>
                $"[INSTRUCTION] Read back the destination address as \"{rawData.DestinationRaw}\" and say " +
                "\"let me just confirm that for you\". Then STOP and wait silently. " +
                "Do NOT alter or normalize the address — read it back exactly as shown.",

            CollectionState.CollectingPassengers =>
                $"[INSTRUCTION] Destination confirmed as \"{verifiedDestination?.Address ?? rawData.DestinationRaw}\". " +
                "Ask how many passengers.",

            CollectionState.CollectingPickupTime =>
                $"[INSTRUCTION] {rawData.PassengersRaw} passenger(s). " +
                "Ask when they need the taxi — now (ASAP) or a specific time?",

            CollectionState.ReadyForExtraction =>
                "[INSTRUCTION] All details collected. Say ONLY: \"Just checking availability for you, one moment please.\" " +
                "Then STOP. Do NOT say anything else. Do NOT confirm the booking. Do NOT say goodbye.",

            CollectionState.Extracting =>
                "[INSTRUCTION] ⚠️ ABSOLUTE SILENCE. Do NOT speak at all. Do NOT say 'your taxi is on its way'. " +
                "Do NOT confirm anything. Do NOT say goodbye. Say NOTHING. Wait for the next instruction.",

            CollectionState.Geocoding =>
                "[INSTRUCTION] ⚠️ ABSOLUTE SILENCE. Do NOT speak at all. Do NOT say 'your taxi is on its way'. " +
                "Do NOT confirm anything. Do NOT say goodbye. Say NOTHING. Wait for the next instruction.",

            CollectionState.AwaitingClarification =>
                BuildClarificationInstruction(rawData, clarification),

            CollectionState.PresentingFare when fareResult != null && booking != null =>
                $"[INSTRUCTION] ⚠️ IMPORTANT: Do NOT greet the caller again. Do NOT say 'Welcome to Ada Taxi'. " +
                $"Present the booking summary and fare NOW:\n" +
                $"- Name: {booking.CallerName}\n" +
                $"- Pickup: {fareResult.Pickup.Address}\n" +
                $"- Destination: {fareResult.Destination.Address}\n" +
                $"- Passengers: {booking.Passengers}\n" +
                $"- Time: {booking.PickupTime}\n" +
                $"- Fare: {fareResult.FareSpoken}\n" +
                $"- Driver ETA: {fareResult.BusyMessage}\n" +
                "Say something like: \"So {booking.CallerName}, that's from {fareResult.Pickup.Address} to {fareResult.Destination.Address}, " +
                "the fare will be around {fareResult.FareSpoken}, and {fareResult.BusyMessage}. " +
                "Would you like to go ahead with this booking?\"",

            CollectionState.PresentingFare when booking != null =>
                $"[INSTRUCTION] Present the booking summary to the caller:\n" +
                $"- Name: {booking.CallerName}\n" +
                $"- Pickup: {booking.Pickup.DisplayName}\n" +
                $"- Destination: {booking.Destination.DisplayName}\n" +
                $"- Passengers: {booking.Passengers}\n" +
                $"- Time: {booking.PickupTime}\n" +
                "Fare is being calculated. Ask if they'd like to proceed.",

            CollectionState.PresentingFare =>
                "[INSTRUCTION] Present the fare and booking summary to the caller. Ask if they'd like to proceed.",

            CollectionState.AwaitingPaymentChoice =>
                "[INSTRUCTION] Ask the caller: would they like to pay by card or cash to the driver (meter)?",

            CollectionState.AwaitingConfirmation when fareResult != null && booking != null =>
                $"[INSTRUCTION] Read back the full booking for final confirmation:\n" +
                $"- Name: {booking.CallerName}\n" +
                $"- From: {fareResult.Pickup.Address}\n" +
                $"- To: {fareResult.Destination.Address}\n" +
                $"- Passengers: {booking.Passengers}\n" +
                $"- Time: {booking.PickupTime}\n" +
                $"- Fare: {fareResult.FareSpoken}\n" +
                "Ask: \"Shall I confirm this booking?\"",

            CollectionState.AwaitingConfirmation =>
                "[INSTRUCTION] Read back the full booking and ask for final confirmation.",

            CollectionState.Dispatched when fareResult != null =>
                $"[INSTRUCTION] Booking confirmed! Tell the caller their taxi is on the way. " +
                $"{fareResult.BusyMessage} Say goodbye warmly.",

            CollectionState.Dispatched =>
                "[INSTRUCTION] Booking confirmed! Tell the caller their taxi is on the way. Say goodbye warmly.",

            CollectionState.Ending =>
                "[INSTRUCTION] Say goodbye warmly and end the conversation.",

            _ => "[INSTRUCTION] Continue the conversation naturally."
        };
    }

    /// <summary>
    /// Build instruction for slot correction.
    /// </summary>
    public static string BuildCorrectionInstruction(string slotName, string oldValue, string newValue)
    {
        return $"[INSTRUCTION] The caller corrected their {slotName} from \"{oldValue}\" to \"{newValue}\". " +
               "Acknowledge the correction and continue.";
    }

    /// <summary>
    /// Build instruction for address clarification.
    /// Per user preference: ask "Which area is that in?" — do NOT enumerate alternatives
    /// unless the caller explicitly asks.
    /// </summary>
    public static string BuildClarificationInstruction(RawBookingData data, ClarificationInfo? info = null)
    {
        if (info == null)
            return "[INSTRUCTION] The address is ambiguous. Ask which area it's in.";

        var fieldLabel = info.AmbiguousField == "pickup" ? "pickup" : "destination";
        var rawValue = info.AmbiguousField == "pickup" ? data.PickupRaw : data.DestinationRaw;

        // If FareGeo provided a specific clarification message (e.g., "Did you mean David Road?"),
        // use that instead of the generic "Which area?" question
        var hasSpecificMessage = !string.IsNullOrWhiteSpace(info.Message)
            && !info.Message.Equals("Which area is that in?", StringComparison.OrdinalIgnoreCase);

        if (hasSpecificMessage)
        {
            return $"[INSTRUCTION] Ask the caller about their {fieldLabel} address: {info.Message}";
        }

        // Attempt 1: just ask "which area?"
        // Attempt 2+: if alternatives available, offer them
        if (info.Attempt <= 1 || info.Alternatives == null || info.Alternatives.Count == 0)
        {
            return $"[INSTRUCTION] The {fieldLabel} address \"{rawValue}\" exists in multiple areas. " +
                   "Simply ask: \"Which area is that in?\" " +
                   "Do NOT list the areas — only list them if the caller asks.";
        }
        else
        {
            var altList = string.Join(", ", info.Alternatives);
            return $"[INSTRUCTION] The caller couldn't specify the area for \"{rawValue}\". " +
                   $"This time, offer the options: {altList}. " +
                   $"Ask which one they mean.";
        }
    }

    private static string NameAck(RawBookingData data) =>
        string.IsNullOrWhiteSpace(data.NameRaw) ? "" : $"Thanks, {data.NameRaw}.";

    /// <summary>
    /// Build the greeting message to inject as a conversation item (like AdaSdkModel).
    /// This is sent as a user message that the AI responds to naturally.
    /// </summary>
    public static string BuildGreetingMessage(string companyName, CallerContext? context, CollectionState currentState)
    {
        if (context?.IsReturningCaller == true && !string.IsNullOrWhiteSpace(context.CallerName))
        {
            // Returning caller — greet by name and ask whereabouts (skip name collection)
            var lastPickupHint = !string.IsNullOrWhiteSpace(context.LastPickup)
                ? $" Their last pickup was \"{context.LastPickup}\" — you may offer it as a suggestion."
                : "";

            return $"[SYSTEM] A returning caller named {context.CallerName} has connected. " +
                   $"Greet them BY NAME. Say something like: \"Welcome to {companyName}. Hello {context.CallerName}, " +
                   $"my name is Ada and I am here to help you with your booking today. And whereabouts are you?\" " +
                   $"⚠️ Ask for their PICKUP AREA/ADDRESS directly — do NOT ask for their name again. " +
                   $"They must include a house number if it's a street address.{lastPickupHint} " +
                   $"⚠️ CRITICAL: After this greeting, WAIT PATIENTLY for the caller to respond. Do NOT repeat the question.";
        }

        // New caller — greet and ask for name
        return $"[SYSTEM] A new caller has connected. " +
               $"Greet them warmly. Say: \"Welcome to {companyName}, my name is Ada and I am here to help you with your booking today. " +
               $"Can I have your name please?\" " +
               $"⚠️ CRITICAL: After greeting, WAIT PATIENTLY for the caller to respond. Do NOT repeat the question or re-prompt.";
    }
}
