using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhatsAppTaxiBooker.Config;
using WhatsAppTaxiBooker.Helpers;
using WhatsAppTaxiBooker.Models;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// iCabbi booking API client.
/// Aligned with validated integration: Basic auth, Phone header, CamelCase, site_id, vehicle_group mapping.
/// </summary>
public sealed class IcabbiService
{
    private readonly IcabbiConfig _config;
    private static readonly HttpClient _http;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public event Action<string>? OnLog;
    private void Log(string msg) => OnLog?.Invoke(msg);

    static IcabbiService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public IcabbiService(IcabbiConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Submit a confirmed booking to iCabbi.
    /// </summary>
    public async Task<string?> CreateBookingAsync(Booking booking)
    {
        if (!_config.Enabled)
        {
            Log("⚪ [iCabbi] Disabled, skipping submission");
            return null;
        }

        Log($"📤 [iCabbi] Submitting booking {booking.Id}...");

        try
        {
            var phone = FormatPhoneForIcabbi(booking.Phone);
            var vehicleGroup = MapVehicleGroup(booking.Passengers);

            var payload = new
            {
                site_id = _config.SiteId,
                source = "APP",
                vehicle_group = vehicleGroup,
                pickup_location = new
                {
                    address = booking.Pickup,
                    lat = booking.PickupLat ?? 0.0,
                    lng = booking.PickupLng ?? 0.0
                },
                destination_location = new
                {
                    address = booking.Destination,
                    lat = booking.DropoffLat ?? 0.0,
                    lng = booking.DropoffLng ?? 0.0
                },
                passenger_count = booking.Passengers,
                passenger_name = booking.CallerName ?? "WhatsApp Customer",
                notes = booking.Notes ?? "",
                extras = "WHATSAPP",
                pickup_time = ParsePickupTime(booking.PickupTime),
                app_metadata = new
                {
                    source = "whatsapp_taxi_booker",
                    booking_ref = booking.Id,
                    whatsapp_phone = booking.Phone
                }
            };

            var json = JsonSerializer.Serialize(payload, _jsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{_config.TenantBase.TrimEnd('/')}/api/v2/bookings";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);

            // Basic auth: AppKey:SecretKey
            var authBytes = Encoding.UTF8.GetBytes($"{_config.AppKey}:{_config.SecretKey}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            req.Headers.Add("Phone", phone);
            req.Content = content;

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                Log($"✅ [iCabbi] Booking created: {body[..Math.Min(200, body.Length)]}");

                // Try to extract trip/booking ID from response
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("id", out var id))
                        return id.ToString();
                    if (doc.RootElement.TryGetProperty("booking_id", out var bid))
                        return bid.ToString();
                }
                catch { }

                return "OK";
            }

            Log($"⚠️ [iCabbi] Create failed {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");
            return null;
        }
        catch (Exception ex)
        {
            Log($"❌ [iCabbi] Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Format phone for iCabbi: prefix with '00', strip '+' and spaces.
    /// </summary>
    private static string FormatPhoneForIcabbi(string phone)
    {
        phone = PhoneFormatter.ToWhatsAppFormat(phone); // normalise to 44...
        return "00" + phone; // iCabbi expects 0044...
    }

    /// <summary>
    /// Map passenger count to iCabbi vehicle group.
    /// R4 = standard (1-4), R6 = estate (5-6), R7 = MPV (7+)
    /// </summary>
    private static string MapVehicleGroup(int passengers) => passengers switch
    {
        <= 4 => "Taxi",
        <= 6 => "Estate",
        _ => "MPV"
    };

    private static string? ParsePickupTime(string? pickupTime)
    {
        if (string.IsNullOrWhiteSpace(pickupTime) || pickupTime.Equals("now", StringComparison.OrdinalIgnoreCase))
            return DateTime.UtcNow.ToString("o");
        if (DateTime.TryParse(pickupTime, out var dt))
            return dt.ToUniversalTime().ToString("o");
        return DateTime.UtcNow.ToString("o");
    }
}
