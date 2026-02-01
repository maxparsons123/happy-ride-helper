using System.Collections.Concurrent;

namespace TaxiSipBridge;

/// <summary>
/// Cheaper AI pipeline: Deepgram STT → GPT-4o-mini → OpenAI TTS (Shimmer).
/// Implements IAudioAIClient for drop-in replacement of OpenAI Realtime.
/// 
/// Cost comparison (20K calls × 30 sec):
/// - OpenAI Realtime: ~$900/month
/// - This pipeline: ~$160/month
/// 
/// Trade-off: ~1-2 seconds additional latency per turn.
/// </summary>
public class TextPipelineClient : IAudioAIClient
{
    private readonly string _openAiApiKey;
    private readonly string _deepgramApiKey;
    private readonly string _voice;
    private readonly string? _systemPrompt;
    
    private DeepgramSttClient? _stt;
    private OpenAITextClient? _llm;
    private OpenAITtsClient? _tts;
    
    private CancellationTokenSource? _cts;
    private bool _disposed = false;
    private bool _isProcessing = false;
    private string _partialTranscript = "";
    private string? _callerId;
    
    // Outbound audio queue (24kHz PCM → will be converted to µ-law)
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 500;
    
    // Events
    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnAdaSpeaking;
    public event Action<byte[]>? OnPcm24Audio;
    public event Action? OnResponseStarted;
    public event Action? OnResponseCompleted;
    public event Action? OnCallEnded;
    
    public bool IsConnected => _stt?.IsConnected == true;
    public int PendingFrameCount => _outboundQueue.Count;
    
    /// <summary>
    /// Create a cheaper text-based pipeline client.
    /// </summary>
    /// <param name="openAiApiKey">OpenAI API key (for GPT-4o-mini and TTS)</param>
    /// <param name="deepgramApiKey">Deepgram API key (for STT)</param>
    /// <param name="voice">TTS voice (default: shimmer)</param>
    /// <param name="systemPrompt">Custom system prompt (optional)</param>
    public TextPipelineClient(
        string openAiApiKey,
        string deepgramApiKey,
        string voice = "shimmer",
        string? systemPrompt = null)
    {
        _openAiApiKey = openAiApiKey;
        _deepgramApiKey = deepgramApiKey;
        _voice = voice;
        _systemPrompt = systemPrompt;
    }
    
    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        _callerId = caller;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        // Initialize components
        _stt = new DeepgramSttClient(_deepgramApiKey);
        _llm = new OpenAITextClient(_openAiApiKey, "gpt-4o-mini", _systemPrompt);
        _tts = new OpenAITtsClient(_openAiApiKey, _voice);
        
        // Wire up logging
        _stt.OnLog += msg => OnLog?.Invoke(msg);
        _llm.OnLog += msg => OnLog?.Invoke(msg);
        _tts.OnLog += msg => OnLog?.Invoke(msg);
        
        // Wire up STT events
        _stt.OnPartialTranscript += HandlePartialTranscript;
        _stt.OnTranscript += HandleFinalTranscript;
        _stt.OnSpeechStarted += HandleSpeechStarted;
        _stt.OnSpeechEnded += HandleSpeechEnded;
        _stt.OnConnected += () => OnConnected?.Invoke();
        _stt.OnDisconnected += () => OnDisconnected?.Invoke();
        
        // Wire up LLM tool calls
        _llm.OnToolCall += HandleToolCall;
        
