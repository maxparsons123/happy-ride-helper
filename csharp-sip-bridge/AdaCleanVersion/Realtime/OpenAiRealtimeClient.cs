using System.Text.RegularExpressions;
using AdaCleanVersion.Audio;
using AdaCleanVersion.Session;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// v5 Orchestrator: thin top-level class that wires components and routes events.
/// 
/// Components:
///   RealtimeAudioBridge  — RTP ↔ OpenAI audio (G.711 passthrough + playout)
///   MicGateController    — deterministic mic gating (buffer-all, flush-tail)
///   RealtimeToolRouter   — sync_booking_data handling (pacer, slot lock, transcript injection)
///   InstructionCoordinator — cancel → update → response.create sequencing
///   IRealtimeTransport   — raw WebSocket protocol (swappable)
///
/// All business logic lives in CleanCallSession (session layer).
/// This class contains NO booking logic — only media/protocol orchestration.
/// </summary>
public sealed class OpenAiRealtimeClient : IAsyncDisposable
{
    private readonly string _callId;
    private readonly string _voice;
    private readonly G711CodecType _codec;
    private readonly CleanCallSession _session;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    // ── Components ──
    private readonly IRealtimeTransport _transport;
    private readonly MicGateController _micGate;
    private readonly RealtimeAudioBridge _audio;
    private readonly RealtimeToolRouter _tools;
    private readonly InstructionCoordinator _instructions;

    // ── Events ──
    public event Action<string>? OnLog;
    public event Action<byte[]>? OnAudioOut;
    public event Action? OnBargeIn;
    public event Action? OnMicUngated;

    public OpenAiRealtimeClient(
        string apiKey,
        string model,
        string voice,
        string callId,
        RTPSession rtpSession,
        CleanCallSession session,
        ILogger logger,
        G711CodecType codec = G711CodecType.PCMU,
        IRealtimeTransport? transport = null)
    {
        _callId = callId;
        _voice = voice;
        _codec = codec;
        _session = session;
        _logger = logger;

        // ── Build component graph ──
        _transport = transport ?? new WebSocketRealtimeTransport();

        _micGate = new MicGateController();

        _audio = new RealtimeAudioBridge(rtpSession, _transport, codec, _micGate, _cts.Token);
        _audio.OnLog += Log;
        _audio.OnAudioOut += frame => { try { OnAudioOut?.Invoke(frame); } catch { } };
        _audio.OnBargeIn += () => { try { OnBargeIn?.Invoke(); } catch { } };
        _audio.OnMicUngated += () =>
        {
            _session.NotifyMicUngated();
            try { OnMicUngated?.Invoke(); } catch { }
        };

        _instructions = new InstructionCoordinator(
            _transport,
            () => _session.Engine.State,
            () => _micGate.IsGated,
            _cts.Token);
        _instructions.OnLog += Log;

        _tools = new RealtimeToolRouter(session, _transport, _instructions, _cts.Token);
        _tools.OnLog += Log;

        // ── Wire transport events ──
        _transport.OnMessage += HandleServerMessageAsync;
        _transport.OnDisconnected += reason => Log($"🔌 Transport disconnected: {reason}");

        // Build connection headers (stored for ConnectAsync)
        _apiKey = apiKey;
        _model = model;
    }

    private readonly string _apiKey;
    private readonly string _model;

    // ─── Lifecycle ──────────────────────────────────────────

    public async Task ConnectAsync()
    {
        var url = $"wss://api.openai.com/v1/realtime?model={_model}";
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {_apiKey}",
            ["OpenAI-Beta"] = "realtime=v1"
        };

        await _transport.ConnectAsync(url, headers, _cts.Token);
        Log("🔌 Connected to OpenAI Realtime");

        // Send session config
        var sessionConfig = RealtimeSessionConfig.Build(
            _session.GetSystemPrompt(), _voice, _codec);
        await _transport.SendAsync(sessionConfig, _cts.Token);

        var audioFormat = _codec == G711CodecType.PCMU ? "g711_ulaw" : "g711_alaw";
        Log($"📋 Session configured: {audioFormat} passthrough, sync_booking_data tool");

        // Wire session layer events → instruction coordinator
        _session.OnAiInstruction += _instructions.OnSessionInstruction;
        _session.OnTruncateConversation += () => _ = _instructions.TruncateConversationAsync();
        _session.OnTypingSoundsChanged += enabled =>
            Log(enabled ? "🔊 Typing sounds signal (recalculation)" : "🔇 Typing sounds signal (fare ready)");

        // Start audio bridge
        _audio.Start();

        Log("✅ Bidirectional audio bridge active (v5 architecture)");

        // Send greeting
        await SendGreetingAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        _session.OnAiInstruction -= _instructions.OnSessionInstruction;

        _audio.Dispose();
        await _transport.DisposeAsync();
        _cts.Dispose();

