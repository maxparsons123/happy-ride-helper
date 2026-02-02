using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TaxiSipBridge.WinForms;

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

public class TaxiBotClient
{
    private static readonly string Url =
        "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/voice_AI_taxibot";

    private static readonly string BearerToken = "RhjpZxqLXl2snLNMBwK7iIVq"; // Same as WhatsApp notifier

    public static event Action<string>? OnLog;

    /// <summary>
    /// Fire-and-forget dispatch with logging
    /// </summary>
    public static void DispatchBooking(TaxiBotRequest payload)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(payload);
            }
            catch (Exception ex)
            {
                Log($"❌ TaxiBot Dispatch ERROR: {ex.Message}");
            }
        });
    }

    public static async Task SendAsync(TaxiBotRequest payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Log($"🚕 TaxiBot Dispatch → {payload.phoneNumber}");
        Log($"📦 Payload: {json}");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BearerToken);

        var response = await client.PostAsync(Url, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            Log($"✅ TaxiBot Dispatch OK: {(int)response.StatusCode}");
        }
        else
        {
            Log($"❌ TaxiBot Dispatch FAIL: {(int)response.StatusCode} - {responseBody}");
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Helper to create request from call data
    /// </summary>
    public static TaxiBotRequest CreateFromCallData(
        string phoneNumber,
        string? firstName,
        string? pickup,
        double pickupLat,
        double pickupLon,
        string? destination,
        double destLat,
        double destLon,
        DateTimeOffset? departureTime,
        string? fare,
        int passengers = 1)
    {
        var (pickupStreet, pickupNumber, pickupPostal, pickupCity) = ParseAddress(pickup);
        var (destStreet, destNumber, destPostal, destCity) = ParseAddress(destination);

        return new TaxiBotRequest
        {
            departure_address = new AddressDto
            {
                lat = pickupLat,
                lon = pickupLon,
                street_name = pickupStreet,
                street_number = pickupNumber,
                postal_code = pickupPostal,
                city = pickupCity,
                formatted_depa_address = pickup ?? "Unknown"
            },
            destination_address = new AddressDto
            {
                lat = destLat,
                lon = destLon,
                street_name = destStreet,
                street_number = destNumber,
                postal_code = destPostal,
                city = destCity,
                formatted_dest_address = destination ?? "Unknown"
            },
            departure_time = departureTime ?? DateTimeOffset.Now,
            first_name = firstName ?? "Customer",
            total_price = ParseFare(fare),
            phoneNumber = FormatE164(phoneNumber),
            passengers = passengers.ToString()
        };
    }

    private static string FormatE164(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "+31000000000";

        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());

        if (clean.StartsWith("00"))
            clean = "+" + clean.Substring(2);

        if (!clean.StartsWith("+"))
            clean = "+" + clean;

        if (clean.StartsWith("+310") && clean.Length > 11)
            clean = "+31" + clean.Substring(4);

        return clean;
    }

    private static string ParseFare(string? fare)
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

    private static (string street, int number, string postal, string city) ParseAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return ("Unknown", 0, "", "");

        try
        {
            var parts = address.Split(',').Select(p => p.Trim()).ToArray();

            string street = "";
            int number = 0;
            string postal = "";
            string city = "";

            if (parts.Length >= 1)
            {
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

            if (string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(postal))
            {
                city = "Amsterdam";
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
