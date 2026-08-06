using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ChargingPanel.Infrastructure.Windows.ApiBridge;

/// <summary>
/// 官方 YOKONEX API Bridge 的 WebSocket 面。
/// 覆盖 ping/getStatus/login/sendCommand/reinit 请求与
/// connected/status/network/message/heartbeat/error 推送。
/// </summary>
public sealed class ApiBridgeWebSocketClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] ResponseTypes =
        ["pong", "status", "commandResult", "reinitResult"];

    private readonly Uri _uri;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly System.Threading.Channels.Channel<JsonElement> _pushes;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private bool _disposed;

    public ApiBridgeWebSocketClient(Uri uri)
    {
        _uri = uri ?? throw new ArgumentNullException(nameof(uri));
        _pushes = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_uri, cancellationToken);
        _socket = socket;
        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReadLoopAsync(socket, _readerCts.Token));
    }

    public async Task<JsonElement> GetStatusAsync(CancellationToken cancellationToken) =>
        await RequestAsync("getStatus", null, "status", cancellationToken);

    public async Task<JsonElement> PingAsync(CancellationToken cancellationToken) =>
        await RequestAsync("ping", null, "pong", cancellationToken);

    public async Task<JsonElement> LoginAsync(string uid, string token, CancellationToken cancellationToken) =>
        await RequestAsync("login", new { uid, token }, "status", cancellationToken);

    public async Task<JsonElement> SendCommandAsync(string commandId, CancellationToken cancellationToken) =>
        await RequestAsync("sendCommand", new { commandId }, "commandResult", cancellationToken);

    public async Task<JsonElement> ReinitAsync(CancellationToken cancellationToken) =>
        await RequestAsync("reinit", null, "reinitResult", cancellationToken);

    /// <summary>服务器主动推送的帧流（connected/status/network/message/heartbeat/error）。</summary>
    public IAsyncEnumerable<JsonElement> ReadPushesAsync(CancellationToken cancellationToken) =>
        _pushes.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// 发送请求帧并等待匹配的响应帧。status 类帧在无待处理请求时视为推送。
    /// 超时抛 TimeoutException；是否重发由上层依据交付语义决定。
    /// </summary>
    private async Task<JsonElement> RequestAsync(
        string type,
        object? payload,
        string responseType,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            _pending[responseType] = completion;
        }

        try
        {
            var final = BuildRequestFrame(type, payload);
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                var socket = _socket
                    ?? throw new InvalidOperationException("API Bridge WebSocket 未连接。");
                if (socket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("API Bridge WebSocket 已关闭。");
                }

                var bytes = Encoding.UTF8.GetBytes(final);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);
            return await completion.Task.WaitAsync(timeoutCts.Token);
        }
        finally
        {
            lock (_pendingGate)
            {
                _pending.Remove(responseType);
            }
        }
    }

    private static string BuildRequestFrame(string type, object? payload)
    {
        if (payload is null)
        {
            return JsonSerializer.Serialize(new { type });
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, payload);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                JsonElement frame;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    frame = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue; // 非法帧忽略，不中断读者。
                }

                var type = GetType(frame);
                TaskCompletionSource<JsonElement>? pending = null;
                if (type is not null && ResponseTypes.Contains(type))
                {
                    lock (_pendingGate)
                    {
                        if (_pending.TryGetValue(type, out pending))
                        {
                            _pending.Remove(type);
                        }
                    }
                }

                if (pending is not null)
                {
                    pending.TrySetResult(frame);
                }
                else
                {
                    // 无待处理请求的帧视为服务器主动推送。
                    _pushes.Writer.TryWrite(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭。
        }
        catch
        {
            _pushes.Writer.TryComplete();
        }
    }

    private static string? GetType(JsonElement frame) =>
        frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("type", out var type)
            ? type.GetString()
            : null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var readerCts = _readerCts;
        _readerCts = null;
        readerCts?.Cancel();

        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "宿主关闭",
                        CancellationToken.None);
                }
            }
            catch
            {
                // 关闭失败不阻止释放。
            }

            socket.Dispose();
        }

        if (readerCts is not null)
        {
            try
            {
                await (_readerTask ?? Task.CompletedTask);
            }
            catch
            {
                // 读者退出方式不影响释放。
            }

            readerCts.Dispose();
        }

        _pushes.Writer.TryComplete();
        _sendGate.Dispose();
    }
}
