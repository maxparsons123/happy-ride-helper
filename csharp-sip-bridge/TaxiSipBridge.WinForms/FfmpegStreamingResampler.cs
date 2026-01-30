using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Persistent FFmpeg process for real-time audio resampling.
/// Keeps FFmpeg running and pipes audio through continuously.
/// Optional enhancement for high-quality resampling via libsoxr.
/// </summary>
public class FfmpegStreamingResampler : IDisposable
{
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly string _ffmpegPath;
    
    private Process? _process;
    private Stream? _stdin;
    private Stream? _stdout;
    private Thread? _readerThread;
    private CancellationTokenSource? _cts;
    
    private readonly ConcurrentQueue<byte[]> _outputQueue = new();
    private readonly object _lock = new();
    private bool _isRunning;
    private bool _disposed;
    
    // Buffer for accumulating output samples
    private readonly MemoryStream _outputBuffer = new();
    
    public event Action<string>? OnDebugLog;
    public event Action<string>? OnError;
    
    public bool IsRunning => _isRunning;
    
    public FfmpegStreamingResampler(int inputRate = 24000, int outputRate = 8000, string ffmpegPath = "ffmpeg")
    {
        _inputRate = inputRate;
        _outputRate = outputRate;
        _ffmpegPath = ffmpegPath;
    }
    
    /// <summary>
    /// Start the persistent FFmpeg process.
    /// </summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (_isRunning) return true;
            
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    // Input: raw PCM s16le at input rate, mono
                    // Output: raw PCM s16le at output rate, mono
                    // Using soxr resampler for high quality (same algorithm as libsoxr)
                    Arguments = $"-hide_banner -loglevel error " +
                                $"-f s16le -ar {_inputRate} -ac 1 -i pipe:0 " +
                                $"-af \"aresample={_outputRate}:resampler=soxr:precision=28\" " +
                                $"-f s16le -ar {_outputRate} -ac 1 pipe:1",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                _process = Process.Start(psi);
                if (_process == null)
                {
                    OnError?.Invoke("Failed to start FFmpeg process");
                    return false;
                }
                
                _stdin = _process.StandardInput.BaseStream;
                _stdout = _process.StandardOutput.BaseStream;
                
                // Start background reader thread
                _cts = new CancellationTokenSource();
                _readerThread = new Thread(ReaderLoop)
                {
                    Name = "FFmpegReader",
                    IsBackground = true
                };
                _readerThread.Start();
                
