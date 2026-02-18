using WhatsAppTaxiBooker.Config;
using WhatsAppTaxiBooker.Data;
using WhatsAppTaxiBooker.Models;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// Orchestrates the booking flow with intent-aware processing.
/// On confirm: dispatches via MQTT AND submits to iCabbi if enabled.
/// </summary>
public sealed class BookingEngine
{
    private readonly GeminiService _gemini;
    private readonly WhatsAppService _whatsApp;
    private readonly BookingDb _db;
    private readonly WhatsAppConfig _waConfig;
    private readonly MqttDispatcher _mqtt;
    private readonly IcabbiService? _icabbi;

    public event Action<string>? OnLog;
    public event Action<Booking>? OnBookingCreated;
    public event Action<Booking>? OnBookingUpdated;
    public event Action<Booking>? OnBookingDispatched;
    private void Log(string msg) => OnLog?.Invoke(msg);

    public BookingEngine(GeminiService gemini, WhatsAppService whatsApp, BookingDb db,
        WhatsAppConfig waConfig, MqttDispatcher mqtt, IcabbiService? icabbi = null)
    {
        _gemini = gemini;
        _whatsApp = whatsApp;
        _db = db;
        _waConfig = waConfig;
        _mqtt = mqtt;
        _icabbi = icabbi;
    }

    public async Task HandleMessageAsync(IncomingWhatsAppMessage msg)
    {
        await _whatsApp.MarkReadAsync(msg.MessageId);

        string? textContent = null;

        switch (msg.Type)
        {
            case "text":
                textContent = msg.Text;
                break;

            case "audio":
                if (msg.MediaId != null)
                {
                    var media = await _whatsApp.GetMediaUrlAsync(msg.MediaId);
                    if (media != null)
                        textContent = await _gemini.TranscribeAudioAsync(media.Value.url, _waConfig.AccessToken, media.Value.mimeType);
                }
                if (textContent == null)
                {
                    await _whatsApp.SendTextAsync(msg.From, "Sorry, I couldn't understand that voice message. Could you try again or type your request?");
                    return;
                }
                break;

            case "location":
                await HandleLocationAsync(msg);
                return;

            default:
                await _whatsApp.SendTextAsync(msg.From, "Hi! I can help you book a taxi. Tell me your pickup, destination, and passengers. You can also send a voice message or share your location! 🚕");
                return;
        }

        if (string.IsNullOrWhiteSpace(textContent)) return;

        _db.SaveMessage(msg.From, "user", textContent);

        var existingBooking = _db.GetActiveBookingByPhone(msg.From);
        var history = _db.GetConversation(msg.From, 8);

        var extraction = await _gemini.ExtractBookingAsync(textContent, history, existingBooking);
        if (extraction == null)
        {
            await _whatsApp.SendTextAsync(msg.From, "Sorry, I had trouble processing that. Could you tell me your pickup and destination?");
            return;
        }

        var intent = extraction.Intent?.ToLowerInvariant() ?? "new_booking";
        Log($"🎯 Intent: {intent} from {msg.From}");

        switch (intent)
        {
            case "confirm":
                await HandleConfirmAsync(msg.From, existingBooking);
                break;
            case "cancel":
                await HandleCancelAsync(msg.From, existingBooking);
                break;
            case "query":
                await HandleQueryAsync(msg.From, existingBooking);
                break;
            case "update":
                await HandleUpdateAsync(msg.From, msg.ContactName, existingBooking, extraction);
                break;
            case "greeting":
                if (existingBooking != null)
                    await SendBookingSummary(msg.From, existingBooking, "Welcome back! Here's your current booking:");
                else
                    await SendReply(msg.From, "Hi! 🚕 I'd love to help you book a taxi. Where would you like to be picked up from, and where are you heading?");
                break;
            default:
                await HandleNewOrMergeAsync(msg.From, msg.ContactName, existingBooking, extraction);
                break;
        }
    }

