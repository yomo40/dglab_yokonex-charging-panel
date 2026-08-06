using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ChargingPanel.DeviceIntegration.Tests.Networking;

/// <summary>
/// 进程内假 API Bridge 服务器：按官方 yokonex-API-bridge 文档实现
/// HTTP（health/status/login/send-command/reinit）与 WS（ping/getStatus/
/// login/sendCommand/reinit + connected 推送）。用于真实 HTTP/WS 集成测试。
/// </summary>
internal sealed class FakeApiBridgeServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public FakeApiBridgeServer()
    {
        _listener = new HttpListener();
        var port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Uri = new Uri($"http://127.0.0.1:{port}");
        WebSocketUri = new Uri($"ws://127.0.0.1:{port}/");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public Uri Uri { get; }

    public Uri WebSocketUri { get; }

    public string Uid { get; } = "game_5";

    public string UserId { get; } = "5";

    public string AppId { get; } = "1400000000";

    /// <summary>置为 true 时 WS 握手后立即推送 connected 帧。</summary>
    public bool PushConnectedOnConnect { get; set; } = true;

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(context, cancellationToken));
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

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.IsWebSocketRequest)
            {
                await HandleWebSocketAsync(context, cancellationToken);
                return;
            }

            await HandleHttpAsync(context);
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // 忽略。
            }
        }
    }

    private async Task HandleHttpAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
        var json = new Dictionary<string, string>
        {
            ["uid"] = UserId,
            ["token"] = body
        };

        switch (path)
        {
            case "/health":
                WriteJson(context, new { status = "ok", imReady = true, uid = Uid, userId = UserId });
                break;
            case "/api/status":
                WriteJson(context, new
                {
                    isReady = true,
                    config = new { uid = Uid, userId = UserId, appId = AppId, hasToken = true, hasSign = true }
                });
                break;
            case "/api/login":
                WriteJson(context, new
                {
                    success = true,
                    message = "IM 登录成功",
                    data = new { uid = Uid, userId = UserId, appId = AppId }
                });
                break;
            case "/api/send-command":
                WriteJson(context, new
                {
                    success = true,
                    message = "指令发送成功",
                    data = new { message = new { ID = "msg_1", type = "TIMTextElem" } }
                });
                break;
            case "/api/reinit":
                WriteJson(context, new { success = true, message = "IM 重新初始化成功" });
                break;
            default:
                context.Response.StatusCode = 404;
                context.Response.Close();
                break;
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var webSocketContext = await context.AcceptWebSocketAsync(null);
        var socket = webSocketContext.WebSocket;
        if (PushConnectedOnConnect)
        {
            await SendJsonAsync(socket, new
            {
                type = "connected",
                message = "WebSocket 连接成功",
                data = new { isReady = true, uid = Uid, userId = UserId }
            }, cancellationToken);
        }

        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var doc = JsonDocument.Parse(text);
            var type = doc.RootElement.GetProperty("type").GetString();
            switch (type)
            {
                case "ping":
                    await SendJsonAsync(socket, new { type = "pong", timestamp = 1234567890 }, cancellationToken);
                    break;
                case "getStatus":
                    await SendJsonAsync(socket, new
                    {
                        type = "status",
                        data = new { isReady = true, config = new { uid = Uid, userId = UserId, appId = AppId, hasToken = true, hasSign = true } }
                    }, cancellationToken);
                    break;
                case "login":
                    await SendJsonAsync(socket, new
                    {
                        type = "status",
                        data = new { isReady = true, config = new { uid = Uid, userId = UserId, appId = AppId, hasToken = true, hasSign = true } }
                    }, cancellationToken);
                    break;
                case "sendCommand":
                    await SendJsonAsync(socket, new
                    {
                        type = "commandResult",
                        success = true,
                        data = new { success = true, message = "指令发送成功" }
                    }, cancellationToken);
                    break;
                case "reinit":
                    await SendJsonAsync(socket, new { type = "reinitResult", success = true, message = "IM 重新初始化成功" }, cancellationToken);
                    break;
            }
        }

        socket.Dispose();
    }

    private static async Task SendJsonAsync(WebSocket socket, object value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static void WriteJson(HttpListenerContext context, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
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
