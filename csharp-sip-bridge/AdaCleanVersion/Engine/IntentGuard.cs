// Ported from AdaSdkModel — Stage-aware intent guard
using System.Text.RegularExpressions;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Stage-aware intent guard that deterministically resolves user intent
/// based on the current collection state + transcript keywords.
/// 
/// Enforces critical actions when the AI model fails to call them,
/// eliminating bugs where the model "understands" but skips the action.
/// 
/// This runs BEFORE the AI — if it resolves an intent, the AI is never consulted.
/// </summary>
public sealed class IntentGuard
{
    public enum ResolvedIntent
    {
        None,               // No actionable intent — let AI handle
        ConfirmBooking,     // User said yes after fare
        RejectFare,         // User said no after fare
        EndCall,            // User said no to "anything else?"
        NewBooking,         // User said yes to "anything else?"
        SelectOption,       // User picked an option during disambiguation
        CancelBooking,      // User wants to cancel existing booking
        AmendBooking,       // User wants to amend existing booking
        CheckStatus,        // User wants to check booking status
        WantsToChange,      // User wants to change a slot (generic — "can I change", "wrong")
    }

    private static readonly Regex AffirmativePattern = new(
        @"\b(yes|yeah|yep|yup|sure|ok|okay|correct|confirm|go ahead|that'?s? (right|fine|correct|good)|please|book it|do it|absolutely|definitely)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NegativePattern = new(
        @"\b(no|nah|nope|not right|wrong|incorrect|change|cancel|don'?t|too (much|expensive|high)|actually|can i change|i want to change|let me change)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NothingElsePattern = new(
        @"\b(no|nah|nope|nothing|that'?s? (all|it|everything)|i'?m? (good|fine|done|ok)|all good|no thank|bye|goodbye|cheers)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CancelPattern = new(
        @"\b(cancel|cancel\s*(it|that|the booking|my booking)|don'?t want|scrap it|forget it|remove it|never mind)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AmendPattern = new(
        @"\b(amend|change|update|modify|alter|edit|different)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StatusPattern = new(
        @"\b(status|where.*(driver|taxi|cab)|how long|eta|track|when.*(arrive|coming|here)|on.*(the |its? )?way)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SomethingElsePattern = new(
        @"\b(yes|yeah|actually|one more|another|also|i need|can you|could you)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CurrentBookingActionPattern = new(
        @"\b(payment|pay\s*(by|with|now|for)|send\s*(me|the)|tracking|track\s*(my|the)|where.*(driver|taxi)|special\s*instructions?|notes?\s*for|child\s*seat|wheelchair|link|card|meter|confirmation|receipt)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NewBookingPattern = new(
        @"\b(new booking|another (taxi|cab|booking|one)|book (a|another)|i need a|can (i|you) book|want (a|to book))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// "Change" patterns that indicate generic intent to modify without specifying what.
    /// Distinct from NegativePattern — these don't imply rejection, just a desire to revise.
    /// </summary>
    private static readonly Regex ChangeIntentPattern = new(
        @"\b(can i change|i want to change|let me change|i need to change|change (the|my)|wrong|that'?s? wrong|that'?s? not right|not correct|incorrect)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Resolve user intent based on current collection state and what they said.
    /// Maps CollectionState to the intent resolution logic.
    /// </summary>
    public static ResolvedIntent Resolve(CollectionState state, string? userTranscript)
    {
        if (string.IsNullOrWhiteSpace(userTranscript))
            return ResolvedIntent.None;

        var text = userTranscript.Trim();

        return state switch
        {
            CollectionState.PresentingFare or CollectionState.AwaitingPaymentChoice => ResolveFareResponse(text),
            CollectionState.AwaitingConfirmation => ResolveFareResponse(text),
            CollectionState.Dispatched => ResolveAnythingElseResponse(text),
            CollectionState.AwaitingClarification => ResolvedIntent.SelectOption,
            CollectionState.ManagingExistingBooking => ResolveManageBookingResponse(text),
            CollectionState.AwaitingCancelConfirmation => ResolveCancelConfirmation(text),
            _ => ResolvedIntent.None
        };
    }

    private static ResolvedIntent ResolveFareResponse(string text)
    {
        // Check for explicit change intent first — "can I change the pickup?" is NOT a rejection
        if (ChangeIntentPattern.IsMatch(text)) return ResolvedIntent.WantsToChange;
        if (AffirmativePattern.IsMatch(text)) return ResolvedIntent.ConfirmBooking;
        if (NegativePattern.IsMatch(text)) return ResolvedIntent.RejectFare;
        return ResolvedIntent.None;
    }

    private static ResolvedIntent ResolveAnythingElseResponse(string text)
    {
        if (NothingElsePattern.IsMatch(text)) return ResolvedIntent.EndCall;
        if (CurrentBookingActionPattern.IsMatch(text)) return ResolvedIntent.None;
        if (SomethingElsePattern.IsMatch(text)) return ResolvedIntent.NewBooking;
        return ResolvedIntent.None;
    }

    private static ResolvedIntent ResolveManageBookingResponse(string text)
    {
        if (CancelPattern.IsMatch(text)) return ResolvedIntent.CancelBooking;
        if (AmendPattern.IsMatch(text)) return ResolvedIntent.AmendBooking;
        if (StatusPattern.IsMatch(text)) return ResolvedIntent.CheckStatus;
        // Only resolve as NewBooking if explicitly mentions wanting a new booking —
        // bare "yes" is ambiguous (could be confirming cancel/amend that Ada just asked about)
        if (NewBookingPattern.IsMatch(text)) return ResolvedIntent.NewBooking;
        return ResolvedIntent.None;
    }

    private static ResolvedIntent ResolveCancelConfirmation(string text)
    {
        // "Yes, cancel" → confirm cancellation (maps to CancelBooking)
        if (AffirmativePattern.IsMatch(text)) return ResolvedIntent.CancelBooking;
        // "No, don't cancel" → back to manage options
        if (NegativePattern.IsMatch(text)) return ResolvedIntent.None; // Let engine revert
        return ResolvedIntent.None;
    }
}