        Log("🔌 OpenAI Realtime disconnected");
    }

    // ─── Event Routing ──────────────────────────────────────

    private async Task HandleServerMessageAsync(string json)
    {
        var evt = RealtimeEventParser.Parse(json);

        switch (evt.Type)
        {
            // ── FAST PATH: Audio deltas — must never be blocked ──
            case RealtimeEventType.AudioDelta:
                _audio.HandleAudioDelta(evt.AudioBase64);
                break;

            // ── AI response starting → arm mic gate ──
            case RealtimeEventType.ResponseCreated:
                _micGate.Arm();
                break;

            // ── AI finished sending audio → ungate mic ──
            case RealtimeEventType.AudioDone:
                _audio.HandleResponseAudioDone();
                break;

            // ── Barge-in ──
            case RealtimeEventType.SpeechStarted:
                _tools.ResetTurn();
                HandleSpeechStarted();
                break;

            // ── Speech ended (no-op — let AI auto-respond for tool calls) ──
            case RealtimeEventType.SpeechStopped:
                break;

            // ── Caller transcript ──
            case RealtimeEventType.CallerTranscript:
                HandleCallerTranscript(evt.Transcript);
                break;

            // ── Ada's spoken transcript ──
            case RealtimeEventType.AdaTranscriptDone:
                HandleAdaTranscript(evt.Transcript);
                break;

            // ── Tool call ──
            case RealtimeEventType.ToolCallDone:
                await _tools.HandleToolCallAsync(evt);
                break;

            // ── Response canceled → apply pending instruction ──
            case RealtimeEventType.ResponseCanceled:
                await _instructions.OnResponseCanceledAsync();
                break;

            // ── Session lifecycle ──
            case RealtimeEventType.SessionCreated:
                Log("📡 Session created by server");
                break;

            case RealtimeEventType.SessionUpdated:
                Log("📋 Session config accepted");
                break;

            // ── Errors ──
            case RealtimeEventType.Error:
                HandleError(evt.ErrorMessage);
                break;
        }
    }

    // ─── Event Handlers ─────────────────────────────────────

    private void HandleSpeechStarted()
    {
        if (!_micGate.IsGated)
        {
            Log("🎤 Barge-in — mic already ungated, skipping re-flush");
            return;
        }

        if (_audio.HandleBargeIn())
        {
            // Barge-in processed successfully
        }
        else
        {
            Log("🎤 Barge-in debounced");
        }
    }

    private void HandleCallerTranscript(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return;

        // Store raw Whisper transcript BEFORE any AI reinterpretation
        _tools.SetWhisperTranscript(transcript);
        Log($"👤 Caller: {transcript}");

        // If tool call already handled this turn, skip
        if (_tools.ToolCalledInResponse)
        {
            Log("📋 Transcript skipped — sync_booking_data already processed this turn");
            return;
        }

        // ── Hybrid Tool-First Strategy ──
        // Wait up to 1.5s for a tool call to arrive before falling back to deterministic path.
        var capturedTranscript = transcript;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 15; i++)
                {
                    await Task.Delay(100, _cts.Token);
                    if (_tools.ToolCalledInResponse)
                    {
                        Log("📋 Transcript skipped (deferred) — sync_booking_data processed this turn");
                        return;
                    }
                }

                Log("📋 No tool call received — falling back to transcript processing");
                await _transport.SendAsync(new { type = "response.cancel" }, _cts.Token);
                await _session.ProcessCallerResponseAsync(capturedTranscript, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"⚠ Error processing transcript: {ex.Message}");
            }
        });
    }

    private void HandleAdaTranscript(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return;

        // Strip [CORRECTION:xxx] tags — metadata for session layer, not spoken content
        var cleanText = Regex.Replace(rawText, @"^\[CORRECTION:\w+\]\s*", "").Trim();
        Log($"🤖 AI: {cleanText}");

        // Store for injection into subsequent tool calls
        _tools.SetAdaTranscript(cleanText);

        // Feed to session on background task — don't block receive loop
        // Pass ORIGINAL text (with tags) so session can detect corrections
        var text = rawText;
        _ = Task.Run(async () =>
        {
            try { await _session.ProcessAdaTranscriptAsync(text); }
            catch (Exception ex) { Log($"⚠️ Ada transcript processing error: {ex.Message}"); }
        });
    }

    private void HandleError(string? errMsg)
    {
        // Ignore benign errors
        if (errMsg != null && (
            errMsg.Contains("no active response found") ||
            errMsg.Contains("buffer too small")))
            return;

        Log($"⚠ OpenAI error: {errMsg}");
    }

    // ─── Greeting ───────────────────────────────────────────

    private async Task SendGreetingAsync()
    {
        try
        {
            var greetingMessage = _session.BuildGreetingMessage();
            await _transport.SendAsync(
                RealtimeSessionConfig.BuildGreetingItem(greetingMessage), _cts.Token);
            await _transport.SendAsync(
                RealtimeSessionConfig.BuildGreetingResponse(), _cts.Token);
            Log("📢 Greeting sent via conversation item");
        }
        catch (Exception ex)
        {
            Log($"⚠ Greeting send error: {ex.Message}");
        }
    }

    // ─── Logging ────────────────────────────────────────────

    private void Log(string msg)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _logger.LogInformation(msg); } catch { }
        });
        OnLog?.Invoke($"[RT:{_callId}] {msg}");
    }
}
