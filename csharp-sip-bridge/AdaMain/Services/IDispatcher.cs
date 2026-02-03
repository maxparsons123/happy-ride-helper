using AdaMain.Core;

namespace AdaMain.Services;

/// <summary>
/// Interface for dispatch and notification services.
/// </summary>
public interface IDispatcher
{
    /// <summary>Dispatch booking to BSQD API.</summary>
    Task<bool> DispatchAsync(BookingState booking, string phoneNumber);
    
    /// <summary>Send WhatsApp notification.</summary>
    Task<bool> SendWhatsAppAsync(string phoneNumber);
}
