using System.Collections.Concurrent;
using NAudio.Wave;

namespace TaxiSipBridge;

/// <summary>
/// Real-time audio monitor that plays µ-law 8kHz audio through speakers.
/// Useful for debugging SIP audio streams.
/// MEMORY LEAK FIXES: Bounded queue, proper disposal.
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
    
    // MEMORY LEAK FIX: Bound the queue
    private const int MAX_QUEUE_SIZE = 250; // ~5 seconds at 20ms/frame

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public AudioMonitor()
    {
        // µ-law 8kHz mono → PCM 8kHz mono for playback
        var pcmFormat = new WaveFormat(8000, 16, 1);
        _waveProvider = new BufferedWaveProvider(pcmFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true // MEMORY LEAK FIX
        };

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100
        };
        _waveOut.Init(_waveProvider);
        _waveOut.Play();

        _playbackTask = Task.Run(ProcessAudioQueue);
    }

    /// <summary>
    /// Add a µ-law frame (160 bytes = 20ms) to the monitor.
    /// </summary>
    public void AddFrame(byte[] ulawFrame)
    {
        if (!_isEnabled || _isDisposed) return;
        
        // MEMORY LEAK FIX: Drop oldest if queue is full
        if (_audioQueue.Count >= MAX_QUEUE_SIZE)
        {
            _audioQueue.TryDequeue(out _);
        }
        
        _audioQueue.Enqueue(ulawFrame);
    }

    /// <summary>
    /// Clear all pending audio frames.
    /// </summary>
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
                if (_audioQueue.TryDequeue(out var ulawFrame))
                {
                    // Decode µ-law to PCM16
                    var pcm = AudioCodecs.MuLawDecode(ulawFrame);
                    var pcmBytes = AudioCodecs.ShortsToBytes(pcm);
                    
                    _waveProvider.AddSamples(pcmBytes, 0, pcmBytes.Length);
                }
                else
                {
                    await Task.Delay(5, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore playback errors
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try { _cts.Cancel(); } catch { }
        
        try 
        { 
            _playbackTask.Wait(500); 
        } 
        catch { }

        try
        {
            _waveOut.Stop();
            _waveOut.Dispose();
        }
        catch { }
        
        try { _cts.Dispose(); } catch { }
        
        // Clear remaining audio
        while (_audioQueue.TryDequeue(out _)) { }
        
        GC.SuppressFinalize(this);
    }
}
