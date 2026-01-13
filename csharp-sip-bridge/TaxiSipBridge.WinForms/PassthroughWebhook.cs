using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TaxiSipBridge.WinForms
{
    /// <summary>
    /// Reusable class for responding to taxi-passthrough-ws webhook requests.
    /// Usage: Return WebhookResponse from your webhook handler, or use SendResponseAsync for async callbacks.
    /// </summary>
    public static class PassthroughWebhook
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        #region Parsing & Serialization

        /// <summary>
        /// Parse incoming webhook JSON to WebhookRequest
        /// </summary>
        public static WebhookRequest ParseRequest(string json)
        {
            return JsonSerializer.Deserialize<WebhookRequest>(json, _jsonOptions) ?? new WebhookRequest();
        }

        /// <summary>
        /// Serialize response to JSON for HTTP response body
        /// </summary>
        public static string SerializeResponse(WebhookResponse response)
        {
            return JsonSerializer.Serialize(response, _jsonOptions);
        }

        #endregion

        #region Response Builders

        /// <summary>
        /// Ada makes a statement (no question)
        /// </summary>
        public static WebhookResponse Say(string message, Dictionary<string, object> sessionState = null)
        {
            return new WebhookResponse
            {
                AdaResponse = message,
                SessionState = sessionState ?? new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Ada asks a question
        /// </summary>
        public static WebhookResponse Ask(string question, Dictionary<string, object> sessionState = null)
        {
            return new WebhookResponse
            {
                AdaQuestion = question,
                SessionState = sessionState ?? new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Confirm validated addresses (pickup and/or destination)
        /// </summary>
        public static WebhookResponse ConfirmAddresses(
            string adaMessage,
            string validatedPickup = null,
            string validatedDestination = null,
            Dictionary<string, object> sessionState = null)
        {
            return new WebhookResponse
            {
                AdaResponse = adaMessage,
                AdaPickup = validatedPickup,
                AdaDestination = validatedDestination,
                SessionState = sessionState ?? new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Confirm the booking is complete
        /// </summary>
        public static WebhookResponse ConfirmBooking(
            string confirmationMessage,
            string pickup,
            string destination,
            string fare,
            Dictionary<string, object> sessionState = null)
        {
            var state = sessionState ?? new Dictionary<string, object>();
            state["booking_confirmed"] = true;
            state["fare"] = fare;

            return new WebhookResponse
            {
                AdaResponse = confirmationMessage,
                AdaPickup = pickup,
                AdaDestination = destination,
                BookingConfirmed = true,
                SessionState = state
            };
        }

        /// <summary>
        /// End the call gracefully
        /// </summary>
        public static WebhookResponse EndCall(string goodbyeMessage = null)
        {
            return new WebhookResponse
            {
                EndCall = true,
                EndMessage = goodbyeMessage ?? "Thank you for calling. Goodbye!"
            };
        }

        /// <summary>
        /// Handle an error gracefully
        /// </summary>
        public static WebhookResponse Error(string fallbackMessage = null)
        {
            return new WebhookResponse
            {
                AdaResponse = fallbackMessage ?? "Sorry, I had a small technical issue. Could you repeat that?"
            };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get a value from session state with type conversion
        /// </summary>
        public static T GetSessionValue<T>(WebhookRequest request, string key, T defaultValue = default)
        {
            if (request?.SessionState == null || !request.SessionState.TryGetValue(key, out var value))
                return defaultValue;

            try
            {
                if (value is JsonElement element)
                {
                    if (typeof(T) == typeof(double))
                        return (T)(object)element.GetDouble();
                    if (typeof(T) == typeof(int))
                        return (T)(object)element.GetInt32();
                    if (typeof(T) == typeof(bool))
                        return (T)(object)element.GetBoolean();
                    if (typeof(T) == typeof(string))
                        return (T)(object)element.GetString();
                }

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Check if we have a valid pickup address
        /// </summary>
        public static bool HasPickup(WebhookRequest request)
        {
            return !string.IsNullOrWhiteSpace(request?.AddressSources?.Stt?.Pickup)
                || !string.IsNullOrWhiteSpace(request?.AddressSources?.Ada?.Pickup)
                || !string.IsNullOrWhiteSpace(request?.Booking?.Pickup);
        }

        /// <summary>
        /// Check if we have a valid destination address
        /// </summary>
        public static bool HasDestination(WebhookRequest request)
        {
            return !string.IsNullOrWhiteSpace(request?.AddressSources?.Stt?.Destination)
                || !string.IsNullOrWhiteSpace(request?.AddressSources?.Ada?.Destination)
                || !string.IsNullOrWhiteSpace(request?.Booking?.Destination);
        }

        /// <summary>
        /// Get the best available pickup address (STT preferred over Ada)
        /// </summary>
        public static string GetBestPickup(WebhookRequest request)
        {
            return request?.AddressSources?.Stt?.Pickup
                ?? request?.AddressSources?.Ada?.Pickup
                ?? request?.Booking?.Pickup;
        }

        /// <summary>
        /// Get the best available destination address (STT preferred over Ada)
        /// </summary>
        public static string GetBestDestination(WebhookRequest request)
        {
            return request?.AddressSources?.Stt?.Destination
                ?? request?.AddressSources?.Ada?.Destination
                ?? request?.Booking?.Destination;
        }

        #endregion

        #region Async Callback (Optional)

        /// <summary>
        /// Send response asynchronously to a callback URL (if not returning directly)
        /// </summary>
        public static async Task<bool> SendResponseAsync(string callbackUrl, WebhookResponse response)
        {
            try
            {
                var json = SerializeResponse(response);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var result = await _httpClient.PostAsync(callbackUrl, content);
                return result.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PassthroughWebhook] Error sending response: {ex.Message}");
                return false;
            }
        }

        #endregion
    }

    // -----------------------------------------------------------------
    // REQUEST MODELS
    // -----------------------------------------------------------------

    public class WebhookRequest
    {
        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("caller_phone")]
        public string CallerPhone { get; set; }

        [JsonPropertyName("caller_name")]
        public string CallerName { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }

        [JsonPropertyName("transcript")]
        public string Transcript { get; set; }

        [JsonPropertyName("turn_number")]
        public int TurnNumber { get; set; }

        [JsonPropertyName("booking")]
        public BookingInfo Booking { get; set; }

        [JsonPropertyName("address_sources")]
        public AddressSources AddressSources { get; set; }

        [JsonPropertyName("session_state")]
        public Dictionary<string, object> SessionState { get; set; } = new();

        [JsonPropertyName("conversation")]
        public List<ConversationTurn> Conversation { get; set; } = new();

        [JsonPropertyName("ada_last_response")]
        public string AdaLastResponse { get; set; }
    }

    public class BookingInfo
    {
        [JsonPropertyName("pickup")]
        public string Pickup { get; set; }

        [JsonPropertyName("destination")]
        public string Destination { get; set; }

        [JsonPropertyName("passengers")]
        public int? Passengers { get; set; }

        [JsonPropertyName("pickup_time")]
        public string PickupTime { get; set; }

        [JsonPropertyName("luggage")]
        public string Luggage { get; set; }

        [JsonPropertyName("special_requests")]
        public string SpecialRequests { get; set; }

        [JsonPropertyName("intent")]
        public string Intent { get; set; }

        [JsonPropertyName("confirmed")]
        public bool? Confirmed { get; set; }
    }

    public class AddressSources
    {
        [JsonPropertyName("stt")]
        public AddressSource Stt { get; set; }

        [JsonPropertyName("ada")]
        public AddressSource Ada { get; set; }
    }

    public class AddressSource
    {
        [JsonPropertyName("pickup")]
        public string Pickup { get; set; }

        [JsonPropertyName("destination")]
        public string Destination { get; set; }
    }

    public class ConversationTurn
    {
        [JsonPropertyName("role")]
        public string Role { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }
    }

    // -----------------------------------------------------------------
    // RESPONSE MODELS
    // -----------------------------------------------------------------

    public class WebhookResponse
    {
        [JsonPropertyName("ada_response")]
        public string AdaResponse { get; set; }

        [JsonPropertyName("ada_question")]
        public string AdaQuestion { get; set; }

        [JsonPropertyName("ada_pickup")]
        public string AdaPickup { get; set; }

        [JsonPropertyName("ada_destination")]
        public string AdaDestination { get; set; }

        [JsonPropertyName("booking_confirmed")]
        public bool? BookingConfirmed { get; set; }

        [JsonPropertyName("end_call")]
        public bool? EndCall { get; set; }

        [JsonPropertyName("end_message")]
        public string EndMessage { get; set; }

        [JsonPropertyName("session_state")]
        public Dictionary<string, object> SessionState { get; set; } = new();
    }

    // -----------------------------------------------------------------
    // VALIDATION RESULT (for your address validation)
    // -----------------------------------------------------------------

    public class AddressValidationResult
    {
        public bool IsValid { get; set; }
        public string FormattedAddress { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string Source { get; set; } // "memory", "postcode", "google"
        public string ErrorMessage { get; set; }
    }
}
