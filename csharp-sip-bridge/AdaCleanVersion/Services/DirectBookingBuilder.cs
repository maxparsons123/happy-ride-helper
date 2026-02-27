using AdaCleanVersion.Models;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// Deterministic booking builder — replaces the LLM-based StructureOnlyEngine.
///
/// Builds a StructuredBooking directly from:
/// 1. Verified geocoded addresses (ground truth from inline geocoding)
/// 2. Raw slot values (name, passengers from sync_booking_data tool)
/// 3. Deterministic UK time parsing (UkTimeParser)
///
/// No LLM call needed — all data is already structured by this point:
/// - Addresses: verified via geocoding during collection
/// - Passengers: extracted as integers by OpenAI Realtime tool call
/// - Time: parsed deterministically with UK colloquialism support
/// - Name: passed through verbatim
///
/// This eliminates the risk of LLM hallucination/drift (e.g., "7 Russell Street"
/// being corrupted to "7 Brent Russell Street" from noisy conversation context).
/// </summary>
public class DirectBookingBuilder : IExtractionService
{
    private readonly ILogger _logger;

    public DirectBookingBuilder(ILogger logger)
    {
        _logger = logger;
    }

    // ─── IExtractionService: New Booking ──────────────────────

    public Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("[DirectBuilder] Building booking from verified data");

        var result = BuildBooking(request);
        return Task.FromResult(result);
    }

    // ─── IExtractionService: Update Booking ───────────────────

    public Task<ExtractionResult> ExtractUpdateAsync(
        ExtractionRequest request,
        StructuredBooking existingBooking,
        IReadOnlySet<string> changedSlots,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[DirectBuilder] Updating booking for slots: {Slots}",
            string.Join(", ", changedSlots));

        // Build fresh from current slots (they already contain the updated values)
        var result = BuildBooking(request);

        if (!result.Success || result.Booking == null)
            return Task.FromResult(result);

        // Merge: only overwrite fields that were changed
        var merged = new StructuredBooking
        {
            CallerName = changedSlots.Contains("name")
                ? result.Booking.CallerName : existingBooking.CallerName,
            Pickup = changedSlots.Contains("pickup")
                ? result.Booking.Pickup : existingBooking.Pickup,
            Destination = changedSlots.Contains("destination")
                ? result.Booking.Destination : existingBooking.Destination,
            Passengers = changedSlots.Contains("passengers")
                ? result.Booking.Passengers : existingBooking.Passengers,
            PickupTime = changedSlots.Contains("pickup_time")
                ? result.Booking.PickupTime : existingBooking.PickupTime,
            PickupDateTime = changedSlots.Contains("pickup_time")
                ? result.Booking.PickupDateTime : existingBooking.PickupDateTime
        };

        return Task.FromResult(new ExtractionResult
        {
            Success = true,
            Booking = merged,
            Warnings = result.Warnings
        });
    }

    // ─── Core Builder ────────────────────────────────────────

    private ExtractionResult BuildBooking(ExtractionRequest request)
    {
        var slots = request.Slots;
        var warnings = new List<string>();

        // 1. Parse addresses (already verified/geocoded — just structure them)
        var pickup = ParseAddress(slots.Pickup);
        var destination = ParseAddress(slots.Destination);

        if (string.IsNullOrWhiteSpace(slots.Pickup))
            warnings.Add("Pickup address is missing");
        if (string.IsNullOrWhiteSpace(slots.Destination))
            warnings.Add("Destination address is missing");

        // 2. Parse passengers
        var passengers = ParsePassengers(slots.Passengers);

        // 3. Parse time deterministically
        var timeResult = UkTimeParser.Parse(slots.PickupTime);
        var pickupTime = timeResult?.Normalized ?? "ASAP";
        var pickupDateTime = timeResult is { IsAsap: false } ? timeResult.Resolved : null;

        var booking = new StructuredBooking
        {
            CallerName = slots.Name,
            Pickup = pickup,
            Destination = destination,
            Passengers = passengers,
            PickupTime = pickupTime,
            PickupDateTime = pickupDateTime
        };

        _logger.LogInformation(
            "[DirectBuilder] Built: pickup={Pickup}, dest={Dest}, pax={Pax}, time={Time}",
            pickup.DisplayName, destination.DisplayName, passengers, pickupTime);

        return new ExtractionResult
        {
            Success = true,
            Booking = booking,
            Warnings = warnings
        };
    }

    // ─── Address Parsing ─────────────────────────────────────

    /// <summary>
    /// Parse a verified address string into StructuredAddress.
    /// Uses the existing AddressParser for house/street decomposition.
    /// </summary>
    private static StructuredAddress ParseAddress(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new StructuredAddress();

        // Extract UK postcode at end
        string? postcode = null;
        var postcodeMatch = System.Text.RegularExpressions.Regex.Match(
            raw, @"\b([A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var addressPart = raw;
        if (postcodeMatch.Success)
        {
            postcode = postcodeMatch.Value.Trim();
            addressPart = raw[..postcodeMatch.Index].Trim().TrimEnd(',');
        }

        // Extract house number from start
        string? houseNumber = null;
        string? streetName;
        var houseMatch = System.Text.RegularExpressions.Regex.Match(
            addressPart, @"^(\d+[A-Za-z]?)\s+(.+)$");

        if (houseMatch.Success)
        {
            houseNumber = houseMatch.Groups[1].Value;
            streetName = houseMatch.Groups[2].Value;
        }
        else
        {
            streetName = addressPart;
        }

        // Split by comma for area/city
        string? area = null;
        string? city = null;
        var parts = streetName.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        streetName = parts[0];
        if (parts.Length > 2) { area = parts[1]; city = parts[2]; }
        else if (parts.Length > 1) { city = parts[1]; }

        return new StructuredAddress
        {
            HouseNumber = houseNumber,
            StreetName = streetName,
            Area = area,
            City = city,
            Postcode = postcode
        };
    }

    // ─── Passenger Parsing ───────────────────────────────────

    private static int ParsePassengers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 1;
        if (int.TryParse(raw.Trim(), out var num) && num is >= 1 and <= 8) return num;

        // Word numbers
        return raw.Trim().ToLowerInvariant() switch
        {
            "one" or "1" => 1,
            "two" or "a couple" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            _ => 1
        };
    }
}