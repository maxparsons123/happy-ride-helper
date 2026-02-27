using System.Text.Json;
using AdaCleanVersion.Engine;
using AdaCleanVersion.Models;
using AdaCleanVersion.Services;

namespace AdaCleanVersion.Session;

/// <summary>
/// Clean call session — orchestrates the deterministic engine + AI voice interface.
/// 
/// Architecture:
/// 1. Engine drives flow deterministically (no AI involvement in state)
/// 2. AI is voice-only — responds to [INSTRUCTION] messages
/// 3. Caller responses stored as raw slot values
/// 4. Single AI extraction pass when all slots filled
/// 5. Validation → fare → payment → confirmation → dispatch
/// </summary>
public class CleanCallSession
{
    private readonly CallStateEngine _engine = new();
    private readonly IExtractionService _extractionService;
    private readonly FareGeocodingService? _fareService;
    private readonly LocalGeminiReconciler? _reconciler;
    private readonly EdgeBurstDispatcher? _burstDispatcher;
    private readonly IcabbiBookingService? _icabbiService;
    private readonly string _companyName;
    private readonly CallerContext? _callerContext;
    private readonly ActiveBookingInfo? _activeBooking;
    private readonly HashSet<string> _changedSlots = new();
    private int _clarificationAttempts; // loop-breaker counter

    // iCabbi dispatch result — stored after successful dispatch
    private IcabbiBookingResult? _icabbiResult;

    // ─── Ada Transcript Tracking (Source of Truth) ─────────────
    // Ada's spoken responses are accumulated for extraction context.
    private readonly List<string> _adaTranscripts = new();
    // The last instruction emitted to Ada — used as "Ada's question" for 3-way geocoding context.
    private string _lastEmittedInstruction = "";
    private bool _nameRefined; // prevents double name-refinement attempts
    // Pending verification correction: raw STT stored here until Ada's interpretation arrives
    private string? _pendingVerificationTranscript;
    private CollectionState? _pendingVerificationState;
    // Geocode deduplication: prevents cascading geocode calls from multiple Ada transcripts
    private volatile bool _geocodeInFlight;

    // No-reply watchdog: recovers when VAD/transcription misses a short caller reply
    private CancellationTokenSource? _noReplyCts;
    private int _noReplyCount;
    private const int NoReplyTimeoutSeconds = 12;
    private const int NoReplyTimeoutLongSeconds = 25; // For fare recap (long audio)
    private const int MaxNoReplyReprompts = 2;

