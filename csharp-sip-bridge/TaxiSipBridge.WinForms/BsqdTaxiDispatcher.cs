using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TaxiSipBridge;

// =========================
// BSQD API DTOs
// =========================

public class AddressDto
{
    public double lat { get; set; }
    public double lon { get; set; }
    public string street_name { get; set; } = "";
    public int street_number { get; set; }
    public string postal_code { get; set; } = "";
    public string city { get; set; } = "";
    public string? formatted_depa_address { get; set; }   // for departure
    public string? formatted_dest_address { get; set; }   // for destination
}

public class TaxiBotRequest
{
    public AddressDto departure_address { get; set; } = new();
    public AddressDto destination_address { get; set; } = new();
    public DateTimeOffset departure_time { get; set; }
    public string first_name { get; set; } = "";
    public string total_price { get; set; } = "";
    public string phoneNumber { get; set; } = "";
    public string passengers { get; set; } = "1";
}

// =========================
// REUSABLE DISPATCHER
// =========================

/// <summary>
/// Unified BSQD TaxiBot dispatcher. 
/// Takes BookingState + caller info and sends to BSQD API.
/// Uses geocoded address components when available, falls back to string parsing.
/// </summary>
public static class BsqdDispatcher
{
    private static readonly string Url =
        "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/voice_AI_taxibot";

    private static readonly string BearerToken = "sriifvfedn5ktsbw4for7noulxtapb2ff6wf326v";

    public static event Action<string>? OnLog;

    /// <summary>
    /// Fire-and-forget dispatch from BookingState.
    /// Call this after booking confirmation.
    /// </summary>
    public static void Dispatch(BookingState booking, string callerId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var request = BuildRequest(booking, callerId);
                await SendAsync(request);
            }
            catch (Exception ex)
            {
                Log($"❌ BSQD Dispatch ERROR: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Build TaxiBotRequest from BookingState.
    /// Uses geocoded components when available, falls back to string parsing.
    /// </summary>
    public static TaxiBotRequest BuildRequest(BookingState booking, string callerId)
    {
        // Parse departure time
        var departureTime = DateTimeOffset.Now.AddMinutes(5);
        if (!string.IsNullOrWhiteSpace(booking.PickupTime))
        {
            var normalized = booking.PickupTime.Trim().ToLowerInvariant();
            if (normalized == "now" || normalized == "asap" || normalized == "nu" || normalized == "direct")
            {
                departureTime = DateTimeOffset.Now.AddMinutes(2);
            }
            else if (DateTimeOffset.TryParse(booking.PickupTime, out var parsed))
            {
                departureTime = parsed;
            }
        }

        return new TaxiBotRequest
        {
            departure_address = new AddressDto
            {
                lat = booking.PickupLat ?? 0,
                lon = booking.PickupLon ?? 0,
                street_name = booking.PickupStreet ?? ParseStreetName(booking.Pickup),
                street_number = booking.PickupNumber ?? ParseStreetNumber(booking.Pickup),
                postal_code = booking.PickupPostalCode ?? ParsePostalCode(booking.Pickup),
                city = booking.PickupCity ?? ParseCity(booking.Pickup),
                formatted_depa_address = booking.PickupFormatted ?? booking.Pickup ?? "Unknown"
            },
            destination_address = new AddressDto
            {
                lat = booking.DestLat ?? 0,
                lon = booking.DestLon ?? 0,
                street_name = booking.DestStreet ?? ParseStreetName(booking.Destination),
                street_number = booking.DestNumber ?? ParseStreetNumber(booking.Destination),
                postal_code = booking.DestPostalCode ?? ParsePostalCode(booking.Destination),
                city = booking.DestCity ?? ParseCity(booking.Destination),
                formatted_dest_address = booking.DestFormatted ?? booking.Destination ?? "Unknown"
            },
            departure_time = departureTime,
            first_name = booking.Name ?? "Customer",
            total_price = ParseFare(booking.Fare),
            phoneNumber = FormatE164(callerId),
            passengers = (booking.Passengers ?? 1).ToString()
        };
    }

    /// <summary>
    /// Send request to BSQD API.
    /// </summary>
    public static async Task SendAsync(TaxiBotRequest request)
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(request, options);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Log($"🚕 BSQD Dispatch → {request.phoneNumber}");
        Log($"📦 Payload: {json}");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);

        var response = await client.PostAsync(Url, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            Log($"✅ BSQD Dispatch OK: {(int)response.StatusCode}");
        }
        else
        {
            Log($"❌ BSQD Dispatch FAIL: {(int)response.StatusCode} - {responseBody}");
        }
    }

