using WhatsAppTaxiBooker.Config;
using WhatsAppTaxiBooker.Data;
using WhatsAppTaxiBooker.Models;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// Orchestrates the booking flow:
/// 1. Receives WhatsApp message (text/audio/GPS)
/// 2. Uses Gemini to extract booking details
/// 3. Stores booking in SQLite
/// 4. Sends confirmation back via WhatsApp
/// </summary>
public sealed class BookingEngine
{
    private readonly GeminiService _gemini;
    private readonly WhatsAppService _whatsApp;
    private readonly BookingDb _db;
    private readonly WhatsAppConfig _waConfig;

    // Track partial bookings per phone number
    private readonly Dictionary<string, Booking> _pendingBookings = new();

    public event Action<string>? OnLog;
    public event Action<Booking>? OnBookingCreated;
    private void Log(string msg) => OnLog?.Invoke(msg);

    public BookingEngine(GeminiService gemini, WhatsAppService whatsApp, BookingDb db, WhatsAppConfig waConfig)
    {
        _gemini = gemini;
        _whatsApp = whatsApp;
        _db = db;
        _waConfig = waConfig;
    }

    public async Task HandleMessageAsync(IncomingWhatsAppMessage msg)
    {
        // Mark as read
        await _whatsApp.MarkReadAsync(msg.MessageId);

        string? textContent = null;

        switch (msg.Type)
        {
            case "text":
                textContent = msg.Text;
                break;

            case "audio":
                // Transcribe voice message via Gemini
                if (msg.MediaId != null)
                {
                    var media = await _whatsApp.GetMediaUrlAsync(msg.MediaId);
                    if (media != null)
                    {
                        textContent = await _gemini.TranscribeAudioAsync(
                            media.Value.url, _waConfig.AccessToken, media.Value.mimeType);
                    }
                }
                if (textContent == null)
                {
                    await _whatsApp.SendTextAsync(msg.From,
                        "Sorry, I couldn't understand that voice message. Could you try again or type your request?");
                    return;
                }
                break;

            case "location":
                // Store GPS as pickup location for pending booking
                await HandleLocationAsync(msg);
                return;

            default:
                await _whatsApp.SendTextAsync(msg.From,
                    "Hi! I can help you book a taxi. Just tell me your pickup address, destination, and number of passengers. You can also send a voice message or share your location! 🚕");
                return;
        }

        if (string.IsNullOrWhiteSpace(textContent)) return;

        // Save user message
        _db.SaveMessage(msg.From, "user", textContent);

        // Get conversation history for context
        var history = _db.GetConversation(msg.From, 6);

        // Extract booking details via Gemini
        var extraction = await _gemini.ExtractBookingAsync(textContent, history);

        if (extraction == null)
        {
            await _whatsApp.SendTextAsync(msg.From,
                "Sorry, I had trouble processing that. Could you tell me where you'd like to be picked up and where you're going?");
            return;
        }

        // Merge with any pending booking for this user
        var booking = GetOrCreatePending(msg.From, msg.ContactName);
        MergeExtraction(booking, extraction);

        if (extraction.IsComplete && !string.IsNullOrWhiteSpace(booking.Pickup) && !string.IsNullOrWhiteSpace(booking.Destination))
        {
            // Complete booking
            booking.Status = "confirmed";
            _db.SaveBooking(booking);
            _pendingBookings.Remove(msg.From);

            var confirmation = $"✅ *Booking Confirmed!*\n\n" +
                              $"🆔 Ref: *{booking.Id}*\n" +
                              $"📍 Pickup: {booking.Pickup}\n" +
                              $"🏁 Destination: {booking.Destination}\n" +
                              $"👥 Passengers: {booking.Passengers}\n" +
                              (booking.Notes != null ? $"📝 Notes: {booking.Notes}\n" : "") +
                              $"\nYour taxi is being arranged! 🚕";

            await _whatsApp.SendTextAsync(msg.From, confirmation);
            _db.SaveMessage(msg.From, "assistant", confirmation);

            Log($"✅ Booking created: {booking.Id} — {booking.Pickup} → {booking.Destination}");
            OnBookingCreated?.Invoke(booking);
        }
        else
        {
            // Ask for missing information
            var followUp = GenerateFollowUp(booking, extraction);
            await _whatsApp.SendTextAsync(msg.From, followUp);
            _db.SaveMessage(msg.From, "assistant", followUp);
        }
    }

    private async Task HandleLocationAsync(IncomingWhatsAppMessage msg)
    {
        var booking = GetOrCreatePending(msg.From, msg.ContactName);
        booking.PickupLat = msg.Latitude;
        booking.PickupLng = msg.Longitude;

        if (string.IsNullOrWhiteSpace(booking.Pickup))
            booking.Pickup = msg.Text ?? $"GPS: {msg.Latitude:F5}, {msg.Longitude:F5}";

        Log($"📍 GPS received from {msg.From}: {msg.Latitude}, {msg.Longitude}");

        if (!string.IsNullOrWhiteSpace(booking.Destination))
        {
            booking.Status = "confirmed";
            _db.SaveBooking(booking);
            _pendingBookings.Remove(msg.From);

            var confirmation = $"✅ *Booking Confirmed!*\n\n" +
                              $"🆔 Ref: *{booking.Id}*\n" +
                              $"📍 Pickup: {booking.Pickup}\n" +
                              $"🏁 Destination: {booking.Destination}\n" +
                              $"👥 Passengers: {booking.Passengers}\n" +
                              $"\nYour taxi is being arranged! 🚕";

            await _whatsApp.SendTextAsync(msg.From, confirmation);
            OnBookingCreated?.Invoke(booking);
        }
        else
        {
            await _whatsApp.SendTextAsync(msg.From,
                "📍 Got your location! Now, where would you like to go?");
        }
    }

    private Booking GetOrCreatePending(string phone, string? contactName)
    {
        if (!_pendingBookings.TryGetValue(phone, out var booking))
        {
            booking = new Booking { Phone = phone, CallerName = contactName };
            _pendingBookings[phone] = booking;
        }
        if (contactName != null && booking.CallerName == null)
            booking.CallerName = contactName;
        return booking;
    }

    private static void MergeExtraction(Booking booking, GeminiBookingExtraction ext)
    {
        if (!string.IsNullOrWhiteSpace(ext.Pickup)) booking.Pickup = ext.Pickup;
        if (!string.IsNullOrWhiteSpace(ext.Destination)) booking.Destination = ext.Destination;
        if (ext.Passengers.HasValue && ext.Passengers > 0) booking.Passengers = ext.Passengers.Value;
        if (!string.IsNullOrWhiteSpace(ext.CallerName)) booking.CallerName = ext.CallerName;
        if (!string.IsNullOrWhiteSpace(ext.Notes)) booking.Notes = ext.Notes;
    }

    private static string GenerateFollowUp(Booking booking, GeminiBookingExtraction extraction)
    {
        if (string.IsNullOrWhiteSpace(booking.Pickup) && string.IsNullOrWhiteSpace(booking.Destination))
            return "Hi! 🚕 I'd love to help you book a taxi. Where would you like to be picked up from, and where are you heading?";

        if (string.IsNullOrWhiteSpace(booking.Pickup))
            return $"Great, heading to *{booking.Destination}*! Where should we pick you up from? You can also share your 📍 location.";

        if (string.IsNullOrWhiteSpace(booking.Destination))
            return $"Got it, picking up from *{booking.Pickup}*! Where would you like to go?";

        return "Almost there! Could you confirm the details so I can book your taxi?";
    }
}
