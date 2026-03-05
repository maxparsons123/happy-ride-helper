// Last updated: 2026-03-02 (v1.0 — Deterministic Session Driver)
namespace AdaSdkModel.Core.Deterministic;

/// <summary>
/// Mutable booking draft with opportunistic slot filling.
/// Unlike BookingState, this is purpose-built for the deterministic driver:
/// - Clear slot presence checks
/// - No geocoded fields (those live in BookingState after validation)
/// - Merge from any source (tool call, regex, LLM extraction)
/// </summary>
public sealed class BookingDraft
{
    // ── Core slots ──
    public string? Name { get; set; }
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int? Passengers { get; set; }
    public string? PickupTime { get; set; }
    public string? VehicleType { get; set; } = "Saloon";
    public string? Luggage { get; set; }
    public string? SpecialInstructions { get; set; }
    public string? PaymentPreference { get; set; }

    // ── Validation flags (set by backend after geocoding) ──
    public bool PickupValidated { get; set; }
    public bool DestinationValidated { get; set; }
    public bool PickupVerifiedByCaller { get; set; }
    public bool DestVerifiedByCaller { get; set; }

    // ── Backend pending flags (geocoding in flight — suppresses readback/recovery) ──
    public bool PickupValidationPending { get; set; }
    public bool DestValidationPending { get; set; }

