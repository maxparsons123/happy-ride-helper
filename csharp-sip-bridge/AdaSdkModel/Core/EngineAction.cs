// Last updated: 2026-02-25 (v1.0 — Split-Brain engine output)
namespace AdaSdkModel.Core;

/// <summary>
/// Deterministic output from CallStateEngine.HandleInput().
/// Tells the backend EXACTLY what to do — no AI interpretation needed.
/// 
/// The conversation model receives only the PromptInstruction for speech generation.
/// All tool execution decisions are made here, not by the model.
/// </summary>
public sealed class EngineAction
{
    // ── SPEECH INSTRUCTION (for conversation model) ──
    
    /// <summary>
    /// Short instruction for the conversation model to generate speech.
    /// Examples: "Ask for pickup address.", "Confirm cancellation.", "Present fare of £4.50."
    /// Null = no speech needed (backend handles silently).
    /// </summary>
    public string? PromptInstruction { get; set; }
    
    // ── TOOL EXECUTION FLAGS (backend executes these, NOT the model) ──
    
    /// <summary>Execute cancel_booking. Backend handles DB update, iCabbi cancellation, state reset.</summary>
    public bool ExecuteCancel { get; set; }
    
    /// <summary>Execute book_taxi(confirmed). Backend handles dispatch, payment, Supabase save.</summary>
    public bool ExecuteBooking { get; set; }
    
    /// <summary>Request fare quote. Backend triggers address-dispatch edge function.</summary>
    public bool RequestFareQuote { get; set; }
    
    /// <summary>Transfer call to human operator via SIP REFER.</summary>
    public bool TransferToOperator { get; set; }
    
    /// <summary>End the call (goodbye + SIP BYE).</summary>
    public bool EndCall { get; set; }
    
    /// <summary>Send airport/station booking link via WhatsApp.</summary>
    public bool SendBookingLink { get; set; }
    
    /// <summary>Check booking status (driver ETA, tracking).</summary>
    public bool CheckStatus { get; set; }
    
    // ── VAD MODE SWITCH ──
    
    /// <summary>Switch to semantic VAD (patient, for addresses). Null = don't change.</summary>
    public bool? UseSemanticVad { get; set; }
    
    /// <summary>Semantic VAD eagerness (0.0-1.0). Only used when UseSemanticVad=true.</summary>
    public float VadEagerness { get; set; } = 0.5f;
    
    // ── BLOCKING FLAGS ──
    
    /// <summary>If true, the model's tool call that triggered this action should be REJECTED.</summary>
    public bool BlockToolCall { get; set; }
    
    /// <summary>Error message to return to the model when BlockToolCall=true.</summary>
    public string? BlockReason { get; set; }
    
    // ── METADATA ──
    
    /// <summary>New state after this action (for logging/debugging).</summary>
    public CallState NewState { get; set; }
    
    /// <summary>Create a blocking action that rejects a tool call.</summary>
    public static EngineAction Block(CallState currentState, string reason) => new()
    {
        BlockToolCall = true,
        BlockReason = reason,
        NewState = currentState
    };
    
    /// <summary>Create a speech-only action (no tool execution).</summary>
    public static EngineAction Speak(CallState newState, string instruction) => new()
    {
        PromptInstruction = instruction,
        NewState = newState
    };
    
    /// <summary>Create a silent action (state transition only, no speech or tools).</summary>
    public static EngineAction Silent(CallState newState) => new()
    {
        NewState = newState
    };
}