    public string SessionId { get; }
    public string CallerId { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public CallStateEngine Engine => _engine;

    public event Action<string>? OnLog;
    public event Action<string, bool, bool>? OnAiInstruction; // (instruction, isReprompt, isSilent)
    public event Action<StructuredBooking>? OnBookingReady;
    public event Action<FareResult>? OnFareReady;
    public event Action<bool>? OnTypingSoundsChanged; // enable/disable typing sounds during recalculation
    /// <summary>
    /// Fired when the session needs to truncate/reset the AI's conversation history
    /// to prevent stale context from causing loops after field corrections.
    /// </summary>
    public event Func<Task>? OnTruncateConversation;

    public CleanCallSession(
        string sessionId,
        string callerId,
        string companyName,
        IExtractionService extractionService,
        FareGeocodingService? fareService = null,
        CallerContext? callerContext = null,
        LocalGeminiReconciler? reconciler = null,
        EdgeBurstDispatcher? burstDispatcher = null,
        IcabbiBookingService? icabbiService = null,
        ActiveBookingInfo? activeBooking = null)
    {
        SessionId = sessionId;
        CallerId = callerId;
        _companyName = companyName;
        _extractionService = extractionService;
        _fareService = fareService;
        _callerContext = callerContext;
        _reconciler = reconciler;
        _burstDispatcher = burstDispatcher;
        _icabbiService = icabbiService;
        _activeBooking = activeBooking;

        _engine.OnLog += msg => OnLog?.Invoke(msg);
        _engine.OnStateChanged += OnEngineStateChanged;
    }

    /// <summary>
    /// Start the call — prepare engine state. Greeting is sent separately via SendGreeting.
    /// Matches AdaSdkModel sequence: history → connect → inject context → greeting.
    /// </summary>
    public void Start()
    {
        Log($"Call started: {CallerId}");

        // If returning caller has an active booking, go to ManagingExistingBooking state
        if (_activeBooking != null)
        {
            _engine.HasActiveBooking = true;
            _engine.ExistingBookingId = _activeBooking.BookingId;
            _engine.ExistingIcabbiJourneyId = _activeBooking.IcabbiJourneyId;
            _engine.ForceState(CollectionState.ManagingExistingBooking);
            Log($"📋 Active booking loaded: {_activeBooking.BookingId} ({_activeBooking.Pickup} → {_activeBooking.Destination})");
            return; // Don't begin normal collection — greeting will handle the active booking flow
        }

        // Auto-fill name for returning callers (so BeginCollection skips to pickup)
        if (_callerContext?.IsReturningCaller == true && !string.IsNullOrWhiteSpace(_callerContext.CallerName))
        {
            _engine.RawData.SetSlot("name", _callerContext.CallerName);
            Log($"Auto-filled name from caller history: {_callerContext.CallerName}");
        }

        // Advance engine to first missing slot (skips name if pre-filled)
        _engine.BeginCollection();
        // NOTE: Do NOT emit instruction here — the greeting will be sent by OpenAiRealtimeClient
        // after the session is configured, matching AdaSdkModel's flow.
    }

    /// <summary>
    /// Build the greeting message to inject as a conversation item.
    /// Matches AdaSdkModel's explicit greeting style.
    /// </summary>
    public string BuildGreetingMessage()
    {
        return PromptBuilder.BuildGreetingMessage(_companyName, _callerContext, _engine.State, _activeBooking);
    }

    /// <summary>
    /// Get the active booking system injection message (if any).
    /// This should be injected before the greeting so the AI has context.
    /// </summary>
    public string? BuildActiveBookingInjection()
    {
        return _activeBooking?.BuildSystemMessage();
    }
    public async Task ProcessCallerResponseAsync(string transcript, CancellationToken ct = default)
    {
        // Any caller transcript means they did respond — stop no-reply watchdog.
        CancelNoReplyWatchdog();
        _noReplyCount = 0;

        // ── Priority -1: DETERMINISTIC INTENT GUARD ──
        // Fires BEFORE any AI processing. If IntentGuard resolves an intent,
        // we handle it instantly without consulting the AI model.
        // This eliminates the class of bugs where the AI "understands" but skips the action.
        var intent = IntentGuard.Resolve(_engine.State, transcript);
        if (intent != IntentGuard.ResolvedIntent.None)
        {
            Log($"🛡️ IntentGuard resolved: {intent} (state: {_engine.State}, transcript: \"{transcript}\")");
            await HandleDeterministicIntent(intent, transcript, ct);
            return;
        }

        // ── Priority 0: If awaiting clarification, route directly — do NOT treat as a new slot ──
        if (_engine.State == CollectionState.AwaitingClarification)
        {
            await HandlePostCollectionInput(transcript, ct);
            return;
        }

        // ── Priority 0.5: If verifying an address and caller speaks, send to Gemini with context ──
        // When in VerifyingPickup/VerifyingDestination, the caller is hearing Ada's readback.
        // Store the raw STT and wait for Ada's interpretation to arrive in ProcessAdaTranscriptAsync.
        // Then we send BOTH signals to Gemini for maximum accuracy (dual approach).
        // Without this gate, NextMissingSlot() would return "destination" during VerifyingPickup,
        // causing a corrected pickup to be stored as destination.
        if (_engine.State == CollectionState.VerifyingPickup || 
            _engine.State == CollectionState.VerifyingDestination)
        {
            var verifyingSlot = _engine.State == CollectionState.VerifyingPickup ? "pickup" : "destination";
            Log($"Caller spoke during {_engine.State}: \"{transcript}\" — storing for dual Gemini check when Ada's interpretation arrives");
            _pendingVerificationTranscript = transcript;
            _pendingVerificationState = _engine.State;
            return; // Wait for Ada's interpretation in ProcessAdaTranscriptAsync
        }

        // Step 1: Check for correction intent BEFORE normal slot processing
        // Use AI (Gemini via burst-dispatch) for reliable slot detection,
        // with regex CorrectionDetector as a fast fallback if AI is unavailable.
        var filledSlots = _engine.RawData.FilledSlots;
        if (filledSlots.Count > 0 && HasCorrectionSignal(transcript))
        {
            // Try AI correction detection first
            if (_burstDispatcher != null)
            {
                try
                {
                    var slotValues = new Dictionary<string, string>();
                    foreach (var slot in filledSlots)
                        slotValues[slot] = _engine.RawData.GetSlot(slot) ?? "";

                    var aiCorrection = await _burstDispatcher.DetectCorrectionAsync(
                        transcript, slotValues, ct);

                    if (aiCorrection != null)
                    {
                        Log($"AI correction detected: {aiCorrection.SlotName} → \"{aiCorrection.NewValue}\"");
                        await CorrectSlotAsync(aiCorrection.SlotName, aiCorrection.NewValue, ct);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log($"AI correction detection failed, falling back to regex: {ex.Message}");
                }
            }

            // Regex fallback
            var correction = CorrectionDetector.Detect(
                transcript,
                _engine.RawData.NextMissingSlot(),
                filledSlots);

            if (correction != null)
            {
                Log($"Regex correction detected: {correction.SlotName} → \"{correction.NewValue}\"");
                await CorrectSlotAsync(correction.SlotName, correction.NewValue, ct);
                return;
            }
        }

        var currentSlot = _engine.RawData.NextMissingSlot();
        if (currentSlot == null)
        {
            // All slots filled — might be a correction or confirmation
            await HandlePostCollectionInput(transcript, ct);
            return;
        }

        // ── Freeform Burst Detection + Edge Burst Dispatch ──
        // If the transcript looks "dense" (multiple keywords, long sentence), try burst-dispatch.
        // This splits ALL fields AND geocodes addresses in a single round-trip.
        // Falls back to slot-by-slot processing on timeout/error.
        var aiHandled = false;
        if (_burstDispatcher != null && EdgeBurstDispatcher.LooksLikeBurst(transcript))
        {
            try
            {
                Log($"[BurstDispatch] Burst detected, calling edge function...");
                var burst = await _burstDispatcher.DispatchAsync(
                    transcript,
                    phone: CallerId,
                    callerArea: null,
                    ct: ct);

                if (burst != null && burst.Status != "error")
                {
                    aiHandled = true;

                    // Map extracted data to raw slots
                    if (burst.Name != null)
                    {
                        _engine.RawData.SetSlot("name", burst.Name);
                        Log($"[BurstDispatch] name=\"{burst.Name}\"");
                    }
                    if (burst.Pickup != null)
                    {
                        // Clear stale verified address if this is a correction
                        if (_engine.VerifiedPickup != null)
                        {
                            _engine.ClearVerifiedAddress("pickup");
                            _engine.ClearFareResult();
                            Log($"[BurstDispatch] Cleared stale verified pickup for correction");
                        }
                        _engine.RawData.SetSlot("pickup", burst.Pickup);
                        _engine.RawData.SetGeminiSlot("pickup", burst.Pickup);
                        Log($"[BurstDispatch] pickup=\"{burst.Pickup}\"");
                    }
                    if (burst.Destination != null)
                    {
                        // Clear stale verified address if this is a correction
                        if (_engine.VerifiedDestination != null)
                        {
                            _engine.ClearVerifiedAddress("destination");
                            _engine.ClearFareResult();
                            Log($"[BurstDispatch] Cleared stale verified destination for correction");
                        }
                        _engine.RawData.SetSlot("destination", burst.Destination);
                        _engine.RawData.SetGeminiSlot("destination", burst.Destination);
                        Log($"[BurstDispatch] destination=\"{burst.Destination}\"");
                    }
                    if (burst.Passengers.HasValue)
                    {
                        _engine.RawData.SetSlot("passengers", burst.Passengers.Value.ToString());
                        Log($"[BurstDispatch] passengers={burst.Passengers.Value}");
                    }
                    if (burst.PickupTime != null)
                    {
                        _engine.RawData.SetSlot("pickup_time", burst.PickupTime);
                        Log($"[BurstDispatch] pickup_time=\"{burst.PickupTime}\"");
                    }

                    // ── Smart Slot Picker: if we're collecting pickup and burst only returned a destination,
                    // treat the single address as the pickup (caller is answering the pickup question).
                    // Only keep it as destination if they explicitly said "to" or "going to".
                    if (currentSlot == "pickup" && burst.Pickup == null && burst.Destination != null
                        && !System.Text.RegularExpressions.Regex.IsMatch(transcript, @"\b(to|going to|drop off|drop me)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        Log($"[BurstDispatch] Smart slot picker: reassigning destination → pickup (caller answering pickup question)");
                        _engine.RawData.SetSlot("pickup", burst.Destination);
                        _engine.RawData.SetGeminiSlot("pickup", burst.Destination);
                        _engine.RawData.SetSlot("destination", null!); // Clear the misclassified destination
                        _engine.RawData.SetGeminiSlot("destination", null!);
                        burst = burst with { Pickup = burst.Destination, Destination = null };
                    }

                    _engine.RawData.IsMultiSlotBurst = true;

                    // Cache geocoded result so verification states can skip separate geocoding
                    if (burst.Geocoded.HasValue)
                    {
                        _engine.RawData.SetBurstGeocodedResult(burst.Geocoded.Value);
                        Log($"[BurstDispatch] ✅ Geocoded data cached (status={burst.Status})");
                    }

                    // Fast-track: jump to verification of the first address
                    if (burst.Pickup != null)
                    {
                        _engine.ForceState(CollectionState.VerifyingPickup);
                        EmitCurrentInstruction();
                        return;
                    }
                    else if (burst.Destination != null)
                    {
                        _engine.ForceState(CollectionState.VerifyingDestination);
                        EmitCurrentInstruction();
                        return;
                    }
                    else
                    {
                        // Only non-address fields extracted — let normal flow continue
                        var primaryValue = burst.Name ?? burst.Passengers?.ToString() ?? burst.PickupTime;
                        if (primaryValue != null)
                            transcript = primaryValue;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[BurstDispatch] Failed, falling back to regex: {ex.Message}");
            }
        }

        // AI burst dispatch handles all splitting — no regex fallback needed.

        // ── Cross-Slot Reference Detection ──
        // If the caller explicitly mentions a DIFFERENT slot (e.g., "the pickup is X" while
        // we're collecting destination), route to that slot instead of blindly storing as current.
        // This prevents garbage like "Right, the pick-up is dig in the middle" being stored as destination.
        if (!aiHandled)
        {
            var crossSlotTarget = DetectCrossSlotReference(transcript, currentSlot);
            if (crossSlotTarget != null)
            {
                var extractedValue = ExtractValueAfterSlotReference(transcript, crossSlotTarget);
                Log($"[CrossSlot] Caller referenced '{crossSlotTarget}' while collecting '{currentSlot}': \"{transcript}\" → value=\"{extractedValue}\"");
                
                if (!string.IsNullOrWhiteSpace(extractedValue))
                {
                    // Store in the correct slot and re-verify if it's an address
                    _engine.RawData.SetSlot(crossSlotTarget, extractedValue);
                    if (crossSlotTarget == "pickup")
                    {
                        _engine.ForceState(CollectionState.VerifyingPickup);
                    }
                    else if (crossSlotTarget == "destination")
                    {
                        _engine.ForceState(CollectionState.VerifyingDestination);
                    }
                    else
                    {
                        // Non-address slot — just advance normally
                        _engine.AdvanceToSlot(currentSlot);
                    }
                    EmitCurrentInstruction();
                    return;
                }
                else
                {
                    // Caller mentioned the slot but didn't provide a value — re-ask for it
                    Log($"[CrossSlot] No value extracted for '{crossSlotTarget}', re-asking");
                    _engine.ForceState(CallStateEngine.SlotToState(crossSlotTarget));
                    EmitCurrentInstruction();
                    return;
                }
            }
        }

        // Validate the input before accepting it as a slot value
        var validationError = SlotValidator.Validate(currentSlot, transcript);
        if (validationError != null)
        {
            // For passengers, try Ada's readback as authoritative source before rejecting.
            // Ada's LLM context understands "four" even when STT hears "poor".
            if (currentSlot == "passengers" && (validationError == "no_number_found" || validationError == "bare_passengers_no_number"))
            {
                var adaVerified = GetVerifiedPassengerCount(transcript);
                if (adaVerified != null)
                {
                    Log($"Passengers recovered from Ada readback: \"{transcript}\" → \"{adaVerified}\"");
                    transcript = adaVerified; // Override with Ada's interpretation
                    validationError = null;   // Clear rejection
                }
                else if (validationError == "bare_passengers_no_number")
                {
                    // Whisper dropped the number entirely (e.g., "four passengers" → "passengers.")
                    // Use a specific reprompt that asks Ada to confirm what she heard
                    Log($"Slot 'passengers' bare word detected: \"{transcript}\" — reprompting with number-specific ask");
                    EmitRepromptInstruction("bare_passengers_no_number");
                    return;
                }
            }

            if (validationError != null)
            {
                Log($"Slot '{currentSlot}' rejected: \"{transcript}\" (reason: {validationError})");
                EmitRepromptInstruction(validationError);
                return;
            }
        }

        var valueToStore = transcript;

        // Strip common name prefixes ("It's Max" → "Max", "My name is John" → "John")
        if (currentSlot == "name")
        {
            // Strip conversational filler ("All right, Trevor" → "Trevor", "Hi, it's Max" → "Max")
            valueToStore = System.Text.RegularExpressions.Regex.Replace(
                valueToStore,
                @"^(all\s*right\s*,?\s*|alright\s*,?\s*|hi\s*,?\s*|hello\s*,?\s*|hey\s*,?\s*|yeah\s*,?\s*|yes\s*,?\s*|oh\s*,?\s*|well\s*,?\s*|okay\s*,?\s*|ok\s*,?\s*|right\s*,?\s*|so\s*,?\s*|erm\s*,?\s*|um\s*,?\s*|uh\s*,?\s*)",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Strip common name prefixes ("It's Max" → "Max", "My name is John" → "John")
            valueToStore = System.Text.RegularExpressions.Regex.Replace(
                valueToStore,
                @"^(it'?s\s+|that'?s\s+|i'?m\s+|my\s+name\s+is\s+|i\s+am\s+|they\s+call\s+me\s+|call\s+me\s+|this\s+is\s+)",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim().TrimEnd('.', '!', ',');
            Log($"Name cleaned: \"{transcript}\" → \"{valueToStore}\"");
        }

        // Quick ASAP detection for pickup_time — full normalization happens in DirectBookingBuilder
        if (currentSlot == "pickup_time")
        {
            var lower = valueToStore.ToLowerInvariant();
            if (lower.Contains("now") || lower.Contains("asap") || lower.Contains("straight away") ||
                lower.Contains("right away") || lower.Contains("immediately") ||
                lower.Contains("ace up") || lower.Contains("as up") || lower.Contains("a sap") ||
                lower.Contains("just possible") || lower.Contains("that's just") ||
                lower.Contains("possible"))
            {
                Log($"Time detected as ASAP: \"{valueToStore}\"");
                valueToStore = "ASAP";
            }
        }

        // Normalize phonetic passenger homophones (e.g., "for passengers" → "4")
        if (currentSlot == "passengers")
        {
            var phoneticPassengerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "for", 4 }, { "fore", 4 }, { "pour", 4 }, { "poor", 4 },
                { "tree", 3 }, { "free", 3 }, { "really", 3 }, { "freely", 3 },
                { "to", 2 }, { "too", 2 }, { "tue", 2 },
                { "won", 1 }, { "wan", 1 },
                { "sex", 6 }, { "sax", 6 },
                { "ate", 8 }, { "ape", 8 },
                { "fife", 5 }, { "hive", 5 },
            };

            var paxWords = valueToStore.ToLowerInvariant()
                .TrimEnd('.', ',', '!', '?')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var w in paxWords)
            {
                if (phoneticPassengerMap.TryGetValue(w, out var num))
                {
                    Log($"Passengers phonetic fix: \"{valueToStore}\" → \"{num}\" (word: \"{w}\")");
                    valueToStore = $"{num}";
                    break;
                }
            }
        }

        // Resolve aliases ("home", "work", "the usual") for address slots
        var resolved = AliasResolver.TryResolve(currentSlot, valueToStore, _callerContext);
        if (resolved != null)
        {
            Log($"Alias resolved: \"{valueToStore}\" → \"{resolved.ResolvedAddress}\" (alias: {resolved.AliasName})");
            valueToStore = resolved.ResolvedAddress;
        }

        // Store raw (or alias-resolved) value for current slot
        var nextSlot = _engine.AcceptSlotValue(currentSlot, valueToStore);

        // For address slots, AcceptSlotValue transitions to VerifyingPickup/VerifyingDestination.
        // Run through burst-dispatch to get Gemini-cleaned version for Ada's readback,
        // then emit instruction. Geocoding is triggered AFTER Ada's readback arrives.
        if (_engine.State == CollectionState.VerifyingPickup ||
            _engine.State == CollectionState.VerifyingDestination)
        {
            // Try to get Gemini-cleaned version for better readback (non-blocking, best-effort)
            var addressField = _engine.State == CollectionState.VerifyingPickup ? "pickup" : "destination";
            if (_burstDispatcher != null &&
                string.IsNullOrWhiteSpace(_engine.RawData.GetGeminiSlot(addressField)))
            {
                try
                {
                    using var cleanCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var cleanResult = await _burstDispatcher.DispatchAsync(
                        valueToStore, phone: CallerId, ct: cleanCts.Token);
                    
                    if (cleanResult != null)
                    {
                        var cleaned = addressField == "pickup" ? cleanResult.Pickup : cleanResult.Destination;
                        // Fallback: if Gemini put it in the wrong field, check the other
                        cleaned ??= addressField == "pickup" ? cleanResult.Destination : cleanResult.Pickup;
                        
                        if (!string.IsNullOrWhiteSpace(cleaned))
                        {
                            _engine.RawData.SetGeminiSlot(addressField, cleaned);
                            Log($"[GeminiClean] {addressField}: \"{valueToStore}\" → \"{cleaned}\"");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[GeminiClean] Failed for {addressField}, Ada will use own interpretation: {ex.Message}");
                }
            }

            EmitCurrentInstruction(); // Instruction now uses Gemini-cleaned version if available
            return; // Geocoding will be triggered when Ada's readback arrives
        }

        if (nextSlot == null && _engine.State == CollectionState.ReadyForExtraction)
        {
            // All slots collected — trigger extraction
            await RunExtractionAsync(ct);
        }
        else
        {
            // When the name is processed via transcript fallback (no tool call),
            // the AI already asked for pickup in its autonomous response.
            // Emit the instruction silently (session.update only, no response.create)
            // to set the right context without causing a duplicate question.
            bool silent = currentSlot == "name"
                && _engine.State == CollectionState.CollectingPickup;
            EmitCurrentInstruction(silent: silent);
        }
    }

    /// <summary>
    /// Process Ada's spoken response transcript.
    /// Ada's interpretation is stored for extraction context only.
    /// Called from OpenAiRealtimeClient on response.audio_transcript.done.
    /// 
    /// NOTE: Ada's transcripts are accumulated and fed to DirectBookingBuilder
    /// as extraction context for time normalization reference.
    /// 
    /// ADDITIONALLY: When we're in VerifyingPickup/VerifyingDestination, Ada's readback
    /// triggers inline geocoding with BOTH the raw caller STT and Ada's interpretation.
    /// This gives the geocoder two signals to reconcile for maximum accuracy.
    /// </summary>
    public async Task ProcessAdaTranscriptAsync(string adaText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adaText)) return;

        // ── Hybrid correction tag detection ──
        // The AI prefixes corrections with [CORRECTION:slotname] based on conversational context.
        // This is more reliable than regex pattern matching on caller STT.
        var correctionMatch = System.Text.RegularExpressions.Regex.Match(
            adaText, @"^\[CORRECTION:(\w+)\]\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (correctionMatch.Success)
        {
            var correctionSlot = correctionMatch.Groups[1].Value.ToLowerInvariant();
            var correctionText = correctionMatch.Groups[2].Value.Trim();
            Log($"[HybridCorrection] Ada tagged correction for '{correctionSlot}': \"{correctionText}\"");
            
            // Validate slot name
            var validSlots = new HashSet<string> { "pickup", "destination", "name", "passengers", "pickup_time" };
            if (validSlots.Contains(correctionSlot))
            {
                // Extract the new value from what the caller actually said (use pending STT if available)
                var newValue = _pendingVerificationTranscript ?? correctionText;
                _pendingVerificationTranscript = null;
                _pendingVerificationState = null;
                
                Log($"[HybridCorrection] Correcting slot '{correctionSlot}' → \"{newValue}\"");
                await CorrectSlotAsync(correctionSlot, newValue, ct);
                return;
            }
            else
            {
                Log($"[HybridCorrection] Unknown slot '{correctionSlot}' — ignoring tag, processing normally");
                adaText = correctionText; // Strip invalid tag, process rest normally
            }
        }

        // ── Farewell filter: discard rogue farewell responses during active collection ──
        // When the AI hallucinates farewell text during verification/collection states,
        // we must NOT process it (it would trigger duplicate geocoding and cascading loops).
        if (_engine.State < CollectionState.ReadyForExtraction && IsFarewellText(adaText))
        {
            Log($"⚠️ Farewell transcript DISCARDED in {_engine.State}: \"{adaText[..Math.Min(60, adaText.Length)]}...\"");
            return;
        }

        // Store for extraction context (without correction tag)
        _adaTranscripts.Add(adaText);

        // ── Name refinement from Ada's readback ──
        if (!_nameRefined && _engine.State >= CollectionState.CollectingPickup &&
            _engine.State <= CollectionState.CollectingDestination &&
            !string.IsNullOrWhiteSpace(_engine.RawData.NameRaw))
        {
            Log($"[NameRefine] Checking Ada text (state={_engine.State}, transcripts={_adaTranscripts.Count}): \"{adaText}\"");
            TryRefineNameFromAda(adaText);
        }

        // ── Pending verification correction check (dual approach) ──
        if (_pendingVerificationTranscript != null && 
            (_engine.State == CollectionState.VerifyingPickup || _engine.State == CollectionState.VerifyingDestination))
        {
            var pendingRawStt = _pendingVerificationTranscript;
            var pendingState = _pendingVerificationState;
            _pendingVerificationTranscript = null;
            _pendingVerificationState = null;

            var verifyingSlot = pendingState == CollectionState.VerifyingPickup ? "pickup" : "destination";
            Log($"[DualCorrection] Processing pending verification: raw=\"{pendingRawStt}\", adaInterpretation=\"{adaText}\"");

            if (_burstDispatcher != null)
            {
                try
                {
                    var slotValues = new Dictionary<string, string>();
                    foreach (var slot in _engine.RawData.FilledSlots)
                        slotValues[slot] = _engine.RawData.GetSlot(slot) ?? "";

                    var adaContext = _adaTranscripts.Count > 1 ? _adaTranscripts[^2] : null;

                    var aiCorrection = await _burstDispatcher.DetectCorrectionAsync(
                        pendingRawStt, slotValues, ct,
                        adaContext: adaContext,
                        adaReadback: adaText);

                    if (aiCorrection != null)
                    {
                        Log($"[DualCorrection] ✅ Correction detected: {aiCorrection.SlotName} → \"{aiCorrection.NewValue}\"");
                        await CorrectSlotAsync(aiCorrection.SlotName, aiCorrection.NewValue, ct);
                        return;
                    }
                    else
                    {
                        Log($"[DualCorrection] No correction — caller confirmed {verifyingSlot}, proceeding with geocoding");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[DualCorrection] Gemini failed: {ex.Message} — falling back to treat as correction to {verifyingSlot}");
                    await CorrectSlotAsync(verifyingSlot, pendingRawStt, ct);
                    return;
                }
            }
            else
            {
                Log($"[DualCorrection] No Gemini — treating as correction to {verifyingSlot}");
                await CorrectSlotAsync(verifyingSlot, pendingRawStt, ct);
                return;
            }
        }

        // ── Geocode deduplication: prevent cascading geocode calls ──
        // When Ada generates multiple responses rapidly (hallucinated farewells),
        // each triggers ProcessAdaTranscriptAsync → RunInlineGeocodeAsync.
        // Only the FIRST call should proceed; subsequent ones are no-ops.
        if (_engine.State == CollectionState.VerifyingPickup)
        {
            // Always require an address-like readback before geocoding.
            // This prevents accidental fast-forward when Ada asks an unrelated question
            // (e.g., "What is your destination address?") during pickup re-verification.
            if (!LooksLikeAddressReadback(adaText, "pickup", _engine.RawData.PickupRaw))
            {
                Log($"⚠️ Ignoring non-readback AI transcript in VerifyingPickup: \"{adaText[..Math.Min(80, adaText.Length)]}\"");
                EmitCurrentInstruction();
                return;
            }

            if (_geocodeInFlight)
            {
                Log($"⚠️ Geocode already in flight for pickup — skipping duplicate trigger from: \"{adaText[..Math.Min(60, adaText.Length)]}\"");
                return;
            }
            _geocodeInFlight = true;
            // Inject SILENCE instruction to prevent Ada from speaking while geocoding runs
            OnAiInstruction?.Invoke(
                "[INSTRUCTION] ⚠️ ABSOLUTE SILENCE. Geocoding in progress. Do NOT speak. Do NOT ask any questions. Say NOTHING. Wait for the next instruction.",
                false, true);
            try
            {
                var rawAddress = _engine.RawData.PickupRaw ?? "";
                var rawTranscript = _engine.RawData.GetLastUtterance("pickup");
                Log($"Ada readback for pickup: \"{adaText}\" (raw STT: \"{rawAddress}\")");
                await RunInlineGeocodeAsync("pickup", rawAddress, ct, adaReadback: adaText, rawTranscript: rawTranscript);
            }
            finally { _geocodeInFlight = false; }
            return;
        }
        if (_engine.State == CollectionState.VerifyingDestination)
        {
            // Always require an address-like readback before geocoding.
            // This prevents accidental fast-forward when Ada asks an unrelated question.
            if (!LooksLikeAddressReadback(adaText, "destination", _engine.RawData.DestinationRaw))
            {
                Log($"⚠️ Ignoring non-readback AI transcript in VerifyingDestination: \"{adaText[..Math.Min(80, adaText.Length)]}\"");
                EmitCurrentInstruction();
                return;
            }

            if (_geocodeInFlight)
            {
                Log($"⚠️ Geocode already in flight for destination — skipping duplicate trigger from: \"{adaText[..Math.Min(60, adaText.Length)]}\"");
                return;
            }
            _geocodeInFlight = true;
            // Inject SILENCE instruction to prevent Ada from speaking while geocoding runs
            OnAiInstruction?.Invoke(
                "[INSTRUCTION] ⚠️ ABSOLUTE SILENCE. Geocoding in progress. Do NOT speak. Do NOT ask any questions. Say NOTHING. Wait for the next instruction.",
                false, true);
            try
            {
                var rawAddress = _engine.RawData.DestinationRaw ?? "";
                var rawTranscript = _engine.RawData.GetLastUtterance("destination");
                Log($"Ada readback for destination: \"{adaText}\" (raw STT: \"{rawAddress}\")");
                await RunInlineGeocodeAsync("destination", rawAddress, ct, adaReadback: adaText, rawTranscript: rawTranscript);
            }
            finally { _geocodeInFlight = false; }
            return;
        }
    }

    /// <summary>
    /// Detect whether Ada's transcript looks like an address readback (not a stale reprompt).
    /// Prevents stale "repeat your pickup" prompts from triggering verification geocoding.
    /// </summary>
    private static bool LooksLikeAddressReadback(string text, string field, string? rawAddress = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lower = text.ToLowerInvariant();

        // Known stale reprompt/question shapes that must NEVER be treated as readbacks
        if (lower.Contains("would you like to go ahead with this booking")) return false;
        if (field == "pickup")
        {
            if (lower.Contains("repeat your pickup address") ||
                lower.Contains("provide your pickup address") ||
                lower.Contains("pickup address please"))
                return false;
        }
        else
        {
            if (lower.Contains("repeat your destination address") ||
                lower.Contains("provide your destination address") ||
                lower.Contains("destination address please"))
                return false;
        }

        // Positive readback cues
        if (lower.Contains("let me just confirm")) return true;
        if (lower.Contains("let me confirm")) return true;
        if (lower.Contains("confirm the pickup")) return true;
        if (lower.Contains("confirm the destination")) return true;
        if (lower.Contains("confirm if the")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b(road|street|lane|avenue|drive|close|way|crescent|court|place|grove|terrace)\b")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b[a-z]{1,2}\d{1,2}\s*\d[a-z]{2}\b")) return true; // UK postcode
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b\d{1,5}[a-z]?\b")) return true; // house number

        // POI readback: if the raw address has no street tokens (it's a POI like "Sweet Spot"),
        // accept any transcript that contains the POI name — that IS the readback.
        if (!string.IsNullOrWhiteSpace(rawAddress))
        {
            var rawLower = rawAddress.ToLowerInvariant();
            var streetPattern = new System.Text.RegularExpressions.Regex(@"\b(road|rd|street|st|lane|ln|avenue|ave|drive|dr|close|way|crescent|court|place|grove|terrace|boulevard|blvd)\b");
            if (!streetPattern.IsMatch(rawLower))
            {
                // It's a POI — check if Ada mentioned the POI name in her readback
                var poiName = rawLower.Split(',')[0].Trim();
                if (poiName.Length >= 3 && lower.Contains(poiName))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detect hallucinated farewell/closing responses that should be discarded.
    /// These occur when the AI ignores instruction updates and generates stale context responses.
    /// </summary>
    private static bool IsFarewellText(string text)
    {
        var lower = text.ToLowerInvariant();
        // Quick keyword scan — these phrases NEVER appear in legitimate mid-call instructions
        return lower.Contains("have a great day") ||
               lower.Contains("have a safe journey") ||
               lower.Contains("have a good day") ||
               lower.Contains("safe travels") ||
               lower.Contains("your ride will be on its way") ||
               lower.Contains("your taxi is on its way") ||
               lower.Contains("booking confirmed") ||
               lower.Contains("ride will be on its way shortly") ||
               lower.Contains("thank you for confirming the details") ||
               lower.Contains("is there anything else you need") ||
               (lower.Contains("goodbye") && !lower.Contains("say goodbye")) ||
               (lower.Contains("you're welcome") && lower.Length < 60);
    }

    /// <summary>
    /// Extract the name Ada used in her response and correct NameRaw if it differs.
    /// Ada's LLM interpretation resolves STT garbles like "much" → "Max", "MUX" → "Max".
    /// Looks for patterns like "Thanks, Max", "Thank you, Max", "Nice to meet you, Max".
    /// </summary>
    private void TryRefineNameFromAda(string adaText)
    {
        var currentName = _engine.RawData.NameRaw;
        if (string.IsNullOrWhiteSpace(currentName)) return;

        var namePatterns = new[]
        {
            @"(?:[Tt]hank\s+you|[Tt]hanks|[Nn]ice\s+to\s+meet\s+you|[Hh]i|[Hh]ello),?\s+([A-Z][a-zA-Z]+)",
            @"(?:[Ss]o|[Oo][Kk]|[Aa]lright|[Gg]reat),?\s+([A-Z][a-zA-Z]+)[,.\s]",
        };

        foreach (var pattern in namePatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(adaText, pattern);
            if (match.Success)
            {
                var adaName = match.Groups[1].Value.Trim().TrimEnd('.', ',', '!');
                _nameRefined = true;

                var skipWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "Can", "Could", "Please", "Now", "For", "So", "What", "Where", "How", "And", "The" };
                if (skipWords.Contains(adaName))
                {
                    Log($"[NameRefine] Skipping non-name word: \"{adaName}\"");
                    continue;
                }

                // Only correct if Ada used a different name (case-insensitive compare)
                if (!string.Equals(adaName, currentName, StringComparison.OrdinalIgnoreCase) && adaName.Length >= 2)
                {
                    // ── Hallucination guard: require phonetic similarity ──
                    // Ada sometimes hallucinates completely unrelated names (e.g., "Mark" → "Prachi").
                    // Only accept refinement if the names share the same first letter OR
                    // have significant character overlap (≥40% of shorter name's chars).
                    if (!NamesAreSimilar(currentName, adaName))
                    {
                        Log($"[NameRefine] ⚠️ Hallucination blocked: \"{currentName}\" → \"{adaName}\" (no phonetic similarity)");
                        return;
                    }

                    Log($"📝 Name refined from Ada: \"{currentName}\" → \"{adaName}\"");
                    _engine.RawData.NameRaw = adaName;
                    return;
                }
                Log($"[NameRefine] Ada confirmed same name: \"{adaName}\"");
                return;
            }
        }
        Log($"[NameRefine] No name pattern matched in: \"{adaText[..Math.Min(80, adaText.Length)]}...\"");
    }

    /// <summary>
    /// Check if two names are phonetically similar enough to allow refinement.
    /// Returns true if: same first letter, OR ≥40% character overlap of the shorter name.
    /// This prevents Ada hallucinating completely unrelated names (e.g., "Mark" → "Prachi").
    /// </summary>
    private static bool NamesAreSimilar(string rawName, string adaName)
    {
        var a = rawName.ToLowerInvariant();
        var b = adaName.ToLowerInvariant();

        // Same first letter is a strong phonetic signal (e.g., "much" → "Max")
        if (a.Length > 0 && b.Length > 0 && a[0] == b[0])
            return true;

        // Character overlap: count shared characters
        var shorter = a.Length <= b.Length ? a : b;
        var longer = a.Length <= b.Length ? b : a;
        var sharedCount = 0;
        var longerChars = new List<char>(longer);
        foreach (var c in shorter)
        {
            var idx = longerChars.IndexOf(c);
            if (idx >= 0)
            {
                sharedCount++;
                longerChars.RemoveAt(idx); // prevent double-counting
            }
        }

        // ≥40% overlap of shorter name
        return shorter.Length > 0 && (double)sharedCount / shorter.Length >= 0.4;
    }

    /// <summary>
    /// Synchronous version for backward compatibility — fire-and-forget the async version.
    /// </summary>
    public void ProcessAdaTranscript(string adaText)
    {
        if (string.IsNullOrWhiteSpace(adaText)) return;
        _ = Task.Run(() => ProcessAdaTranscriptAsync(adaText));
    }

    /// <summary>
    /// Get Ada's accumulated transcript for extraction context.
    /// </summary>
    public string GetAdaTranscriptContext()
    {
        return string.Join("\n", _adaTranscripts.TakeLast(10)); // last 10 responses
    }

    /// <summary>
    /// Correct a specific slot by name.
    /// For address slots (pickup/destination), routes through verification → inline geocoding.
    /// </summary>
    public async Task CorrectSlotAsync(string slotName, string newValue, CancellationToken ct = default)
    {
        // Strip trailing politeness from correction values ("sweetspot, please?" → "sweetspot")
        newValue = System.Text.RegularExpressions.Regex.Replace(
            newValue,
            @"[,.]?\s*(?:please|thanks|thank you|cheers|ta)[?.!]*$",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim().TrimEnd('.', ',', '?', '!');

        // If the caller said "can I change the pickup" without providing a new value,
        // clear the slot and re-collect it — don't verify/geocode an empty string
        if (string.IsNullOrWhiteSpace(newValue) && slotName is "pickup" or "destination")
        {
            Log($"Change request for {slotName} with no new value — clearing slot and re-collecting");
            _engine.RawData.SetSlot(slotName, "");
            _engine.ClearFareResult();
            _engine.ClearVerifiedAddress(slotName);

            // Force to CollectingPickup/CollectingDestination so Ada asks for the new address
            if (slotName == "pickup")
                _engine.ForceState(CollectionState.CollectingPickup);
            else
                _engine.ForceState(CollectionState.CollectingDestination);

            EmitCurrentInstruction();
            return;
        }

        var oldValue = _engine.RawData.GetSlot(slotName);
        _engine.CorrectSlot(slotName, newValue);

        // Track which slot changed for update extraction
        _changedSlots.Add(slotName);

        // For address corrections, route through verification to trigger inline geocoding
        // (address-dispatch + Gemini reconciler) instead of skipping straight to extraction
        if (slotName is "pickup" or "destination")
        {
            Log($"Address correction for {slotName}: \"{oldValue}\" → \"{newValue}\" — routing through verification");

            // Reset clarification counter — fresh correction, not a continuation
            _clarificationAttempts = 0;

            // Truncate conversation to prevent AI from repeating stale questions
            if (OnTruncateConversation != null)
            {
                try { await OnTruncateConversation.Invoke(); }
                catch (Exception ex) { Log($"⚠️ Truncation failed: {ex.Message}"); }
            }

            // Clear stale fare, verified address, and Gemini slot for this slot
            _engine.ClearFareResult();
            _engine.ClearVerifiedAddress(slotName);
            _engine.RawData.SetGeminiSlot(slotName, null);
            _engine.IsRecalculating = true;

            // Enable typing sounds to keep the line alive during recalculation
            OnTypingSoundsChanged?.Invoke(true);

            // Transition engine to the appropriate verifying state
            if (slotName == "pickup")
                _engine.ForceState(CollectionState.VerifyingPickup);
            else
                _engine.ForceState(CollectionState.VerifyingDestination);

            // Emit readback instruction (Ada will read back, then ProcessAdaTranscriptAsync triggers geocoding)
            EmitCurrentInstruction();
            return;
        }

        // Non-address correction during/after fare presentation (e.g. passengers changed
        // while PresentingFare) — must re-extract → re-geocode → re-present fare → await confirmation.
        // Without this, the AI generates from stale context and says "taxi on the way" without confirmation.
        if (_engine.State >= CollectionState.ReadyForExtraction &&
            _engine.State < CollectionState.Dispatched)
        {
            Log($"Non-address correction ({slotName}) during fare flow (state={_engine.State}) — re-extracting");
            _engine.ClearFareResult();
            await RunExtractionAsync(ct);
            return;
        }

        var instruction = PromptBuilder.BuildCorrectionInstruction(
            slotName, oldValue ?? "", newValue);
        OnAiInstruction?.Invoke(instruction, false, false);

        // Re-check if ready for extraction after correction
        if (_engine.RawData.AllRequiredPresent &&
            _engine.State < CollectionState.ReadyForExtraction)
        {
            // If we already have a structured result, use update extraction
            if (_engine.StructuredResult != null && _changedSlots.Count > 0)
                await RunUpdateExtractionAsync(ct);
            else
                await RunExtractionAsync(ct);
        }
    }

    /// <summary>
    /// Accept payment choice and advance.
    /// </summary>
    public void AcceptPayment(string method)
    {
        _engine.AcceptPaymentChoice(method);
        EmitCurrentInstruction();
    }

    /// <summary>
    /// Confirm the booking and dispatch — sends to iCabbi if enabled.
    /// </summary>
    public async Task ConfirmBookingAsync(CancellationToken ct = default)
    {
        // Advance through intermediate states if we're still at PresentingFare
        // (payment choice is currently skipped — go straight to confirmation)
        if (_engine.State == CollectionState.PresentingFare)
        {
            _engine.AcceptPaymentChoice("meter"); // Default to meter when skipping payment step
        }
        _engine.ConfirmBooking();

        // Dispatch to iCabbi if enabled
        if (_icabbiService != null && _engine.StructuredResult != null && _engine.FareResult != null)
        {
            Log("Dispatching booking to iCabbi...");
            try
            {
                _icabbiResult = await _icabbiService.CreateAndDispatchAsync(
                    _engine.StructuredResult,
                    _engine.FareResult,
                    CallerId,
                    _callerContext?.CallerName,
                    ct: ct);

                if (_icabbiResult.Success)
                {
                    Log($"✅ iCabbi booking dispatched — Journey: {_icabbiResult.JourneyId}, TripId: {_icabbiResult.TripId}, Tracking: {_icabbiResult.TrackingUrl}");
                }
                else
                {
                    Log($"⚠️ iCabbi dispatch failed: {_icabbiResult.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ iCabbi dispatch error: {ex.Message}");
            }
        }

        EmitCurrentInstruction();
    }

    /// <summary>
    /// Synchronous ConfirmBooking for backward compatibility — fires async dispatch.
    /// </summary>
    public void ConfirmBooking()
    {
        if (_engine.State == CollectionState.PresentingFare)
        {
            _engine.AcceptPaymentChoice("meter");
        }
        _engine.ConfirmBooking();
        
        // Fire-and-forget iCabbi dispatch
        if (_icabbiService != null && _engine.StructuredResult != null && _engine.FareResult != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _icabbiResult = await _icabbiService.CreateAndDispatchAsync(
                        _engine.StructuredResult,
                        _engine.FareResult,
                        CallerId,
                        _callerContext?.CallerName);

                    Log(_icabbiResult.Success
                        ? $"✅ iCabbi dispatched — Journey: {_icabbiResult.JourneyId}"
                        : $"⚠️ iCabbi failed: {_icabbiResult.Message}");
                }
                catch (Exception ex) { Log($"❌ iCabbi error: {ex.Message}"); }
            });
        }

        EmitCurrentInstruction();
    }

    /// <summary>Get the iCabbi booking result (if dispatched).</summary>
    public IcabbiBookingResult? IcabbiResult => _icabbiResult;

    /// <summary>
    /// End the call. Only forces ending if the caller explicitly hangs up.
    /// Otherwise, the engine will block if slots are still missing.
    /// </summary>
    public void EndCall(bool force = false)
    {
        _engine.EndCall(force: force);
        EmitCurrentInstruction();
    }

    /// <summary>
    /// Get the system prompt for the AI voice interface.
    /// </summary>
    public string GetSystemPrompt() => PromptBuilder.BuildSystemPrompt(_companyName, _callerContext);

    // ─── Tool Call Handling (Hybrid Freeform Extraction) ────

    /// <summary>
    /// Handle a tool call from the Realtime API.
    /// Currently supports sync_booking_data for freeform/burst extraction.
    /// The deterministic engine remains the authority for state transitions.
    /// </summary>
    public async Task<object> HandleToolCallAsync(string toolName, Dictionary<string, object?> args, CancellationToken ct = default)
    {
        Log($"🔧 HandleToolCallAsync: {toolName} ({args.Count} args)");

        return toolName switch
        {
            "sync_booking_data" => await HandleSyncBookingDataAsync(args, ct),
            _ => new { status = "error", message = $"Unknown tool: {toolName}" }
        };
    }

    /// <summary>
    /// Process sync_booking_data tool call from the Realtime API.
    /// Routes extracted fields to the engine, replacing burst-dispatch for freeform input.
    /// The engine remains the authority — this just populates raw slots.
    /// </summary>
    private async Task<object> HandleSyncBookingDataAsync(Dictionary<string, object?> args, CancellationToken ct)
    {
        // Cancel no-reply watchdog — the AI just processed caller input
        CancelNoReplyWatchdog();
        _noReplyCount = 0;

        // ── INTENT-DRIVEN ROUTING ──────────────────────────────────
        // The AI signals caller intent via the 'intent' parameter.
        // This lets us route corrections, confirmations, and cancellations
        // BEFORE doing any slot processing — preventing cross-contamination.
        string? intent = null;
        if (TryGetArg(args, "intent", out var intentRaw))
        {
            intent = intentRaw.ToLowerInvariant();
            Log($"[SyncTool] intent=\"{intent}\"");
        }

        // ── EXISTING BOOKING MANAGEMENT ──────────────────────────────
        // When in ManagingExistingBooking or AwaitingCancelConfirmation, route intents accordingly.
        if (_engine.State == CollectionState.ManagingExistingBooking)
        {
            var interpLower = "";
            if (TryGetArg(args, "interpretation", out var interpVal))
                interpLower = interpVal.ToLowerInvariant();
            var lastUttLower = "";
            if (TryGetArg(args, "last_utterance", out var luVal))
                lastUttLower = luVal.ToLowerInvariant();

            var combined = interpLower + " " + lastUttLower;

            if (intent == "cancel_booking" || combined.Contains("cancel") || combined.Contains("get rid"))
            {
                Log("[SyncTool] ManagingExistingBooking → requesting cancel confirmation");
                _engine.RequestCancelConfirmation();
                EmitCurrentInstruction();
                return BuildSyncResponse("ok", managingBooking: true);
            }

            if (combined.Contains("status") || combined.Contains("where is") || combined.Contains("how long") ||
                combined.Contains("driver") || combined.Contains("eta"))
            {
                Log("[SyncTool] ManagingExistingBooking → status query");
                var statusMsg = $"[INSTRUCTION] The booking status is: {_activeBooking?.Status ?? "active"}. " +
                    $"Pickup: {_activeBooking?.Pickup}, Destination: {_activeBooking?.Destination}. " +
                    "Ask if they need anything else.";
                OnAiInstruction?.Invoke(statusMsg, false, false);
                return BuildSyncResponse("ok", managingBooking: true);
            }

            if (combined.Contains("change") || combined.Contains("amend") || combined.Contains("update") ||
                combined.Contains("different"))
            {
                Log("[SyncTool] ManagingExistingBooking → amend flow (starting new booking with current data)");
                // Pre-fill from existing booking then start collection
                if (_activeBooking != null)
                {
                    if (!string.IsNullOrEmpty(_callerContext?.CallerName))
                        _engine.RawData.SetSlot("name", _callerContext.CallerName);
                }
                _engine.StartNewBookingFromManaging();
                var instruction = "[INSTRUCTION] The caller wants to make changes to their booking. " +
                    "Ask what they'd like to change: pickup, destination, passengers, or time.";
                OnAiInstruction?.Invoke(instruction, false, false);
                return BuildSyncResponse("ok");
            }

            if (combined.Contains("new") || combined.Contains("fresh") || combined.Contains("another"))
            {
                Log("[SyncTool] ManagingExistingBooking → new booking");
                if (!string.IsNullOrEmpty(_callerContext?.CallerName))
                    _engine.RawData.SetSlot("name", _callerContext.CallerName);
                _engine.StartNewBookingFromManaging();
                EmitCurrentInstruction();
                return BuildSyncResponse("ok");
            }

            // Unrecognized — re-prompt
            Log("[SyncTool] ManagingExistingBooking → unrecognized intent, re-prompting");
            EmitCurrentInstruction();
            return BuildSyncResponse("ok", managingBooking: true);
        }

        if (_engine.State == CollectionState.AwaitingCancelConfirmation)
        {
            var interpLower = "";
            if (TryGetArg(args, "interpretation", out var interpVal2))
                interpLower = interpVal2.ToLowerInvariant();
            var lastUttLower = "";
            if (TryGetArg(args, "last_utterance", out var luVal2))
                lastUttLower = luVal2.ToLowerInvariant();

            var combined = interpLower + " " + lastUttLower;

            if (_engine.IsCancelConfirmationExpired())
            {
                Log("[SyncTool] Cancel confirmation EXPIRED — returning to managing");
                _engine.ClearCancelConfirmation();
                _engine.ForceState(CollectionState.ManagingExistingBooking);
                OnAiInstruction?.Invoke("[INSTRUCTION] The confirmation timed out. Ask the caller again what they'd like to do.", false, false);
                return BuildSyncResponse("ok", managingBooking: true);
            }

            bool hasYes = combined.Contains("yes") || combined.Contains("confirm") || combined.Contains("go ahead") ||
                combined.Contains("sure") || combined.Contains("definitely");
            bool hasNo = combined.Contains("no") || combined.Contains("never mind") || combined.Contains("keep") ||
                combined.Contains("don't cancel") || combined.Contains("changed my mind");

            if (hasYes && !hasNo)
            {
                Log("[SyncTool] Cancel confirmed — cancelling booking");
                _engine.ClearCancelConfirmation();

                // Cancel iCabbi journey if available
                if (!string.IsNullOrEmpty(_engine.ExistingIcabbiJourneyId) && _icabbiService != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var (ok, msg) = await _icabbiService.CancelBookingAsync(_engine.ExistingIcabbiJourneyId);
                            Log(ok ? $"✅ iCabbi journey {_engine.ExistingIcabbiJourneyId} cancelled"
                                   : $"⚠️ iCabbi cancel failed: {msg}");
                        }
                        catch (Exception ex) { Log($"❌ iCabbi cancel error: {ex.Message}"); }
                    });
                }

                // Update booking status in Supabase (fire-and-forget)
                if (!string.IsNullOrEmpty(_engine.ExistingBookingId))
                {
                    Log($"📋 Booking {_engine.ExistingBookingId} cancelled");
                }

                _engine.HasActiveBooking = false;
                var instruction = "[INSTRUCTION] Tell the caller their booking has been cancelled. Ask if they'd like to make a new booking.";
                OnAiInstruction?.Invoke(instruction, false, false);
                return BuildSyncResponse("cancelled");
            }

            if (hasNo)
            {
                Log("[SyncTool] Cancel rejected — returning to managing");
                _engine.ClearCancelConfirmation();
                _engine.ForceState(CollectionState.ManagingExistingBooking);
                OnAiInstruction?.Invoke("[INSTRUCTION] The caller decided not to cancel. Ask what else they'd like to do.", false, false);
                return BuildSyncResponse("ok", managingBooking: true);
            }

            // Re-prompt for confirmation
            EmitCurrentInstruction();
            return BuildSyncResponse("ok", managingBooking: true);
        }

        // Handle explicit confirmation intent
        if (intent == "confirm_booking")
        {
            if (_engine.State is CollectionState.PresentingFare or CollectionState.AwaitingPaymentChoice or CollectionState.AwaitingConfirmation)
            {
                Log($"[SyncTool] Intent=confirm_booking in {_engine.State} — dispatching");
                await ConfirmBookingAsync(ct);
                return BuildSyncResponse("confirmed");
            }
            Log($"[SyncTool] Intent=confirm_booking ignored — state {_engine.State} not confirmable");
        }

        // Handle explicit rejection during fare presentation
        if (intent == "reject_booking" || intent == "reject_fare")
        {
            if (_engine.State is CollectionState.PresentingFare or CollectionState.AwaitingPaymentChoice or CollectionState.AwaitingConfirmation)
            {
                Log($"[SyncTool] Intent={intent} in {_engine.State} — ending call gracefully");
                _engine.EndCall(force: true);
                return BuildSyncResponse("cancelled");
            }
        }

        // Handle explicit cancellation intent
        if (intent == "cancel_booking")
        {
            Log($"[SyncTool] Intent=cancel_booking — ending call");
            // Cancel active iCabbi journey if one was dispatched
            if (_icabbiResult?.Success == true && !string.IsNullOrEmpty(_icabbiResult.JourneyId))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (ok, msg) = await _icabbiService!.CancelBookingAsync(_icabbiResult.JourneyId);
                        Log(ok
                            ? $"✅ iCabbi journey {_icabbiResult.JourneyId} cancelled"
                            : $"⚠️ iCabbi cancel failed: {msg}");
                    }
                    catch (Exception ex) { Log($"❌ iCabbi cancel error: {ex.Message}"); }
                });
            }
            _engine.EndCall(force: true);
            return BuildSyncResponse("cancelled");
        }

