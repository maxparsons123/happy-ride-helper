using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Timer = System.Threading.Timer;

namespace TaxiSipBridge;

/// <summary>
/// Test audio source that sends a continuous 440Hz sine wave.
/// Used to verify RTP/codec configuration independently of Ada.
/// </summary>
public class TestToneAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const double TONE_FREQ = 440.0; // A4 note

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;

    private Timer? _sendTimer;
    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private volatile bool _disposed;

    private int _sampleIndex;
    private int _framesSent;
    private DateTime _startTime;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError;
    
    event Action<EncodedAudioFrame>? IAudioSource.OnAudioSourceEncodedFrameReady
    {
        add { }
        remove { }
    }

    public event Action<string>? OnLog;

    public TestToneAudioSource()
    {
        _audioEncoder = new AudioEncoder();
        _audioFormatManager = new MediaFormatManager<AudioFormat>(_audioEncoder.SupportedFormats);

        Log($"🔧 TestToneAudioSource created");
        Log($"📋 Supported formats:");
        foreach (var fmt in _audioEncoder.SupportedFormats)
        {
            Log($"   - {fmt.FormatName} (ID={fmt.ID}, {fmt.ClockRate}Hz, {fmt.RtpClockRate}Hz RTP)");
        }
    }

    public List<AudioFormat> GetAudioSourceFormats() => _audioFormatManager.GetSourceFormats();

    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        _audioFormatManager.SetSelectedFormat(audioFormat);
        Log($"✅ Format selected: {audioFormat.FormatName} (ID={audioFormat.ID}) @ {audioFormat.ClockRate}Hz");
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        _audioFormatManager.RestrictFormats(filter);
    }

    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
    public bool IsAudioSourcePaused() => _isPaused;

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        // Not used
    }

    public Task StartAudio()
    {
        if (_audioFormatManager.SelectedFormat.IsEmpty())
            throw new ApplicationException("Audio format not set.");

        if (!_isStarted)
        {
            _isStarted = true;
            _startTime = DateTime.Now;
            _sendTimer = new Timer(SendTone, null, 0, AUDIO_SAMPLE_PERIOD_MS);

            var fmt = _audioFormatManager.SelectedFormat;
            Log($"▶️ STARTED sending {TONE_FREQ}Hz tone");
            Log($"   Format: {fmt.FormatName} (ID={fmt.ID})");
            Log($"   Clock: {fmt.ClockRate}Hz");
            Log($"   Period: {AUDIO_SAMPLE_PERIOD_MS}ms");
            Log($"   Samples/frame: {fmt.ClockRate / 1000 * AUDIO_SAMPLE_PERIOD_MS}");
        }

        return Task.CompletedTask;
    }

    public Task PauseAudio()
    {
        _isPaused = true;
        Log("⏸️ Paused");
        return Task.CompletedTask;
    }

    public Task ResumeAudio()
    {
        _isPaused = false;
        Log("▶️ Resumed");
        return Task.CompletedTask;
    }

    public Task CloseAudio()
    {
        if (!_isClosed)
        {
            _isClosed = true;
            _sendTimer?.Dispose();
            _sendTimer = null;

            var duration = DateTime.Now - _startTime;
            Log($"⏹️ STOPPED after {_framesSent} frames ({duration.TotalSeconds:F1}s)");
        }
        return Task.CompletedTask;
    }

    private void SendTone(object? state)
    {
        if (_isClosed || _isPaused || _disposed) return;

        try
        {
            var format = _audioFormatManager.SelectedFormat;
            int sampleRate = format.ClockRate;
            int samplesNeeded = sampleRate / 1000 * AUDIO_SAMPLE_PERIOD_MS;

            // Generate sine wave
            var samples = new short[samplesNeeded];
            double angularFreq = 2.0 * Math.PI * TONE_FREQ / sampleRate;

            for (int i = 0; i < samplesNeeded; i++)
            {
                double sample = Math.Sin(angularFreq * _sampleIndex);
                samples[i] = (short)(sample * 16000); // ~50% volume
                _sampleIndex++;
            }

            // Encode
            byte[] encoded = _audioEncoder.EncodeAudio(samples, format);

            // Log first few frames and every 50th frame
            if (_framesSent < 5 || _framesSent % 50 == 0)
            {
                Log($"📤 Frame {_framesSent}: {samplesNeeded} samples → {encoded.Length} bytes ({format.FormatName})");
            }

            // Send
            uint durationRtpUnits = (uint)samplesNeeded;
            _framesSent++;
            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch (Exception ex)
        {
            Log($"❌ Error: {ex.Message}");
            OnAudioSourceError?.Invoke(ex.Message);
        }
    }

    private void Log(string msg)
    {
        OnLog?.Invoke($"[TestTone] {msg}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sendTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
