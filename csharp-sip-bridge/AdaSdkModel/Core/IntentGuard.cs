// Last updated: 2026-02-21 (v2.8)
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Core;

/// <summary>
/// Stage-aware intent guard that deterministically resolves user intent
/// based on the current booking stage + transcript keywords.
/// 
/// This enforces critical tool calls when the AI model fails to call them,
/// eliminating the class of bugs where the model "understands" but skips the tool.
/// </summary>
public sealed class IntentGuard
{
    /// <summary>Resolved intent from user transcript + current stage.</summary>
    public enum ResolvedIntent
    {
        None,               // No actionable intent detected — let AI handle
        ConfirmBooking,     // User said yes after fare → force book_taxi(confirmed)
        RejectFare,         // User said no after fare → they want to modify
        EndCall,            // User said no to "anything else?" → force end_call
        NewBooking,         // User said yes to "anything else?" → restart flow
        SelectOption,       // User picked an option during disambiguation
        CancelBooking,      // User wants to cancel their existing booking
        AmendBooking,       // User wants to amend their existing booking
        CheckStatus,        // User wants to check booking status
    }

    // ── Affirmative patterns ──
    private static readonly Regex AffirmativePattern = new(
        @"\b(yes|yeah|yep|yup|sure|ok|okay|correct|confirm|go ahead|that'?s? (right|fine|correct|good)|please|book it|do it|absolutely|definitely)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Negative patterns ──
    private static readonly Regex NegativePattern = new(
        @"\b(no|nah|nope|not right|wrong|incorrect|change|cancel|don'?t|too (much|expensive|high)|actually)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── "Nothing else" patterns (for anything_else stage) ──
    private static readonly Regex NothingElsePattern = new(
        @"\b(no|nah|nope|nothing|that'?s? (all|it|everything)|i'?m? (good|fine|done|ok)|all good|no thank|bye|goodbye|cheers)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Cancel booking patterns ──
    private static readonly Regex CancelPattern = new(
        @"\b(cancel|cancel\s*(it|that|the booking|my booking)|don'?t want|scrap it|forget it|remove it)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Amend booking patterns ──
    private static readonly Regex AmendPattern = new(
        @"\b(amend|change|update|modify|alter|edit|different)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Status check patterns ──
    private static readonly Regex StatusPattern = new(
        @"\b(status|where.*(driver|taxi|cab)|how long|eta|track|when.*(arrive|coming|here)|on.*(the |its? )?way)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── "Something else" patterns ──
    private static readonly Regex SomethingElsePattern = new(
        @"\b(yes|yeah|actually|one more|another|also|i need|can you|could you)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Resolve user intent based on current booking stage and what they said.
    /// Returns None if no deterministic action can be taken (let AI handle it).
    /// </summary>
    public static ResolvedIntent Resolve(BookingStage stage, string? userTranscript)
    {
        if (string.IsNullOrWhiteSpace(userTranscript))
            return ResolvedIntent.None;

        var text = userTranscript.Trim();

        return stage switch
        {
            BookingStage.FarePresented => ResolveFareResponse(text),
            BookingStage.AnythingElse => ResolveAnythingElseResponse(text),
            BookingStage.Disambiguation => ResolvedIntent.SelectOption,
            BookingStage.ManagingExistingBooking => ResolveManageBookingResponse(text),
            _ => ResolvedIntent.None
        };
    }

    private static ResolvedIntent ResolveFareResponse(string text)
    {
        // Check affirmative first (more common)
        if (AffirmativePattern.IsMatch(text))
            return ResolvedIntent.ConfirmBooking;

        if (NegativePattern.IsMatch(text))
            return ResolvedIntent.RejectFare;

        return ResolvedIntent.None;
    }

    private static ResolvedIntent ResolveAnythingElseResponse(string text)
    {
        // Check "nothing else" first
        if (NothingElsePattern.IsMatch(text))
            return ResolvedIntent.EndCall;

        if (SomethingElsePattern.IsMatch(text))
            return ResolvedIntent.NewBooking;

        return ResolvedIntent.None;
    }

    private static ResolvedIntent ResolveManageBookingResponse(string text)
    {
        if (CancelPattern.IsMatch(text))
            return ResolvedIntent.CancelBooking;
        if (AmendPattern.IsMatch(text))
            return ResolvedIntent.AmendBooking;
        if (StatusPattern.IsMatch(text))
            return ResolvedIntent.CheckStatus;
        // Check if they want a new booking instead
        if (AffirmativePattern.IsMatch(text) && !NegativePattern.IsMatch(text))
            return ResolvedIntent.NewBooking;
        return ResolvedIntent.None;
    }
}
