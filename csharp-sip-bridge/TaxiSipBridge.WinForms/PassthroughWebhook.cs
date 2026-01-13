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
        /// Parse incoming webhook JSON to WebhookRequest (for passthrough mode)
        /// </summary>
        public static WebhookRequest ParseRequest(string json)
        {
            return JsonSerializer.Deserialize<WebhookRequest>(json, _jsonOptions) ?? new WebhookRequest();
        }

        /// <summary>
        /// Parse Ada booking webhook payload (sent to DISPATCH_WEBHOOK_URL)
        /// </summary>
        public static AdaBookingPayload ParseAdaBookingPayload(string json)
        {
            return JsonSerializer.Deserialize<AdaBookingPayload>(json, _jsonOptions) ?? new AdaBookingPayload();
        }

        /// <summary>
        /// Parse RAW PASSTHROUGH webhook payload (sent when bookingMode="raw")
        /// Ada collects details without validation, your system handles all validation.
        /// </summary>
        public static RawPassthroughPayload ParseRawPassthroughPayload(string json)
        {
            return JsonSerializer.Deserialize<RawPassthroughPayload>(json, _jsonOptions) ?? new RawPassthroughPayload();
        }

        /// <summary>
        /// Convert RAW passthrough payload to your BookTaxiResponse for validation
        /// </summary>
        public static BookTaxiResponse ToBookTaxiResponse(RawPassthroughPayload payload)
        {
            return new BookTaxiResponse
            {
                pickup_location = payload.Pickup ?? "",
                dropoff_location = payload.Destination ?? "",
                pickup_time = payload.PickupTime ?? "",
                number_of_passengers = payload.Passengers,
                luggage = payload.Luggage ?? "",
                username = payload.CallerName ?? "",
                usertelephone = payload.Phone ?? "",
                jobid = payload.CallId ?? "",
                vehicle_type = payload.VehicleType ?? "",
                timestamp = payload.Timestamp ?? ""
            };
        }

        /// <summary>
        /// Convert Ada booking payload to your BookTaxiResponse for processing
        /// </summary>
        public static BookTaxiResponse ToBookTaxiResponse(AdaBookingPayload payload)
        {
            return new BookTaxiResponse
            {
                pickup_location = payload.Pickup ?? "",
                dropoff_location = payload.Destination ?? "",
                pickup_time = payload.PickupTime ?? "",
                number_of_passengers = payload.Passengers,
                luggage = payload.Luggage ?? "",
                reference_number = payload.Reference ?? "",
                username = payload.CallerName ?? "",
                usertelephone = payload.Phone ?? "",
                jobid = payload.CallId ?? "",
                Rawjson = "",
                bookingmessage = "",
                pickup_name = payload.PickupName ?? "",
                destination_name = payload.DestinationName ?? "",
                vehicle_type = payload.VehicleType ?? "",
                is_asap = payload.IsAsap,
                scheduled_for = payload.ScheduledFor ?? "",
                estimated_fare = payload.EstimatedFare ?? "",
                estimated_eta = payload.EstimatedEta ?? "",
                pickup_verified = payload.PickupVerified,
                destination_verified = payload.DestinationVerified,
                timestamp = payload.Timestamp ?? ""
            };
        }

        /// <summary>
        /// Serialize WebhookResponse to JSON for HTTP response body
        /// </summary>
        public static string SerializeResponse(WebhookResponse response)
        {
            return JsonSerializer.Serialize(response, _jsonOptions);
        }

        /// <summary>
        /// Serialize RawPassthroughResponse to JSON for HTTP response body
        /// </summary>
        public static string SerializeResponse(RawPassthroughResponse response)
        {
            return JsonSerializer.Serialize(response, _jsonOptions);
        }

        #endregion

        #region Raw Passthrough Response Builders

        /// <summary>
        /// Ask Ada to request clarification from the caller
        /// </summary>
        public static RawPassthroughResponse AskClarification(string question)
        {
            return new RawPassthroughResponse
            {
                NeedsClarification = true,
                ClarificationQuestion = question
            };
        }

        /// <summary>
        /// Confirm booking with fare and ETA
        /// </summary>
        public static RawPassthroughResponse ConfirmBooking(string fare, int etaMinutes, string jobId = null, string message = null)
        {
            return new RawPassthroughResponse
            {
                NeedsClarification = false,
                Fare = fare,
                EtaMinutes = etaMinutes,
                Eta = $"{etaMinutes} minutes",
                JobId = jobId ?? "",
                Message = message ?? $"Booked! Your taxi will be with you in {etaMinutes} minutes, the fare is {fare}. Is there anything else I can help with?"
            };
        }

        /// <summary>
        /// Confirm booking with custom message
        /// </summary>
        public static RawPassthroughResponse ConfirmWithMessage(string message, string jobId = null)
        {
            return new RawPassthroughResponse
            {
                NeedsClarification = false,
                Message = message,
                JobId = jobId ?? ""
            };
        }

        /// <summary>
        /// Report an error to Ada
        /// </summary>
        public static RawPassthroughResponse Error(string message)
        {
            return new RawPassthroughResponse
            {
                NeedsClarification = true,
                ClarificationQuestion = message
            };
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
        /// Confirm the booking is complete with all details
        /// </summary>
        public static WebhookResponse ConfirmBooking(
            string pickup,
            string destination,
            string fare,
            int etaMinutes,
            int passengers = 1,
            string vehicleType = null,
            double? distanceMiles = null,
            string bookingRef = null,
            string customMessage = null,
            Dictionary<string, object> sessionState = null)
        {
            var state = sessionState ?? new Dictionary<string, object>();
            state["booking_confirmed"] = true;

            // Build confirmation message if not provided
            string message = customMessage ?? 
                $"Brilliant! That's booked. The fare is £{fare} and your driver will be with you in {etaMinutes} minutes.";

            return new WebhookResponse
            {
                AdaResponse = message,
                AdaPickup = pickup,
                AdaDestination = destination,
                Fare = fare,
                EtaMinutes = etaMinutes,
                Passengers = passengers,
                VehicleType = vehicleType,
                DistanceMiles = distanceMiles,
                BookingRef = bookingRef,
                BookingConfirmed = true,
                SessionState = state
            };
        }

        /// <summary>
        /// Simple booking confirmation (backwards compatible)
        /// </summary>
        public static WebhookResponse ConfirmBookingSimple(
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
                Fare = fare,
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

        /// <summary>
        /// Convert incoming WebhookRequest to your BookTaxiResponse for processing
        /// </summary>
        public static BookTaxiResponse ToBookTaxiResponse(WebhookRequest request)
        {
            return new BookTaxiResponse
            {
                pickup_location = request?.AddressSources?.Stt?.Pickup 
                    ?? request?.AddressSources?.Ada?.Pickup 
                    ?? request?.Booking?.Pickup ?? "",
                dropoff_location = request?.AddressSources?.Stt?.Destination 
                    ?? request?.AddressSources?.Ada?.Destination 
                    ?? request?.Booking?.Destination ?? "",
                pickup_time = request?.Booking?.PickupTime ?? "",
                number_of_passengers = request?.Booking?.Passengers ?? 1,
                luggage = request?.Booking?.Luggage ?? "",
                special_requests = request?.Booking?.SpecialRequests ?? "",
                username = request?.CallerName ?? "",
                usertelephone = request?.CallerPhone ?? "",
                jobid = request?.CallId ?? "",
                Rawjson = "",
                bookingmessage = "",
                userlat = GetSessionValue(request, "pickup_lat", 0.0),
                userlon = GetSessionValue(request, "pickup_lon", 0.0)
            };
        }

        /// <summary>
        /// Convert your existing BookTaxiResponse to a webhook response for Ada
        /// </summary>
        public static WebhookResponse FromBookTaxiResponse(
            BookTaxiResponse booking,
            string message = null,
            bool isConfirmed = true,
            Dictionary<string, object> sessionState = null)
        {
            var state = sessionState ?? new Dictionary<string, object>();
            
            // Store coordinates in session for future fare calculations
            if (booking.userlat != 0) state["pickup_lat"] = booking.userlat;
            if (booking.userlon != 0) state["pickup_lon"] = booking.userlon;
            state["validated_pickup"] = booking.pickup_location;
            state["validated_destination"] = booking.dropoff_location;
            state["booking_ref"] = booking.reference_number;
            state["job_id"] = booking.jobid;
            state["booking_confirmed"] = isConfirmed;

            // Build message if not provided
            string adaMessage = message ?? booking.bookingmessage;
            if (string.IsNullOrWhiteSpace(adaMessage) && isConfirmed)
            {
                adaMessage = $"That's booked! Your reference is {booking.reference_number}. " +
                             $"Driver on the way to {booking.pickup_location}.";
            }

            return new WebhookResponse
            {
                AdaResponse = isConfirmed ? adaMessage : null,
                AdaQuestion = isConfirmed ? null : adaMessage,
                AdaPickup = booking.pickup_location,
                AdaDestination = booking.dropoff_location,
                Passengers = booking.number_of_passengers > 0 ? booking.number_of_passengers : null,
                BookingRef = booking.reference_number,
                BookingConfirmed = isConfirmed,
                SessionState = state
            };
        }

        /// <summary>
        /// Create a booking confirmation response from BookTaxiResponse with fare/ETA
        /// </summary>
        public static WebhookResponse ConfirmFromBookTaxiResponse(
            BookTaxiResponse booking,
            string fare,
            int etaMinutes,
            string vehicleType = null,
            double? distanceMiles = null,
            string customMessage = null)
        {
            var state = new Dictionary<string, object>
            {
                ["pickup_lat"] = booking.userlat,
                ["pickup_lon"] = booking.userlon,
                ["validated_pickup"] = booking.pickup_location,
                ["validated_destination"] = booking.dropoff_location,
                ["booking_ref"] = booking.reference_number,
                ["job_id"] = booking.jobid,
                ["booking_confirmed"] = true
            };

            string message = customMessage ?? 
                $"Brilliant! That's booked from {booking.pickup_location} to {booking.dropoff_location}. " +
                $"The fare is £{fare} and your driver will be with you in {etaMinutes} minutes. " +
                $"Your reference is {booking.reference_number}.";

            return new WebhookResponse
            {
                AdaResponse = message,
                AdaPickup = booking.pickup_location,
                AdaDestination = booking.dropoff_location,
                Fare = fare,
                EtaMinutes = etaMinutes,
                Passengers = booking.number_of_passengers > 0 ? booking.number_of_passengers : null,
                VehicleType = vehicleType,
                DistanceMiles = distanceMiles,
                BookingRef = booking.reference_number,
                BookingConfirmed = true,
                SessionState = state
            };
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

        #region Example Handler

        /// <summary>
        /// Example: Wire this to your HTTP listener. Returns the JSON response string.
        /// </summary>
        public static async Task<string> HandleWebhookAsync(
            string requestJson,
            Func<WebhookRequest, Task<AddressValidationResult>> validatePickup,
            Func<WebhookRequest, Task<AddressValidationResult>> validateDestination,
            Func<double, double, double, double, int, Task<(decimal Fare, int EtaMinutes)>> calculateFare)
        {
            try
            {
                var request = ParseRequest(requestJson);
                Console.WriteLine($"📥 Webhook: call_id={request.CallId}, transcript={request.Transcript}");

                var response = new WebhookResponse { SessionState = request.SessionState ?? new() };

                // Get addresses from STT or Ada
                string sttPickup = request.AddressSources?.Stt?.Pickup;
                string sttDestination = request.AddressSources?.Stt?.Destination;
                string adaPickup = request.AddressSources?.Ada?.Pickup;
                string adaDestination = request.AddressSources?.Ada?.Destination;
                int passengers = request.Booking?.Passengers ?? 1;

                double pickupLat = GetSessionValue(request, "pickup_lat", 0.0);
                double pickupLon = GetSessionValue(request, "pickup_lon", 0.0);
                double dropoffLat = GetSessionValue(request, "dropoff_lat", 0.0);
                double dropoffLon = GetSessionValue(request, "dropoff_lon", 0.0);

                string validatedPickup = GetSessionValue(request, "validated_pickup", "");
                string validatedDestination = GetSessionValue(request, "validated_destination", "");

                // ---------------------------------------------------------
                // VALIDATE PICKUP (if new one provided)
                // ---------------------------------------------------------
                if (!string.IsNullOrWhiteSpace(sttPickup) && string.IsNullOrWhiteSpace(validatedPickup))
                {
                    var result = await validatePickup(request);
                    if (result.IsValid)
                    {
                        validatedPickup = result.FormattedAddress;
                        pickupLat = result.Lat;
                        pickupLon = result.Lon;

                        response.AdaPickup = validatedPickup;
                        response.SessionState["validated_pickup"] = validatedPickup;
                        response.SessionState["pickup_lat"] = pickupLat;
                        response.SessionState["pickup_lon"] = pickupLon;
                    }
                    else
                    {
                        return SerializeResponse(Ask(result.ErrorMessage ?? 
                            "I couldn't find that pickup. Could you give me a postcode?"));
                    }
                }

                // ---------------------------------------------------------
                // VALIDATE DESTINATION (if new one provided)
                // ---------------------------------------------------------
                if (!string.IsNullOrWhiteSpace(sttDestination) && string.IsNullOrWhiteSpace(validatedDestination))
                {
                    var result = await validateDestination(request);
                    if (result.IsValid)
                    {
                        validatedDestination = result.FormattedAddress;
                        dropoffLat = result.Lat;
                        dropoffLon = result.Lon;

                        response.AdaDestination = validatedDestination;
                        response.SessionState["validated_destination"] = validatedDestination;
                        response.SessionState["dropoff_lat"] = dropoffLat;
                        response.SessionState["dropoff_lon"] = dropoffLon;
                    }
                    else
                    {
                        return SerializeResponse(Ask(result.ErrorMessage ?? 
                            "I couldn't find that destination. Could you give me more details?"));
                    }
                }

                // ---------------------------------------------------------
                // DETERMINE NEXT STEP
                // ---------------------------------------------------------
                bool hasPickup = !string.IsNullOrWhiteSpace(validatedPickup) || !string.IsNullOrWhiteSpace(adaPickup);
                bool hasDestination = !string.IsNullOrWhiteSpace(validatedDestination) || !string.IsNullOrWhiteSpace(adaDestination);

                if (!hasPickup)
                {
                    return SerializeResponse(Ask("Where would you like to be picked up from?"));
                }
                if (!hasDestination)
                {
                    return SerializeResponse(Ask("And where are you heading to?"));
                }
                if (passengers <= 0)
                {
                    return SerializeResponse(Ask("How many passengers?"));
                }

                // ---------------------------------------------------------
                // CALCULATE FARE & CONFIRM
                // ---------------------------------------------------------
                var (fare, eta) = await calculateFare(pickupLat, pickupLon, dropoffLat, dropoffLon, passengers);

                response.AdaResponse = $"Brilliant! That's booked from {validatedPickup ?? adaPickup} to " +
                    $"{validatedDestination ?? adaDestination}. The fare is £{fare:F2} and your driver will be " +
                    $"with you in {eta} minutes. Anything else?";
                response.BookingConfirmed = true;
                response.SessionState["booking_confirmed"] = true;

                return SerializeResponse(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Webhook error: {ex.Message}");
                return SerializeResponse(Error());
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

        // Booking details - pass these back so Ada can announce them
        [JsonPropertyName("fare")]
        public string Fare { get; set; }

        [JsonPropertyName("eta_minutes")]
        public int? EtaMinutes { get; set; }

        [JsonPropertyName("booking_ref")]
        public string BookingRef { get; set; }

        [JsonPropertyName("passengers")]
        public int? Passengers { get; set; }

        [JsonPropertyName("vehicle_type")]
        public string VehicleType { get; set; }

        [JsonPropertyName("distance_miles")]
        public double? DistanceMiles { get; set; }

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

    /// <summary>
    /// Your existing dispatch system booking response model - extended for Ada webhook.
    /// Use with FromBookTaxiResponse() or ConfirmFromBookTaxiResponse() to convert to webhook response.
    /// </summary>
    public class BookTaxiResponse
    {
        // Original fields
        public string pickup_location { get; set; } = "";
        public string dropoff_location { get; set; } = "";
        public string pickup_time { get; set; } = "";
        public int number_of_passengers { get; set; }
        public string luggage { get; set; } = "";
        public string special_requests { get; set; } = "";
        public string nearest_place { get; set; } = "";
        public string reference_number { get; set; } = "";
        public string username { get; set; } = "";
        public string usertelephone { get; set; } = "";
        public string jobid { get; set; } = "";
        public string Rawjson { get; set; } = "";
        public string bookingmessage { get; set; } = "";
        public double userlat { get; set; }
        public double userlon { get; set; }

        // Extended fields from Ada webhook payload
        public string pickup_name { get; set; } = "";
        public string destination_name { get; set; } = "";
        public string vehicle_type { get; set; } = "";
        public bool is_asap { get; set; } = true;
        public string scheduled_for { get; set; } = "";
        public string estimated_fare { get; set; } = "";
        public string estimated_eta { get; set; } = "";
        public bool pickup_verified { get; set; }
        public bool destination_verified { get; set; }
        public string timestamp { get; set; } = "";
    }

    /// <summary>
    /// Direct Ada webhook booking payload (matches exactly what Ada sends to DISPATCH_WEBHOOK_URL).
    /// Parse this with ParseAdaBookingPayload() to get a BookTaxiResponse.
    /// </summary>
    public class AdaBookingPayload
    {
        [JsonPropertyName("call_id")]
        public string CallId { get; set; } = "";

        [JsonPropertyName("reference")]
        public string Reference { get; set; } = "";

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = "";

        [JsonPropertyName("caller_name")]
        public string CallerName { get; set; } = "";

        [JsonPropertyName("pickup")]
        public string Pickup { get; set; } = "";

        [JsonPropertyName("pickup_name")]
        public string PickupName { get; set; } = "";

        [JsonPropertyName("destination")]
        public string Destination { get; set; } = "";

        [JsonPropertyName("destination_name")]
        public string DestinationName { get; set; } = "";

        [JsonPropertyName("passengers")]
        public int Passengers { get; set; } = 1;

        [JsonPropertyName("luggage")]
        public string Luggage { get; set; } = "";

        [JsonPropertyName("vehicle_type")]
        public string VehicleType { get; set; } = "";

        [JsonPropertyName("pickup_time")]
        public string PickupTime { get; set; } = "";

        [JsonPropertyName("is_asap")]
        public bool IsAsap { get; set; } = true;

        [JsonPropertyName("scheduled_for")]
        public string ScheduledFor { get; set; } = "";

        [JsonPropertyName("estimated_fare")]
        public string EstimatedFare { get; set; } = "";

        [JsonPropertyName("estimated_eta")]
        public string EstimatedEta { get; set; } = "";

        [JsonPropertyName("pickup_verified")]
        public bool PickupVerified { get; set; }

        [JsonPropertyName("destination_verified")]
        public bool DestinationVerified { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = "";
    }

    /// <summary>
    /// RAW PASSTHROUGH MODE: Payload sent when bookingMode="raw"
    /// Ada collects details without validation and sends them here for YOUR system to validate.
    /// Parse with ParseRawPassthroughPayload(), then call your ValidateAddressTest().
    /// Respond with RawPassthroughResponse to control what Ada says.
    /// </summary>
    public class RawPassthroughPayload
    {
        [JsonPropertyName("call_id")]
        public string CallId { get; set; } = "";

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = "";

        /// <summary>What the caller said (all user turns joined by " | ")</summary>
        [JsonPropertyName("passenger_booking")]
        public string PassengerBooking { get; set; } = "";

        /// <summary>What Ada extracted: "Pickup: X, Destination: Y, Passengers: N, ..."</summary>
        [JsonPropertyName("ada_booking")]
        public string AdaBooking { get; set; } = "";

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = "";
    }

    /// <summary>
    /// RAW PASSTHROUGH MODE: Your response to control Ada's speech.
    /// If needs_clarification=true, Ada will ask the clarification_question.
    /// If needs_clarification=false, Ada will confirm with the message.
    /// </summary>
    public class RawPassthroughResponse
    {
        /// <summary>If true, booking not confirmed - Ada asks clarification_question</summary>
        [JsonPropertyName("needs_clarification")]
        public bool NeedsClarification { get; set; }

        /// <summary>Question for Ada to ask if needs_clarification=true</summary>
        [JsonPropertyName("clarification_question")]
        public string ClarificationQuestion { get; set; } = "";

        /// <summary>Confirmation message for Ada (if booking confirmed)</summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        /// <summary>Fare to quote (e.g., "£12.50")</summary>
        [JsonPropertyName("fare")]
        public string Fare { get; set; } = "";

        /// <summary>ETA in minutes (e.g., "6")</summary>
        [JsonPropertyName("eta_minutes")]
        public int? EtaMinutes { get; set; }

        /// <summary>Full ETA string (e.g., "6 minutes")</summary>
        [JsonPropertyName("eta")]
        public string Eta { get; set; } = "";

        /// <summary>Job reference from your system</summary>
        [JsonPropertyName("job_id")]
        public string JobId { get; set; } = "";

        /// <summary>Booking confirmation message (alternative to message)</summary>
        [JsonPropertyName("booking_message")]
        public string BookingMessage { get; set; } = "";
    }
}
