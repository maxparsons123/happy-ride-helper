using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TaxiSipBridge.WinForms
{
    /// <summary>
    /// Reusable class for responding to taxi-passthrough-ws webhook requests.
    /// Usage: Return PassthroughResponse from your webhook handler, or use SendResponseAsync for async callbacks.
    /// </summary>
    public class PassthroughWebhook
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        #region Request Models

        /// <summary>
        /// Incoming webhook payload from taxi-passthrough-ws
        /// </summary>
        public class WebhookRequest
        {
            [JsonPropertyName("call_id")]
            public string CallId { get; set; }

            [JsonPropertyName("caller_phone")]
            public string CallerPhone { get; set; }

            [JsonPropertyName("transcript")]
            public string Transcript { get; set; }

            [JsonPropertyName("turn_number")]
            public int TurnNumber { get; set; }

            [JsonPropertyName("booking")]
            public BookingDetails Booking { get; set; }

            [JsonPropertyName("session_state")]
            public SessionState SessionState { get; set; }

            [JsonPropertyName("ada_last_response")]
            public string AdaLastResponse { get; set; }

            [JsonPropertyName("ada_pickup")]
            public string AdaPickup { get; set; }

            [JsonPropertyName("ada_destination")]
            public string AdaDestination { get; set; }
        }

        public class BookingDetails
        {
            [JsonPropertyName("pickup")]
            public string Pickup { get; set; }

            [JsonPropertyName("destination")]
            public string Destination { get; set; }

            [JsonPropertyName("passengers")]
            public string Passengers { get; set; }

            [JsonPropertyName("luggage")]
            public string Luggage { get; set; }

            [JsonPropertyName("intent")]
            public string Intent { get; set; }

            [JsonPropertyName("special_requests")]
            public string SpecialRequests { get; set; }
        }

        public class SessionState
        {
            [JsonPropertyName("pickup_validated")]
            public bool PickupValidated { get; set; }

            [JsonPropertyName("destination_validated")]
            public bool DestinationValidated { get; set; }

            [JsonPropertyName("pickup_lat")]
            public double? PickupLat { get; set; }

            [JsonPropertyName("pickup_lon")]
            public double? PickupLon { get; set; }

            [JsonPropertyName("destination_lat")]
            public double? DestinationLat { get; set; }

            [JsonPropertyName("destination_lon")]
            public double? DestinationLon { get; set; }

            [JsonPropertyName("fare")]
            public string Fare { get; set; }

            [JsonPropertyName("customer_name")]
            public string CustomerName { get; set; }

            // Add custom fields as needed
            [JsonExtensionData]
            public Dictionary<string, JsonElement> CustomFields { get; set; }
        }

        #endregion

        #region Response Models

        /// <summary>
        /// Response to send back to taxi-passthrough-ws
        /// </summary>
        public class WebhookResponse
        {
            /// <summary>
            /// Statement for Ada to speak (use for confirmations, info)
            /// </summary>
            [JsonPropertyName("ada_response")]
            public string AdaResponse { get; set; }

            /// <summary>
            /// Question for Ada to ask (use when you need more info)
            /// </summary>
            [JsonPropertyName("ada_question")]
            public string AdaQuestion { get; set; }

            /// <summary>
            /// Validated/formatted pickup address to store
            /// </summary>
            [JsonPropertyName("ada_pickup")]
            public string AdaPickup { get; set; }

            /// <summary>
            /// Validated/formatted destination address to store
            /// </summary>
            [JsonPropertyName("ada_destination")]
            public string AdaDestination { get; set; }

            /// <summary>
            /// Updated session state to persist across turns
            /// </summary>
            [JsonPropertyName("session_state")]
            public SessionState SessionState { get; set; }

            /// <summary>
            /// Set true when booking is complete and confirmed
            /// </summary>
            [JsonPropertyName("booking_confirmed")]
            public bool? BookingConfirmed { get; set; }

            /// <summary>
            /// Set true to end the call after speaking
            /// </summary>
            [JsonPropertyName("end_call")]
            public bool? EndCall { get; set; }
        }

        #endregion

        #region Response Builders

        /// <summary>
        /// Create a response where Ada speaks a statement
        /// </summary>
        public static WebhookResponse Say(string message, SessionState state = null)
        {
            return new WebhookResponse
            {
                AdaResponse = message,
                SessionState = state
            };
        }

        /// <summary>
        /// Create a response where Ada asks a question
        /// </summary>
        public static WebhookResponse Ask(string question, SessionState state = null)
        {
            return new WebhookResponse
            {
                AdaQuestion = question,
                SessionState = state
            };
        }

        /// <summary>
        /// Create a response confirming validated addresses
        /// </summary>
        public static WebhookResponse ConfirmAddresses(
            string message,
            string validatedPickup,
            string validatedDestination,
            SessionState state)
        {
            return new WebhookResponse
            {
                AdaResponse = message,
                AdaPickup = validatedPickup,
                AdaDestination = validatedDestination,
                SessionState = state
            };
        }

        /// <summary>
        /// Create a booking confirmation response
        /// </summary>
        public static WebhookResponse ConfirmBooking(
            string confirmationMessage,
            string pickup,
            string destination,
            string fare,
            SessionState state)
        {
            state ??= new SessionState();
            state.Fare = fare;
            state.PickupValidated = true;
            state.DestinationValidated = true;

            return new WebhookResponse
            {
                AdaResponse = confirmationMessage,
                AdaPickup = pickup,
                AdaDestination = destination,
                SessionState = state,
                BookingConfirmed = true
            };
        }

        /// <summary>
        /// Create a response that ends the call
        /// </summary>
        public static WebhookResponse EndCall(string goodbyeMessage)
        {
            return new WebhookResponse
            {
                AdaResponse = goodbyeMessage,
                EndCall = true
            };
        }

        #endregion

        #region Parsing

        /// <summary>
        /// Parse incoming webhook JSON to WebhookRequest
        /// </summary>
        public static WebhookRequest ParseRequest(string json)
        {
            return JsonSerializer.Deserialize<WebhookRequest>(json, _jsonOptions);
        }

        /// <summary>
        /// Serialize response to JSON for HTTP response body
        /// </summary>
        public static string SerializeResponse(WebhookResponse response)
        {
            return JsonSerializer.Serialize(response, _jsonOptions);
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
}