        // Handle update_field intent: if the AI signals a correction for a field
        // that doesn't match the current collection state, route it directly
        // to the correct slot WITHOUT contaminating the current state.
        if (intent == "update_field")
        {
            var targetSlot = DetectIntentTargetSlot(args);
            if (targetSlot != null)
            {
                var targetState = CallStateEngine.SlotToState(targetSlot);
                var currentSlotState = _engine.State;

                // Only do intent-jump if the target is different from what we're currently collecting
                if (targetState != currentSlotState &&
                    !IsCurrentlyCollecting(currentSlotState, targetSlot))
                {
                    Log($"[SyncTool] Intent-jump: {currentSlotState} → {targetSlot} (AI detected out-of-order correction)");

                    // Reset clarification counter — this is a NEW correction, not a continuation
                    _clarificationAttempts = 0;

                    // ── CONTEXT TRUNCATION ──
                    // Physically reset the AI's conversation memory so it doesn't
                    // "remember" its previous question (e.g., asking for passengers)
                    // when the caller just said "change the pickup."
                    if (OnTruncateConversation != null)
                    {
                        try { await OnTruncateConversation.Invoke(); }
                        catch (Exception ex) { Log($"⚠️ Truncation failed: {ex.Message}"); }
                    }

                    // Extract the value for the target slot
                    if (TryGetArg(args, targetSlot, out var newValue) && !string.IsNullOrWhiteSpace(newValue))
                    {
                        // Clear downstream verification for the changed slot
                        _engine.ClearVerifiedAddress(targetSlot);
                        _engine.ClearFareResult();
                        _engine.IsRecalculating = true;
                        _engine.RawData.SetSlot(targetSlot, newValue);

                        // Store last_utterance for POI matching
                        string? whisperT = null;
                        TryGetArg(args, "whisper_transcript", out whisperT);
                        string? lastUtt = null;
                        TryGetArg(args, "last_utterance", out lastUtt);
                        var rawForPoi = whisperT ?? lastUtt;
                        if (!string.IsNullOrWhiteSpace(rawForPoi))
                            _engine.RawData.SetLastUtterance(targetSlot, rawForPoi);

                        if (TryGetArg(args, "interpretation", out var interp2))
                            Log($"[SyncTool] interpretation=\"{interp2}\"");
                        if (!string.IsNullOrWhiteSpace(lastUtt))
                            Log($"[SyncTool] last_utterance=\"{lastUtt}\"");
                        if (!string.IsNullOrWhiteSpace(whisperT) && whisperT != lastUtt)
                            Log($"[SyncTool] whisper_transcript=\"{whisperT}\" (differs from AI last_utterance)");

                        // Jump to verification state for address slots
                        if (targetSlot is "pickup" or "destination")
                        {
                            var verifyState = targetSlot == "pickup"
                                ? CollectionState.VerifyingPickup
                                : CollectionState.VerifyingDestination;
                            _engine.ForceState(verifyState);
                            EmitCurrentInstruction();
                            return BuildSyncResponse("ok", new List<string> { targetSlot });
                        }

                        // Non-address slot: just update and re-advance
                        _engine.AdvanceToSlot(_engine.RawData.NextMissingSlot() ?? targetSlot);
                        EmitCurrentInstruction();
                        return BuildSyncResponse("ok", new List<string> { targetSlot });
                    }
                    else
                    {
                        // update_field with no value: caller said "change the pickup" but gave no new address
                        Log($"[SyncTool] Intent-jump to {targetSlot} with no value — clearing and re-collecting");
                        await CorrectSlotAsync(targetSlot, "", ct);
                        return BuildSyncResponse("ok", new List<string> { targetSlot });
                    }
                }
            }
        }

