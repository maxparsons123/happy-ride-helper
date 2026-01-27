using System.Collections.Concurrent;
using NAudio.Wave;

namespace TaxiSipBridge;

/// <summary>
/// Audio monitor that plays back PCM audio through speakers.
/// Supports both 8kHz µ-law (from SIP) and 24kHz PCM16 (processed for OpenAI).
/// </summary>
public class AudioMonitor : IDisposable
{
    private readonly BufferedWaveProvider _waveProvider;
    private readonly WaveOutEvent _waveOut;
    private readonly ConcurrentQueue<byte[]> _audioQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _playbackTask;
    private volatile bool _isEnabled = false;
    private volatile bool _isDisposed = false;
    
    private const int MAX_QUEUE_SIZE = 250;
    
    // Audio format mode
    private readonly bool _isPcm24k;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>
    /// Create an audio monitor.
    /// </summary>
    /// <param name="usePcm24k">If true, expects 24kHz PCM16 input. If false, expects 8kHz µ-law.</param>
    public AudioMonitor(bool usePcm24k = false)
    {
        _isPcm24k = usePcm24k;
        
        // Set format based on mode
        var sampleRate = usePcm24k ? 24000 : 8000;
        var pcmFormat = new WaveFormat(sampleRate, 16, 1);
        
        _waveProvider = new BufferedWaveProvider(pcmFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
        _waveOut.Init(_waveProvider);
        _waveOut.Play();

        _playbackTask = Task.Run(ProcessAudioQueue);
    }

    /// <summary>
    /// Add a frame of audio to the playback queue.
    /// For 8kHz mode: expects µ-law encoded bytes.
    /// For 24kHz mode: expects PCM16 bytes (little-endian).
    /// </summary>
    public void AddFrame(byte[] audioFrame)
    {
        if (!_isEnabled || _isDisposed || audioFrame == null || audioFrame.Length == 0) return;
        
        if (_audioQueue.Count >= MAX_QUEUE_SIZE)
        {
            _audioQueue.TryDequeue(out _);
        }
        
        _audioQueue.Enqueue(audioFrame);
    }

    public void Clear()
    {
        while (_audioQueue.TryDequeue(out _)) { }
        _waveProvider.ClearBuffer();
    }

    private async Task ProcessAudioQueue()
    {
        while (!_cts.Token.IsCancellationRequested && !_isDisposed)
        {
            try
            {
                if (_audioQueue.TryDequeue(out var frame))
                {
                    byte[] pcmBytes;
                    
                    if (_isPcm24k)
                    {
                        // Already PCM16 at 24kHz - use directly
                        pcmBytes = frame;
                    }
                    else
                    {
                        // Decode µ-law to PCM16 at 8kHz
                        var pcm = AudioCodecs.MuLawDecode(frame);
                        pcmBytes = AudioCodecs.ShortsToBytes(pcm);
                    }
                    
                    _waveProvider.AddSamples(pcmBytes, 0, pcmBytes.Length);
                }
                else
                {
                    await Task.Delay(5, _cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try { _cts.Cancel(); } catch { }
        try { _playbackTask.Wait(500); } catch { }
        try { _waveOut.Stop(); _waveOut.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        
        while (_audioQueue.TryDequeue(out _)) { }
        
        GC.SuppressFinalize(this);
    }
}