    // ── Geocoded coordinates (populated after inline verification) ──
    public double? PickupLat { get; set; }
    public double? PickupLon { get; set; }
    public string? PickupVerifiedAddress { get; set; }
    public string? PickupCity { get; set; }
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }
    public string? DestVerifiedAddress { get; set; }
    public string? DestCity { get; set; }

    // ── STT alternative addresses (for geocode race when model vs transcript diverge) ──
    public string? PickupAlt { get; set; }
    public string? DestAlt { get; set; }

    // ── Disambiguation state ──
    public bool IsDisambiguating { get; set; }
    public bool DisambiguatingPickup { get; set; }
    public List<string>? DisambiguationOptions { get; set; }

    // ── Fare ──
    public string? Fare { get; set; }
    public string? Eta { get; set; }
    public bool FareAvailable { get; set; }
    public bool ConfirmedByCaller { get; set; }
    public bool FareRejected { get; set; }

    // ── Dispatch ──
    public bool BookingDispatched { get; set; }
    public string? BookingRef { get; set; }

    // ── Existing booking ──
    public string? ExistingBookingId { get; set; }
    public bool HasActiveBooking { get; set; }

    // ── Notes prompt flags ──
    /// <summary>True once the driver-notes question has been asked (caller said yes/no or provided notes).</summary>
    public bool NotesPrompted { get; set; }
    /// <summary>True once the notes question instruction has been delivered to the model (prevents stale-transcript auto-skip).</summary>
    public bool NotesQuestionDelivered { get; set; }

    // ── POI pickup location prompt ──
    /// <summary>True once the "whereabouts will you be?" question has been asked for a POI pickup.</summary>
    public bool PoiLocationPrompted { get; set; }

    /// <summary>
    /// Heuristic: pickup looks like a POI/business if it has no leading house number.
    /// e.g. "Morrisons" / "Tesco on Walsgrave Road" → true
    ///      "52A David Road" / "14 High Street" → false
    /// </summary>
    public bool PickupLooksLikePoi =>
        HasPickup && !System.Text.RegularExpressions.Regex.IsMatch(Pickup!.TrimStart(), @"^\d");

    // ── Slot presence checks ──
    public bool HasName => !string.IsNullOrWhiteSpace(Name);
    public bool HasPickup => !string.IsNullOrWhiteSpace(Pickup);
    public bool HasDestination => !string.IsNullOrWhiteSpace(Destination);
    public bool HasPassengers => Passengers.HasValue && Passengers > 0;
    public bool HasPickupTime => !string.IsNullOrWhiteSpace(PickupTime);
    public bool HasPayment => !string.IsNullOrWhiteSpace(PaymentPreference);
    public bool HasFare => FareAvailable && !string.IsNullOrWhiteSpace(Fare);

    /// <summary>
    /// Opportunistic merge: apply all non-null fields from extraction.
    /// When isModification=true, allows overwriting existing fields and
    /// invalidates dependent downstream state (fare, payment, dispatch).
    /// </summary>
    public void MergeFrom(ExtractionResult extraction, bool isModification = false)
    {
        // Snapshot route fields for change detection
        var prevPickup = Pickup;
        var prevDest = Destination;
        var prevPax = Passengers;

        if (!string.IsNullOrWhiteSpace(extraction.Name) && (!HasName || isModification))
            Name = extraction.Name;

        if (!string.IsNullOrWhiteSpace(extraction.Pickup))
        {
            // Also compare against verified address — model often re-sends the geocoded form
            var changed = !SemanticEquals(Pickup, extraction.Pickup)
                       && !SemanticEquals(PickupVerifiedAddress, extraction.Pickup);
            if (changed)
            {
                Pickup = extraction.Pickup;
                PickupAlt = extraction.PickupAlt; // Carry STT alternative for geocode race
                PickupValidated = false;
                PickupVerifiedByCaller = false;
                PickupLat = PickupLon = null;
                PickupVerifiedAddress = null;
                PickupCity = null;
            }
            // If semantically the same (raw or verified), keep existing state
        }

        if (!string.IsNullOrWhiteSpace(extraction.Destination))
        {
            // Also compare against verified address — model often re-sends the geocoded form
            var changed = !SemanticEquals(Destination, extraction.Destination)
                       && !SemanticEquals(DestVerifiedAddress, extraction.Destination);
            if (changed)
            {
                Destination = extraction.Destination;
                DestAlt = extraction.DestAlt; // Carry STT alternative for geocode race
                DestinationValidated = false;
                DestVerifiedByCaller = false;
                DestLat = DestLon = null;
                DestVerifiedAddress = null;
                DestCity = null;
            }
        }

        if (extraction.Passengers.HasValue)
            Passengers = extraction.Passengers;
        if (!string.IsNullOrWhiteSpace(extraction.PickupTime))
            PickupTime = extraction.PickupTime;
        if (!string.IsNullOrWhiteSpace(extraction.VehicleType))
            VehicleType = extraction.VehicleType;
        if (!string.IsNullOrWhiteSpace(extraction.Luggage))
            Luggage = extraction.Luggage;
        if (!string.IsNullOrWhiteSpace(extraction.SpecialInstructions))
            SpecialInstructions = extraction.SpecialInstructions;

        // If route-affecting fields changed, invalidate downstream state
        if (isModification)
            InvalidateIfRouteChanged(prevPickup, prevDest, prevPax);
    }

    /// <summary>
    /// Reset fare, payment, and dispatch when route-affecting fields change.
    /// This prevents stale fares from being presented after corrections.
    /// </summary>
    public void InvalidateIfRouteChanged(string? prevPickup, string? prevDest, int? prevPax)
    {
        bool routeChanged = prevPickup != Pickup || prevDest != Destination;
        bool paxChanged = prevPax != Passengers;

        if (routeChanged || paxChanged)
        {
            Fare = null;
            Eta = null;
            FareAvailable = false;
            FareRejected = false;
            ConfirmedByCaller = false;
            PaymentPreference = null;
            BookingDispatched = false;
            BookingRef = null;
        }
    }

    /// <summary>Reset all booking fields for a fresh booking (preserves caller identity).</summary>
    public void ResetBooking(bool preserveName = true)
    {
        var savedName = preserveName ? Name : null;
        Pickup = Destination = PickupTime = Fare = Eta = BookingRef = null;
        VehicleType = "Saloon";
        Luggage = SpecialInstructions = PaymentPreference = null;
        Passengers = null;
        PickupValidated = DestinationValidated = false;
        PickupVerifiedByCaller = DestVerifiedByCaller = false;
        PickupValidationPending = DestValidationPending = false;
        PickupLat = PickupLon = DestLat = DestLon = null;
        PickupVerifiedAddress = PickupCity = DestVerifiedAddress = DestCity = null;
        IsDisambiguating = false;
        DisambiguatingPickup = false;
        DisambiguationOptions = null;
        NotesPrompted = false;
        NotesQuestionDelivered = false;
        PoiLocationPrompted = false;
        FareAvailable = FareRejected = BookingDispatched = ConfirmedByCaller = false;
        ExistingBookingId = null;
        HasActiveBooking = false;
        Name = savedName;
    }

    /// <summary>
    /// Semantic equality check for addresses: ignores case, punctuation,
    /// and minor whitespace/spelling differences from ASR.
    /// e.g. "52A David Road" ≈ "52A, David Rose" ≈ "52a david road"
    /// </summary>
    private static bool SemanticEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return Normalize(a) == Normalize(b);
    }

    private static string Normalize(string s)
    {
        // Lowercase, remove punctuation (but PRESERVE hyphens in house numbers),
        // collapse whitespace.
        // Hyphens are meaningful: "12-14A" ≠ "1214A" (range vs single number)
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-')
                sb.Append(c);
        }
        // Collapse multiple spaces
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
    }
}
