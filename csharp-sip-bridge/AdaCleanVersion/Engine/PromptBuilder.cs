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
            - You simply have a natural conversation to gather details

            The system will tell you what to ask for next via [INSTRUCTION] messages.
            Follow those instructions naturally — don't skip ahead or assume.
            Keep responses concise — this is a phone call, not a chat.
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
        ClarificationInfo? clarification = null)
    {
        return state switch
        {
            CollectionState.Greeting when context?.IsReturningCaller == true =>
                $"[INSTRUCTION] Greet {context.CallerName ?? "the caller"} warmly by name — they're a returning customer. " +
                "Confirm their name and move on to pickup.",

            CollectionState.Greeting =>
                "[INSTRUCTION] Greet the caller warmly and ask for their name.",

            CollectionState.CollectingName =>
                "[INSTRUCTION] Ask the caller for their name.",

            CollectionState.CollectingPickup when context?.LastPickup != null =>
                $"[INSTRUCTION] {NameAck(rawData)} Ask for their PICKUP address. " +
                $"Their last pickup was \"{context.LastPickup}\" — you can offer it as a suggestion. " +
                "They must include a house number if it's a street address.",

            CollectionState.CollectingPickup =>
                $"[INSTRUCTION] {NameAck(rawData)} Ask for their PICKUP address. " +
                "They must include a house number if it's a street address.",

            CollectionState.CollectingDestination when context?.LastDestination != null =>
                $"[INSTRUCTION] Pickup noted as \"{rawData.PickupRaw}\". " +
                $"Their last destination was \"{context.LastDestination}\" — you can offer it. " +
                "Now ask for their DESTINATION address.",

            CollectionState.CollectingDestination =>
                $"[INSTRUCTION] Pickup noted as \"{rawData.PickupRaw}\". " +
                "Now ask for their DESTINATION address.",

            CollectionState.CollectingPassengers =>
                $"[INSTRUCTION] Destination noted as \"{rawData.DestinationRaw}\". " +
                "Ask how many passengers.",

            CollectionState.CollectingPickupTime =>
                $"[INSTRUCTION] {rawData.PassengersRaw} passenger(s). " +
                "Ask when they need the taxi — now (ASAP) or a specific time?",

            CollectionState.ReadyForExtraction =>
                "[INSTRUCTION] All details collected. Tell the caller you're just checking availability, one moment.",

            CollectionState.Extracting =>
                "[INSTRUCTION] Stay silent — processing in progress.",

            CollectionState.Geocoding =>
                "[INSTRUCTION] Stay silent — calculating fare.",

            CollectionState.AwaitingClarification =>
                BuildClarificationInstruction(rawData, clarification),

            CollectionState.PresentingFare when fareResult != null && booking != null =>
                $"[INSTRUCTION] Present the booking summary and fare to the caller:\n" +
                $"- Name: {booking.CallerName}\n" +
                $"- Pickup: {fareResult.Pickup.Address}\n" +
                $"- Destination: {fareResult.Destination.Address}\n" +
                $"- Passengers: {booking.Passengers}\n" +
                $"- Time: {booking.PickupTime}\n" +
                $"- Fare: {fareResult.FareSpoken}\n" +
                $"- Driver ETA: {fareResult.BusyMessage}\n" +
                "Read the fare naturally (e.g. \"that'll be around twelve pounds fifty\"). " +
                "Ask if they'd like to proceed or change anything.",

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
}
