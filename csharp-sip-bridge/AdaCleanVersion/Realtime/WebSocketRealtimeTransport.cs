using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Raw ClientWebSocket implementation of <see cref="IRealtimeTransport"/>.
/// Owns the connection lifecycle, send serialization, and receive loop.
/// </summary>
public sealed class WebSocketRealtimeTransport : IRealtimeTransport
{
    private const int ReceiveBufferSize = 16384;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Func<string, Task>? OnMessage;
    public event Action<string>? OnDisconnected;

    public async Task ConnectAsync(string url, Dictionary<string, string> headers, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _ws = new ClientWebSocket();
        foreach (var (key, value) in headers)
            _ws.Options.SetRequestHeader(key, value);

        await _ws.ConnectAsync(new Uri(url), _cts.Token);

        // Start receive loop
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    public async Task SendAsync(object payload, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        var token = _cts?.Token ?? ct;
        await _sendLock.WaitAsync(token);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        var msgBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnDisconnected?.Invoke("Server closed connection");
                    break;
                }

                msgBuffer.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(
                    msgBuffer.GetBuffer(), 0, (int)msgBuffer.Length);
                msgBuffer.SetLength(0);

                if (OnMessage != null)
                {
                    try { await OnMessage.Invoke(json); }
                    catch { /* handler errors must not kill receive loop */ }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnDisconnected?.Invoke(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "disposed",
                    CancellationToken.None);
            }
            catch { }
        }

        _ws?.Dispose();
        _cts?.Dispose();
        _sendLock.Dispose();
    }
}
