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
    /// Build the system prompt for the conversation AI (v4 — Deterministic Mode).
    /// Leaner, clearer authority hierarchy. AI is a voice interface only.
    /// </summary>
    public static string BuildSystemPrompt(string companyName, CallerContext? context = null)
    {
        var callerInfo = "";
        if (context?.IsReturningCaller == true)
        {
            callerInfo = $"""

                RETURNING CALLER CONTEXT (use naturally, don't read out verbatim):
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
            You are Ada, a professional AI taxi booking assistant for {companyName}.
            You are a VOICE INTERFACE only.

            REFERENCE_DATETIME (London): {referenceDateTime}
            {languagePrewarn}

            ────────────────────────────────
            ROLE & STATE AUTHORITY (CRITICAL)
            ────────────────────────────────

            You are NOT the booking engine.
            You do NOT track booking state internally.
            You do NOT make decisions.

            The system sends [INSTRUCTION] messages.
            The latest [INSTRUCTION] is the ONLY authority.

            If any instruction conflicts with your memory, prior speech, or transcript —
            the [INSTRUCTION] is ALWAYS correct.

            If unsure what to do → follow the latest [INSTRUCTION] exactly.

            ────────────────────────────────
            TOOL EXECUTION RULE (ABSOLUTE)
            ────────────────────────────────

            You have ONE tool: sync_booking_data.

            If the caller provides ANY booking detail
            (name, pickup, destination, passengers, time, corrections):

            1) Call sync_booking_data.
            2) WAIT for the tool result.
            3) ONLY AFTER the tool result arrives, follow the next [INSTRUCTION].

            Speaking before calling the tool when required is a CRITICAL ERROR.
            If you do not call the tool, the booking will FAIL.

            If multiple fields are given in one sentence,
            include ALL fields in ONE tool call.

            You MUST include:
            - interpretation (what you understood)
            - last_utterance (exact raw transcript — verbatim, no cleanup)

            Never split a compound utterance into multiple tool calls.

            Keywords to detect compound input:
            "from X to Y", "going to", "heading to", "X with N passengers", "for N people".

            ────────────────────────────────
            SPEECH STYLE RULES
            ────────────────────────────────

            - Under 15 words per response where possible.
            - One question at a time.
            - No filler phrases.
            - No repetition.
            - Never rush, never sound robotic.
            - NEVER repeat the greeting. You greet ONCE at the start.

            ────────────────────────────────
            SCRIPT ENFORCEMENT (HARD RULES)
            ────────────────────────────────

            You MUST follow the latest [INSTRUCTION] exactly.
            Ask ONLY for the current required field.

            You MUST NOT:
            - Confirm or dispatch a booking
            - Say goodbye or "safe travels"
            - Say "anything else" or offer general help
            - Skip to later fields (e.g. passengers/time) unless instructed
            - End the call

            Unless the [INSTRUCTION] explicitly tells you to.

            If [INSTRUCTION] says SILENCE — say NOTHING.
            If caller gives multiple details in one turn, acknowledge briefly
            and still follow the latest [INSTRUCTION].

            ────────────────────────────────
            CORRECTION TAGGING (MANDATORY)
            ────────────────────────────────

            When the caller corrects or rejects something you said
            (e.g., "no", "that's wrong", "actually it's...", "not X, it's Y"):

            1) Identify WHICH field is being corrected.
            2) Prefix response with silent tag: [CORRECTION:fieldname]

            Example:
            Ada: "Pickup is Morrison Street."
            Caller: "No, Morrison's Supermarket."
            You: [CORRECTION:pickup] Sorry, Morrison's Supermarket?
            sync_booking_data: pickup="Morrison's Supermarket" (NOT "Morrison Street")

            The tag is NEVER spoken aloud — it's metadata.
            The OLD value is DEAD — replace it fully in sync_booking_data.
            Never reuse the previous value.

            If unsure which field → use the one you most recently read back or asked about.

            COMPOUND CORRECTIONS:
            If the caller corrects multiple fields at once, use multiple tags:
            [CORRECTION:pickup][CORRECTION:passengers]
            Include ALL corrected values in ONE sync_booking_data call.

            ────────────────────────────────
            HOUSE NUMBER PROTECTION (STRICT)
            ────────────────────────────────

            House numbers are SACRED STRINGS.

            NEVER:
            - Insert hyphens
            - Drop digits
            - Rearrange characters
            - Convert to ranges
            - Guess missing characters

            Forbidden: 52A → 52-84, 1214A → 12-14A, 52A → 528.

            If 3+ characters (e.g. 1214A), read DIGIT BY DIGIT:
            "one-two-one-four-A".

            If ANY part is uncertain → ask the caller to confirm.

            MID-SPEECH SELF-CORRECTION:
            If caller says one number then hesitates and says another,
            the LATTER number is the correction.
            "52A no 1214A David Road" → 1214A David Road.

            ────────────────────────────────
            SPELLED-OUT NAMES
            ────────────────────────────────

            Callers may spell street names: "D-O-V-E-Y" or "D as in David, O, V, E, Y".
            Assemble into word (D-O-V-E-Y → "Dovey").
            Confirm: "So that's Dovey Road, D-O-V-E-Y?"
            NATO phonetic alphabet applies. Spelled result is ABSOLUTE.

            ────────────────────────────────
            POSTCODE RULE
            ────────────────────────────────

            A full UK postcode is stronger than street name.
            If postcode is provided, trust it fully. Never override it.
            If geocoder resolves to a different postcode, the geocoder is WRONG.
            Postcodes count as city context — no need to ask for the city.

            ────────────────────────────────
            MISSING HOUSE NUMBER DETECTION
            ────────────────────────────────

            If a street-type address (Road, Street, Close, Avenue, Lane, Drive, Way,
            Crescent, Terrace, Grove, Place, Court, Gardens, Square) is given
            WITHOUT a house number:
            - ALWAYS ask for the house number BEFORE accepting.
            - Exception: "near", "opposite", "outside", "corner of" — accept without.
            - Exception: Named buildings, pubs, shops, stations, airports, hospitals — accept without.

            ────────────────────────────────
            TIME NORMALISATION
            ────────────────────────────────

            "now", "straight away", "as soon as possible", "immediately" → ASAP.
            All other times: convert to YYYY-MM-DD HH:MM (24-hour).
            Never return raw phrases.

            ────────────────────────────────
            LANGUAGE AUTO-SWITCH
            ────────────────────────────────

            Detect language every turn.
            If caller switches language → switch immediately.
            Do NOT ask permission.

            Supported: English, Dutch, French, German, Spanish,
            Italian, Polish, Portuguese.
            Default: English.
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
        // ── Fare address override: prefer inline-verified addresses over fare pipeline addresses ──
        // The fare pipeline's address-dispatch can return stale/wrong addresses (caching, Gemini drift).
        // The inline-verified addresses are authoritative — they were confirmed during the call.
        if (fareResult != null && verifiedPickup != null && fareResult.Pickup.Address != verifiedPickup.Address)
        {
            fareResult = fareResult with { Pickup = verifiedPickup };
        }
        if (fareResult != null && verifiedDestination != null && fareResult.Destination.Address != verifiedDestination.Address)
        {
            fareResult = fareResult with { Destination = verifiedDestination };
        }
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
                (CollectionState.PresentingFare, _) =>
                    "[INSTRUCTION] ⛔ MANDATORY: I didn't hear your response. You MUST ask again: " +
                    "\"Sorry, would you like to go ahead with this booking?\" Do NOT say anything else. " +
                    "Do NOT repeat the fare. Do NOT repeat the addresses. Do NOT ask about pickup time. ONLY ask for confirmation.",
                (CollectionState.AwaitingConfirmation, _) =>
                    "[INSTRUCTION] ⛔ MANDATORY: I didn't hear your response. You MUST ask again: " +
                    "\"Sorry, shall I confirm this booking for you?\" Do NOT say anything else.",
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

        // Build dynamic remaining fields based on what's actually missing
        string RemainingFields()
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(rawData.PickupRaw)) missing.Add("pickup");
            if (string.IsNullOrWhiteSpace(rawData.DestinationRaw)) missing.Add("destination");
            if (string.IsNullOrWhiteSpace(rawData.PassengersRaw)) missing.Add("passengers");
            if (string.IsNullOrWhiteSpace(rawData.PickupTimeRaw)) missing.Add("pickup time");
            return missing.Count > 0
                ? $"REQUIRED FIELDS REMAINING: {string.Join(", ", missing)}."
                : "All fields collected.";
        }

        return state switch
        {
            // Greeting states are now handled via BuildGreetingMessage — these are fallbacks
            CollectionState.Greeting when context?.IsReturningCaller == true =>
                $"[INSTRUCTION] Greet {context.CallerName ?? "the caller"} warmly by name — they're a returning customer. " +
                "Confirm their name and ask whereabouts they are.",

            CollectionState.Greeting =>
                "[INSTRUCTION] Greet the caller warmly and ask for their name.",

            CollectionState.ManagingExistingBooking =>
                "[INSTRUCTION] The caller has an active booking. Ask: would you like to cancel, make changes, or check on your driver? " +
                "Wait for their response. Do NOT start a new booking unless they explicitly ask for one.",

            CollectionState.AwaitingCancelConfirmation =>
                "[INSTRUCTION] The caller wants to cancel their booking. Ask them to confirm: \"Are you sure you'd like to cancel your booking?\" " +
                "Wait for a clear yes or no.",

            CollectionState.CollectingName =>
                $"[INSTRUCTION] {SLOT_GUARD}Ask the caller for their name. " +
                $"{RemainingFields()}",

            CollectionState.CollectingPickup when context?.IsReturningCaller == true && context.LastPickup != null =>
                $"[INSTRUCTION] {SLOT_GUARD}{NameAck(rawData)} Ask for their PICKUP address. " +
                $"Their last pickup was \"{context.LastPickup}\" — you can offer it as a suggestion. " +
                "They must include a house number if it's a street address. " +
                $"{RemainingFields()}",

            CollectionState.CollectingPickup =>
                $"[INSTRUCTION] {SLOT_GUARD}{NameAck(rawData)} Ask for their PICKUP address. " +
                "They must include a house number if it's a street address. " +
                $"{RemainingFields()}",

            CollectionState.VerifyingPickup when isRecalculating =>
                "[INSTRUCTION] The caller just changed their pickup address. " +
                "Say \"No problem, let me update that for you\" and then read back the new pickup address " +
                $"\"{FormatAddressForSpeech(rawData.GetGeminiSlot("pickup") ?? rawData.GetSlot("pickup") ?? "")}\" VERBATIM. " +
                "Then say \"One moment while I recalculate the fare.\" " +
                "Then STOP and wait silently. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\".",

            CollectionState.VerifyingPickup when rawData.IsMultiSlotBurst =>
                "[INSTRUCTION] The caller provided multiple details at once. " +
                "You must prioritize verifying the PICKUP first. " +
                $"Say: \"I've got all those details. First, let me confirm the pickup: {FormatAddressForSpeech(rawData.GetGeminiSlot("pickup") ?? rawData.PickupRaw ?? "")}.\" " +
                "Do NOT confirm the destination or passengers yet. Then STOP and wait silently. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\". " +
                "If the house number has 3 or more characters (e.g., 1214A), read it DIGIT BY DIGIT " +
                "(e.g., \"one-two-one-four-A Warwick Road\"). NEVER shorten or truncate house numbers.",

            CollectionState.VerifyingPickup when rawData.GetGeminiSlot("pickup") != null =>
                "[INSTRUCTION] Say \"Let me just confirm that for you\" and then read back EXACTLY this pickup address: " +
                $"\"{FormatAddressForSpeech(rawData.GetGeminiSlot("pickup") ?? "")}\". " +
                "This is the AI-cleaned version — read it VERBATIM, do NOT change or reinterpret it. " +
                "Then STOP and wait silently. Do NOT say \"let me just confirm\" again. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\". " +
                "If the house number has 3 or more characters (e.g., 1214A), read it DIGIT BY DIGIT " +
                "(e.g., \"one-two-one-four-A Warwick Road\"). NEVER shorten or truncate house numbers.",

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
                $"[INSTRUCTION] {SLOT_GUARD}Pickup confirmed as \"{FormatAddressForSpeech(verifiedPickup.Address)}\". " +
                "Say this confirmed address to the caller so they know it's correct. " +
                $"Their last destination was \"{context.LastDestination}\" — you can offer it. " +
                "Then ask for their DESTINATION address. " +
                $"{RemainingFields()}",

            CollectionState.CollectingDestination when verifiedPickup != null =>
                $"[INSTRUCTION] {SLOT_GUARD}Pickup confirmed as \"{FormatAddressForSpeech(verifiedPickup.Address)}\". " +
                "Say this confirmed address to the caller so they know it's correct. " +
                "Then ask for their DESTINATION address. " +
                $"{RemainingFields()}",

            CollectionState.CollectingDestination when context?.LastDestination != null =>
                $"[INSTRUCTION] {SLOT_GUARD}Pickup address verified. " +
                $"Their last destination was \"{context.LastDestination}\" — you can offer it. " +
                "Then ask for their DESTINATION address. " +
                "Do NOT read any raw transcript text back to the caller. " +
                $"{RemainingFields()}",

            CollectionState.CollectingDestination =>
                $"[INSTRUCTION] {SLOT_GUARD}Pickup address verified. " +
                "Ask for their DESTINATION address. " +
                "Do NOT read any raw transcript text back to the caller. " +
                $"{RemainingFields()}",

            CollectionState.VerifyingDestination when isRecalculating =>
                "[INSTRUCTION] The caller just changed their destination address. " +
                "Say \"No problem, let me update that for you\" and then read back the new destination address " +
                $"\"{FormatAddressForSpeech(rawData.GetGeminiSlot("destination") ?? rawData.GetSlot("destination") ?? "")}\" VERBATIM. " +
                "Then say \"One moment while I recalculate the fare.\" " +
                "Then STOP and wait silently. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\".",

            CollectionState.VerifyingDestination when rawData.IsMultiSlotBurst =>
                "[INSTRUCTION] The caller provided multiple details at once. " +
                "Now verify the DESTINATION. " +
                $"Say: \"And for the destination, that's {FormatAddressForSpeech(rawData.GetGeminiSlot("destination") ?? rawData.DestinationRaw ?? "")}.\" " +
                "Then STOP and wait silently. Do NOT confirm passengers or time yet. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\". " +
                "If the house number has 3 or more characters (e.g., 1214A), read it DIGIT BY DIGIT " +
                "(e.g., \"one-two-one-four-A Warwick Road\"). NEVER shorten or truncate house numbers.",

            CollectionState.VerifyingDestination when rawData.GetGeminiSlot("destination") != null =>
                "[INSTRUCTION] Say \"Let me just confirm that for you\" and then read back EXACTLY this destination address: " +
                $"\"{FormatAddressForSpeech(rawData.GetGeminiSlot("destination") ?? "")}\". " +
                "This is the AI-cleaned version — read it VERBATIM, do NOT change or reinterpret it. " +
                "Then STOP and wait silently. Do NOT say \"let me just confirm\" again. " +
                "IMPORTANT: House numbers are SACRED STRINGS — NEVER convert them into ranges. " +
                "\"52A\" means house fifty-two-A, NOT a range 52-84. Read it as \"52 A\". " +
                "If the house number has 3 or more characters (e.g., 1214A), read it DIGIT BY DIGIT " +
                "(e.g., \"one-two-one-four-A Warwick Road\"). NEVER shorten or truncate house numbers.",

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
                $"[INSTRUCTION] {SLOT_GUARD}Destination confirmed as \"{FormatAddressForSpeech(verifiedDestination.Address)}\". " +
                "Say this confirmed address to the caller so they know it's correct. " +
                "Then ask how many passengers. " +
                "IMPORTANT: When confirming the count, always repeat the number clearly " +
                "(e.g., \"Great, four passengers\" or \"Got it, that's for 3 people\"). " +
                $"{RemainingFields()}",

            CollectionState.CollectingPassengers =>
                $"[INSTRUCTION] {SLOT_GUARD}Destination address verified. " +
                "Ask how many passengers. IMPORTANT: When confirming the count, always repeat the number clearly " +
                "(e.g., \"Great, four passengers\" or \"Got it, that's for 3 people\"). " +
                "Do NOT read any raw transcript text back to the caller. " +
                $"{RemainingFields()}",

            CollectionState.CollectingPickupTime =>
                $"[INSTRUCTION] {SLOT_GUARD}{rawData.PassengersRaw} passenger(s) confirmed. " +
                "Ask when they need the taxi — now (ASAP) or a specific time? " +
                $"{RemainingFields()} This is the LAST field before we can check availability.",

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
                $"[INSTRUCTION] ⚠️ You are MID-CALL. Do NOT greet the caller again. Do NOT say 'Welcome to...' or re-introduce yourself.\n" +
                "⛔ TRUTH OVERRIDE: The caller may have mispronounced or misspelled addresses. " +
                "You MUST NOT repeat the caller's words for addresses. You MUST ONLY use the VERIFIED addresses " +
                "provided in the bullet points below. These are the geocode-confirmed ground truth. " +
                "If you repeat the caller's misspelling instead of the verified address, the system WILL FAIL.\n" +
                "You MUST read the verified pickup and destination EXACTLY as written below (no shortening):\n" +
                $"- Name: {booking.CallerName}\n" +
                $"- Pickup (VERIFIED — use ONLY this): {FormatAddressWithPoiAlias(fareResult.Pickup)}\n" +
                $"- Destination (VERIFIED — use ONLY this): {FormatAddressWithPoiAlias(fareResult.Destination)}\n" +
                $"- Passengers: {booking.Passengers}\n" +
                $"- Time: {booking.PickupTime}\n" +
                $"- Fare: {fareResult.FareSpoken}\n" +
                $"- ⚠️ Driver ETA (MANDATORY — you MUST say this): {fareResult.BusyMessage}\n" +
                BuildStreetNameGuard(fareResult.Pickup.Address) +
                BuildStreetNameGuard(fareResult.Destination.Address) +
                BuildPostcodeGuard(fareResult.Pickup.Address) +
                BuildPostcodeGuard(fareResult.Destination.Address) +
                $"Say EXACTLY: \"So {booking.CallerName}, that's from {FormatAddressWithPoiAlias(fareResult.Pickup)} to {FormatAddressWithPoiAlias(fareResult.Destination)}, the fare will be around {fareResult.FareSpoken}. {fareResult.BusyMessage} Would you like to go ahead with this booking?\"\n" +
                "Do NOT paraphrase addresses. Do NOT skip the driver ETA / busy message — it is MANDATORY.\n" +
                "WRONG: Repeating caller's mispronunciation (e.g., 'Fargosworth Street')\n" +
                "CORRECT: Using verified address (e.g., 'Far Gosford Street')",

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
                $"[INSTRUCTION] ⛔ TRUTH OVERRIDE: Use ONLY the verified addresses below, NOT the caller's words.\n" +
                $"Read back the full booking for final confirmation:\n" +
                $"- Name: {booking.CallerName}\n" +
                $"- From (VERIFIED): {FormatAddressWithPoiAlias(fareResult.Pickup)}\n" +
                $"- To (VERIFIED): {FormatAddressWithPoiAlias(fareResult.Destination)}\n" +
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

        // Special case: geocode failed entirely — tell caller location wasn't found
        if (!string.IsNullOrWhiteSpace(info.Message) && info.Message.StartsWith("GEOCODE_FAILED:"))
        {
            var failedAddr = info.Message.Substring("GEOCODE_FAILED:".Length);
            return $"[INSTRUCTION] ⚠️ Tell the caller: \"I'm sorry, I wasn't able to locate '{failedAddr}'. " +
                   $"Could you confirm the address or give me a bit more detail like the area or postcode?\" " +
                   "Wait for their response. Do NOT ask for a completely new address — just ask for clarification.";
        }

        // If FareGeo provided a specific clarification message (e.g., "Did you mean David Road?"),
        // use that instead of the generic "Which area?" question
        var hasSpecificMessage = !string.IsNullOrWhiteSpace(info.Message)
            && !info.Message.Equals("Which area is that in?", StringComparison.OrdinalIgnoreCase);

        if (hasSpecificMessage)
        {
            return $"[INSTRUCTION] Ask the caller about their {fieldLabel} address: {info.Message}";
        }

        // If exactly 2 alternatives, present them immediately — no point asking "which area?"
        if (info.Alternatives != null && info.Alternatives.Count == 2)
        {
            var alt1 = info.Alternatives[0];
            var alt2 = info.Alternatives[1];
            return $"[INSTRUCTION] The {fieldLabel} address \"{rawValue}\" exists in two areas. " +
                   $"Ask the caller: \"Is that {rawValue} in {alt1} or {alt2}?\" " +
                   "Do NOT say anything else. Wait for their answer.";
        }

        // Attempt 1: just ask "which area?"
        // Attempt 2+: if alternatives available, offer them; if caller says "I don't know", accept and proceed with best match
        if (info.Attempt <= 1 || info.Alternatives == null || info.Alternatives.Count == 0)
        {
            return $"[INSTRUCTION] The {fieldLabel} address \"{rawValue}\" exists in multiple areas. " +
                   "Simply ask: \"Which area is that in?\" " +
                   "Do NOT list the areas — only list them if the caller asks. " +
                   "If the caller says they don't know the area, say \"No problem\" and accept the address as-is.";
        }
        else
        {
            var altList = string.Join(", ", info.Alternatives);
            return $"[INSTRUCTION] The caller couldn't specify the area for \"{rawValue}\". " +
                   $"This time, offer the options: {altList}. " +
                   $"Ask which one they mean. " +
                   "If they still can't answer, say \"No problem, I'll use the closest match\" and accept.";
        }
    }

    private static string NameAck(RawBookingData data)
    {
        if (string.IsNullOrWhiteSpace(data.NameRaw)) return "";
        // If the name looks like a sentence or STT noise (e.g. "I said Max", "my name is"),
        // don't echo it back — just acknowledge generically.
        if (data.NameRaw.Split(' ').Length > 2) return "Got that.";
        return $"Thanks, {data.NameRaw}.";
    }

    /// <summary>
    /// Build the greeting message to inject as a conversation item (like AdaSdkModel).
    /// This is sent as a user message that the AI responds to naturally.
    /// </summary>
    public static string BuildGreetingMessage(string companyName, CallerContext? context, CollectionState currentState, ActiveBookingInfo? activeBooking = null)
    {
        // Returning caller with active booking — greet and offer manage options
        if (activeBooking != null && context?.IsReturningCaller == true && !string.IsNullOrWhiteSpace(context.CallerName))
        {
            var pickup = activeBooking.Pickup ?? "unknown";
            var destination = activeBooking.Destination ?? "unknown";

            return $"[SYSTEM] A returning caller named {context.CallerName} has connected. " +
                   $"They have an ACTIVE BOOKING from {pickup} to {destination}. " +
                   $"Greet them BY NAME, then tell them about their existing booking. Say something like: " +
                   $"\"Welcome back to {companyName}, {context.CallerName}. I can see you have an active booking " +
                   $"from {pickup} to {destination}. Would you like to cancel it, make any changes, or check on your driver?\" " +
                   $"Wait for their response before proceeding.";
        }

        if (context?.IsReturningCaller == true && !string.IsNullOrWhiteSpace(context.CallerName))
        {
            // Returning caller without active booking — greet by name and ask whereabouts (skip name collection)
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

        var houseNum = parsed.HouseNumber; // e.g. "52A", "1214A", "3B", "43", "12-14A"

        // Check if house number contains letters or hyphens (needs TTS help)
        bool hasLetters = houseNum.Any(char.IsLetter);
        bool hasHyphen = houseNum.Contains('-');

        if (!hasLetters && !hasHyphen)
        {
            // Pure numeric house number — no mangling risk, keep as-is
            return address;
        }

        string spoken;
        if (hasHyphen)
        {
            // Hyphenated range like "12-14A" → split on hyphen, keep numeric groups intact,
            // space out trailing letters: "12-14A" → "12 14 A"
            var parts = houseNum.Split('-');
            var spokenParts = parts.Select(part =>
            {
                // Separate trailing letter from digits: "14A" → "14 A"
                var m = System.Text.RegularExpressions.Regex.Match(part, @"^(\d+)([A-Za-z])?$");
                if (m.Success && m.Groups[2].Success)
                    return $"{m.Groups[1].Value} {m.Groups[2].Value.ToUpperInvariant()}";
                return part;
            });
            spoken = string.Join(" ", spokenParts);
        }
        else if (houseNum.Length >= 3)
        {
            // For 3+ character alphanumeric house numbers, read digit-by-digit
            spoken = string.Join(" ", houseNum.ToUpperInvariant().ToCharArray());
        }
        else
        {
            // Short like "3B" — just space the letter away
            spoken = string.Join(" ", houseNum.ToUpperInvariant().ToCharArray());
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

    /// <summary>
    /// Build a phonetic reinforcement warning for the street name in a verified address.
    /// This prevents the AI from substituting similar-sounding common words
    /// (e.g., "Dovey" → "Dover", "Hockley" → "Hockey").
    /// </summary>
    internal static string BuildStreetNameGuard(string verifiedAddress)
    {
        if (string.IsNullOrWhiteSpace(verifiedAddress)) return "";

        var parsed = AddressParser.ParseAddress(verifiedAddress);
        var streetName = parsed.StreetName;
        if (string.IsNullOrWhiteSpace(streetName)) return "";

        // Extract just the name part before the street type (Road, Street, etc.)
        var streetTypes = new[] { "Road", "Street", "Close", "Avenue", "Lane", "Drive", "Way",
            "Crescent", "Terrace", "Grove", "Place", "Court", "Gardens", "Square",
            "Parade", "Rise", "Mews", "Hill", "Boulevard", "Walk", "Row", "View", "Green", "End" };

        string? nameOnly = null;
        foreach (var st in streetTypes)
        {
            if (streetName.EndsWith($" {st}", StringComparison.OrdinalIgnoreCase))
            {
                nameOnly = streetName[..^(st.Length + 1)].Trim();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(nameOnly)) return "";

        // Spell it out letter-by-letter for phonetic anchoring
        var spelled = string.Join("-", nameOnly.ToUpperInvariant().ToCharArray());

        return $"⚠️ STREET NAME PRONUNCIATION: The street is \"{nameOnly}\" (spelled {spelled}). " +
               $"Say \"{nameOnly}\" EXACTLY — do NOT substitute a similar word. ";
    }

    /// <summary>
    /// Build postcode enforcement reminder for readback instructions.
    /// </summary>
    internal static string BuildPostcodeGuard(string verifiedAddress)
    {
        if (string.IsNullOrWhiteSpace(verifiedAddress)) return "";

        // Check if address contains a UK postcode pattern
        var postcodeMatch = System.Text.RegularExpressions.Regex.Match(
            verifiedAddress,
            @"[A-Z]{1,2}\d{1,2}[A-Z]?\s*\d[A-Z]{2}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!postcodeMatch.Success) return "";

        return $"⚠️ POSTCODE MANDATORY: You MUST say the postcode \"{postcodeMatch.Value}\" — do NOT skip it. ";
    }

    /// <summary>
    /// Format a geocoded address for speech, prepending the caller's POI name if it differs
    /// from the geocoder's resolved name (e.g., "Pig in the Middle, Far Gosford Street" 
    /// instead of "Sweet Spot, 161-167 Far Gosford Street").
    /// </summary>
    internal static string FormatAddressWithPoiAlias(GeocodedAddress geocoded)
    {
        var formatted = FormatAddressForSpeech(geocoded.Address);
        if (!string.IsNullOrEmpty(geocoded.CallerPoiName))
        {
            // Replace the geocoder's business name with the caller's POI name
            // e.g., "Sweet Spot, 161-167 Far Gosford Street" → "Pig in the Middle, 161-167 Far Gosford Street"
            // Find the first comma (business name separator) and replace everything before it
            var commaIdx = formatted.IndexOf(',');
            if (commaIdx > 0)
            {
                formatted = geocoded.CallerPoiName + formatted[commaIdx..];
            }
            else
            {
                // No comma — just prepend
                formatted = $"{geocoded.CallerPoiName} at {formatted}";
            }
        }
        return formatted;
    }
}
