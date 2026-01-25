using System.Text.Json.Serialization;

namespace TaxiSipBridge.WinForms;

/// <summary>
/// Represents the dispatch webhook payload sent by Ada.
/// </summary>
public class AdaDispatchRequest
{
    /// <summary>
    /// URL to send the response back to Ada
    /// </summary>
    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; set; }

    /// <summary>
    /// Unique identifier for the call session
    /// </summary>
    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = string.Empty;

    /// <summary>
    /// Action type: say | ask | booked | quote | error | end
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Message content from Ada
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Caller's phone number - REQUIRED for dispatch
    /// </summary>
    [JsonPropertyName("caller_phone")]
    public string CallerPhone { get; set; } = string.Empty;

    /// <summary>
    /// Fare amount (e.g. "£12.50")
    /// </summary>
    [JsonPropertyName("fare")]
    public string? Fare { get; set; }

    /// <summary>
    /// Estimated time of arrival in minutes
    /// </summary>
    [JsonPropertyName("eta_minutes")]
    public int? EtaMinutes { get; set; }

    /// <summary>
    /// Booking reference number
    /// </summary>
    [JsonPropertyName("booking_ref")]
    public string? BookingRef { get; set; }

    /// <summary>
    /// Additional call action instruction
    /// </summary>
    [JsonPropertyName("call_action")]
    public string? CallAction { get; set; }

    /// <summary>
    /// Pickup address
    /// </summary>
    [JsonPropertyName("pickup")]
    public string? Pickup { get; set; }

    /// <summary>
    /// Destination address
    /// </summary>
    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    /// <summary>
    /// Number of passengers
    /// </summary>
    [JsonPropertyName("passengers")]
    public int? Passengers { get; set; }

    /// <summary>
    /// Pickup time
    /// </summary>
    [JsonPropertyName("time")]
    public string? Time { get; set; }
}

/// <summary>
/// Response to send back to Ada
/// </summary>
public class AdaDispatchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("fare")]
    public string? Fare { get; set; }

    [JsonPropertyName("eta_minutes")]
    public int? EtaMinutes { get; set; }

    [JsonPropertyName("booking_ref")]
    public string? BookingRef { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
