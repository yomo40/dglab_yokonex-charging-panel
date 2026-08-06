using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ChargingPanel.Infrastructure.Windows.WebSockets;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Networking;

/// <summary>
/// WebSocket 连接池测试。使用进程内真实 HttpListener WebSocket 服务器，
/// 验证按 URI+认证范围复用、回显收发与最后租约释放后关闭物理连接。
/// </summary>
public sealed class WebSocketConnectionPoolTests : IDisposable
{
    private readonly InProcessWebSocketServer _server;

    public WebSocketConnectionPoolTests()
    {
        _server = new InProcessWebSocketServer();
    }

    [Fact]
    public async Task SameKey_AcquireTwice_ReusesOnePhysicalConnection()
    {
        await using var pool = new WebSocketConnectionPool(TimeProvider.System);
        var key = new WebSocketConnectionKey(_server.Uri, "scope-a");

        await using var first = await pool.AcquireAsync(key, CancellationToken.None);
        await using var second = await pool.AcquireAsync(key, CancellationToken.None);

        Assert.Equal(1, _server.AcceptedConnectionCount);
        Assert.False(first.IsClosed);
        Assert.False(second.IsClosed);
    }

    [Fact]
    public async Task DifferentAuthScope_CreatesSeparateConnection()
    {
        await using var pool = new WebSocketConnectionPool(TimeProvider.System);
        var firstKey = new WebSocketConnectionKey(_server.Uri, "scope-a");
        var secondKey = new WebSocketConnectionKey(_server.Uri, "scope-b");

        await using var first = await pool.AcquireAsync(firstKey, CancellationToken.None);
        await using var second = await pool.AcquireAsync(secondKey, CancellationToken.None);

        Assert.Equal(2, _server.AcceptedConnectionCount);
    }

    [Fact]
    public async Task SendAndRead_EchoesFrameBack()
    {
        await using var pool = new WebSocketConnectionPool(TimeProvider.System);
        await using var lease = await pool.AcquireAsync(
            new WebSocketConnectionKey(_server.Uri, null),
            CancellationToken.None);

        var send = await lease.SendAsync(
            WebSocketMessageKind.Text,
            Encoding.UTF8.GetBytes("hello"),
            CancellationToken.None);
        Assert.Equal(WebSocketSendStatus.Success, send.Status);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var frame in lease.ReadMessagesAsync(cts.Token))
        {
            Assert.Equal(WebSocketMessageKind.Text, frame.Kind);
            Assert.Equal("hello", Encoding.UTF8.GetString(frame.Payload.ToArray()));
            return;
        }

        Assert.Fail("未收到回显帧。");
    }

    [Fact]
    public async Task ReleaseLastLease_ClosesPhysicalConnection()
    {
        await using var pool = new WebSocketConnectionPool(TimeProvider.System);
        var key = new WebSocketConnectionKey(_server.Uri, "scope-a");

        var first = await pool.AcquireAsync(key, CancellationToken.None);
        var second = await pool.AcquireAsync(key, CancellationToken.None);

        await first.DisposeAsync();
        Assert.Equal(1, _server.AcceptedConnectionCount);
        Assert.Equal(0, _server.ClosedConnectionCount);

        await second.DisposeAsync();
        await _server.WaitForAllConnectionsClosedAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, _server.ClosedConnectionCount);
    }

    [Fact]
    public async Task SendAfterConnectionLost_ReturnsFailedNotFakeSuccess()
    {
        // 服务器主动关闭后，发送必须如实失败，绝不返回"看起来成功"。
        await using var pool = new WebSocketConnectionPool(TimeProvider.System);
        var key = new WebSocketConnectionKey(_server.Uri, "scope-close");
        await using var lease = await pool.AcquireAsync(key, CancellationToken.None);

        await _server.CloseAllAsync();

        var result = await lease.SendAsync(
            WebSocketMessageKind.Text,
            Encoding.UTF8.GetBytes("x"),
            CancellationToken.None);
        Assert.Equal(WebSocketSendStatus.Failed, result.Status);
    }

    public void Dispose() => _server.Dispose();
}

/// <summary>进程内 WebSocket 回显服务器。</summary>
internal sealed class InProcessWebSocketServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private int _acceptedCount;
    private int _closedCount;
    private readonly List<(WebSocket Socket, CancellationTokenSource Cts)> _sockets = [];
    private readonly Task _acceptLoop;

    public InProcessWebSocketServer()
    {
        _listener = new HttpListener();
        var port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Uri = new Uri($"ws://127.0.0.1:{port}/");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public Uri Uri { get; }

    public int AcceptedConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _acceptedCount;
            }
        }
    }

    public int ClosedConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _closedCount;
            }
        }
    }

    public async Task WaitForAllConnectionsClosedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ClosedConnectionCount == AcceptedConnectionCount)
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    public async Task CloseAllAsync()
    {
        (WebSocket Socket, CancellationTokenSource Cts)[] sockets;
        lock (_gate)
        {
            sockets = [.. _sockets];
        }

        // 必须先取消每连接 CTS，让在途 ReceiveAsync 先退出，
        // 否则 CloseAsync 会等待在途操作完成而永久挂起。
        foreach (var (_, cts) in sockets)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // 忽略。
            }
        }

        foreach (var (socket, _) in sockets)
        {
            try
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "测试关闭",
                    CancellationToken.None);
            }
            catch
            {
                // 关闭失败不影响测试结果判定。
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleConnectionAsync(context, cancellationToken));
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
        }
    }

    private async Task HandleConnectionAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            // 收到一次 WebSocket 升级请求即视为一条新的物理连接，
            // 必须在 AcceptWebSocketAsync 之前计数：否则客户端 ConnectAsync
            // 返回时服务器端可能尚未完成计数，造成断言竞争。
            lock (_gate)
            {
                _acceptedCount++;
            }

            var webSocketContext = await context.AcceptWebSocketAsync(null);
            var socket = webSocketContext.WebSocket;
            lock (_gate)
            {
                _sockets.Add((socket, connectionCts));
            }

            var buffer = new byte[4096];
            while (!connectionCts.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, connectionCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                // 回显：同一帧原样返回。
                await socket.SendAsync(
                    buffer.AsMemory(0, result.Count),
                    result.MessageType,
                    endOfMessage: true,
                    connectionCts.Token);
            }

            lock (_gate)
            {
                _sockets.RemoveAll(entry => entry.Socket == socket);
                _closedCount++;
            }

            socket.Dispose();
        }
        catch (Exception)
        {
            lock (_gate)
            {
                _closedCount++;
            }
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // 忽略。
        }

        _listener.Close();
    }
}