        // ── CLARIFICATION STATE GUARD ──────────────────────────────
        // When engine is in AwaitingClarification, the ONLY valid input is
        // clarification data (area, postcode) for the ambiguous address.
        // Reject any slot updates that aren't the field being clarified —
        // the AI is ignoring our instruction and advancing prematurely.
        if (_engine.State == CollectionState.AwaitingClarification)
        {
            var clarField = _engine.PendingClarification?.AmbiguousField;
            Log($"[SyncTool] AwaitingClarification for '{clarField}' — checking if tool call is relevant");
            
            // Check if the AI is trying to fill a DIFFERENT slot (passengers, time, etc.)
            bool hasRelevantUpdate = false;
            if (clarField == "pickup" && TryGetArg(args, "pickup", out var clPickup) && !string.IsNullOrWhiteSpace(clPickup))
                hasRelevantUpdate = true;
            if (clarField == "destination" && TryGetArg(args, "destination", out var clDest) && !string.IsNullOrWhiteSpace(clDest))
                hasRelevantUpdate = true;
            
            // Also accept caller_area as clarification data
            if (TryGetArg(args, "caller_area", out var clArea) && !string.IsNullOrWhiteSpace(clArea))
                hasRelevantUpdate = true;
            
            if (!hasRelevantUpdate)
            {
                // AI ignored the clarification instruction and tried to advance.
                // Reject ALL slot updates and re-emit the clarification instruction.
                Log($"⛔ [SyncTool] REJECTED — AI tried to fill slots while AwaitingClarification for '{clarField}'. Re-emitting clarification instruction.");
                EmitCurrentInstruction();
                return BuildSyncResponse("awaiting_clarification", null);
            }
        }

        var slotsUpdated = new List<string>();
        var currentState = _engine.State;

