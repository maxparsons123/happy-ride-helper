using AdaCleanVersion.Models;

namespace AdaCleanVersion.Services;

/// <summary>
/// Single-pass AI extraction service. Called ONCE when all raw slots are collected.
/// AI acts as pure normalization function — no flow control, no decisions.
/// 
/// Supports two operation types:
/// - New: Normalize all raw slots into a structured booking
/// - Update: Normalize only changed fields, preserving existing booking data
/// </summary>
public interface IExtractionService
{
    /// <summary>
    /// Send raw slot data to AI for structured extraction (new booking).
    /// Returns normalized, validated booking data.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Send changed fields for an existing booking to AI for re-normalization (update).
    /// Only the changed fields are re-processed; unchanged fields are preserved.
    /// </summary>
    Task<ExtractionResult> ExtractUpdateAsync(
        ExtractionRequest request,
        StructuredBooking existingBooking,
        IReadOnlySet<string> changedSlots,
        CancellationToken ct = default);
}

/// <summary>
/// Result of AI extraction pass.
/// </summary>
public sealed class ExtractionResult
{
    public bool Success { get; init; }
    public StructuredBooking? Booking { get; init; }
    public string? Error { get; init; }

    /// <summary>Warnings that don't block booking but should be logged.</summary>
    public List<string> Warnings { get; init; } = new();
}
