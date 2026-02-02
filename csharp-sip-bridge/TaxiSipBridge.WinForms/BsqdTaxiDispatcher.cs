using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TaxiSipBridge.WinForms;

/// <summary>
/// Sends taxi booking data to BSQD API for dispatch processing.
/// Uses the same bearer token as WhatsAppNotifier.
/// </summary>
public static class BsqdTaxiDispatcher
{
    private const string ApiUrl = "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/voice_AI_taxibot";
    private const string BearerToken = "RhjpZxqLXl2snLNMBwK7iIVq"; // Same as WhatsApp notifier

    private static readonly HttpClient _http = new HttpClient();

    public static event Action<string>? OnLog;

    /// <summary>
    /// Dispatches a taxi booking to BSQD API.
    /// Fire-and-forget with logging.
    /// </summary>
    public static void DispatchBooking(BsqdBookingRequest booking)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var json = JsonSerializer.Serialize(booking, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                Log($"🚕 BSQD Dispatch → {booking.PhoneNumber}");
                Log($"📦 Payload: {json}");

                using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BearerToken);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(request);
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
            catch (Exception ex)
            {
                Log($"❌ BSQD Dispatch ERROR: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Creates a booking request from extracted call data.
    /// </summary>
    public static BsqdBookingRequest CreateFromCallData(
        string phoneNumber,
        string? firstName,
        string? pickup,
        double? pickupLat,
        double? pickupLon,
        string? destination,
        double? destLat,
        double? destLon,
        DateTime? departureTime,
        string? fare,
        int passengers = 1)
    {
        // Parse addresses into components (best effort)
        var (pickupStreet, pickupNumber, pickupPostal, pickupCity) = ParseAddress(pickup);
        var (destStreet, destNumber, destPostal, destCity) = ParseAddress(destination);

        return new BsqdBookingRequest
        {
            PhoneNumber = FormatE164(phoneNumber),
            FirstName = firstName ?? "Customer",
            Passengers = passengers.ToString(),
            TotalPrice = ParseFare(fare),
            DepartureTime = (departureTime ?? DateTime.Now).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            DepartureAddress = new BsqdAddress
            {
                Lat = pickupLat ?? 0,
                Lon = pickupLon ?? 0,
                StreetName = pickupStreet,
                StreetNumber = pickupNumber,
                PostalCode = pickupPostal,
                City = pickupCity,
                FormattedDepaAddress = pickup ?? "Unknown"
            },
            DestinationAddress = new BsqdAddress
            {
                Lat = destLat ?? 0,
                Lon = destLon ?? 0,
                StreetName = destStreet,
                StreetNumber = destNumber,
                PostalCode = destPostal,
                City = destCity,
                FormattedDestAddress = destination ?? "Unknown"
            }
        };
    }

    /// <summary>
    /// Format phone to E.164 format (+31652328530)
    /// </summary>
    private static string FormatE164(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "+31000000000";

        // Remove spaces, dashes, parentheses
        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());

        // Handle 00 prefix -> +
        if (clean.StartsWith("00"))
            clean = "+" + clean.Substring(2);

        // Ensure + prefix
        if (!clean.StartsWith("+"))
            clean = "+" + clean;

        // Dutch number fix: +310 -> +31
        if (clean.StartsWith("+310") && clean.Length > 11)
            clean = "+31" + clean.Substring(4);

        return clean;
    }

    /// <summary>
    /// Parse fare string like "£12.50" or "€15.15" to "12.50" or "15.15"
    /// </summary>
    private static string ParseFare(string? fare)
    {
        if (string.IsNullOrWhiteSpace(fare)) return "0.00";

        // Remove currency symbols and whitespace
        var clean = new string(fare.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());

        // Replace comma with dot for decimal
        clean = clean.Replace(',', '.');

        // Ensure valid decimal format
        if (decimal.TryParse(clean, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            return amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }

        return "0.00";
    }

    /// <summary>
    /// Best-effort address parsing into components.
    /// Example: "Hoofdweg 4, 1275 AA, Amsterdam" -> ("Hoofdweg", 4, "1275 AA", "Amsterdam")
    /// </summary>
    private static (string street, int number, string postal, string city) ParseAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return ("Unknown", 0, "", "");

        try
        {
            // Split by comma
            var parts = address.Split(',').Select(p => p.Trim()).ToArray();

            string street = "";
            int number = 0;
            string postal = "";
            string city = "";

            if (parts.Length >= 1)
            {
                // First part: "Hoofdweg 4" or "Hoofdweg 4A"
                var streetPart = parts[0];
                var match = System.Text.RegularExpressions.Regex.Match(streetPart, @"^(.+?)\s+(\d+[A-Za-z]?)$");
                if (match.Success)
                {
                    street = match.Groups[1].Value;
                    int.TryParse(new string(match.Groups[2].Value.Where(char.IsDigit).ToArray()), out number);
                }
                else
                {
                    street = streetPart;
                }
            }

            if (parts.Length >= 2)
            {
                // Second part might be postal code or city
                var secondPart = parts[1].Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(secondPart, @"\d{4}\s*[A-Z]{2}"))
                {
                    postal = secondPart;
                }
                else
                {
                    city = secondPart;
                }
            }

            if (parts.Length >= 3)
            {
                city = parts[2].Trim();
            }

            // If city is empty, try to infer from known locations
            if (string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(postal))
            {
                city = "Amsterdam"; // Default for NL postcodes
            }

            return (street, number, postal, city);
        }
        catch
        {
            return (address, 0, "", "");
        }
    }

    private static void Log(string msg) => OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
}

/// <summary>
/// BSQD API booking request model
/// </summary>
public class BsqdBookingRequest
{
    [JsonPropertyName("departure_address")]
    public BsqdAddress DepartureAddress { get; set; } = new();

    [JsonPropertyName("destination_address")]
    public BsqdAddress DestinationAddress { get; set; } = new();

    /// <summary>
    /// ISO 8601 format: "2026-02-02T10:45:00.000+01:00"
    /// </summary>
    [JsonPropertyName("departure_time")]
    public string DepartureTime { get; set; } = "";

    [JsonPropertyName("first_name")]
    public string FirstName { get; set; } = "";

    /// <summary>
    /// Price as string, e.g. "15.15"
    /// </summary>
    [JsonPropertyName("total_price")]
    public string TotalPrice { get; set; } = "";

    /// <summary>
    /// E.164 format: "+31652328530"
    /// </summary>
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = "";

    /// <summary>
    /// Passenger count as string
    /// </summary>
    [JsonPropertyName("passengers")]
    public string Passengers { get; set; } = "1";
}

/// <summary>
/// Address component for BSQD API
/// </summary>
public class BsqdAddress
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("street_name")]
    public string StreetName { get; set; } = "";

    [JsonPropertyName("street_number")]
    public int StreetNumber { get; set; }

    [JsonPropertyName("postal_code")]
    public string PostalCode { get; set; } = "";

    [JsonPropertyName("city")]
    public string City { get; set; } = "";

    /// <summary>
    /// Human-readable pickup address (only for departure)
    /// </summary>
    [JsonPropertyName("formatted_depa_address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormattedDepaAddress { get; set; }

    /// <summary>
    /// Human-readable destination address (only for destination)
    /// </summary>
    [JsonPropertyName("formatted_dest_address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormattedDestAddress { get; set; }
}
