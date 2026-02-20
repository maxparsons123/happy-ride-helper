using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdaSdkModel.Config;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Services;

/// <summary>
/// Generates SumUp payment checkout links for fixed-price taxi bookings.
/// Uses the SumUp Checkouts API (POST /v0.1/checkouts).
/// </summary>
public sealed class SumUpService
{
    private readonly ILogger<SumUpService> _logger;
    private readonly SumUpSettings _settings;
    private readonly HttpClient _httpClient;

    private const string BaseUrl = "https://api.sumup.com/v0.1";

    public SumUpService(ILogger<SumUpService> logger, SumUpSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
    }

    /// <summary>
    /// Creates a SumUp checkout for the given amount and returns the payment URL.
    /// Returns null if SumUp is disabled, the API key is missing, or the call fails.
    /// </summary>
    public async Task<string?> CreateCheckoutLinkAsync(
        string bookingRef,
        decimal amount,
        string description,
        string? callerPhone = null)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("[SumUp] Disabled — skipping checkout creation");
            return null;
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _logger.LogWarning("[SumUp] No API key configured — cannot create checkout");
            return null;
        }

        try
        {
            // Build a unique checkout reference (SumUp requires unique IDs per merchant)
            var checkoutRef = $"{_settings.MerchantCode}-{bookingRef}-{DateTime.UtcNow:HHmmss}";

            var payload = new
            {
                checkout_reference = checkoutRef,
                amount = Math.Round(amount, 2),
                currency = _settings.Currency,
                merchant_code = _settings.MerchantCode,
                description = description,
                // Optional: redirect caller name as pay-to label
                pay_to_email = (string?)null   // can be set if merchant email is known
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            _logger.LogInformation("[SumUp] Creating checkout: ref={Ref}, amount={Amount} {Currency}",
                checkoutRef, amount, _settings.Currency);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/checkouts");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[SumUp] Checkout creation failed [{Status}]: {Body}",
                    (int)response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // SumUp returns the checkout ID — construct the hosted payment URL
            if (root.TryGetProperty("id", out var idEl))
            {
                var checkoutId = idEl.GetString();
                var paymentUrl = $"https://pay.sumup.com/b2c/checkout/{checkoutId}";
                _logger.LogInformation("[SumUp] ✅ Checkout created — id={Id}, url={Url}", checkoutId, paymentUrl);
                return paymentUrl;
            }

            _logger.LogWarning("[SumUp] Checkout response missing 'id' field: {Body}", body);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SumUp] Exception creating checkout for {BookingRef}", bookingRef);
            return null;
        }
    }
}
