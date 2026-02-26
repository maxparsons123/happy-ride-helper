using AdaCleanVersion.Models;
using AdaCleanVersion.Services;

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

        // Inject London time for AI time parsing reference
        var londonTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var londonNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, londonTz);
        var referenceDateTime = londonNow.ToString("dddd, dd MMMM yyyy HH:mm");

        // Derive language pre-warning from phone country code or caller history
        var languagePrewarn = GetLanguagePrewarn(context);

        return $"""
            You are Ada, a friendly and professional AI taxi booking assistant for {companyName}.
            You speak naturally with a warm tone. Respond in the caller's language.

            REFERENCE_DATETIME (London): {referenceDateTime}
            {languagePrewarn}

            IMPORTANT — YOUR ROLE:
            - You are a VOICE INTERFACE only
            - You collect information by asking questions
            - You do NOT have any tools or functions
            - You do NOT make decisions about booking
            - You do NOT confirm, dispatch, or end a booking unless explicitly instructed
            - You do NOT normalize, shorten, or alter house numbers/addresses

            CORE BEHAVIOUR:
            - Under 15 words per response where possible
            - One question at a time
            - No filler phrases
            - Never rush, never sound robotic

            LANGUAGE AUTO-SWITCH (MANDATORY):
            After EVERY caller utterance, detect their spoken language.
            If different from current, SWITCH immediately. Do NOT ask permission.
            Supported: English, Dutch, French, German, Spanish, Italian, Polish, Portuguese.
            Default: English.

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

            HOUSE NUMBER PROTECTION (CRITICAL):
            House numbers are SACRED STRINGS — they are NOT mathematical expressions.
            Copy house numbers VERBATIM. NEVER drop, add, or rearrange digits/letters.
            You are PROHIBITED from guessing or expanding address ranges.
            If the data says "52A", you MUST say "52A". NEVER invent address ranges
            (e.g., turning "52A" into "52-84" or "52-84A") even if you think it sounds
            more professional or complete. "52A" is a SINGLE house, not a range.
            Forbidden transformations: 52A → 52-84, 52A → 3A, 1214A → 1214, 52A → 528.
            If the house number has 3+ characters (e.g. 1214A), read it DIGIT BY DIGIT:
            "one-two-one-four-A Warwick Road". NEVER shorten or truncate.
            If ANY part is uncertain, ASK the caller to spell or confirm it.

            MID-SPEECH SELF-CORRECTION:
            If the caller says one house number then hesitates (uh, um, no, sorry, actually, wait)
            and says a DIFFERENT number, the LATTER number is the correction.
            Examples:
              "52A no 1214A David Road" → 1214A David Road
              "43 um 97 Warwick Road" → 97 Warwick Road
            Extract ONLY the corrected (latter) number.

            SPELLED-OUT NAMES (LETTER-BY-LETTER DETECTION):
            Callers may spell street names: "D-O-V-E-Y" or "D as in David, O, V, E, Y".
            Assemble into the intended word (D-O-V-E-Y → "Dovey").
            Confirm back: "So that's Dovey Road, D-O-V-E-Y?"
            NATO/phonetic: "Alpha"=A, "Bravo"=B, "Charlie"=C, "Delta"=D, etc.
            "D for David"=D, "S for Sierra"=S. The spelled result is ABSOLUTE.

            POSTCODE AS TRUTH ANCHOR:
            If a caller provides a FULL UK postcode (e.g. "B13 9NT", "CV1 4QN"):
            - This is the STRONGEST address signal, MORE precise than the street name.
            - If a geocoder resolves to a DIFFERENT postcode, the geocoder is WRONG.
            - Postcodes count as city context — no need to ask for the city.

            MISSING HOUSE NUMBER DETECTION:
            If a street-type address (Road, Street, Close, Avenue, Lane, Drive, Way, Crescent,
            Terrace, Grove, Place, Court, Gardens, Square) is given WITHOUT a house number:
            - ALWAYS ask for the house number BEFORE accepting.
            - Exception: "near", "opposite", "outside", "corner of" — accept without number.
            - Exception: Named buildings, pubs, shops, stations, airports, hospitals — accept without number.

            TIME NORMALISATION:
            "now", "straight away", "as soon as possible", "right now", "immediately" → ASAP.
            All other times: convert to YYYY-MM-DD HH:MM (24-hour). Never return raw phrases.
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
        GeocodedAddress? verifiedDestination = null,
        string? rejectedReason = null,
        bool isRecalculating = false)
    {
        // If a slot was just rejected, generate a specific re-prompt instead of the default
        if (rejectedReason != null)
        {
            return (state, rejectedReason) switch
            {
                (CollectionState.CollectingPassengers, "bare_passengers_no_number") =>
                    "[INSTRUCTION] ⛔ MANDATORY: I heard you mention passengers but I didn't catch the number. " +
                    "You MUST ask: \"Sorry, I didn't quite catch that — how many passengers will there be?\" " +
                    "Do NOT say anything else. Do NOT say thank you. Do NOT say please hold on. " +
                    "Do NOT offer assistance. Do NOT say goodbye. ONLY ask for the number.",
                (CollectionState.CollectingPassengers, _) =>
                    "[INSTRUCTION] ⛔ MANDATORY: I didn't catch the number of passengers. You MUST ask again: " +
                    "\"Sorry, how many passengers will there be?\" Do NOT say anything else. Do NOT offer assistance. " +
                    "Do NOT say goodbye. Do NOT say thank you. Do NOT say please hold on. ONLY ask for the passenger count.",
                (CollectionState.CollectingName, _) =>
                    "[INSTRUCTION] ⛔ MANDATORY: I didn't catch the name. You MUST ask again: " +
                    "\"Sorry, could I have your name for the booking?\" Do NOT say anything else.",
                (CollectionState.CollectingPickup, _) =>
                    "[INSTRUCTION] ⛔ MANDATORY: I didn't catch the pickup address. You MUST ask again: " +
                    "\"Sorry, could you repeat your pickup address please?\" Do NOT say anything else.",
                (CollectionState.CollectingDestination, _) =>
                    "[INSTRUCTION] ⛔ MANDATORY: I didn't catch the destination. You MUST ask again: " +
                    "\"Sorry, could you repeat your destination please?\" Do NOT say anything else.",
                (CollectionState.CollectingPickupTime, _) =>
                    "[INSTRUCTION] ⛔ MANDATORY: I didn't catch when you need the taxi. You MUST ask again: " +
                    "\"Sorry, when do you need the taxi — now or at a specific time?\" Do NOT say anything else.",
                _ => $"[INSTRUCTION] ⛔ MANDATORY: I didn't catch that. Ask the caller to repeat. Do NOT say anything else."
            };
        }

        // Hard enforcement prefix — injected into every collecting state
        const string SLOT_GUARD =
            "⛔ CRITICAL: You are collecting booking details. The booking is NOT complete. " +
            "Do NOT say 'if you need any more assistance', 'is there anything else', 'have a good day', " +
            "'I've noted that', 'safe travels', or ANY closing/farewell phrase. " +
            "Do NOT offer general assistance. Do NOT say the booking is confirmed. " +
            "You MUST ask ONLY for the specific field described below. NOTHING ELSE. ";

        return state switch
        {
            // Greeting states are now handled via BuildGreetingMessage — these are fallbacks
            CollectionState.Greeting when context?.IsReturningCaller == true =>
                $"[INSTRUCTION] Greet {context.CallerName ?? "the caller"} warmly by name — they're a returning customer. " +
                "Confirm their name and ask whereabouts they are.",

            CollectionState.Greeting =>
                "[INSTRUCTION] Greet the caller warmly and ask for their name.",

            CollectionState.CollectingName =>
                $"[INSTRUCTION] {SLOT_GUARD}Ask the caller for their name. " +
                "REQUIRED FIELDS REMAINING: name, pickup, destination, passengers, pickup time.",

            CollectionState.CollectingPickup when context?.IsReturningCaller == true && context.LastPickup != null =>
                $"[INSTRUCTION] {SLOT_GUARD}{NameAck(rawData)} Ask for their PICKUP address. " +
                $"Their last pickup was \"{context.LastPickup}\" — you can offer it as a suggestion. " +
                "They must include a house number if it's a street address. " +
                "REQUIRED FIELDS REMAINING: pickup, destination, passengers, pickup time.",

            CollectionState.CollectingPickup =>
                $"[INSTRUCTION] {SLOT_GUARD}{NameAck(rawData)} Ask for their PICKUP address. " +
                "They must include a house number if it's a street address. " +
                "REQUIRED FIELDS REMAINING: pickup, destination, passengers, pickup time.",

            CollectionState.VerifyingPickup when isRecalculating =>
                "[INSTRUCTION] The caller just changed their pickup address. " +
                "Say \"No problem, let me update that for you\" and then read back the new pickup address " +
                $"\"{rawData.GetSlot("pickup")}\" AS YOU UNDERSTOOD IT. " +
                "Then say \"One moment while I recalculate the fare.\" " +
                "Then STOP and wait silently. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\".",

            CollectionState.VerifyingPickup =>
                "[INSTRUCTION] Say \"Let me just confirm that for you\" and then read back the pickup address " +
                "AS YOU UNDERSTOOD IT from the caller's speech. Do NOT read the raw transcript — use your own " +
                "interpretation of what the caller actually said (e.g., if the transcript says \"This is your way, David Rhoads\" " +
                "but you heard \"52A David Road\", say \"52A David Road\"). " +
                "Then STOP and wait silently. Do NOT say \"let me just confirm\" again. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\". " +
                "If the house number has 3 or more characters (e.g., 1214A), read it DIGIT BY DIGIT " +
                "(e.g., \"one-two-one-four-A Warwick Road\"). NEVER shorten or truncate house numbers.",

            CollectionState.CollectingDestination when verifiedPickup != null && context?.LastDestination != null =>
                $"[INSTRUCTION] {SLOT_GUARD}You MUST read the verified pickup address to the caller EXACTLY and IN FULL. " +
                $"Say: \"Great, so that's {FormatAddressForSpeech(verifiedPickup.Address)} for pickup.\" " +
                "Do NOT shorten, summarize, or omit any part of that address — read every word including the street number, street name, city, and postcode. " +
                $"Their last destination was \"{context.LastDestination}\" — you can offer it. " +
                "Then ask for their DESTINATION address. " +
                "REQUIRED FIELDS REMAINING: destination, passengers, pickup time.",

            CollectionState.CollectingDestination when verifiedPickup != null =>
                $"[INSTRUCTION] {SLOT_GUARD}You MUST read the verified pickup address to the caller EXACTLY and IN FULL. " +
                $"Say: \"Great, so that's {FormatAddressForSpeech(verifiedPickup.Address)} for pickup.\" " +
                "Do NOT shorten, summarize, or omit any part of that address — read every word including the street number, street name, city, and postcode. " +
                "Then ask for their DESTINATION address. " +
                "REQUIRED FIELDS REMAINING: destination, passengers, pickup time.",

            CollectionState.CollectingDestination when context?.LastDestination != null =>
                $"[INSTRUCTION] {SLOT_GUARD}Pickup address verified. " +
                $"Their last destination was \"{context.LastDestination}\" — you can offer it. " +
                "Then ask for their DESTINATION address. " +
                "Do NOT read any raw transcript text back to the caller. " +
                "REQUIRED FIELDS REMAINING: destination, passengers, pickup time.",

            CollectionState.CollectingDestination =>
                $"[INSTRUCTION] {SLOT_GUARD}Pickup address verified. " +
                "Ask for their DESTINATION address. " +
                "Do NOT read any raw transcript text back to the caller. " +
                "REQUIRED FIELDS REMAINING: destination, passengers, pickup time.",

            CollectionState.VerifyingDestination when isRecalculating =>
                "[INSTRUCTION] The caller just changed their destination address. " +
                "Say \"No problem, let me update that for you\" and then read back the new destination address " +
                $"\"{rawData.GetSlot("destination")}\" AS YOU UNDERSTOOD IT. " +
                "Then say \"One moment while I recalculate the fare.\" " +
                "Then STOP and wait silently. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\".",

            CollectionState.VerifyingDestination =>
                "[INSTRUCTION] Say \"Let me just confirm that for you\" and then read back the destination address " +
                "AS YOU UNDERSTOOD IT from the caller's speech. Do NOT read the raw transcript — use your own " +
                "interpretation of what the caller actually said. " +
                "Then STOP and wait silently. Do NOT say \"let me just confirm\" again. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\". " +
                "If the house number has 3 or more characters (e.g., 1214A), read it DIGIT BY DIGIT " +
                "(e.g., \"one-two-one-four-A Warwick Road\"). NEVER shorten or truncate house numbers.",

            CollectionState.CollectingPassengers when verifiedDestination != null =>
                $"[INSTRUCTION] {SLOT_GUARD}You MUST read the verified destination address to the caller EXACTLY and IN FULL. " +
                $"Say: \"Great, so that's {FormatAddressForSpeech(verifiedDestination.Address)} for the destination.\" " +
                "Do NOT shorten, summarize, or omit any part of that address — read every word including the street number, street name, city, and postcode. " +
                "Then ask how many passengers. IMPORTANT: When confirming the count, always repeat the number clearly " +
                "(e.g., \"Great, four passengers\" or \"Got it, that's for 3 people\"). " +
                "REQUIRED FIELDS REMAINING: passengers, pickup time.",

            CollectionState.CollectingPassengers =>
                $"[INSTRUCTION] {SLOT_GUARD}Destination address verified. " +
                "Ask how many passengers. IMPORTANT: When confirming the count, always repeat the number clearly " +
                "(e.g., \"Great, four passengers\" or \"Got it, that's for 3 people\"). " +
                "Do NOT read any raw transcript text back to the caller. " +
                "REQUIRED FIELDS REMAINING: passengers, pickup time.",

            CollectionState.CollectingPickupTime =>
                $"[INSTRUCTION] {SLOT_GUARD}{rawData.PassengersRaw} passenger(s) confirmed. " +
                "Ask when they need the taxi — now (ASAP) or a specific time? " +
                "REQUIRED FIELD REMAINING: pickup time. This is the LAST field before we can check availability.",

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
                $"[INSTRUCTION] ⚠️ You are MID-CALL. Do NOT greet the caller again. Do NOT say 'Welcome to Ada Taxi'. " +
                "You MUST read the verified pickup and destination EXACTLY as written below (no shortening):\n" +
                $"- Name: {booking.CallerName}\n" +
                $"- Pickup (VERBATIM): {FormatAddressForSpeech(fareResult.Pickup.Address)}\n" +
                $"- Destination (VERBATIM): {FormatAddressForSpeech(fareResult.Destination.Address)}\n" +
                $"- Passengers: {booking.Passengers}\n" +
                $"- Time: {booking.PickupTime}\n" +
                $"- Fare: {fareResult.FareSpoken}\n" +
                $"- Driver ETA: {fareResult.BusyMessage}\n" +
                $"Say EXACTLY: \"So {booking.CallerName}, that's from {FormatAddressForSpeech(fareResult.Pickup.Address)} to {FormatAddressForSpeech(fareResult.Destination.Address)}, the fare will be around {fareResult.FareSpoken}, and {fareResult.BusyMessage}. Would you like to go ahead with this booking?\" " +
                "Do NOT paraphrase addresses.",

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

            _ => "[INSTRUCTION] ⛔ MANDATORY: The booking is NOT complete. Do NOT offer general assistance. " +
                 "Do NOT say goodbye. Ask the caller what information they'd like to provide next."
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

    /// <summary>
    /// Derive a language pre-warning from the caller's phone country code or stored preference.
    /// This hints Whisper STT and Ada to expect non-English speech, preventing gibberish transcription.
    /// </summary>
    private static string GetLanguagePrewarn(CallerContext? context)
    {
        // Priority 1: Stored language preference from caller history
        if (context?.PreferredLanguage != null && context.PreferredLanguage != "en")
        {
            var langName = MapLanguageCode(context.PreferredLanguage);
            if (langName != null)
                return $"\n[LANGUAGE PREWARN] This caller previously spoke {langName}. " +
                       $"EXPECT them to speak {langName}. Greet in {langName}. " +
                       "Switch if they respond in a different language.";
        }

        // Priority 2: Derive from phone country code
        if (!string.IsNullOrWhiteSpace(context?.CallerPhone))
        {
            var phone = context.CallerPhone.TrimStart('+');
            var langName = phone switch
            {
                _ when phone.StartsWith("31") => "Dutch",       // Netherlands
                _ when phone.StartsWith("32") => "Dutch",       // Belgium (Flemish majority)
                _ when phone.StartsWith("33") => "French",      // France
                _ when phone.StartsWith("34") => "Spanish",     // Spain
                _ when phone.StartsWith("39") => "Italian",     // Italy
                _ when phone.StartsWith("48") => "Polish",      // Poland
                _ when phone.StartsWith("49") => "German",      // Germany
                _ when phone.StartsWith("351") => "Portuguese", // Portugal
                _ when phone.StartsWith("352") => "French",     // Luxembourg
                _ when phone.StartsWith("353") => null,         // Ireland (English)
                _ when phone.StartsWith("41") => "German",      // Switzerland
                _ when phone.StartsWith("43") => "German",      // Austria
                _ when phone.StartsWith("44") => null,          // UK (English)
                _ when phone.StartsWith("1") => null,           // US/Canada (English)
                _ => null
            };

            if (langName != null)
                return $"\n[LANGUAGE PREWARN] Caller's phone number is from a {langName}-speaking country. " +
                       $"EXPECT the caller to speak {langName}. Greet in {langName}. " +
                       "Switch if they respond in a different language.";
        }

        return ""; // English default — no pre-warning needed
    }

    private static string? MapLanguageCode(string code) => code.ToLowerInvariant() switch
    {
        "nl" => "Dutch",
        "fr" => "French",
        "de" => "German",
        "es" => "Spanish",
        "it" => "Italian",
        "pl" => "Polish",
        "pt" => "Portuguese",
        "en" => null, // No pre-warning for English
        _ => null
    };

    /// <summary>
    /// Format an address for TTS readback by parsing the house number with AddressParser
    /// and reattaching it as a spaced-out "sacred string" that the voice model cannot
    /// misinterpret as a mathematical range (e.g. "52A David Road" → "5 2 A David Road").
    /// </summary>
    internal static string FormatAddressForSpeech(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return address;

        var parsed = AddressParser.ParseAddress(address);

        // No house number found — return as-is
        if (!parsed.HasHouseNumber) return address;

        var houseNum = parsed.HouseNumber; // e.g. "52A", "1214A", "3B", "43"

        // Check if house number contains letters (alphanumeric)
        bool hasLetters = houseNum.Any(char.IsLetter);

        string spoken;
        if (hasLetters)
        {
            // For 3+ character alphanumeric house numbers, read digit-by-digit
            if (houseNum.Length >= 3)
            {
                spoken = string.Join(" ", houseNum.ToUpperInvariant().ToCharArray());
            }
            else
            {
                // Short like "3B" — just space the letter away
                spoken = string.Join(" ", houseNum.ToUpperInvariant().ToCharArray());
            }
        }
        else
        {
            // Pure numeric house number — no mangling risk, keep as-is
            return address;
        }

        // Rebuild: replace the original house number with the spaced version
        // The parsed street/town/etc form the remainder
        var remainder = address;
        var houseIdx = remainder.IndexOf(houseNum, StringComparison.OrdinalIgnoreCase);
        if (houseIdx >= 0)
        {
            remainder = remainder[..houseIdx] + spoken + remainder[(houseIdx + houseNum.Length)..];
        }

        return remainder;
    }
}