    private async Task HandleConfirmAsync(string phone, Booking? booking)
    {
        if (booking == null || string.IsNullOrWhiteSpace(booking.Pickup) || string.IsNullOrWhiteSpace(booking.Destination))
        {
            await SendReply(phone, "I don't have a complete booking to confirm yet. Please provide your pickup and destination first.");
            return;
        }

        booking.Status = "confirmed";
        _db.SaveBooking(booking);

        // 1) Dispatch via MQTT
        try
        {
            await _mqtt.PublishBookingAsync(booking);
            Log($"🚀 Booking {booking.Id} dispatched via MQTT");
            OnBookingDispatched?.Invoke(booking);
        }
        catch (Exception ex)
        {
            Log($"⚠️ MQTT dispatch failed: {ex.Message}");
        }

        // 2) Submit to iCabbi if enabled
        if (_icabbi != null)
        {
            try
            {
                var icabbiId = await _icabbi.CreateBookingAsync(booking);
                if (icabbiId != null)
                {
                    Log($"✅ iCabbi booking created: {icabbiId}");
                    booking.Notes = (booking.Notes ?? "") + $" [iCabbi:{icabbiId}]";
                    _db.SaveBooking(booking);
                }
                else
                {
                    Log("⚠️ iCabbi booking submission returned null");
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ iCabbi submission failed: {ex.Message}");
            }
        }

        var confirmation = $"✅ *Booking Confirmed & Dispatched!*\n\n" +
                          $"🆔 Ref: *{booking.Id}*\n" +
                          $"📍 Pickup: {booking.Pickup}\n" +
                          $"🏁 Destination: {booking.Destination}\n" +
                          $"👥 Passengers: {booking.Passengers}\n" +
                          (booking.Notes != null ? $"📝 Notes: {booking.Notes}\n" : "") +
                          (booking.PickupTime != null ? $"🕐 Time: {booking.PickupTime}\n" : "") +
                          $"\nYour taxi is being arranged! A driver will be assigned shortly. 🚕";

        await SendReply(phone, confirmation);
        OnBookingCreated?.Invoke(booking);
    }

    private async Task HandleCancelAsync(string phone, Booking? booking)
    {
        if (booking == null)
        {
            await SendReply(phone, "You don't have an active booking to cancel.");
            return;
        }

        booking.Status = "cancelled";
        _db.SaveBooking(booking);
        await SendReply(phone, $"❌ Booking *{booking.Id}* has been cancelled. Send a new message anytime to book again!");
        OnBookingUpdated?.Invoke(booking);
    }

    private async Task HandleQueryAsync(string phone, Booking? booking)
    {
        if (booking == null)
        {
            var latest = _db.GetLatestBookingByPhone(phone);
            if (latest != null)
                await SendBookingSummary(phone, latest, $"Your last booking ({latest.Status}):");
            else
                await SendReply(phone, "You don't have any bookings yet. Send me your pickup and destination to get started! 🚕");
            return;
        }
        await SendBookingSummary(phone, booking, "Here's your current booking:");
    }

    private async Task HandleUpdateAsync(string phone, string? contactName, Booking? booking, GeminiBookingExtraction extraction)
    {
        if (booking == null)
        {
            await HandleNewOrMergeAsync(phone, contactName, null, extraction);
            return;
        }

        MergeExtraction(booking, extraction);
        booking.Status = IsBookingReady(booking) ? "ready" : "collecting";
        _db.SaveBooking(booking);
        OnBookingUpdated?.Invoke(booking);

        var reply = $"✏️ *Booking Updated!*\n\n" +
                   $"🆔 Ref: *{booking.Id}*\n" +
                   $"📍 Pickup: {booking.Pickup}\n" +
                   $"🏁 Destination: {booking.Destination}\n" +
                   $"👥 Passengers: {booking.Passengers}\n" +
                   (booking.Notes != null ? $"📝 Notes: {booking.Notes}\n" : "");

        if (IsBookingReady(booking))
            reply += "\n✅ All details look good! Send *confirm* to dispatch your taxi.";
        else
            reply += $"\n⏳ Still need: {GetMissingFields(booking)}";

        await SendReply(phone, reply);
    }

    private async Task HandleNewOrMergeAsync(string phone, string? contactName, Booking? existing, GeminiBookingExtraction extraction)
    {
        var booking = existing ?? new Booking { Phone = phone, CallerName = contactName };
        if (contactName != null && booking.CallerName == null)
            booking.CallerName = contactName;

        MergeExtraction(booking, extraction);

        if (IsBookingReady(booking))
        {
            booking.Status = "ready";
            _db.SaveBooking(booking);

            var summary = $"📋 *Booking Ready for Confirmation*\n\n" +
                         $"🆔 Ref: *{booking.Id}*\n" +
                         $"📍 Pickup: {booking.Pickup}\n" +
                         $"🏁 Destination: {booking.Destination}\n" +
                         $"👥 Passengers: {booking.Passengers}\n" +
                         (booking.Notes != null ? $"📝 Notes: {booking.Notes}\n" : "") +
                         $"\nSend *confirm* to book, or update any details.";

            await SendReply(phone, summary);
            OnBookingUpdated?.Invoke(booking);
        }
        else
        {
            booking.Status = "collecting";
            _db.SaveBooking(booking);
            OnBookingUpdated?.Invoke(booking);

            var followUp = GenerateFollowUp(booking);
            await SendReply(phone, followUp);
        }
    }

    private async Task HandleLocationAsync(IncomingWhatsAppMessage msg)
    {
        var booking = _db.GetActiveBookingByPhone(msg.From) ?? new Booking { Phone = msg.From, CallerName = msg.ContactName };
        booking.PickupLat = msg.Latitude;
        booking.PickupLng = msg.Longitude;

        if (string.IsNullOrWhiteSpace(booking.Pickup))
            booking.Pickup = msg.Text ?? $"GPS: {msg.Latitude:F5}, {msg.Longitude:F5}";

        Log($"📍 GPS from {msg.From}: {msg.Latitude}, {msg.Longitude}");

        if (IsBookingReady(booking))
        {
            booking.Status = "ready";
            _db.SaveBooking(booking);
            await SendBookingSummary(msg.From, booking, "📍 Got your location! Booking ready:");
            await SendReply(msg.From, "Send *confirm* to dispatch your taxi.");
        }
        else
        {
            booking.Status = "collecting";
            _db.SaveBooking(booking);
            await SendReply(msg.From, "📍 Got your location! Now, where would you like to go?");
        }
        OnBookingUpdated?.Invoke(booking);
    }

    // ── Helpers ──

    private static void MergeExtraction(Booking booking, GeminiBookingExtraction ext)
    {
        if (!string.IsNullOrWhiteSpace(ext.Pickup)) booking.Pickup = ext.Pickup;
        if (!string.IsNullOrWhiteSpace(ext.Destination)) booking.Destination = ext.Destination;
        if (ext.Passengers.HasValue && ext.Passengers > 0) booking.Passengers = ext.Passengers.Value;
        if (!string.IsNullOrWhiteSpace(ext.CallerName)) booking.CallerName = ext.CallerName;
        if (!string.IsNullOrWhiteSpace(ext.Notes)) booking.Notes = ext.Notes;
        if (!string.IsNullOrWhiteSpace(ext.PickupTime)) booking.PickupTime = ext.PickupTime;
    }

    private static bool IsBookingReady(Booking b) =>
        !string.IsNullOrWhiteSpace(b.Pickup) && !string.IsNullOrWhiteSpace(b.Destination);

    private static string GetMissingFields(Booking b)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(b.Pickup)) missing.Add("pickup");
        if (string.IsNullOrWhiteSpace(b.Destination)) missing.Add("destination");
        return missing.Count > 0 ? string.Join(", ", missing) : "none";
    }

    private static string GenerateFollowUp(Booking booking)
    {
        if (string.IsNullOrWhiteSpace(booking.Pickup) && string.IsNullOrWhiteSpace(booking.Destination))
            return "Hi! 🚕 Where would you like to be picked up from, and where are you heading?";
        if (string.IsNullOrWhiteSpace(booking.Pickup))
            return $"Great, heading to *{booking.Destination}*! Where should we pick you up? You can also share your 📍 location.";
        if (string.IsNullOrWhiteSpace(booking.Destination))
            return $"Got it, picking up from *{booking.Pickup}*! Where would you like to go?";
        return "All set! Send *confirm* to book your taxi.";
    }

    private async Task SendBookingSummary(string phone, Booking b, string header)
    {
        var summary = $"{header}\n\n" +
                     $"🆔 Ref: *{b.Id}*\n" +
                     $"📍 Pickup: {b.Pickup}\n" +
                     $"🏁 Destination: {b.Destination}\n" +
                     $"👥 Passengers: {b.Passengers}\n" +
                     $"📌 Status: {b.Status}\n" +
                     (b.Notes != null ? $"📝 Notes: {b.Notes}\n" : "") +
                     (b.PickupTime != null ? $"🕐 Time: {b.PickupTime}\n" : "");
        await SendReply(phone, summary);
    }

    private async Task SendReply(string phone, string message)
    {
        await _whatsApp.SendTextAsync(phone, message);
        _db.SaveMessage(phone, "assistant", message);
    }
}
