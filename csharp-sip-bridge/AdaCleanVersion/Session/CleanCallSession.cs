using AdaCleanVersion.Engine;
using AdaCleanVersion.Models;
using AdaCleanVersion.Services;

namespace AdaCleanVersion.Session;

/// <summary>
/// Clean call session — orchestrates the deterministic engine + AI voice interface.
/// 
/// Architecture:
/// 1. Engine drives flow deterministically (no AI involvement in state)
/// 2. AI is voice-only — responds to [INSTRUCTION] messages
/// 3. Caller responses stored as raw slot values
/// 4. Single AI extraction pass when all slots filled
/// 5. Validation → fare → payment → confirmation → dispatch
/// </summary>
public class CleanCallSession
{
    private readonly CallStateEngine _engine = new();
    private readonly IExtractionService _extractionService;
    private readonly string _companyName;
    private readonly CallerContext? _callerContext;

    public string SessionId { get; }
    public string CallerId { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public CallStateEngine Engine => _engine;

    public event Action<string>? OnLog;
    public event Action<string>? OnAiInstruction;  // Instruction to send to AI
    public event Action<StructuredBooking>? OnBookingReady;

    public CleanCallSession(
        string sessionId,
        string callerId,
        string companyName,
        IExtractionService extractionService,
        CallerContext? callerContext = null)
    {
        SessionId = sessionId;
        CallerId = callerId;
        _companyName = companyName;
        _extractionService = extractionService;
        _callerContext = callerContext;

        _engine.OnLog += msg => OnLog?.Invoke(msg);
        _engine.OnStateChanged += OnEngineStateChanged;
    }

    /// <summary>
    /// Start the call — send greeting instruction to AI.
    /// </summary>
    public void Start()
    {
        Log($"Call started: {CallerId}");

        // Auto-fill name for returning callers
        if (_callerContext?.IsReturningCaller == true && !string.IsNullOrWhiteSpace(_callerContext.CallerName))
        {
            _engine.RawData.SetSlot("name", _callerContext.CallerName);
            Log($"Auto-filled name from caller history: {_callerContext.CallerName}");
        }

        _engine.BeginCollection();
        EmitCurrentInstruction();
    }

    /// <summary>
    /// Process caller's spoken response for the current slot.
    /// The SIP bridge's transcript handler calls this with the raw text.
    /// </summary>
    public async Task ProcessCallerResponseAsync(string transcript, CancellationToken ct = default)
    {
        var currentSlot = _engine.RawData.NextMissingSlot();
        if (currentSlot == null)
        {
            // All slots filled — might be a correction or confirmation
            await HandlePostCollectionInput(transcript, ct);
            return;
        }

        // Store raw value for current slot
        var nextSlot = _engine.AcceptSlotValue(currentSlot, transcript);

        if (nextSlot == null && _engine.State == CollectionState.ReadyForExtraction)
        {
            // All slots collected — trigger extraction
            await RunExtractionAsync(ct);
        }
        else
        {
            EmitCurrentInstruction();
        }
    }

    /// <summary>
    /// Correct a specific slot by name.
    /// </summary>
    public async Task CorrectSlotAsync(string slotName, string newValue, CancellationToken ct = default)
    {
        var oldValue = _engine.RawData.GetSlot(slotName);
        _engine.CorrectSlot(slotName, newValue);

        var instruction = PromptBuilder.BuildCorrectionInstruction(
            slotName, oldValue ?? "", newValue);
        OnAiInstruction?.Invoke(instruction);

        // Re-check if ready for extraction after correction
        if (_engine.RawData.AllRequiredPresent &&
            _engine.State < CollectionState.ReadyForExtraction)
        {
            await RunExtractionAsync(ct);
        }
    }

    /// <summary>
    /// Accept payment choice and advance.
    /// </summary>
    public void AcceptPayment(string method)
    {
        _engine.AcceptPaymentChoice(method);
        EmitCurrentInstruction();
    }

    /// <summary>
    /// Confirm the booking and dispatch.
    /// </summary>
    public void ConfirmBooking()
    {
        _engine.ConfirmBooking();
        EmitCurrentInstruction();
    }

    /// <summary>
    /// End the call.
    /// </summary>
    public void EndCall()
    {
        _engine.EndCall();
        EmitCurrentInstruction();
    }

    /// <summary>
    /// Get the system prompt for the AI voice interface.
    /// </summary>
    public string GetSystemPrompt() => PromptBuilder.BuildSystemPrompt(_companyName, _callerContext);

    // ─── Private ─────────────────────────────────────────────

    private async Task RunExtractionAsync(CancellationToken ct)
    {
        _engine.BeginExtraction();
        EmitCurrentInstruction(); // "Checking availability..."

        try
        {
            var request = _engine.BuildExtractionRequest(_callerContext);
            Log($"Extracting: name={request.Slots.Name}, pickup={request.Slots.Pickup}, " +
                $"dest={request.Slots.Destination}, pax={request.Slots.Passengers}, " +
                $"time={request.Slots.PickupTime}");

            var result = await _extractionService.ExtractAsync(request, ct);

            if (result.Success && result.Booking != null)
            {
                _engine.CompleteExtraction(result.Booking);
                OnBookingReady?.Invoke(result.Booking);
                EmitCurrentInstruction();
            }
            else
            {
                _engine.ExtractionFailed(result.Error ?? "Unknown extraction error");
                EmitCurrentInstruction();
            }
        }
        catch (Exception ex)
        {
            Log($"Extraction error: {ex.Message}");
            _engine.ExtractionFailed(ex.Message);
            EmitCurrentInstruction();
        }
    }

    private Task HandlePostCollectionInput(string transcript, CancellationToken ct)
    {
        // After all slots are collected, handle confirmation/rejection/correction
        // This is where you'd add keyword detection for "yes", "no", "change pickup", etc.
        Log($"Post-collection input: \"{transcript}\" (state: {_engine.State})");

        // Simple keyword matching — no AI needed
        var lower = transcript.ToLowerInvariant().Trim();

        switch (_engine.State)
        {
            case CollectionState.AwaitingPaymentChoice:
                if (lower.Contains("card")) AcceptPayment("card");
                else if (lower.Contains("cash") || lower.Contains("meter")) AcceptPayment("meter");
                else EmitCurrentInstruction(); // Re-ask
                break;

            case CollectionState.AwaitingConfirmation:
                if (lower.Contains("yes") || lower.Contains("confirm") || lower.Contains("book"))
                    ConfirmBooking();
                else if (lower.Contains("no") || lower.Contains("cancel"))
                    EndCall();
                else
                    EmitCurrentInstruction(); // Re-ask
                break;

            default:
                EmitCurrentInstruction();
                break;
        }

        return Task.CompletedTask;
    }

    private void EmitCurrentInstruction()
    {
        var instruction = PromptBuilder.BuildInstruction(_engine.State, _engine.RawData, _callerContext);
        OnAiInstruction?.Invoke(instruction);
    }

    private void OnEngineStateChanged(CollectionState from, CollectionState to)
    {
        Log($"State transition: {from} → {to}");
    }

    private void Log(string msg) => OnLog?.Invoke($"[Session:{SessionId}] {msg}");
}
