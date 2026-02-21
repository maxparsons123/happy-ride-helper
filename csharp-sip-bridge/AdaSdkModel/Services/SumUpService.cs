using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaSdkModel.Config;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Services;

/// <summary>
/// Generates SumUp payment checkout links for fixed-price taxi bookings.
/// Uses direct REST API calls for maximum compatibility with sup_sk_ API keys.
/// </summary>
public sealed class SumUpService : IDisposable
{
    private readonly ILogger<SumUpService> _logger;
    private readonly SumUpSettings _settings;
    private readonly HttpClient _http;

    public SumUpService(ILogger<SumUpService> logger, SumUpSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _http = new HttpClient { BaseAddress = new Uri("https://api.sumup.com/"), Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
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
            var checkoutRef = $"{_settings.MerchantCode}-{bookingRef}-{DateTime.UtcNow:HHmmss}";

            _logger.LogInformation("[SumUp] Creating checkout: ref={Ref}, amount={Amount} {Currency}",
                checkoutRef, amount, _settings.Currency ?? "GBP");

            var roundedAmount = (double)Math.Round(amount, 2);
            var curr = _settings.Currency?.ToUpperInvariant() ?? "GBP";

            var payload = new
            {
                amount = roundedAmount,
                currency = curr,
                checkout_reference = checkoutRef,
                merchant_code = _settings.MerchantCode,
                description
            };

            var json = JsonSerializer.Serialize(payload);
            _logger.LogInformation("[SumUp] POST payload: {Json}", json);

            using var req = new HttpRequestMessage(HttpMethod.Post, "v0.1/checkouts")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[SumUp] HTTP {Status}: {Body}", (int)resp.StatusCode, body);
                return null;
            }

            var result = JsonSerializer.Deserialize<SumUpCheckoutResponse>(body);
            var checkoutId = result?.Id;

            if (string.IsNullOrWhiteSpace(checkoutId))
            {
                _logger.LogWarning("[SumUp] Response missing 'id': {Body}", body);
                return null;
            }

            var paymentUrl = $"https://pay.sumup.com/b2c/checkout/{checkoutId}";
            _logger.LogInformation("[SumUp] ✅ Checkout created — id={Id}, url={Url}", checkoutId, paymentUrl);
            return paymentUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SumUp] Exception creating checkout for {BookingRef}: {Type}: {Msg} | Inner: {Inner}",
                bookingRef, ex.GetType().Name, ex.Message, ex.InnerException?.Message ?? "none");
            return null;
        }
    }

    public void Dispose() => _http?.Dispose();

    private sealed class SumUpCheckoutResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
