using System.Text.Json.Serialization;

namespace TaxiSipBridge.WinForms;

/// <summary>
/// Represents the dispatch webhook payload sent by Ada after booking confirmation.
/// </summary>
public class AdaDispatchRequest
{
    /// <summary>
    /// The action type: "request_quote" or "confirmed"
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier for the call session
    /// </summary>
    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = string.Empty;

    /// <summary>
    /// Pickup address
    /// </summary>
    [JsonPropertyName("pickup")]
    public string Pickup { get; set; } = string.Empty;

    /// <summary>
    /// Destination address
    /// </summary>
    [JsonPropertyName("destination")]
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// Number of passengers
    /// </summary>
    [JsonPropertyName("passengers")]
    public int Passengers { get; set; }

    /// <summary>
    /// Pickup time: "now" or a specific time string
    /// </summary>
    [JsonPropertyName("time")]
    public string Time { get; set; } = "now";

    /// <summary>
    /// Optional caller phone number
    /// </summary>
    [JsonPropertyName("caller_phone")]
    public string? CallerPhone { get; set; }

    /// <summary>
    /// Optional caller name
    /// </summary>
    [JsonPropertyName("caller_name")]
    public string? CallerName { get; set; }
}

/// <summary>
/// Response to send back to Ada with fare quote
/// </summary>
public class AdaDispatchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("fare")]
    public string? Fare { get; set; }

    [JsonPropertyName("eta")]
    public string? Eta { get; set; }

    [JsonPropertyName("booking_id")]
    public string? BookingId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
