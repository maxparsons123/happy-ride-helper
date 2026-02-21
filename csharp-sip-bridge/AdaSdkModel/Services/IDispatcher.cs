// Last updated: 2026-02-21 (v2.8)
using AdaSdkModel.Core;

namespace AdaSdkModel.Services;

/// <summary>
/// Interface for dispatch and notification services.
/// </summary>
public interface IDispatcher
{
    Task<bool> DispatchAsync(BookingState booking, string phoneNumber);
    Task<bool> SendWhatsAppAsync(string phoneNumber);
}
