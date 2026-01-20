using System.Collections.Concurrent;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Custom audio source that receives PCM audio from Ada (WebSocket) and provides
/// encoded samples to SIPSorcery's RTP transport. Based on AudioExtrasSource pattern.
/// </summary>
public class AdaAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 500;
    private const int FADE_IN_SAMPLES = 48;

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;
    private readonly ConcurrentQueue<short[]> _pcmQueue = new();
    
    private System.Threading.Timer? _sendTimer;
    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private bool _needsFadeIn = true;
    private volatile bool _disposed;

    // Debug counters
    private int _enqueuedFrames = 0;
    private int _sentFrames = 0;
    private int _silenceFrames = 0;
    private DateTime _lastStatsLog = DateTime.MinValue;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError;
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady { add { } remove { } }
    
    // Debug logging event
    public event Action<string>? OnDebugLog;

    public AdaAudioSource()
    {
        _audioEncoder = new AudioEncoder();
        _audioFormatManager = new MediaFormatManager<AudioFormat>(_audioEncoder.SupportedFormats);
        DebugLog("🔧 AdaAudioSource created");
    }

    public List<AudioFormat> GetAudioSourceFormats() => _audioFormatManager.GetSourceFormats();

    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        _audioFormatManager.SetSelectedFormat(audioFormat);
        DebugLog($"🎵 Format set: {audioFormat.FormatName} @ {audioFormat.ClockRate}Hz");
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        _audioFormatManager.RestrictFormats(filter);
    }

    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;

    public bool IsAudioSourcePaused() => _isPaused;

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        // Not used - we receive audio via EnqueuePcm24
    }

    /// <summary>
    /// Queue PCM audio from Ada (24kHz, 16-bit signed, little-endian).
    /// This will be resampled and encoded based on negotiated codec.
    /// </summary>
    public void EnqueuePcm24(byte[] pcmBytes)
    {
        if (_disposed || _isClosed || pcmBytes.Length == 0) return;

        // Convert bytes to shorts
        var pcm24 = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcm24, 0, pcmBytes.Length);

        // Apply fade-in to avoid clicks
        if (_needsFadeIn && pcm24.Length > 0)
        {
            int fadeLen = Math.Min(FADE_IN_SAMPLES, pcm24.Length);
            for (int i = 0; i < fadeLen; i++)
            {
                float gain = (float)i / fadeLen;
                pcm24[i] = (short)(pcm24[i] * gain);
            }
            _needsFadeIn = false;
            DebugLog("🎚️ Applied fade-in");
        }

        // Bound the queue
        int dropped = 0;
        while (_pcmQueue.Count >= MAX_QUEUED_FRAMES)
        {
            _pcmQueue.TryDequeue(out _);
            dropped++;
        }
        if (dropped > 0)
            DebugLog($"⚠️ Queue overflow, dropped {dropped} frames");

        _pcmQueue.Enqueue(pcm24);
        _enqueuedFrames++;

        // Log stats every 5 seconds
        if ((DateTime.Now - _lastStatsLog).TotalSeconds >= 5)
        {
            DebugLog($"📊 Audio stats: enqueued={_enqueuedFrames}, sent={_sentFrames}, silence={_silenceFrames}, queue={_pcmQueue.Count}");
            _lastStatsLog = DateTime.Now;
        }
    }

    /// <summary>
    /// Signal that a new response is starting (for fade-in).
    /// </summary>
    public void ResetFadeIn()
    {
        _needsFadeIn = true;
        DebugLog("🔄 Fade-in reset (new response)");
    }

    /// <summary>
    /// Clear all queued audio.
    /// </summary>
    public void ClearQueue()
    {
        int cleared = 0;
        while (_pcmQueue.TryDequeue(out _)) cleared++;
        _needsFadeIn = true;
        if (cleared > 0)
            DebugLog($"🗑️ Cleared {cleared} queued frames");
    }

    public Task StartAudio()
    {
        if (_audioFormatManager.SelectedFormat.IsEmpty())
            throw new ApplicationException("Audio format not set. Cannot start AdaAudioSource.");

        if (!_isStarted)
        {
            _isStarted = true;
            _sendTimer = new System.Threading.Timer(SendSample, null, 0, AUDIO_SAMPLE_PERIOD_MS);
            DebugLog($"▶️ Audio timer started ({AUDIO_SAMPLE_PERIOD_MS}ms period)");
        }

        return Task.CompletedTask;
    }

    public Task PauseAudio()
    {
        _isPaused = true;
        DebugLog("⏸️ Audio paused");
        return Task.CompletedTask;
    }

    public Task ResumeAudio()
    {
        _isPaused = false;
        DebugLog("▶️ Audio resumed");
        return Task.CompletedTask;
    }

    public Task CloseAudio()
    {
        if (!_isClosed)
        {
            _isClosed = true;
            _sendTimer?.Dispose();
            _sendTimer = null;
            ClearQueue();
            DebugLog($"⏹️ Audio closed (total: enqueued={_enqueuedFrames}, sent={_sentFrames}, silence={_silenceFrames})");
        }
        return Task.CompletedTask;
    }

    private void SendSample(object? state)
    {
        if (_isClosed || _isPaused || _disposed) return;

        try
        {
            if (!_pcmQueue.TryDequeue(out var pcm24))
            {
                // Send silence to maintain timing
                SendSilence();
                return;
            }

            // Resample 24kHz → selected codec rate (typically 8kHz for PCMU/PCMA)
            int targetRate = _audioFormatManager.SelectedFormat.ClockRate;
            short[] resampled = Resample(pcm24, 24000, targetRate);

            // Encode using negotiated codec
            byte[] encoded = _audioEncoder.EncodeAudio(resampled, _audioFormatManager.SelectedFormat);

            // Calculate RTP duration
            uint durationRtpUnits = (uint)(targetRate / 1000 * AUDIO_SAMPLE_PERIOD_MS);

            _sentFrames++;
            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch (Exception ex)
        {
            DebugLog($"❌ SendSample error: {ex.Message}");
            OnAudioSourceError?.Invoke($"AdaAudioSource error: {ex.Message}");
        }
    }

    private void SendSilence()
    {
        try
        {
            int targetRate = _audioFormatManager.SelectedFormat.ClockRate;
            int samplesNeeded = targetRate / 1000 * AUDIO_SAMPLE_PERIOD_MS;
            var silence = new short[samplesNeeded];

            byte[] encoded = _audioEncoder.EncodeAudio(silence, _audioFormatManager.SelectedFormat);
            uint durationRtpUnits = (uint)(targetRate / 1000 * AUDIO_SAMPLE_PERIOD_MS);

            _silenceFrames++;
            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch { }
    }

    /// <summary>
    /// Simple linear resampling.
    /// </summary>
    private static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;

        double ratio = (double)fromRate / toRate;
        int outputLength = (int)(input.Length / ratio);
        var output = new short[outputLength];

        for (int i = 0; i < output.Length; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            if (srcIndex + 1 < input.Length)
                output[i] = (short)(input[srcIndex] * (1 - frac) + input[srcIndex + 1] * frac);
            else if (srcIndex < input.Length)
                output[i] = input[srcIndex];
        }

        return output;
    }

    private void DebugLog(string message)
    {
        OnDebugLog?.Invoke($"[AdaAudioSource] {message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DebugLog("🗑️ Disposing AdaAudioSource");
        _sendTimer?.Dispose();
        ClearQueue();

        GC.SuppressFinalize(this);
    }
}