        // Extract and store each provided field
        if (TryGetArg(args, "caller_name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            // Strip conversational filler (same as ProcessCallerResponseAsync)
            name = System.Text.RegularExpressions.Regex.Replace(name,
                @"^(it'?s\s+|that'?s\s+|i'?m\s+|my\s+name\s+is\s+|call\s+me\s+|this\s+is\s+)",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim().TrimEnd('.', '!', ',');

            if (string.IsNullOrWhiteSpace(_engine.RawData.NameRaw) || !string.Equals(_engine.RawData.NameRaw, name, StringComparison.OrdinalIgnoreCase))
            {
                _engine.RawData.SetSlot("name", name);
                slotsUpdated.Add("name");
                Log($"[SyncTool] name=\"{name}\"");
            }
        }

        if (TryGetArg(args, "pickup", out var pickup) && !string.IsNullOrWhiteSpace(pickup))
        {
            _engine.RawData.SetSlot("pickup", pickup);
            slotsUpdated.Add("pickup");
            Log($"[SyncTool] pickup=\"{pickup}\"");
        }

        if (TryGetArg(args, "destination", out var dest) && !string.IsNullOrWhiteSpace(dest))
        {
            _engine.RawData.SetSlot("destination", dest);
            slotsUpdated.Add("destination");
            Log($"[SyncTool] destination=\"{dest}\"");
        }

        if (TryGetArg(args, "passengers", out var paxStr) && int.TryParse(paxStr, out var pax))
        {
            _engine.RawData.SetSlot("passengers", pax.ToString());
            slotsUpdated.Add("passengers");
            Log($"[SyncTool] passengers={pax}");
        }

        if (TryGetArg(args, "pickup_time", out var time) && !string.IsNullOrWhiteSpace(time))
        {
            // Quick ASAP detection
            var lower = time.ToLowerInvariant();
            if (lower.Contains("now") || lower.Contains("asap") || lower.Contains("straight away") ||
                lower.Contains("right away") || lower.Contains("immediately"))
                time = "ASAP";

            _engine.RawData.SetSlot("pickup_time", time);
            slotsUpdated.Add("pickup_time");
            Log($"[SyncTool] pickup_time=\"{time}\"");
        }

        if (TryGetArg(args, "caller_area", out var area) && !string.IsNullOrWhiteSpace(area))
        {
            Log($"[SyncTool] caller_area=\"{area}\" (stored as context)");
            // caller_area is informational — no raw slot, but could be used for geocoding bias
        }

        if (TryGetArg(args, "interpretation", out var interp) && !string.IsNullOrWhiteSpace(interp))
        {
            Log($"[SyncTool] interpretation=\"{interp}\"");
        }

        // FIX 3: Log last_utterance for forensic traceability
        string? lastUtterance = null;
        if (TryGetArg(args, "last_utterance", out var utterance) && !string.IsNullOrWhiteSpace(utterance))
        {
            lastUtterance = utterance;
            Log($"[SyncTool] last_utterance=\"{utterance}\"");
        }

        // Use the REAL Whisper transcript (injected by OpenAiRealtimeClient) for POI matching,
        // NOT the AI's last_utterance which may be garbled (e.g., "Akis" → "Arcades").
        // Fall back to last_utterance if whisper_transcript is unavailable.
        string? whisperTranscript = null;
        if (TryGetArg(args, "whisper_transcript", out var wt) && !string.IsNullOrWhiteSpace(wt))
        {
            whisperTranscript = wt;
            if (whisperTranscript != lastUtterance)
                Log($"[SyncTool] whisper_transcript=\"{whisperTranscript}\" (differs from AI last_utterance)");
        }

        // Log Ada's last spoken transcript if injected (for debugging readback vs caller speech)
        if (TryGetArg(args, "ada_transcript", out var adaT) && !string.IsNullOrWhiteSpace(adaT))
        {
            Log($"[SyncTool] ada_transcript=\"{adaT}\"");
        }

        // Whisper transcript is preferred because the AI often garbles POI names.
        var rawTranscriptForPoi = whisperTranscript ?? lastUtterance;
        if (!string.IsNullOrWhiteSpace(rawTranscriptForPoi))
        {
            if (slotsUpdated.Contains("pickup"))
                _engine.RawData.SetLastUtterance("pickup", rawTranscriptForPoi);
            if (slotsUpdated.Contains("destination"))
                _engine.RawData.SetLastUtterance("destination", rawTranscriptForPoi);
        }

        // ── PROACTIVE TOOL-TRANSCRIPT COHERENCE CHECK ──────────────────
        // Verify tool argument values against the transcript that triggered them.
        // IMPORTANT: prefer Whisper transcript over AI last_utterance because
        // last_utterance can be paraphrased/hallucinated by the model.
        //
        // SKIP during AwaitingClarification: the caller provides SUPPLEMENTAL info
        // (e.g., "It's in Coventry") which the AI correctly appends to the existing
        // address. The transcript won't match the full combined address — that's expected.
        var coherenceTranscript = whisperTranscript ?? lastUtterance;
        var coherenceSource = whisperTranscript != null ? "whisper_transcript" : "last_utterance";
        var skipCoherence = _engine.CurrentState == CollectionState.AwaitingClarification;

        if (!string.IsNullOrWhiteSpace(coherenceTranscript) && !skipCoherence)
        {
            if (slotsUpdated.Contains("pickup"))
            {
                var pickupVal = _engine.RawData.PickupRaw ?? "";
                if (!ToolAddressMatchesTranscript(pickupVal, coherenceTranscript))
                {
                    Log($"🚨 TOOL-TRANSCRIPT MISMATCH (pickup) [{coherenceSource}] — tool=\"{pickupVal}\" transcript=\"{coherenceTranscript}\"");
                    _engine.HardClearVerifiedAddress("pickup");
                    _engine.ClearFareResult();
                    _engine.RawData.SetSlot("pickup", null!); // Clear stale value
                    slotsUpdated.Remove("pickup");
                    _engine.ForceState(CollectionState.CollectingPickup);
                    EmitCurrentInstruction();
                    return BuildSyncResponse("tool_transcript_mismatch", slotsUpdated);
                }
            }
            if (slotsUpdated.Contains("destination"))
            {
                var destVal = _engine.RawData.DestinationRaw ?? "";
                if (!ToolAddressMatchesTranscript(destVal, coherenceTranscript))
                {
                    Log($"🚨 TOOL-TRANSCRIPT MISMATCH (destination) [{coherenceSource}] — tool=\"{destVal}\" transcript=\"{coherenceTranscript}\"");
                    _engine.HardClearVerifiedAddress("destination");
                    _engine.ClearFareResult();
                    _engine.RawData.SetSlot("destination", null!); // Clear stale value
                    slotsUpdated.Remove("destination");
                    _engine.ForceState(CollectionState.CollectingDestination);
                    EmitCurrentInstruction();
                    return BuildSyncResponse("tool_transcript_mismatch", slotsUpdated);
                }
            }
        }
        else if (skipCoherence)
        {
            Log($"📋 Coherence check skipped — AwaitingClarification (caller providing supplemental info)");
        }

        if (slotsUpdated.Count == 0)
        {
            // ── CONFIRMATION VIA TOOL CALL ──────────────────────────────
            // The AI sometimes calls sync_booking_data with interpretation="Customer confirmed"
            // but no actual slot fields. Detect confirmation intent and dispatch.
            if (_engine.State is CollectionState.PresentingFare or CollectionState.AwaitingPaymentChoice or CollectionState.AwaitingConfirmation)
            {
                var interpLower = (interp ?? "").ToLowerInvariant();
                var lastUtteranceLower = (args.TryGetValue("last_utterance", out var lu) ? lu?.ToString() ?? "" : "").ToLowerInvariant();

                // Check for cancellation/rejection signals FIRST — these override confirmation keywords
                bool hasCancelSignal = interpLower.Contains("cancel") || interpLower.Contains("reject") ||
                    interpLower.Contains("never mind") || interpLower.Contains("no thank") ||
                    lastUtteranceLower.Contains("cancel") || lastUtteranceLower.Contains("never mind") ||
                    lastUtteranceLower.Contains("no thank") || lastUtteranceLower.Contains("don't want");

                bool hasConfirmSignal = interpLower.Contains("confirm") || interpLower.Contains("yes") ||
                    interpLower.Contains("go ahead") || interpLower.Contains("book") || interpLower.Contains("agreed");

                if (hasConfirmSignal && !hasCancelSignal)
                {
                    Log($"[SyncTool] Confirmation detected via interpretation in {_engine.State} — dispatching");
                    await ConfirmBookingAsync(ct);
                    return BuildSyncResponse("confirmed");
                }

                if (hasCancelSignal)
                {
                    Log($"[SyncTool] Cancellation detected via interpretation in {_engine.State} — NOT dispatching");

                    // Cancel active iCabbi journey if one was dispatched
                    if (_icabbiResult?.Success == true && !string.IsNullOrEmpty(_icabbiResult.JourneyId))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var (ok, msg) = await _icabbiService!.CancelBookingAsync(_icabbiResult.JourneyId);
                                Log(ok
                                    ? $"✅ iCabbi journey {_icabbiResult.JourneyId} cancelled"
                                    : $"⚠️ iCabbi cancel failed: {msg}");
                            }
                            catch (Exception ex) { Log($"❌ iCabbi cancel error: {ex.Message}"); }
                        });
                    }

                    _engine.EndCall(force: true);
                    return BuildSyncResponse("cancelled");
                }
            }

            Log("[SyncTool] No slots updated — returning current state");
            return BuildSyncResponse("no_change");
        }

        // Mark as multi-slot burst if >1 field extracted
        if (slotsUpdated.Count > 1)
        {
            _engine.RawData.IsMultiSlotBurst = true;
            Log($"[SyncTool] Multi-slot burst: {string.Join(", ", slotsUpdated)}");
        }

        // ── SAME-ADDRESS GUARD ──────────────────────────────────────
        // If pickup and destination end up identical after this sync, the AI
        // likely cross-contaminated the slots (e.g. put a pickup correction
        // into the destination field). Detect and recover.
        // SKIP if both were provided in a burst — caller genuinely said the same thing twice,
        // which is their mistake and we should ask for clarification on the destination.
        {
            var currentPickup = (_engine.RawData.PickupRaw ?? "").Trim();
            var currentDest = (_engine.RawData.DestinationRaw ?? "").Trim();
            if (!string.IsNullOrEmpty(currentPickup) && !string.IsNullOrEmpty(currentDest) &&
                string.Equals(currentPickup, currentDest, StringComparison.OrdinalIgnoreCase))
            {
                // Determine which slot was just updated — that's the contaminated one.
                // If both were updated in same burst, clear destination (more likely error).
                // If only one was updated, clear that one (it was written to match the other).
                string contaminatedSlot;
                if (slotsUpdated.Contains("pickup") && slotsUpdated.Contains("destination"))
                    contaminatedSlot = "destination"; // burst: clear dest, keep pickup
                else if (slotsUpdated.Contains("destination"))
                    contaminatedSlot = "destination";
                else
                    contaminatedSlot = "pickup";

                var keepSlot = contaminatedSlot == "destination" ? "pickup" : "destination";
                Log($"⚠ [SyncTool] SAME-ADDRESS GUARD — pickup and destination are identical (\"{currentPickup}\"). " +
                    $"Clearing {contaminatedSlot} (keeping {keepSlot}). Transcript: \"{lastUtterance}\"");

                if (contaminatedSlot == "destination")
                {
                    _engine.RawData.DestinationRaw = null;
                    _engine.HardClearVerifiedAddress("destination");
                    _engine.ClearFareResult();
                    _engine.ForceState(CollectionState.CollectingDestination);
                }
                else
                {
                    _engine.RawData.PickupRaw = null;
                    _engine.HardClearVerifiedAddress("pickup");
                    _engine.ClearFareResult();
                    _engine.ForceState(CollectionState.CollectingPickup);
                }
                EmitCurrentInstruction();
                return BuildSyncResponse("same_address_blocked", slotsUpdated);
            }
        }

        // ── State progression: advance engine based on what was filled ──

        // Mid-fare address correction: if we're past collection (e.g. PresentingFare)
        // and an address was updated, route through CorrectSlotAsync for proper
        // recalculation (clear fare, clear verified address, re-geocode).
        // GUARD: Only trigger if the new value actually differs from the verified/existing address.
        if (currentState >= CollectionState.ReadyForExtraction)
        {
            if (slotsUpdated.Contains("pickup"))
            {
                var newPickup = _engine.RawData.PickupRaw ?? "";
                var existingPickup = _engine.VerifiedPickup?.Address ?? "";
                // Compare: strip whitespace/case and check if the verified address starts with the new value
                // (verified often has postcode appended, e.g. "52A David Road, Coventry CV1 2BW")
                bool pickupActuallyChanged = !existingPickup.StartsWith(newPickup, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(newPickup.Trim(), existingPickup.Trim(), StringComparison.OrdinalIgnoreCase);
                if (pickupActuallyChanged)
                {
                    Log($"[SyncTool] Mid-fare pickup correction detected (state={currentState}): \"{newPickup}\" differs from verified \"{existingPickup}\"");
                    await CorrectSlotAsync("pickup", newPickup, ct);
                    return BuildSyncResponse("ok", slotsUpdated);
                }
                else
                {
                    // FIX 4: Stale detection for pickup
                    if (!string.IsNullOrWhiteSpace(lastUtterance) && TranscriptSuggestsAddressChange(lastUtterance))
                    {
                        // The AI resent the same address but the transcript has correction signals.
                        // If the address is already verified/geocoded, jump to VerifyingPickup
                        // to re-read it back — NOT CollectingPickup (which causes a loop).
                        if (_engine.VerifiedPickup != null)
                        {
                            Log($"⚠ [SyncTool] STALE PICKUP REUSE — but address already verified. Jumping to VerifyingPickup for re-readback.");
                            _engine.ClearFareResult();
                            _engine.ForceState(CollectionState.VerifyingPickup);
                        }
                        else
                        {
                            Log($"⚠ [SyncTool] STALE PICKUP REUSE DETECTED — tool sent \"{newPickup}\" but transcript \"{lastUtterance}\" suggests a new address. Flagging for clarification.");
                            _engine.ClearVerifiedAddress("pickup");
                            _engine.ClearFareResult();
                            _engine.ForceState(CollectionState.CollectingPickup);
                        }
                        EmitCurrentInstruction();
                        return BuildSyncResponse("stale_reuse_detected", slotsUpdated);
                    }
                    Log($"[SyncTool] Pickup re-sent but unchanged — ignoring mid-fare correction (new=\"{newPickup}\", verified=\"{existingPickup}\")");
                }
            }
            if (slotsUpdated.Contains("destination"))
            {
                var newDest = _engine.RawData.DestinationRaw ?? "";
                var existingDest = _engine.VerifiedDestination?.Address ?? "";
                bool destActuallyChanged = !existingDest.StartsWith(newDest, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(newDest.Trim(), existingDest.Trim(), StringComparison.OrdinalIgnoreCase);
                if (destActuallyChanged)
                {
                    Log($"[SyncTool] Mid-fare destination correction detected (state={currentState}): \"{newDest}\" differs from verified \"{existingDest}\"");
                    await CorrectSlotAsync("destination", newDest, ct);
                    return BuildSyncResponse("ok", slotsUpdated);
                }
                else
                {
                    // FIX 4: Stale detection — if transcript suggests a change but tool reused old value
                    if (!string.IsNullOrWhiteSpace(lastUtterance) && TranscriptSuggestsAddressChange(lastUtterance))
                    {
                        // Same pattern as pickup: if already verified, jump to re-readback, not re-collection
                        if (_engine.VerifiedDestination != null)
                        {
                            Log($"⚠ [SyncTool] STALE DESTINATION REUSE — but address already verified. Jumping to VerifyingDestination for re-readback.");
                            _engine.ClearFareResult();
                            _engine.ForceState(CollectionState.VerifyingDestination);
                        }
                        else
                        {
                            Log($"⚠ [SyncTool] STALE DESTINATION REUSE DETECTED — tool sent \"{newDest}\" but transcript \"{lastUtterance}\" suggests a new address. Flagging for clarification.");
                            _engine.ClearVerifiedAddress("destination");
                            _engine.ClearFareResult();
                            _engine.ForceState(CollectionState.CollectingDestination);
                        }
                        EmitCurrentInstruction();
                        return BuildSyncResponse("stale_reuse_detected", slotsUpdated);
                    }
                    Log($"[SyncTool] Destination re-sent but unchanged — ignoring mid-fare correction (new=\"{newDest}\", verified=\"{existingDest}\")");
                }
            }
        }

        // If addresses were provided during collection, route to verification
        if (slotsUpdated.Contains("pickup") && currentState <= CollectionState.CollectingPickup)
        {
            // Advance engine past any earlier collection states
            _engine.AcceptSlotValue("name", _engine.RawData.NameRaw ?? "");

            _engine.ForceState(CollectionState.VerifyingPickup);
            EmitCurrentInstruction();
            return BuildSyncResponse("ok", slotsUpdated);
        }

        // Mid-collection address correction: pickup was corrected while collecting a later slot
        // (e.g., during CollectingPassengers or CollectingPickupTime).
        // Must clear verified address and re-verify.
        if (slotsUpdated.Contains("pickup") && currentState > CollectionState.CollectingPickup && currentState < CollectionState.ReadyForExtraction)
        {
            var newPickup = _engine.RawData.PickupRaw ?? "";
            var existingPickup = _engine.VerifiedPickup?.Address ?? "";
            bool pickupActuallyChanged = !existingPickup.StartsWith(newPickup, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(newPickup.Trim(), existingPickup.Trim(), StringComparison.OrdinalIgnoreCase);
            if (pickupActuallyChanged)
            {
                Log($"[SyncTool] Mid-collection pickup correction (state={currentState}): \"{newPickup}\" differs from verified \"{existingPickup}\" — re-verifying");
                _engine.ClearVerifiedAddress("pickup");
                _engine.ClearFareResult();
                _engine.ForceState(CollectionState.VerifyingPickup);
                EmitCurrentInstruction();
                return BuildSyncResponse("ok", slotsUpdated);
            }
        }

        // Mid-collection destination correction
        if (slotsUpdated.Contains("destination") && !slotsUpdated.Contains("pickup") &&
            currentState > CollectionState.CollectingDestination && currentState < CollectionState.ReadyForExtraction)
        {
            var newDest = _engine.RawData.DestinationRaw ?? "";
            var existingDest = _engine.VerifiedDestination?.Address ?? "";
            bool destActuallyChanged = !existingDest.StartsWith(newDest, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(newDest.Trim(), existingDest.Trim(), StringComparison.OrdinalIgnoreCase);
            if (destActuallyChanged)
            {
                Log($"[SyncTool] Mid-collection destination correction (state={currentState}): \"{newDest}\" differs from verified \"{existingDest}\" — re-verifying");
                _engine.ClearVerifiedAddress("destination");
                _engine.ClearFareResult();
                _engine.ForceState(CollectionState.VerifyingDestination);
                EmitCurrentInstruction();
                return BuildSyncResponse("ok", slotsUpdated);
            }
        }

        // Destination needs verification — whether it came alone or in a multi-slot burst.
        // In a burst (pickup+destination), pickup was already verified above; now verify destination.
        if (slotsUpdated.Contains("destination") && currentState <= CollectionState.CollectingPassengers)
        {
            // Only force verification if destination isn't already verified
            if (_engine.VerifiedDestination == null)
            {
                _engine.ForceState(CollectionState.VerifyingDestination);
                EmitCurrentInstruction();
                return BuildSyncResponse("ok", slotsUpdated);
            }
        }

        // For non-address fields, let the engine figure out the next state.
        // CRITICAL: Transition state FIRST, then build instruction for new state.
        // This prevents the instruction race where CollectingPickupTime instruction
        // is emitted then immediately overridden by ReadyForExtraction/Geocoding silence.
        var nextSlot = _engine.RawData.NextMissingSlot();
        var stateNow = _engine.State;
        if (nextSlot == null && stateNow < CollectionState.ReadyForExtraction
            && stateNow != CollectionState.VerifyingPickup
            && stateNow != CollectionState.VerifyingDestination)
        {
            // All slots filled — transition state BEFORE extraction (atomic)
            // Do NOT emit instruction for old state — go straight to extraction
            _engine.ForceState(CollectionState.ReadyForExtraction);
            await RunExtractionAsync(ct);
        }
        else if (nextSlot != null)
        {
            // Advance to next missing slot
            _engine.AdvanceToSlot(nextSlot);
            EmitCurrentInstruction();
        }

        return BuildSyncResponse("ok", slotsUpdated);
    }

    /// <summary>Build the tool result response for sync_booking_data.</summary>
    private object BuildSyncResponse(string status, List<string>? updatedSlots = null, bool managingBooking = false)
    {
        var state = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(_engine.RawData.NameRaw)) state["name"] = _engine.RawData.NameRaw;
        
        // Use verified addresses in booking_state when available — this prevents the AI
        // from seeing the caller's raw misspelling in conversation history and repeating it.
        // REVERIFICATION GUARD: Don't use stale verified address if flagged for re-verify.
        var verifiedPickup = (!_engine.PickupNeedsReverification && _engine.VerifiedPickup != null)
            ? _engine.VerifiedPickup.Address : null;
        state["pickup"] = !string.IsNullOrEmpty(verifiedPickup) 
            ? verifiedPickup 
            : _engine.RawData.PickupRaw;
        
        var verifiedDest = (!_engine.DestinationNeedsReverification && _engine.VerifiedDestination != null)
            ? _engine.VerifiedDestination.Address : null;
        state["destination"] = !string.IsNullOrEmpty(verifiedDest)
            ? verifiedDest
            : _engine.RawData.DestinationRaw;
        
        if (!string.IsNullOrWhiteSpace(_engine.RawData.PassengersRaw)) state["passengers"] = _engine.RawData.PassengersRaw;
        if (!string.IsNullOrWhiteSpace(_engine.RawData.PickupTimeRaw)) state["pickup_time"] = _engine.RawData.PickupTimeRaw;

        var next = _engine.RawData.NextMissingSlot();

        // When the engine is in a Verifying state, override next_required to indicate
        // verification is pending. Without this, the AI sees "next_required":"destination"
        // and asks for the destination instead of performing the readback.
        var engineState = _engine.State;
        string nextRequired;
        string? action = null;
        
        if (engineState == CollectionState.VerifyingPickup)
        {
            nextRequired = "verifying_pickup";
            action = "VERIFY pickup address by reading it back to caller";
        }
        else if (engineState == CollectionState.VerifyingDestination)
        {
            nextRequired = "verifying_destination";
            action = "VERIFY destination address by reading it back to caller";
        }
        else if (engineState == CollectionState.AwaitingClarification)
        {
            // CRITICAL: Override next_required so the AI sees we're waiting for clarification,
            // NOT that all slots are filled. This prevents premature confirmation.
            var clarField = _engine.PendingClarification?.AmbiguousField ?? "address";
            nextRequired = $"clarifying_{clarField}";
            action = $"Ask caller to clarify the {clarField} address — provide area or postcode";
        }
        else if (next == null)
        {
            // All raw slots are filled, but check if addresses are actually verified.
            // If destination geocode failed and we're not in a verification state,
            // the booking is NOT complete — we need clarification.
            bool pickupVerified = _engine.VerifiedPickup != null && !_engine.PickupNeedsReverification;
            bool destVerified = _engine.VerifiedDestination != null && !_engine.DestinationNeedsReverification;
            
            if (!destVerified && engineState < CollectionState.ReadyForExtraction)
            {
                nextRequired = "clarifying_destination";
                action = "The destination address has NOT been verified. Do NOT confirm the booking. Ask the caller for more details about the destination.";
            }
            else if (!pickupVerified && engineState < CollectionState.ReadyForExtraction)
            {
                nextRequired = "clarifying_pickup";
                action = "The pickup address has NOT been verified. Do NOT confirm the booking. Ask the caller for more details about the pickup.";
            }
            else
            {
                nextRequired = "all_collected";
            }
        }
        else
        {
            nextRequired = next;
        }

        return new
        {
            status,
            updated = updatedSlots ?? new List<string>(),
            booking_state = state,
            next_required = nextRequired,
            engine_state = engineState.ToString(),
            action
        };
    }

    /// <summary>Extract a string arg from tool call arguments (handles JsonElement).</summary>
    private static bool TryGetArg(Dictionary<string, object?> args, string key, out string value)
    {
        value = "";
        if (!args.TryGetValue(key, out var raw) || raw == null) return false;

        if (raw is JsonElement je)
        {
            value = je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.GetRawText();
        }
        else
        {
            value = raw.ToString() ?? "";
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Detect which slot the AI intends to update based on which fields are populated.
    /// Returns the primary target slot name, or null if ambiguous.
    /// </summary>
    private static string? DetectIntentTargetSlot(Dictionary<string, object?> args)
    {
        // Check which booking fields are populated (excluding meta fields)
        string? target = null;
        foreach (var slot in new[] { "pickup", "destination", "passengers", "pickup_time", "caller_name" })
        {
            if (args.TryGetValue(slot, out var val) && val != null)
            {
                var str = val is JsonElement je
                    ? (je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText())
                    : val.ToString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    // Map caller_name → name for engine slot naming
                    target = slot == "caller_name" ? "name" : slot;
                    break; // Take the first populated field as the target
                }
            }
        }
        return target;
    }

    /// <summary>
    /// Check if the engine is currently collecting the given slot.
    /// Prevents unnecessary intent-jumps when the AI says update_field
    /// for the field we're already asking about.
    /// </summary>
    private static bool IsCurrentlyCollecting(CollectionState state, string slot)
    {
        return slot switch
        {
            "name" => state == CollectionState.CollectingName,
            "pickup" => state is CollectionState.CollectingPickup or CollectionState.VerifyingPickup,
            "destination" => state is CollectionState.CollectingDestination or CollectionState.VerifyingDestination,
            "passengers" => state == CollectionState.CollectingPassengers,
            "pickup_time" => state == CollectionState.CollectingPickupTime,
            _ => false
        };
    }

    // ─── Inline Address Verification ────────────────────────

    /// <summary>
    /// Geocode a single address inline after Ada reads it back.
    /// Uses the full 3-way context (Ada's question + raw STT + Ada's readback)
    /// for "Level 5" dispatch accuracy.
    /// 
    /// Strategy:
    ///   1. Primary: address-dispatch edge function with 3-way context
    ///   2. Fallback: local Gemini reconciler (if edge function fails)
    /// </summary>
    private async Task RunInlineGeocodeAsync(string field, string rawAddress, CancellationToken ct, string? adaReadback = null, string? rawTranscript = null)
    {
        if (_fareService == null && _reconciler == null)
        {
            Log($"No fare service or reconciler — skipping inline geocode for {field}");
            _engine.SkipVerification(field, "no fare/reconciler service");
            EmitCurrentInstruction();
            return;
        }

        try
        {
            // Get Ada's question that prompted this address (the instruction before the verifying state)
            var adaQuestion = _lastEmittedInstruction;

            Log($"Inline geocoding {field}: raw=\"{rawAddress}\", adaReadback=\"{adaReadback ?? "none"}\", adaQuestion present={!string.IsNullOrEmpty(adaQuestion)}");

            GeocodedAddress? geocoded = null;

            // Primary path: address-dispatch edge function with 3-way context
            if (_fareService != null)
            {
                // Inject caller's area/city as bias to narrow geocoding search space
                var biasCity = _callerContext?.ServiceArea
                    ?? _callerContext?.LastPickup?.Split(',').LastOrDefault()?.Trim();
                geocoded = await _fareService.GeocodeAddressAsync(
                    rawAddress, field, CallerId, ct,
                    adaReadback: adaReadback,
                    adaQuestion: adaQuestion,
                    rawTranscript: rawTranscript,
                    biasCity: biasCity);
            }

            // Fallback: local Gemini reconciler if primary failed
            if (geocoded == null && _reconciler != null)
            {
                Log($"Primary geocode failed — trying local Gemini reconciler for {field}");
                var reconcileRequest = new ReconcileContextRequest
                {
                    AdaQuestion = adaQuestion,
                    UserRawSpeech = rawAddress,
                    AdaReadback = adaReadback,
                    CurrentCity = _callerContext?.LastPickup?.Split(',').LastOrDefault()?.Trim(),
                    Context = field,
                    CallerPhone = CallerId
                };

                var reconciled = await _reconciler.ReconcileAsync(reconcileRequest, ct);
                if (reconciled != null && reconciled.Confidence >= 0.4)
                {
                    geocoded = reconciled.ToGeocodedAddress();
                    Log($"Local reconciler resolved {field}: \"{geocoded.Address}\" (conf={reconciled.Confidence:F2})");
                }
                else if (reconciled != null)
                {
                    Log($"Local reconciler low confidence ({reconciled.Confidence:F2}) for {field} — treating as ambiguous");
                    geocoded = new GeocodedAddress
                    {
                        Address = reconciled.ReconciledAddress,
                        Lat = reconciled.Lat,
                        Lon = reconciled.Lon,
                        IsAmbiguous = true,
                        Alternatives = reconciled.Alternatives,
                    };
                }
            }

            if (geocoded == null)
            {
                Log($"All geocoding paths returned null for {field} — skipping");
                _engine.SkipVerification(field, "geocode failed");
                EmitCurrentInstruction();
                return;
            }

            if (geocoded.IsAmbiguous)
            {
                _clarificationAttempts++;
                Log($"Inline geocode: {field} is ambiguous — entering clarification (attempt {_clarificationAttempts})");

                // Loop breaker: after 2 consecutive clarification failures, skip verification
                if (_clarificationAttempts >= 2)
                {
                    Log($"Clarification loop breaker: {_clarificationAttempts} inline attempts — skipping verification");
                    _clarificationAttempts = 0;
                    _engine.SkipVerification(field, "Address could not be resolved after multiple clarification attempts", forceRecollect: true);
                    EmitCurrentInstruction();
                    return;
                }

                _engine.EnterClarification(new ClarificationInfo
                {
                    AmbiguousField = field,
                    Message = "Which area is that in?",
                    Alternatives = geocoded.Alternatives,
                    Attempt = _clarificationAttempts
                });
                EmitCurrentInstruction();
                return;
            }

            // Success — reset clarification counter
            _clarificationAttempts = 0;

            // Clean the geocoded address — strip raw STT prefix ONLY if the remaining part
            // still contains a street name (i.e., Gemini duplicated the raw input as a prefix).
            // If the geocoded address IS "raw + city/postcode", stripping would lose the street.
            var rawSlotValue = field == "pickup" ? _engine.RawData.PickupRaw : _engine.RawData.DestinationRaw;
            var streetTokenPattern = @"\b(Road|Rd|Street|St|Avenue|Ave|Lane|Ln|Drive|Dr|Close|Cl|Way|Place|Pl|Crescent|Cres|Court|Ct|Terrace|Tce|Grove|Gr|Hill|Gardens|Gdns|Square|Sq|Parade|Row|Walk|Rise|Mews|Boulevard|Blvd)\b";
            // Only attempt prefix stripping if the raw input is a POI/landmark (no street token).
            // If the raw input already contains a street name (e.g., "52A David Road, Coventry"),
            // stripping would remove the street and leave just the postcode.
            var rawHasStreetToken = !string.IsNullOrEmpty(rawSlotValue) &&
                System.Text.RegularExpressions.Regex.IsMatch(rawSlotValue, streetTokenPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!rawHasStreetToken && !string.IsNullOrEmpty(rawSlotValue) && geocoded.Address.StartsWith(rawSlotValue, StringComparison.OrdinalIgnoreCase))
            {
                var cleaned = geocoded.Address[rawSlotValue.Length..].TrimStart(',', ' ');
                // Only strip if the remainder still contains a street-like token (Road, Street, etc.)
                var hasStreetToken = System.Text.RegularExpressions.Regex.IsMatch(
                    cleaned, streetTokenPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!string.IsNullOrWhiteSpace(cleaned) && hasStreetToken)
                {
                    Log($"🧹 Stripped raw STT prefix from geocoded address: \"{geocoded.Address}\" → \"{cleaned}\"");
                    geocoded = new GeocodedAddress
                    {
                        Address = cleaned,
                        Lat = geocoded.Lat,
                        Lon = geocoded.Lon,
                        StreetName = geocoded.StreetName,
                        StreetNumber = geocoded.StreetNumber,
                        PostalCode = geocoded.PostalCode,
                        City = geocoded.City,
                        IsAmbiguous = geocoded.IsAmbiguous,
                        Alternatives = geocoded.Alternatives,
                        MatchedFromHistory = geocoded.MatchedFromHistory
                    };
                }
                else
                {
                    Log($"🧹 Prefix strip skipped — remainder \"{cleaned}\" has no street token, keeping full address");
                }
            }
            else if (rawHasStreetToken)
            {
                Log($"🧹 Prefix strip skipped — raw input \"{rawSlotValue}\" already contains street token, keeping full geocoded address");
            }

            // POI alias detection: if the geocoder resolved to a different business name
            // than what the caller said, preserve the caller's POI name for readback.
            // Works for both pure POI inputs ("Pig in the Middle") and POI-on-street inputs
            // ("Pig in the Middle on Fargosworth Street").
            if (!string.IsNullOrEmpty(rawSlotValue))
            {
                var resolvedLower = geocoded.Address.ToLowerInvariant();
                // Extract the caller's POI name: everything before "on/at/near" + street reference
                var poiName = ExtractCallerPoiName(rawSlotValue);
                if (!string.IsNullOrEmpty(poiName))
                {
                    var poiLower = poiName.ToLowerInvariant();
                    // If the resolved address does NOT contain the caller's POI name, store it
                    if (!resolvedLower.Contains(poiLower) && poiLower.Length >= 3)
                    {
                        geocoded.CallerPoiName = poiName;
                        Log($"📍 POI alias detected: caller said \"{poiName}\" but geocoded to \"{geocoded.Address}\" — preserving caller POI name");
                    }
                }
            }

            // Success — store verified address and advance
            if (field == "pickup")
            {
                _engine.CompletePickupVerification(geocoded);
                Log($"✅ Pickup verified: \"{geocoded.Address}\"" + 
                    (geocoded.CallerPoiName != null ? $" (caller POI: \"{geocoded.CallerPoiName}\")" : ""));
            }
            else
            {
                _engine.CompleteDestinationVerification(geocoded);
                Log($"✅ Destination verified: \"{geocoded.Address}\"" +
                    (geocoded.CallerPoiName != null ? $" (caller POI: \"{geocoded.CallerPoiName}\")" : ""));
            }

            // Emit instruction with verified address in the prompt
            EmitCurrentInstruction();

            // Check if all slots are now filled after verification
            if (_engine.State == CollectionState.ReadyForExtraction)
            {
                await RunExtractionAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Log($"Inline geocode error for {field}: {ex.Message}");
            _engine.SkipVerification(field, ex.Message);
            EmitCurrentInstruction();
        }
    }

    /// <summary>
    /// Extract the caller's POI/venue name from a raw address input.
    /// Handles patterns like "Pig in the Middle on Fargosworth Street" → "Pig in the Middle"
    /// and pure POI inputs like "Pig in the Middle" → "Pig in the Middle".
    /// Returns null if the input looks like a regular street address (e.g., "52A David Road").
    /// </summary>
    private static string? ExtractCallerPoiName(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return null;
        
        var clean = rawInput.Trim();
        
        // Try to split on prepositions that separate POI name from street
        // e.g., "Pig in the Middle on Fargosworth Street" → "Pig in the Middle"
        var separators = new[] { " on ", " at ", " near ", " beside ", " opposite " };
        foreach (var sep in separators)
        {
            var idx = clean.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 2) // Must have at least a 3-char POI name before the separator
            {
                var candidate = clean[..idx].Trim();
                // Verify the part after separator has a street token
                var afterSep = clean[(idx + sep.Length)..].Trim();
                var streetPattern = @"\b(Road|Rd|Street|St|Avenue|Ave|Lane|Ln|Drive|Dr|Close|Way|Place|Crescent|Court|Terrace|Grove|Hill|Gardens|Square|Parade|Row|Walk|Rise|Mews|Boulevard)\b";
                if (System.Text.RegularExpressions.Regex.IsMatch(afterSep, streetPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return candidate;
                }
            }
        }
        
        // Pure POI: no street token in the entire input → entire input is the POI name
        var hasStreetToken = System.Text.RegularExpressions.Regex.IsMatch(
            clean, 
            @"\b(Road|Rd|Street|St|Avenue|Ave|Lane|Ln|Drive|Dr|Close|Way|Place|Crescent|Court|Terrace|Grove|Hill|Gardens|Square|Parade|Row|Walk|Rise|Mews|Boulevard)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Also check for house numbers — if present, it's a regular address
        var hasHouseNumber = System.Text.RegularExpressions.Regex.IsMatch(clean, @"^\d+[A-Za-z]?\s");
        
        if (!hasStreetToken && !hasHouseNumber && clean.Length >= 3)
            return clean;
        
        return null;
    }

    // ─── Private ─────────────────────────────────────────────

    private async Task RunExtractionAsync(CancellationToken ct)
    {
        _engine.BeginExtraction();
        EmitCurrentInstruction(); // "Checking availability..."

        try
        {
            var request = _engine.BuildExtractionRequest(_callerContext);

            // Include Ada's transcript as additional context — Ada is source of truth
            request.AdaTranscriptContext = GetAdaTranscriptContext();

            Log($"Extracting: name={request.Slots.Name}, pickup={request.Slots.Pickup}, " +
                $"dest={request.Slots.Destination}, pax={request.Slots.Passengers}, " +
                $"time={request.Slots.PickupTime}");
            if (!string.IsNullOrEmpty(request.AdaTranscriptContext))
                Log($"Ada context: {request.AdaTranscriptContext.Length} chars");

            var result = await _extractionService.ExtractAsync(request, ct);

            if (result.Success && result.Booking != null)
            {
                _engine.CompleteExtraction(result.Booking);
                OnBookingReady?.Invoke(result.Booking);

                // Chain into fare/geocoding pipeline
                await RunFarePipelineAsync(result.Booking, ct);
            }
            else
            {
                _engine.ExtractionFailed(result.Error ?? "Unknown extraction error");
                EmitCurrentInstruction();
            }
        }
        catch (Exception ex)
        {
            Log($"Extraction error: {ex.Message}");
            _engine.ExtractionFailed(ex.Message);
            EmitCurrentInstruction();
        }
    }

    private async Task RunUpdateExtractionAsync(CancellationToken ct)
    {
        var previousBooking = _engine.StructuredResult!;
        _engine.BeginExtraction();
        EmitCurrentInstruction();

        try
        {
            var request = _engine.BuildExtractionRequest(_callerContext);
            Log($"Update extraction for changed slots: {string.Join(", ", _changedSlots)}");

            var result = await _extractionService.ExtractUpdateAsync(
                request, previousBooking, _changedSlots, ct);

            _changedSlots.Clear();

            if (result.Success && result.Booking != null)
            {
                _engine.CompleteExtraction(result.Booking);
                OnBookingReady?.Invoke(result.Booking);

                // Chain into fare/geocoding pipeline
                await RunFarePipelineAsync(result.Booking, ct);
            }
            else
            {
                _engine.ExtractionFailed(result.Error ?? "Unknown update extraction error");
                EmitCurrentInstruction();
            }
        }
        catch (Exception ex)
        {
            Log($"Update extraction error: {ex.Message}");
            _engine.ExtractionFailed(ex.Message);
            EmitCurrentInstruction();
        }
    }

    /// <summary>
    /// Run geocoding + fare calculation after extraction.
    /// State flow: Extracting → Geocoding → PresentingFare
    /// </summary>
    private async Task RunFarePipelineAsync(StructuredBooking booking, CancellationToken ct)
    {
        if (_fareService == null)
        {
            // No fare service configured — skip to fare presentation without fare data
            Log("No fare service configured — skipping geocoding");
            _engine.GeocodingFailed("No fare service");
            EmitCurrentInstruction();
            return;
        }

        _engine.BeginGeocoding();
        EmitCurrentInstruction(); // Silent instruction during geocoding

        try
        {
            // Override extraction output with verified addresses when available.
            // The extraction AI can hallucinate old addresses — verified ones are ground truth.
            var effectiveBooking = OverrideWithVerifiedAddresses(booking);

            Log($"Geocoding: pickup=\"{effectiveBooking.Pickup.DisplayName}\", dest=\"{effectiveBooking.Destination.DisplayName}\"");

            var fareResult = await _fareService.CalculateAsync(
                effectiveBooking, CallerId, ct,
                rawPickupTranscript: _engine.RawData.GetLastUtterance("pickup"),
                rawDestinationTranscript: _engine.RawData.GetLastUtterance("destination"));

            if (fareResult == null)
            {
                Log("Fare pipeline returned null");
                _engine.GeocodingFailed("Fare calculation failed");
                EmitCurrentInstruction();
                return;
            }

            if (fareResult.NeedsClarification)
            {
                _clarificationAttempts++;

                // Loop breaker: after 2 consecutive clarification failures, fall through
                if (_clarificationAttempts >= 2)
                {
                    Log($"Clarification loop breaker: {_clarificationAttempts} attempts — falling through");
                    _clarificationAttempts = 0;
                    _engine.GeocodingFailed("Address could not be resolved after multiple attempts");
                    EmitCurrentInstruction();
                    return;
                }

                var ambiguousField = fareResult.Pickup.IsAmbiguous ? "pickup" : "destination";
                var ambiguousAddr = fareResult.Pickup.IsAmbiguous ? fareResult.Pickup : fareResult.Destination;

                Log($"Address clarification needed ({ambiguousField}): {fareResult.ClarificationMessage}");

                _engine.EnterClarification(new ClarificationInfo
                {
                    AmbiguousField = ambiguousField,
                    Message = fareResult.ClarificationMessage ?? "Which area is that in?",
                    Alternatives = ambiguousAddr.Alternatives,
                    Attempt = _clarificationAttempts
                });

                EmitCurrentInstruction();
                return;
            }

            // Success — reset clarification counter
            _clarificationAttempts = 0;

            // Try to get iCabbi fare quote if enabled (overrides local fare)
            if (_icabbiService != null)
            {
                try
                {
                    var paxCount = booking.Passengers > 0 ? booking.Passengers : 1;
                    var scheduledAt = booking.IsAsap ? (DateTime?)null : booking.PickupDateTime;
                    var icabbiFare = await _icabbiService.GetFareQuoteAsync(fareResult, paxCount, scheduledAt, ct);
                    if (icabbiFare != null)
                    {
                        Log($"🚕 iCabbi fare quote: {icabbiFare.FareFormatted} (replacing local: {fareResult.Fare})");
                        fareResult = fareResult with
                        {
                            Fare = icabbiFare.FareFormatted,
                            FareSpoken = $"{icabbiFare.FareDecimal:F2} pounds",
                            DriverEtaMinutes = icabbiFare.EtaMinutes ?? fareResult.DriverEtaMinutes,
                            DriverEta = icabbiFare.EtaMinutes.HasValue
                                ? $"{icabbiFare.EtaMinutes} minutes"
                                : fareResult.DriverEta
                        };
                    }
                    else
                    {
                        Log("iCabbi fare quote unavailable — using local fare");
                    }
                }
                catch (Exception ex)
                {
                    Log($"iCabbi fare quote error: {ex.Message} — using local fare");
                }
            }

            _engine.CompleteGeocoding(fareResult);
            OnFareReady?.Invoke(fareResult);

            // Disable typing sounds and clear recalculation flag now that fare is ready
            if (_engine.IsRecalculating)
            {
                _engine.IsRecalculating = false;
                OnTypingSoundsChanged?.Invoke(false);
                Log("Recalculation complete — typing sounds disabled");
            }

            Log($"🚕 Fare ready: {fareResult.Fare} ({fareResult.DistanceMiles:F1}mi), " +
                $"ETA: {fareResult.DriverEta}, zone: {fareResult.ZoneName ?? "none"}");

            EmitCurrentInstruction();
        }
        catch (Exception ex)
        {
            Log($"Fare pipeline error: {ex.Message}");
            _engine.GeocodingFailed(ex.Message);
            EmitCurrentInstruction();
        }
    }

    /// <summary>
    /// Replace extraction-output addresses with verified (geocoded) addresses when available.
    /// The extraction AI can hallucinate old addresses after mid-fare corrections;
    /// verified addresses are ground truth from the geocoding pipeline.
    /// </summary>
    private StructuredBooking OverrideWithVerifiedAddresses(StructuredBooking booking)
    {
        var pickup = booking.Pickup;
        var destination = booking.Destination;
        var overridden = false;

        if (_engine.VerifiedPickup != null && !_engine.PickupNeedsReverification)
        {
            var v = _engine.VerifiedPickup;
            // Use the full verified display name as the dispatch address
            pickup = ParseVerifiedAddress(v.Address) ?? pickup;
            overridden = true;
            Log($"Fare override: pickup → verified \"{v.Address}\"");
        }

        if (_engine.VerifiedDestination != null && !_engine.DestinationNeedsReverification)
        {
            var v = _engine.VerifiedDestination;
            destination = ParseVerifiedAddress(v.Address) ?? destination;
            overridden = true;
            Log($"Fare override: destination → verified \"{v.Address}\"");
        }

        if (!overridden) return booking;

        return new StructuredBooking
        {
            CallerName = booking.CallerName,
            Pickup = pickup,
            Destination = destination,
            Passengers = booking.Passengers,
            PickupTime = booking.PickupTime,
            PickupDateTime = booking.PickupDateTime
        };
    }

    /// <summary>
    /// Parse a verified address string like "52A David Road, Coventry CV1 2BW" into a StructuredAddress.
    /// Simple heuristic: use the full string as DisplayName by putting it all in StreetName.
    /// </summary>
    private static StructuredAddress? ParseVerifiedAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        // Preserve the verified address verbatim — no reformatting allowed.
        // RawDisplayName ensures DisplayName returns the sacred string as-is.
        return new StructuredAddress
        {
            StreetName = address,
            RawDisplayName = address
        };
    }

    /// <summary>
    /// Handle a deterministically resolved intent — no AI involvement.
    /// Called when IntentGuard.Resolve() returns a non-None intent.
    /// </summary>
    private async Task HandleDeterministicIntent(IntentGuard.ResolvedIntent intent, string transcript, CancellationToken ct)
    {
        switch (intent)
        {
            case IntentGuard.ResolvedIntent.ConfirmBooking:
                Log($"🛡️ Deterministic: ConfirmBooking");
                await ConfirmBookingAsync(ct);
                break;

            case IntentGuard.ResolvedIntent.RejectFare:
                Log($"🛡️ Deterministic: RejectFare — ending call");
                EndCall(force: true);
                break;

            case IntentGuard.ResolvedIntent.WantsToChange:
                Log($"🛡️ Deterministic: WantsToChange — letting AI ask what to change");
                // Don't end the call — emit instruction so AI asks "what would you like to change?"
                EmitCurrentInstruction();
                break;

            case IntentGuard.ResolvedIntent.EndCall:
                Log($"🛡️ Deterministic: EndCall");
                EndCall(force: true);
                break;

            case IntentGuard.ResolvedIntent.NewBooking:
                Log($"🛡️ Deterministic: NewBooking — resetting for new booking");
                // TODO: Reset engine state for a fresh booking
                _engine.ForceState(CollectionState.CollectingName);
                EmitCurrentInstruction();
                break;

            case IntentGuard.ResolvedIntent.CancelBooking:
                if (_engine.State == CollectionState.AwaitingCancelConfirmation)
                {
                    Log($"🛡️ Deterministic: CancelBooking confirmed");
                    // Caller confirmed cancellation — execute it
                    await HandleCancelConfirmedAsync(ct);
                }
                else
                {
                    Log($"🛡️ Deterministic: CancelBooking — moving to cancel confirmation");
                    _engine.ForceState(CollectionState.AwaitingCancelConfirmation);
                    EmitCurrentInstruction();
                }
                break;

            case IntentGuard.ResolvedIntent.AmendBooking:
                Log($"🛡️ Deterministic: AmendBooking");
                // Start a new booking flow (amendment = new booking with same caller context)
                _engine.ForceState(CollectionState.CollectingPickup);
                EmitCurrentInstruction();
                break;

            case IntentGuard.ResolvedIntent.CheckStatus:
                Log($"🛡️ Deterministic: CheckStatus");
                // Let AI handle status check with current instruction
                EmitCurrentInstruction();
                break;

            case IntentGuard.ResolvedIntent.SelectOption:
                // Disambiguation — route to clarification handler
                await HandlePostCollectionInput(transcript, ct);
                break;

            default:
                EmitCurrentInstruction();
                break;
        }
    }

    private async Task HandleCancelConfirmedAsync(CancellationToken ct)
    {
        // If we have an existing booking ID, attempt cancellation
        if (!string.IsNullOrWhiteSpace(_engine.ExistingBookingId))
        {
            Log($"Cancelling booking {_engine.ExistingBookingId}");
            // TODO: Wire to actual cancellation service (iCabbi, Supabase)
        }
        EndCall(force: true);
    }

    private async Task HandlePostCollectionInput(string transcript, CancellationToken ct)
    {
        Log($"Post-collection input: \"{transcript}\" (state: {_engine.State})");

        // ── CRITICAL GATE: If slots are still missing, redirect to collection ──
        // This prevents Ada from drifting into farewell/assistance mode.
        var missingSlot = _engine.RawData.NextMissingSlot();
        if (missingSlot != null && _engine.State < CollectionState.ReadyForExtraction)
        {
            Log($"⛔ Post-collection BLOCKED — slot '{missingSlot}' still missing. Redirecting to collection.");
            // Force the engine back to the correct collection state
            _engine.AcceptSlotValue(missingSlot, ""); // No-op, but ensures state is correct
            EmitCurrentInstruction();
            return;
        }

        // Check for correction intent first — even post-collection (AI then regex fallback)
        // BUT: Skip correction detection during AwaitingClarification — "No, that's incorrect"
        // means the caller is rejecting the disambiguation options, NOT providing a new address.
        // Let the clarification handler (below) process it instead.
        if (_engine.State != CollectionState.AwaitingClarification && HasCorrectionSignal(transcript))
        {
            if (_burstDispatcher != null)
            {
                try
                {
                    var slotValues = new Dictionary<string, string>();
                    foreach (var slot in _engine.RawData.FilledSlots)
                        slotValues[slot] = _engine.RawData.GetSlot(slot) ?? "";

                    var aiCorrection = await _burstDispatcher.DetectCorrectionAsync(
                        transcript, slotValues, ct);

                    if (aiCorrection != null)
                    {
                        Log($"Post-collection AI correction: {aiCorrection.SlotName} → \"{aiCorrection.NewValue}\"");
                        await CorrectSlotAsync(aiCorrection.SlotName, aiCorrection.NewValue, ct);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Post-collection AI correction failed: {ex.Message}");
                }
            }

            var correction = CorrectionDetector.Detect(
                transcript, null, _engine.RawData.FilledSlots);

            if (correction != null)
            {
                Log($"Post-collection regex correction: {correction.SlotName} → \"{correction.NewValue}\"");
                await CorrectSlotAsync(correction.SlotName, correction.NewValue, ct);
                return;
            }
        }

        var lower = transcript.ToLowerInvariant().Trim();

        switch (_engine.State)
        {
            case CollectionState.AwaitingClarification:
                // Caller responded to "which area?" — check if it's a rejection or actual area info
                Log($"Clarification response: \"{transcript}\"");
                var clarifiedField = _engine.PendingClarification?.AmbiguousField ?? "pickup";
                
                // Detect REJECTION of disambiguation options (e.g., "No, that's incorrect", "No, that's wrong", "Neither")
                var clarLower = transcript.ToLowerInvariant().Trim();
                var isRejection = (clarLower.StartsWith("no") && 
                    (clarLower.Contains("incorrect") || clarLower.Contains("wrong") || clarLower.Contains("not") || 
                     clarLower.Contains("neither") || clarLower.Contains("none"))) ||
                    clarLower == "no" || clarLower == "neither" || clarLower == "none of those";
                
                if (isRejection)
                {
                    // Caller rejected the options — clear the slot and re-collect from scratch
                    Log($"⛔ Clarification REJECTED for {clarifiedField} — re-collecting address");
                    _engine.ClearPendingClarification();
                    _engine.RawData.SetSlot(clarifiedField, null!);
                    _engine.HardClearVerifiedAddress(clarifiedField);
                    _engine.ClearFareResult();
                    var reCollectState = clarifiedField == "pickup" 
                        ? CollectionState.CollectingPickup 
                        : CollectionState.CollectingDestination;
                    _engine.ForceState(reCollectState);
                    EmitCurrentInstruction();
                    break;
                }
                
                _engine.AcceptClarification(transcript);

                // Re-run inline geocoding for the clarified field (NOT full extraction)
                if (_engine.State == CollectionState.VerifyingPickup ||
                    _engine.State == CollectionState.VerifyingDestination)
                {
                    var rawAddr = clarifiedField == "pickup"
                        ? _engine.RawData.PickupRaw ?? ""
                        : _engine.RawData.DestinationRaw ?? "";
                    Log($"Re-geocoding {clarifiedField} after clarification: \"{rawAddr}\"");
                    await RunInlineGeocodeAsync(clarifiedField, rawAddr, ct);
                }
                else
                {
                    EmitCurrentInstruction();
                }
                break;

            case CollectionState.PresentingFare:
                if (lower.Contains("yes") || lower.Contains("please") || lower.Contains("go ahead") ||
                    lower.Contains("confirm") || lower.Contains("book") || lower.Contains("sure") ||
                    lower.Contains("yeah") || lower.Contains("yep"))
                {
                    Log($"Fare accepted — confirming booking");
                    await ConfirmBookingAsync(ct);
                }
                else if (lower.Contains("no") || lower.Contains("cancel") || lower.Contains("don't") ||
                         lower.Contains("never mind"))
                {
                    Log($"Fare rejected — ending call");
                    EndCall(force: true); // Explicit rejection = allow ending
                }
                else if (lower.Contains("change") || lower.Contains("wrong") || lower.Contains("actually") ||
                         lower.Contains("different"))
                {
                    Log($"Caller wants to change something");
                    EmitCurrentInstruction(); // Let AI ask what they want to change
                }
                else
                {
                    EmitCurrentInstruction();
                }
                break;

            case CollectionState.AwaitingPaymentChoice:
                if (lower.Contains("card")) AcceptPayment("card");
                else if (lower.Contains("cash") || lower.Contains("meter")) AcceptPayment("meter");
                else EmitCurrentInstruction();
                break;

            case CollectionState.AwaitingConfirmation:
                if (lower.Contains("yes") || lower.Contains("confirm") || lower.Contains("book"))
                    ConfirmBooking();
                else if (lower.Contains("no") || lower.Contains("cancel"))
                    EndCall(force: true); // Explicit rejection = allow ending
                else
                    EmitCurrentInstruction();
                break;

            case CollectionState.Dispatched:
                // ── POST-DISPATCH: Caller spoke after "taxi on the way" ──
                // Auto-end the call. The AI already said goodbye (or should have).
                // Do NOT re-emit the Dispatched instruction — that causes a barrage
                // of repeated questions as each response.create triggers another cycle.
                Log($"📴 Post-dispatch speech detected: \"{transcript}\" — ending call");
                EndCall(force: true);
                break;

            default:
                // ── SAFETY NET: If we somehow got here with missing slots, redirect ──
                if (!_engine.RawData.AllRequiredPresent)
                {
                    var next = _engine.RawData.NextMissingSlot();
                    Log($"⛔ Default handler with missing slots — redirecting to '{next}'");
                }
                EmitCurrentInstruction();
                break;
        }
    }

    private static bool IsAwaitingCallerResponseState(CollectionState state) => state switch
    {
        CollectionState.CollectingName => true,
        CollectionState.CollectingPickup => true,
        CollectionState.CollectingDestination => true,
        CollectionState.CollectingPassengers => true,
        CollectionState.CollectingPickupTime => true,
        CollectionState.AwaitingClarification => true,
        CollectionState.PresentingFare => true,
        CollectionState.AwaitingPaymentChoice => true,
        CollectionState.AwaitingConfirmation => true,
        _ => false
    };

    /// <summary>
    /// Called by the realtime client when the mic is ungated (playout drained).
    /// Arms the no-reply watchdog NOW — the caller can actually speak at this point.
    /// </summary>
    public void NotifyMicUngated()
    {
        ArmNoReplyWatchdog();
    }

    private void ArmNoReplyWatchdog()
    {
        CancelNoReplyWatchdog();

        if (!IsAwaitingCallerResponseState(_engine.State))
            return;

        var expectedState = _engine.State;
        var expectedMissingSlot = _engine.RawData.NextMissingSlot();
        var cts = new CancellationTokenSource();
        _noReplyCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                var timeout = expectedState == CollectionState.PresentingFare
                    ? NoReplyTimeoutLongSeconds
                    : NoReplyTimeoutSeconds;
                await Task.Delay(TimeSpan.FromSeconds(timeout), cts.Token);

                if (cts.IsCancellationRequested)
                    return;

                if (_engine.State != expectedState)
                    return;

                if (_engine.RawData.NextMissingSlot() != expectedMissingSlot)
                    return;

                _noReplyCount++;
                Log($"⏱️ No-reply timeout in state {_engine.State} (attempt {_noReplyCount}/{MaxNoReplyReprompts})");

                if (_noReplyCount <= MaxNoReplyReprompts)
                {
                    EmitRepromptInstruction("no_reply");
                }
                else
                {
                    Log("⏱️ No-reply max attempts reached — keeping current prompt active");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"No-reply watchdog error: {ex.Message}");
            }
        });
    }

    private void CancelNoReplyWatchdog()
    {
        var cts = Interlocked.Exchange(ref _noReplyCts, null);
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private int _repromptCount = 0;
    private string? _lastRepromptSlot;

    private void EmitCurrentInstruction(bool silent = false)
    {
        // Reset reprompt counter when we successfully advance
        _repromptCount = 0;
        _lastRepromptSlot = null;

        // REVERIFICATION GUARD: Don't pass stale verified addresses to PromptBuilder
        var activePickup = _engine.PickupNeedsReverification ? null : _engine.VerifiedPickup;
        var activeDest = _engine.DestinationNeedsReverification ? null : _engine.VerifiedDestination;

        var instruction = PromptBuilder.BuildInstruction(
            _engine.State, _engine.RawData, _callerContext,
            _engine.StructuredResult, _engine.FareResult,
            _engine.PendingClarification,
            activePickup, activeDest,
            isRecalculating: _engine.IsRecalculating);
        _lastEmittedInstruction = instruction;
        OnAiInstruction?.Invoke(instruction, false, silent);

        // Watchdog is now armed by NotifyMicUngated() — not here.
        // This prevents the timer from counting down while Ada is still speaking.
    }

    private void EmitRepromptInstruction(string rejectedReason)
    {
        var currentSlot = _engine.RawData.NextMissingSlot() ?? "unknown";
        if (_lastRepromptSlot == currentSlot)
            _repromptCount++;
        else
        {
            _repromptCount = 1;
            _lastRepromptSlot = currentSlot;
        }

        Log($"Re-prompt #{_repromptCount} for slot '{currentSlot}' (reason: {rejectedReason})");

        var activePickup2 = _engine.PickupNeedsReverification ? null : _engine.VerifiedPickup;
        var activeDest2 = _engine.DestinationNeedsReverification ? null : _engine.VerifiedDestination;

        var instruction = PromptBuilder.BuildInstruction(
            _engine.State, _engine.RawData, _callerContext,
            _engine.StructuredResult, _engine.FareResult,
            _engine.PendingClarification,
            activePickup2, activeDest2,
            rejectedReason: rejectedReason);
        _lastEmittedInstruction = instruction;
        OnAiInstruction?.Invoke(instruction, true, false); // isReprompt = true

        // Watchdog is now armed by NotifyMicUngated() — not here.
    }

    private void OnEngineStateChanged(CollectionState from, CollectionState to)
    {
        Log($"State transition: {from} → {to}");

        // Ensure stale timers don't leak across state transitions.
        if (!IsAwaitingCallerResponseState(to))
            CancelNoReplyWatchdog();
    }

    private void Log(string msg) => OnLog?.Invoke($"[Session:{SessionId}] {msg}");

    /// <summary>
    /// Extract verified passenger count using Ada's readback as the authoritative source.
    /// Ada's LLM context acts as a "denoising filter" — she understands "four" even when
    /// STT garbles it to "poor". We trust her interpretation over raw STT.
    /// </summary>
    private string? GetVerifiedPassengerCount(string rawCallerStt)
    {
        var numberWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "one", 1 }, { "two", 2 }, { "three", 3 }, { "four", 4 },
            { "five", 5 }, { "six", 6 }, { "seven", 7 }, { "eight", 8 },
        };

        // === PRIORITY 1: Ada's readback (highest confidence) ===
        // Ada was asked "how many passengers?" — her response is context-aware.
        // Only consider lines that contain passenger-related keywords to avoid
        // picking up digits from address readbacks (e.g., "1 Lifford Lane").
        var recentAda = _adaTranscripts.TakeLast(3);
        foreach (var adaLine in recentAda.Reverse())
        {
            var adaLower = adaLine.ToLowerInvariant();

            // Skip lines that look like address readbacks (contain street/road/lane etc.)
            if (System.Text.RegularExpressions.Regex.IsMatch(adaLower,
                @"\b(road|street|lane|avenue|drive|close|way|crescent|court|place|grove|terrace|airport|station)\b"))
                continue;

            // Check for digits first (e.g., "Got it, 4 passengers")
            var digitMatch = System.Text.RegularExpressions.Regex.Match(adaLower, @"\b([1-8])\b");
            if (digitMatch.Success)
            {
                var n = int.Parse(digitMatch.Value);
                Log($"Pax from Ada digit: \"{adaLine}\" → {n}");
                return $"{n}";
            }

            // Check for number words (e.g., "Great, four passengers")
            foreach (var (word, num) in numberWords)
            {
                if (adaLower.Contains(word))
                {
                    Log($"Pax from Ada word: \"{adaLine}\" → {num}");
                    return $"{num}";
                }
            }
        }

        // === PRIORITY 2: Phonetic correction on raw STT ===
        var phoneticMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "poor", "four" }, { "pour", "four" }, { "for", "four" },
            { "tree", "three" }, { "free", "three" },
            { "to", "two" }, { "too", "two" }, { "tue", "two" },
            { "won", "one" }, { "wan", "one" },
            { "sex", "six" }, { "sax", "six" },
            { "ate", "eight" }, { "ape", "eight" },
        };

        var callerWords = rawCallerStt.ToLowerInvariant()
            .TrimEnd('.', '!', '?', ',')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in callerWords)
        {
            if (phoneticMap.TryGetValue(word, out var corrected) &&
                numberWords.TryGetValue(corrected, out var num))
            {
                Log($"Pax from phonetic: \"{word}\" → {corrected} ({num})");
                return $"{num}";
            }
        }

        return null;
    }

    /// <summary>
    /// Quick regex check for correction signal words — gates the AI call
    /// so we don't waste a round-trip on every normal utterance.
    /// </summary>
    private static bool HasCorrectionSignal(string transcript)
    {
        // Normalize punctuation so "No, no" matches "no no" patterns
        var lower = System.Text.RegularExpressions.Regex.Replace(
            transcript.ToLowerInvariant(), @"[,\.\!\?;:]+", " ").Trim();
        // Also collapse multiple spaces
        lower = System.Text.RegularExpressions.Regex.Replace(lower, @"\s+", " ");

        // Farewell/exit speech is NEVER a correction — "I'm sorry but I have to go" etc.
        if (lower.Contains("have to go") || lower.Contains("got to go") || lower.Contains("gotta go") ||
            lower.Contains("need to go") || lower.Contains("goodbye") || lower.Contains("bye bye") ||
            lower.Contains("have a good") || lower.Contains("take care"))
            return false;
        
        // Leading "no" is a strong correction signal (e.g., "no Morrison's Supermarket")
        var startsWithNo = lower.StartsWith("no ") || lower == "no";
        
        return startsWithNo ||
               lower.Contains("actually") || lower.Contains("no wait") || lower.Contains("no no") ||
               lower.Contains("sorry") || lower.Contains("i meant") || lower.Contains("i mean") ||
               lower.Contains("change") || lower.Contains("wrong") || lower.Contains("not that") ||
               lower.Contains("correct") || lower.Contains("instead") || lower.Contains("it's not") ||
               lower.Contains("that's not") || lower.Contains("no it's") || lower.Contains("no its") ||
               lower.Contains("not dover") || lower.Contains("not that one") ||
                lower.Contains("hang on") || lower.Contains("hold on") || lower.Contains("update");
    }

    /// <summary>
    /// Proactive tool-transcript coherence check.
    /// Verifies that the address the AI put in the tool call actually matches the transcript.
    /// Returns false if token overlap is below 30%, indicating stale slot reuse.
    /// </summary>
    private static bool ToolAddressMatchesTranscript(string toolValue, string transcript)
    {
        if (string.IsNullOrWhiteSpace(toolValue) || string.IsNullOrWhiteSpace(transcript))
            return true; // Can't check — allow

        var toolLower = toolValue.ToLowerInvariant();
        var transcriptLower = transcript.ToLowerInvariant();

        // Extract meaningful tokens from transcript (4+ chars, starts with letter)
        var transcriptTokens = transcriptLower
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3 && char.IsLetter(t[0]))
            .Where(t => !IsCommonWord(t)) // Exclude filler words
            .Distinct()
            .ToList();

        if (transcriptTokens.Count == 0) return true; // No meaningful tokens to compare

        int overlap = transcriptTokens.Count(t => toolLower.Contains(t));

        // If less than 30% token overlap → likely stale reuse
        return (double)overlap / transcriptTokens.Count >= 0.3;
    }


    /// FIX 4: Detects if a caller transcript contains signals that they're providing a NEW address,
    /// even though the tool reused the old value (stale slot carryover).
    /// Checks for correction keywords + presence of proper nouns / place-like tokens.
    /// </summary>
    private static bool TranscriptSuggestsAddressChange(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return false;

        var lower = transcript.ToLowerInvariant().Trim();

        // Must contain correction signal words
        bool hasCorrectionSignal =
            lower.StartsWith("no") || lower.Contains("actually") || lower.Contains("change") ||
            lower.Contains("sorry") || lower.Contains("wrong") || lower.Contains("instead") ||
            lower.Contains("not that") || lower.Contains("i meant") || lower.Contains("it's not") ||
            lower.Contains("destination is") || lower.Contains("pickup is") ||
            lower.Contains("going to") || lower.Contains("heading to") ||
            lower.Contains("take me to") || lower.Contains("from");

        if (!hasCorrectionSignal) return false;

        // Must also contain address-like content (proper noun heuristic:
        // words with capital letters that aren't common filler)
        var words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var properNouns = words.Count(w =>
            w.Length > 1 &&
            char.IsUpper(w[0]) &&
            !IsCommonWord(w.ToLowerInvariant()));

        return properNouns >= 1;
    }

    private static bool IsCommonWord(string word)
    {
        return word is "the" or "to" or "from" or "is" or "it" or "my" or "no" or "not" or
            "yes" or "and" or "or" or "on" or "in" or "at" or "of" or "for" or "it's" or
            "i" or "i'm" or "that" or "that's" or "this" or "sorry" or "actually" or
            "please" or "can" or "you" or "we" or "going" or "take" or "me" or "want";
    }

    /// <summary>
    /// Detects when the caller explicitly references a DIFFERENT slot than the one being collected.
    /// e.g., "the pick-up is Pig in the Middle" while collecting destination.
    /// Returns the slot name being referenced, or null if no cross-slot reference detected.
    /// </summary>
    private static string? DetectCrossSlotReference(string transcript, string currentSlot)
    {
        var lower = System.Text.RegularExpressions.Regex.Replace(
            transcript.ToLowerInvariant(), @"[,\.\!\?;:]+", " ").Trim();
        lower = System.Text.RegularExpressions.Regex.Replace(lower, @"\s+", " ");

        // Map of slot-reference patterns → slot name
        // Only match if the referenced slot is DIFFERENT from what we're collecting
        var slotPatterns = new Dictionary<string, string[]>
        {
            ["pickup"] = new[] { "the pick-up is", "the pickup is", "pick-up is", "pickup is", 
                                  "the pick up is", "pick up is", "picking up from" },
            ["destination"] = new[] { "the destination is", "destination is", "going to", 
                                      "heading to", "drop off at", "drop-off is", "dropoff is" },
            ["passengers"] = new[] { "passengers", "people traveling" },
            ["pickup_time"] = new[] { "the time is", "pick up time is", "pickup time is" },
        };

        foreach (var (slot, patterns) in slotPatterns)
        {
            if (slot == currentSlot) continue; // Only detect CROSS-slot references

            foreach (var pattern in patterns)
            {
                if (lower.Contains(pattern))
                    return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// Extract the value portion after a slot reference keyword.
    /// e.g., "Right, the pick-up is Pig in the Middle" → "Pig in the Middle"
    /// </summary>
    private static string ExtractValueAfterSlotReference(string transcript, string slotName)
    {
        var lower = transcript.ToLowerInvariant();

        // Patterns ordered by specificity (longest first)
        var extractPatterns = slotName switch
        {
            "pickup" => new[] { @"the\s+pick[\s-]?up\s+is\s+", @"pick[\s-]?up\s+is\s+", 
                                @"picking\s+up\s+from\s+", @"\bfrom\s+" },
            "destination" => new[] { @"the\s+destination\s+is\s+", @"destination\s+is\s+",
                                     @"going\s+to\s+", @"heading\s+to\s+", @"drop[\s-]?off\s+(?:at|is)\s+" },
            _ => Array.Empty<string>()
        };

        foreach (var pattern in extractPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                transcript, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = transcript[(match.Index + match.Length)..].Trim().TrimEnd('.', ',', '!', '?');
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return "";
    }
}
