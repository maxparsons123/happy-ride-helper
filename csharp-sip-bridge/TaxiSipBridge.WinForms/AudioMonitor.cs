using System.Collections.Concurrent;
using NAudio.Wave;

namespace TaxiSipBridge;

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

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public AudioMonitor()
    {
        var pcmFormat = new WaveFormat(8000, 16, 1);
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

    public void AddFrame(byte[] ulawFrame)
    {
        if (!_isEnabled || _isDisposed) return;
        
        if (_audioQueue.Count >= MAX_QUEUE_SIZE)
        {
            _audioQueue.TryDequeue(out _);
        }
        
        _audioQueue.Enqueue(ulawFrame);
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
                if (_audioQueue.TryDequeue(out var ulawFrame))
                {
                    var pcm = AudioCodecs.MuLawDecode(ulawFrame);
                    var pcmBytes = AudioCodecs.ShortsToBytes(pcm);
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
