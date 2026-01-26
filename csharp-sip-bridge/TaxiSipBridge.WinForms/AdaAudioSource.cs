using System.Collections.Concurrent;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Timer = System.Threading.Timer;

namespace TaxiSipBridge;

public enum AudioMode { Standard, JitterBuffer, SimpleResample, TestTone }

public class AdaAudioSource : IAudioSource, IDisposable
{
    private const int AUDIO_SAMPLE_PERIOD_MS = 20;
    private const int MAX_QUEUED_FRAMES = 50; // Reduced: better to drop frames than have high latency
    private const int CROSSFADE_SAMPLES = 16;  // Standard for VoIP transitions

    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly IAudioEncoder _audioEncoder;
    private readonly ConcurrentQueue<short[]> _pcmQueue = new();

    private Timer? _sendTimer;
    private bool _isStarted;
    private bool _isPaused;
    private bool _isClosed;
    private volatile bool _disposed;

    // Smoother transition state
    private short _prevFrameLastSample;
    private bool _lastFrameWasSilence = true;
    private int _consecutiveUnderruns;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError;
    event Action<EncodedAudioFrame>? IAudioSource.OnAudioSourceEncodedFrameReady { add { } remove { } }

    public AdaAudioSource(AudioMode audioMode = AudioMode.Standard, int jitterBufferMs = 60)
    {
        _audioEncoder = new AudioEncoder();
        _audioFormatManager = new MediaFormatManager<AudioFormat>(_audioEncoder.SupportedFormats);
    }

    public void EnqueuePcm24(byte[] pcmBytes)
    {
        if (_disposed || _isClosed || pcmBytes.Length == 0) return;

        var pcm24All = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcm24All, 0, pcmBytes.Length);

        const int PCM24_SAMPLES_PER_20MS = 480;

        for (int offset = 0; offset < pcm24All.Length; offset += PCM24_SAMPLES_PER_20MS)
        {
            int len = Math.Min(PCM24_SAMPLES_PER_20MS, pcm24All.Length - offset);
            var frame = new short[PCM24_SAMPLES_PER_20MS];
            Array.Copy(pcm24All, offset, frame, 0, len);

            // Jitter Control: If queue is getting too deep (> 400ms), clear old audio to maintain "real-time" feel
            if (_pcmQueue.Count > 20) 
            {
                _pcmQueue.TryDequeue(out _);
            }

            _pcmQueue.Enqueue(frame);
        }
    }

    private void SendSample(object? state)
    {
        if (_isClosed || _isPaused || _disposed) return;

        int targetRate = _audioFormatManager.SelectedFormat.ClockRate;
        int samplesNeeded = targetRate / 1000 * AUDIO_SAMPLE_PERIOD_MS;
        short[] audioFrame;

        if (_pcmQueue.TryDequeue(out var pcm24))
        {
            _consecutiveUnderruns = 0;
            
            // High-Quality Resampling
            if (targetRate == 8000)
                audioFrame = Resample24kTo8kHighQuality(pcm24);
            else
                audioFrame = AudioCodecs.Resample(pcm24, 24000, targetRate);

            // Smoothing: Cosine Crossfade from previous frame to prevent "pops"
            ApplySmoothing(audioFrame);
            
            _prevFrameLastSample = audioFrame[^1];
            _lastFrameWasSilence = false;
        }
        else
        {
            // Underrun: Send silence but ramp down smoothly
            audioFrame = new short[samplesNeeded];
            if (!_lastFrameWasSilence)
            {
                for (int i = 0; i < Math.Min(10, audioFrame.Length); i++)
                {
                    float gain = 1.0f - (i / 10f);
                    audioFrame[i] = (short)(_prevFrameLastSample * gain);
                }
            }
            _lastFrameWasSilence = true;
        }

        byte[] encoded = _audioEncoder.EncodeAudio(audioFrame, _audioFormatManager.SelectedFormat);
        OnAudioSourceEncodedSample?.Invoke((uint)samplesNeeded, encoded);
    }

    private void ApplySmoothing(short[] frame)
    {
        if (_lastFrameWasSilence || frame.Length < CROSSFADE_SAMPLES) return;

        for (int i = 0; i < CROSSFADE_SAMPLES; i++)
        {
            // Cosine interpolation: cleaner than linear for audio power levels
            double phase = (double)(i + 1) / (CROSSFADE_SAMPLES + 1);
            float t = (float)((1.0 - Math.Cos(phase * Math.PI)) / 2.0);
            frame[i] = (short)(_prevFrameLastSample * (1.0f - t) + frame[i] * t);
        }
    }

    private static short[] Resample24kTo8kHighQuality(short[] pcm24)
    {
        int outLen = pcm24.Length / 3;
        var output = new short[outLen];

        // 13-tap FIR Filter coefficients (Sinc with Blackman window)
        // This cuts off frequencies above 3.5kHz to stop the "metallic" robotic sound
        float[] h = { -0.005f, -0.012f, 0.010f, 0.080f, 0.180f, 0.240f, 0.180f, 0.080f, 0.010f, -0.012f, -0.005f };
        float sum = 0.746f; // Sum of coefficients

        for (int i = 0; i < outLen; i++)
        {
            float acc = 0;
            int center = i * 3;
            for (int j = 0; j < h.Length; j++)
            {
                int idx = center + j - 5;
                if (idx >= 0 && idx < pcm24.Length) acc += pcm24[idx] * h[j];
            }
            output[i] = (short)Math.Clamp(acc / sum, short.MinValue, short.MaxValue);
        }
        return output;
    }

    // Standard IAudioSource Boilerplate
    public List<AudioFormat> GetAudioSourceFormats() => _audioFormatManager.GetSourceFormats();
    public void SetAudioSourceFormat(AudioFormat audioFormat) => _audioFormatManager.SetSelectedFormat(audioFormat);
    public void RestrictFormats(Func<AudioFormat, bool> filter) => _audioFormatManager.RestrictFormats(filter);
    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
    public bool IsAudioSourcePaused() => _isPaused;
    public Task StartAudio() { 
        if (!_isStarted) { _isStarted = true; _sendTimer = new Timer(SendSample, null, 0, AUDIO_SAMPLE_PERIOD_MS); }
        return Task.CompletedTask; 
    }
    public Task PauseAudio() { _isPaused = true; return Task.CompletedTask; }
    public Task ResumeAudio() { _isPaused = false; return Task.CompletedTask; }
    public Task CloseAudio() { _isClosed = true; _sendTimer?.Dispose(); return Task.CompletedTask; }
    public void Dispose() { _disposed = true; _sendTimer?.Dispose(); GC.SuppressFinalize(this); }
    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum s, uint d, short[] p) { }
}
