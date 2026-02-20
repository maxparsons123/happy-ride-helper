using AdaSdkModel.Config;
using Microsoft.Extensions.Logging;
using SumUp;
using SumUp.Models;

namespace AdaSdkModel.Services;

/// <summary>
/// Generates SumUp payment checkout links for fixed-price taxi bookings.
/// Uses the official SumUp .NET SDK.
/// </summary>
public sealed class SumUpService
{
    private readonly ILogger<SumUpService> _logger;
    private readonly SumUpSettings _settings;
    private readonly SumUpClient _client;

    public SumUpService(ILogger<SumUpService> logger, SumUpSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _client = new SumUpClient(new SumUpClientOptions
        {
            AccessToken = _settings.ApiKey
        });
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

            // Map currency string (e.g. "GBP") to SDK enum (e.g. Currency.Gbp)
            var currency = _settings.Currency?.ToUpperInvariant() switch
            {
                "GBP" => Currency.Gbp,
                "EUR" => Currency.Eur,
                "USD" => Currency.Usd,
                _ => Currency.Gbp  // default
            };

            _logger.LogInformation("[SumUp] Creating checkout: ref={Ref}, amount={Amount} {Currency}",
                checkoutRef, amount, currency);

            var response = await _client.Checkouts.CreateAsync(new CheckoutCreateRequest
            {
                Amount = (float)Math.Round(amount, 2),
                Currency = currency,
                CheckoutReference = checkoutRef,
                MerchantCode = _settings.MerchantCode,
                Description = description
            });

            var checkoutId = response.Data?.Id;
            if (string.IsNullOrWhiteSpace(checkoutId))
            {
                _logger.LogWarning("[SumUp] Checkout response missing 'id'");
                return null;
            }

            var paymentUrl = $"https://pay.sumup.com/b2c/checkout/{checkoutId}";
            _logger.LogInformation("[SumUp] ✅ Checkout created — id={Id}, url={Url}", checkoutId, paymentUrl);
            return paymentUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SumUp] Exception creating checkout for {BookingRef}", bookingRef);
            return null;
        }
    }
}
