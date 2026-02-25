using AdaCleanVersion.Models;

namespace AdaCleanVersion.Services;

/// <summary>
/// Single-pass AI extraction service. Called ONCE when all raw slots are collected.
/// AI acts as pure normalization function — no flow control, no decisions.
/// </summary>
public interface IExtractionService
{
    /// <summary>
    /// Send raw slot data to AI for structured extraction.
    /// Returns normalized, validated booking data.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct = default);
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