    // =========================
    // PHONE FORMATTING
    // =========================

    /// <summary>
    /// Format phone to E.164 (+31612345678 or +447539025332).
    /// Detects country codes intelligently.
    /// </summary>
    public static string FormatE164(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "+31000000000";

        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());

        // Convert 00 prefix to +
        if (clean.StartsWith("00"))
            clean = "+" + clean.Substring(2);

        // If already has +, we're done (after cleaning)
        if (clean.StartsWith("+"))
        {
            // Dutch mobile: remove extra 0 after +31 (e.g., +3106 → +316)
            if (clean.StartsWith("+310") && clean.Length > 11)
                clean = "+31" + clean.Substring(4);
            return clean;
        }

        // Detect existing country code by common prefixes
        // UK: 44, NL: 31, DE: 49, FR: 33, BE: 32, etc.
        if (clean.StartsWith("44") && clean.Length >= 11)  // UK
            return "+" + clean;
        if (clean.StartsWith("31") && clean.Length >= 10)  // Netherlands
            return "+" + clean;
        if (clean.StartsWith("49") && clean.Length >= 10)  // Germany
            return "+" + clean;
        if (clean.StartsWith("33") && clean.Length >= 10)  // France
            return "+" + clean;
        if (clean.StartsWith("32") && clean.Length >= 9)   // Belgium
            return "+" + clean;
        if (clean.StartsWith("1") && clean.Length == 11)   // USA/Canada
            return "+" + clean;

        // Local Dutch number (starts with 0) - add +31
        if (clean.StartsWith("0"))
            return "+31" + clean.Substring(1);

        // Unknown format - default to +31
        return "+31" + clean;
    }

    // =========================
    // FARE PARSING
    // =========================

    /// <summary>
    /// Parse fare string to decimal format "15.50"
    /// </summary>
    public static string ParseFare(string? fare)
    {
        if (string.IsNullOrWhiteSpace(fare)) return "0.00";

        var clean = new string(fare.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
        clean = clean.Replace(',', '.');

        if (decimal.TryParse(clean, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            return amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }

        return "0.00";
    }

    // =========================
    // ADDRESS PARSING (FALLBACK)
    // =========================

    /// <summary>
    /// Extract street name from "Hoofdweg 4, 1275 AA, Amsterdam"
    /// </summary>
    public static string ParseStreetName(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "Unknown";

        var parts = address.Split(',');
        if (parts.Length > 0)
        {
            var streetPart = parts[0].Trim();
            var match = Regex.Match(streetPart, @"^(.+?)\s+\d+[A-Za-z\-]*$");
            if (match.Success)
                return match.Groups[1].Value;
            return streetPart;
        }
        return address;
    }

    /// <summary>
    /// Extract street number from address (handles 52-8 format)
    /// </summary>
    public static int ParseStreetNumber(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return 0;

        var parts = address.Split(',');
        if (parts.Length > 0)
        {
            // Match number with optional suffix like "4", "52-8", "100A"
            var match = Regex.Match(parts[0], @"\s+(\d+)[\-A-Za-z0-9]*$");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var num))
                return num;
        }
        return 0;
    }

    /// <summary>
    /// Extract Dutch postal code (1234 AB format)
    /// </summary>
    public static string ParsePostalCode(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";

        var match = Regex.Match(address, @"\d{4}\s*[A-Z]{2}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : "";
    }

    /// <summary>
    /// Extract city (last non-postal-code part)
    /// </summary>
    public static string ParseCity(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";

        var parts = address.Split(',').Select(p => p.Trim()).ToArray();

        // City is usually the last part that isn't a postal code
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (!Regex.IsMatch(parts[i], @"\d{4}\s*[A-Z]{2}", RegexOptions.IgnoreCase))
                return parts[i];
        }

        return "Amsterdam"; // Default for NL
    }

    private static void Log(string msg) => OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
}

// =========================
// LEGACY COMPATIBILITY
// =========================

/// <summary>
/// Legacy TaxiBotClient - delegates to BsqdDispatcher.
/// Keep for backward compatibility.
/// </summary>
public static class TaxiBotClient
{
    public static event Action<string>? OnLog
    {
        add => BsqdDispatcher.OnLog += value;
        remove => BsqdDispatcher.OnLog -= value;
    }

    public static void DispatchBooking(TaxiBotRequest payload) =>
        Task.Run(async () => await BsqdDispatcher.SendAsync(payload));
}
