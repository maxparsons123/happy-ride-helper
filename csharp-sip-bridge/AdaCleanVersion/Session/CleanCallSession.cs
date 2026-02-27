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
    public event Action<string, bool>? OnAiInstruction; // (instruction, isReprompt)
    public event Action<StructuredBooking>? OnBookingReady;
    public event Action<FareResult>? OnFareReady;
    public event Action<bool>? OnTypingSoundsChanged; // enable/disable typing sounds during recalculation

    public CleanCallSession(
        string sessionId,
        string callerId,
        string companyName,
        IExtractionService extractionService,
        FareGeocodingService? fareService = null,
        CallerContext? callerContext = null,
        LocalGeminiReconciler? reconciler = null,
        EdgeBurstDispatcher? burstDispatcher = null,
        IcabbiBookingService? icabbiService = null)
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
        return PromptBuilder.BuildGreetingMessage(_companyName, _callerContext, _engine.State);
    }

    /// <summary>
    /// Process caller's spoken response for the current slot.
    /// The SIP bridge's transcript handler calls this with the raw text.
    /// </summary>
    public async Task ProcessCallerResponseAsync(string transcript, CancellationToken ct = default)
    {
        // Any caller transcript means they did respond — stop no-reply watchdog.
        CancelNoReplyWatchdog();
        _noReplyCount = 0;

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
                        _engine.RawData.SetSlot("pickup", burst.Pickup);
                        _engine.RawData.SetGeminiSlot("pickup", burst.Pickup); // Store Gemini-cleaned version for readback
                        Log($"[BurstDispatch] pickup=\"{burst.Pickup}\"");
                    }
                    if (burst.Destination != null)
                    {
                        _engine.RawData.SetSlot("destination", burst.Destination);
                        _engine.RawData.SetGeminiSlot("destination", burst.Destination); // Store Gemini-cleaned version for readback
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

        // Quick ASAP detection for pickup_time — full normalization happens in StructureOnlyEngine
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
            EmitCurrentInstruction();
        }
    }

    /// <summary>
    /// Process Ada's spoken response transcript.
    /// Ada's interpretation is stored for extraction context only.
    /// Called from OpenAiRealtimeClient on response.audio_transcript.done.
    /// 
    /// NOTE: We no longer use AdaSlotRefiner to overwrite raw slot values.
    /// Instead, Ada's transcripts are accumulated and fed to StructureOnlyEngine
    /// as extraction context, which does the proper semantic parsing.
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
            if (_geocodeInFlight)
            {
                Log($"⚠️ Geocode already in flight for pickup — skipping duplicate trigger from: \"{adaText[..Math.Min(60, adaText.Length)]}\"");
                return;
            }
            _geocodeInFlight = true;
            try
            {
                var rawAddress = _engine.RawData.PickupRaw ?? "";
                Log($"Ada readback for pickup: \"{adaText}\" (raw STT: \"{rawAddress}\")");
                await RunInlineGeocodeAsync("pickup", rawAddress, ct, adaReadback: adaText);
            }
            finally { _geocodeInFlight = false; }
            return;
        }
        if (_engine.State == CollectionState.VerifyingDestination)
        {
            if (_geocodeInFlight)
            {
                Log($"⚠️ Geocode already in flight for destination — skipping duplicate trigger from: \"{adaText[..Math.Min(60, adaText.Length)]}\"");
                return;
            }
            _geocodeInFlight = true;
            try
            {
                var rawAddress = _engine.RawData.DestinationRaw ?? "";
                Log($"Ada readback for destination: \"{adaText}\" (raw STT: \"{rawAddress}\")");
                await RunInlineGeocodeAsync("destination", rawAddress, ct, adaReadback: adaText);
            }
            finally { _geocodeInFlight = false; }
            return;
        }
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

        var instruction = PromptBuilder.BuildCorrectionInstruction(
            slotName, oldValue ?? "", newValue);
        OnAiInstruction?.Invoke(instruction, false);

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
                    Log($"✅ iCabbi booking dispatched — Journey: {_icabbiResult.JourneyId}, Tracking: {_icabbiResult.TrackingUrl}");
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
    private async Task RunInlineGeocodeAsync(string field, string rawAddress, CancellationToken ct, string? adaReadback = null)
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
                geocoded = await _fareService.GeocodeAddressAsync(
                    rawAddress, field, CallerId, ct,
                    adaReadback: adaReadback,
                    adaQuestion: adaQuestion);
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
                    _engine.SkipVerification(field, "Address could not be resolved after multiple clarification attempts");
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

            // Success — store verified address and advance
            if (field == "pickup")
            {
                _engine.CompletePickupVerification(geocoded);
                Log($"✅ Pickup verified: \"{geocoded.Address}\"");
            }
            else
            {
                _engine.CompleteDestinationVerification(geocoded);
                Log($"✅ Destination verified: \"{geocoded.Address}\"");
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
                effectiveBooking, CallerId, ct);

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
                    var icabbiFare = await _icabbiService.GetFareQuoteAsync(fareResult, paxCount, ct);
                    if (icabbiFare != null)
                    {
                        Log($"🚕 iCabbi fare quote: {icabbiFare.FareDisplay} (replacing local: {fareResult.Fare})");
                        fareResult = fareResult with
                        {
                            Fare = icabbiFare.FareDisplay,
                            FareSpoken = icabbiFare.FareSpoken,
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

        if (_engine.VerifiedPickup != null)
        {
            var v = _engine.VerifiedPickup;
            pickup = new StructuredAddress
            {
                HouseNumber = booking.Pickup.HouseNumber,
                StreetName = booking.Pickup.StreetName,
                Area = booking.Pickup.Area,
                City = booking.Pickup.City,
                Postcode = booking.Pickup.Postcode
            };
            // Use the full verified display name as the dispatch address
            pickup = ParseVerifiedAddress(v.Address) ?? pickup;
            overridden = true;
            Log($"Fare override: pickup → verified \"{v.Address}\"");
        }

        if (_engine.VerifiedDestination != null)
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
        // Put the entire verified address as the display — the fare service sends DisplayName to the edge function
        return new StructuredAddress
        {
            StreetName = address
        };
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
        if (HasCorrectionSignal(transcript))
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
                // Caller responded to "which area?" — accept and re-geocode inline
                Log($"Clarification response: \"{transcript}\"");
                var clarifiedField = _engine.PendingClarification?.AmbiguousField ?? "pickup";
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

    private void EmitCurrentInstruction()
    {
        // Reset reprompt counter when we successfully advance
        _repromptCount = 0;
        _lastRepromptSlot = null;

        var instruction = PromptBuilder.BuildInstruction(
            _engine.State, _engine.RawData, _callerContext,
            _engine.StructuredResult, _engine.FareResult,
            _engine.PendingClarification,
            _engine.VerifiedPickup, _engine.VerifiedDestination,
            isRecalculating: _engine.IsRecalculating);
        _lastEmittedInstruction = instruction;
        OnAiInstruction?.Invoke(instruction, false);

        ArmNoReplyWatchdog();
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

        var instruction = PromptBuilder.BuildInstruction(
            _engine.State, _engine.RawData, _callerContext,
            _engine.StructuredResult, _engine.FareResult,
            _engine.PendingClarification,
            _engine.VerifiedPickup, _engine.VerifiedDestination,
            rejectedReason: rejectedReason);
        _lastEmittedInstruction = instruction;
        OnAiInstruction?.Invoke(instruction, true); // isReprompt = true

        ArmNoReplyWatchdog();
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
}