        try
        {
            // Connect to Deepgram (24kHz for better quality)
            await _stt.ConnectAsync(sampleRate: 24000, channels: 1, ct);
            
            OnLog?.Invoke("[Pipeline] Connected - sending greeting...");
            
            // Generate and speak greeting
            var greeting = await _llm.GetGreetingAsync(_callerId, _cts.Token);
            await SpeakAsync(greeting);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[Pipeline] Connection failed: {ex.Message}");
            throw;
        }
    }
    
    public async Task SendMuLawAsync(byte[] ulawData)
    {
        // Decode µ-law to PCM16 at 8kHz
        var pcm8k = AudioCodecs.MuLawToPcm(ulawData);
        
        // Upsample 8kHz → 24kHz (3x)
        var pcm24k = Resample8kTo24k(pcm8k);
        
        // Send to Deepgram
        var pcmBytes = AudioCodecs.ShortsToBytes(pcm24k);
        await SendAudioAsync(pcmBytes, 24000);
    }
    
    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (_stt == null || !_stt.IsConnected) return;
        
        // If not 24kHz, we'd need to resample (for now, assume 24kHz)
        await _stt.SendAudioAsync(pcmData);
    }
    
    private void HandlePartialTranscript(string text)
    {
        _partialTranscript = text;
        OnTranscript?.Invoke($"[User...] {text}");
    }
    
    private async void HandleFinalTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_isProcessing) return; // Prevent overlapping responses
        
        _isProcessing = true;
        _partialTranscript = "";
        
        try
        {
            OnTranscript?.Invoke($"[User] {text}");
            
            // Get LLM response
            var response = await _llm!.SendMessageAsync(text, _cts?.Token ?? CancellationToken.None);
            
            if (!string.IsNullOrWhiteSpace(response))
            {
                await SpeakAsync(response);
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[Pipeline] Error processing: {ex.Message}");
        }
        finally
        {
            _isProcessing = false;
        }
    }
    
    private void HandleSpeechStarted()
    {
        // Could interrupt current TTS playback here if desired
        OnLog?.Invoke("[Pipeline] User started speaking");
    }
    
    private void HandleSpeechEnded()
    {
        OnLog?.Invoke("[Pipeline] User stopped speaking");
    }
    
    private void HandleToolCall(string toolName, Dictionary<string, object> args)
    {
        OnLog?.Invoke($"[Pipeline] Tool: {toolName} with {args.Count} args");
        
        if (toolName == "end_call")
        {
            OnCallEnded?.Invoke();
        }
        else if (toolName == "book_taxi")
        {
            // In a real implementation, call the dispatch webhook here
            var pickup = args.GetValueOrDefault("pickup")?.ToString() ?? "Unknown";
            var dest = args.GetValueOrDefault("destination")?.ToString() ?? "Unknown";
            var pax = args.GetValueOrDefault("passengers")?.ToString() ?? "1";
            
            OnLog?.Invoke($"[Pipeline] Booking: {pickup} → {dest}, {pax} pax");
            
            // Simulate webhook response
            _llm?.AddToolResult("book_taxi", "fare: £8.50, eta: 5 minutes");
        }
    }
    
    private async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _tts == null) return;
        
        OnResponseStarted?.Invoke();
        OnAdaSpeaking?.Invoke(text);
        OnTranscript?.Invoke($"[Ada] {text}");
        
        try
        {
            // Generate TTS audio
            var pcm24Data = await _tts.GenerateSpeechAsync(text, _cts?.Token ?? CancellationToken.None);
            
            if (pcm24Data != null && pcm24Data.Length > 0)
            {
                // Fire PCM24 event for direct playback
                OnPcm24Audio?.Invoke(pcm24Data);
                
                // Also queue for µ-law conversion (RTP output)
                QueueForRtp(pcm24Data);
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[Pipeline] TTS error: {ex.Message}");
        }
        finally
        {
            // In the pipeline client, treat "response completed" as "TTS generation done".
            OnResponseCompleted?.Invoke();
        }
    }
    
    private void QueueForRtp(byte[] pcm24Data)
    {
        // Convert 24kHz PCM to 8kHz µ-law frames
        var shorts24k = AudioCodecs.BytesToShorts(pcm24Data);
        var shorts8k = Resample24kTo8k(shorts24k);
        var mulaw = AudioCodecs.PcmToMuLaw(shorts8k);
        
        // Split into 160-byte frames (20ms at 8kHz)
        const int frameSize = 160;
        for (int i = 0; i < mulaw.Length; i += frameSize)
        {
            if (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
            {
                // Drop oldest frame to prevent memory issues
                _outboundQueue.TryDequeue(out _);
            }
            
            var remaining = Math.Min(frameSize, mulaw.Length - i);
            var frame = new byte[frameSize];
            Array.Copy(mulaw, i, frame, 0, remaining);
            
            // Pad with silence if needed
            if (remaining < frameSize)
            {
                for (int j = remaining; j < frameSize; j++)
                    frame[j] = 0xFF; // µ-law silence
            }
            
            _outboundQueue.Enqueue(frame);
        }
    }
    
    private static short[] Resample8kTo24k(short[] pcm8k)
    {
        // Linear interpolation 8kHz → 24kHz (3x upsample)
        var pcm24k = new short[pcm8k.Length * 3];
        
        for (int i = 0; i < pcm8k.Length - 1; i++)
        {
            var current = pcm8k[i];
            var next = pcm8k[i + 1];
            var outIdx = i * 3;
            
            pcm24k[outIdx] = current;
            pcm24k[outIdx + 1] = (short)((current * 2 + next) / 3);
            pcm24k[outIdx + 2] = (short)((current + next * 2) / 3);
        }
        
        // Handle last sample
        if (pcm8k.Length > 0)
        {
            var lastIdx = (pcm8k.Length - 1) * 3;
            pcm24k[lastIdx] = pcm8k[^1];
            if (lastIdx + 1 < pcm24k.Length) pcm24k[lastIdx + 1] = pcm8k[^1];
            if (lastIdx + 2 < pcm24k.Length) pcm24k[lastIdx + 2] = pcm8k[^1];
        }
        
        return pcm24k;
    }
    
    private static short[] Resample24kTo8k(short[] pcm24k)
    {
        // Weighted linear interpolation 24kHz → 8kHz (3:1 downsample)
        var pcm8k = new short[pcm24k.Length / 3];
        
        for (int i = 0; i < pcm8k.Length; i++)
        {
            var srcIdx = i * 3;
            if (srcIdx + 2 < pcm24k.Length)
            {
                // Weighted blend: 25% + 50% + 25%
                pcm8k[i] = (short)((pcm24k[srcIdx] + pcm24k[srcIdx + 1] * 2 + pcm24k[srcIdx + 2]) / 4);
            }
            else if (srcIdx < pcm24k.Length)
            {
                pcm8k[i] = pcm24k[srcIdx];
            }
        }
        
        return pcm8k;
    }
    
    public byte[]? GetNextMuLawFrame()
    {
        if (_outboundQueue.TryDequeue(out var frame))
            return frame;
        return null;
    }
    
    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
    }
    
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        
        if (_stt != null)
            await _stt.DisconnectAsync();
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _cts?.Cancel();
        _cts?.Dispose();
        _stt?.Dispose();
        _llm?.Dispose();
        _tts?.Dispose();
        
        ClearPendingFrames();
    }
}