                // Start error reader
                Task.Run(async () =>
                {
                    try
                    {
                        string? error = await _process.StandardError.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(error))
                            OnError?.Invoke($"FFmpeg stderr: {error}");
                    }
                    catch { }
                });
                
                _isRunning = true;
                OnDebugLog?.Invoke($"[FFmpegResampler] Started: {_inputRate}Hz → {_outputRate}Hz (soxr)");
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"FFmpeg start error: {ex.Message}");
                return false;
            }
        }
    }
    
    /// <summary>
    /// Feed input audio and get resampled output.
    /// Returns available output samples (may be empty if FFmpeg is still buffering).
    /// </summary>
    public short[] Resample(short[] input)
    {
        if (!_isRunning || _stdin == null)
        {
            // Fallback: return silence
            int expectedOutput = input.Length * _outputRate / _inputRate;
            return new short[expectedOutput];
        }
        
        try
        {
            // Convert shorts to bytes and write to FFmpeg stdin
            byte[] inputBytes = new byte[input.Length * 2];
            Buffer.BlockCopy(input, 0, inputBytes, 0, inputBytes.Length);
            
            _stdin.Write(inputBytes, 0, inputBytes.Length);
            _stdin.Flush();
            
            // Calculate expected output size
            int expectedOutputSamples = input.Length * _outputRate / _inputRate;
            int expectedOutputBytes = expectedOutputSamples * 2;
            
            // Wait briefly for output (FFmpeg has internal buffering)
            // Use a small spin-wait to allow output to arrive
            int waitMs = 0;
            while (_outputBuffer.Length < expectedOutputBytes && waitMs < 10)
            {
                Thread.Sleep(1);
                waitMs++;
            }
            
            // Read available output
            lock (_outputBuffer)
            {
                if (_outputBuffer.Length >= expectedOutputBytes)
                {
                    // We have enough data
                    byte[] outputBytes = new byte[expectedOutputBytes];
                    _outputBuffer.Position = 0;
                    _outputBuffer.Read(outputBytes, 0, expectedOutputBytes);
                    
                    // Shift remaining data to start of buffer
                    int remaining = (int)_outputBuffer.Length - expectedOutputBytes;
                    if (remaining > 0)
                    {
                        byte[] temp = new byte[remaining];
                        _outputBuffer.Read(temp, 0, remaining);
                        _outputBuffer.SetLength(0);
                        _outputBuffer.Write(temp, 0, remaining);
                    }
                    else
                    {
                        _outputBuffer.SetLength(0);
                    }
                    
                    // Convert bytes to shorts
                    short[] output = new short[expectedOutputSamples];
                    Buffer.BlockCopy(outputBytes, 0, output, 0, expectedOutputBytes);
                    return output;
                }
                else
                {
                    // Not enough data yet - return what we have padded with silence
                    int available = (int)_outputBuffer.Length;
                    byte[] outputBytes = new byte[expectedOutputBytes];
                    
                    if (available > 0)
                    {
                        _outputBuffer.Position = 0;
                        _outputBuffer.Read(outputBytes, 0, available);
                        _outputBuffer.SetLength(0);
                    }
                    
                    short[] output = new short[expectedOutputSamples];
                    Buffer.BlockCopy(outputBytes, 0, output, 0, Math.Min(available, expectedOutputBytes));
                    return output;
                }
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"FFmpeg resample error: {ex.Message}");
            int expectedOutput = input.Length * _outputRate / _inputRate;
            return new short[expectedOutput];
        }
    }
    
    /// <summary>
    /// Background thread that continuously reads FFmpeg output.
    /// </summary>
    private void ReaderLoop()
    {
        byte[] buffer = new byte[4096];
        
        try
        {
            while (!_cts?.Token.IsCancellationRequested ?? false)
            {
                if (_stdout == null) break;
                
                int bytesRead = _stdout.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    // EOF - process ended
                    break;
                }
                
                lock (_outputBuffer)
                {
                    long pos = _outputBuffer.Position;
                    _outputBuffer.Seek(0, SeekOrigin.End);
                    _outputBuffer.Write(buffer, 0, bytesRead);
                    _outputBuffer.Position = pos;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                OnError?.Invoke($"FFmpeg reader error: {ex.Message}");
        }
        
        _isRunning = false;
    }
    
    /// <summary>
    /// Reset the resampler state (clears buffers, restarts if needed).
    /// </summary>
    public void Reset()
    {
        lock (_outputBuffer)
        {
            _outputBuffer.SetLength(0);
            _outputBuffer.Position = 0;
        }
        
        // Clear output queue
        while (_outputQueue.TryDequeue(out _)) { }
    }
    
    /// <summary>
    /// Stop the FFmpeg process.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _isRunning = false;
            _cts?.Cancel();
            
            try
            {
                _stdin?.Close();
            }
            catch { }
            
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(1000);
                }
            }
            catch { }
            
            _process?.Dispose();
            _process = null;
            _stdin = null;
            _stdout = null;
            
            _readerThread?.Join(500);
            _readerThread = null;
            
            _cts?.Dispose();
            _cts = null;
            
            OnDebugLog?.Invoke("[FFmpegResampler] Stopped");
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Stop();
        _outputBuffer.Dispose();
        
        GC.SuppressFinalize(this);
    }
}
