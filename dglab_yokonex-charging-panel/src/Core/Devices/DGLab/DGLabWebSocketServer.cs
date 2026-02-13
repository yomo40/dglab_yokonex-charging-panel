using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ChargingPanel.Core.Devices.DGLab;

/// <summary>
/// 郊狼内置 WebSocket 服务器
/// 作为官方服务器 (wss://ws.dungeon-lab.cn) 的备选方案
/// 实现郊狼 WebSocket 协议，支持局域网设备连接
/// </summary>
public class DGLabWebSocketServer : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<DGLabWebSocketServer>();
    private const int MaxMessageLength = 1950;
    
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    
    private readonly ConcurrentDictionary<string, WebSocketClient> _clients = new();
    private readonly ConcurrentDictionary<string, string> _clientBindings = new(); // clientId -> targetId
    
    /// <summary>服务器端口</summary>
    public int Port { get; private set; }
    
    /// <summary>是否正在运行</summary>
    public bool IsRunning { get; private set; }
    
    /// <summary>服务器 URL（绑定地址视图）</summary>
    public string? LocalUrl => IsRunning ? $"ws://0.0.0.0:{Port}" : null;
    
    /// <summary>客户端连接事件</summary>
    public event EventHandler<string>? ClientConnected;
    
    /// <summary>客户端断开事件</summary>
    public event EventHandler<string>? ClientDisconnected;
    
    /// <summary>收到消息事件</summary>
    public event EventHandler<(string clientId, DGLabMessage message)>? MessageReceived;
    
    /// <summary>
    /// 启动服务器
    /// </summary>
    /// <param name="port">监听端口，默认 3000</param>
    public async Task StartAsync(int port = 3000)
    {
        if (IsRunning)
        {
            Logger.Warning("WebSocket 服务器已在运行");
            return;
        }

        if (IsPortOccupied(port))
        {
            throw new InvalidOperationException($"端口 {port} 已被占用，请更换端口后重试。");
        }
        
        Port = port;
        _cts = new CancellationTokenSource();
        
        try
        {
            _httpListener = CreateListener(port);
            try
            {
                _httpListener.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                throw new InvalidOperationException(
                    $"端口 {port} 的局域网监听启动失败（权限不足）。请以管理员运行，或执行: netsh http add urlacl url=http://+:{port}/ user=Everyone",
                    ex);
            }
            
            IsRunning = true;
            var lanUrls = GetLanWebSocketUrls(port);
            var lanText = lanUrls.Count > 0 ? string.Join(", ", lanUrls) : "未检测到可用局域网 IPv4 地址";
            Logger.Information("郊狼 WebSocket 服务器已启动: ws://0.0.0.0:{Port}; 局域网地址: {LanUrls}", port, lanText);
            
            _acceptTask = AcceptClientsAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "启动 WebSocket 服务器失败");
            throw;
        }
    }

    private static HttpListener CreateListener(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        return listener;
    }

    private static bool IsPortOccupied(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static IReadOnlyList<string> GetLanWebSocketUrls(int port)
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                .Select(ip => $"ws://{ip}:{port}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
    
    /// <summary>
    /// 停止服务器
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        
        _cts?.Cancel();
        
        // 关闭所有客户端连接
        foreach (var client in _clients.Values)
        {
            try
            {
                await client.CloseAsync();
            }
            catch { }
        }
        _clients.Clear();
        _clientBindings.Clear();
        
        _httpListener?.Stop();
        _httpListener?.Close();
        _httpListener = null;
        
        IsRunning = false;
        Logger.Information("郊狼 WebSocket 服务器已停止");
    }
    
    /// <summary>
    /// 向指定客户端发送消息
    /// </summary>
    public async Task SendToClientAsync(string clientId, DGLabMessage message)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            await client.SendAsync(message);
        }
    }
    
    /// <summary>
    /// 广播消息给所有客户端
    /// </summary>
    public async Task BroadcastAsync(DGLabMessage message)
    {
        var tasks = _clients.Values.Select(c => c.SendAsync(message));
        await Task.WhenAll(tasks);
    }
    
    /// <summary>
    /// 获取所有已连接的客户端 ID
    /// </summary>
    public IEnumerable<string> GetConnectedClients() => _clients.Keys;
    
    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _httpListener?.IsListening == true)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                
                if (context.Request.IsWebSocketRequest)
                {
                    _ = HandleWebSocketAsync(context, ct);
                }
                else
                {
                    // 返回简单的状态页面
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    var lanList = GetLanWebSocketUrls(Port);
                    var lanHtml = lanList.Count > 0
                        ? string.Join("<br/>", lanList.Select(WebUtility.HtmlEncode))
                        : "未检测到可用局域网 IPv4 地址";
                    var html = $@"<!DOCTYPE html>
<html>
<head><title>郊狼 WebSocket 服务器</title></head>
<body>
<h1>郊狼 WebSocket 服务器</h1>
<p>状态: 运行中</p>
<p>端口: {Port}</p>
<p>已连接客户端: {_clients.Count}</p>
<p>WebSocket URL: ws://0.0.0.0:{Port}</p>
<p>局域网可用地址:<br/>{lanHtml}</p>
</body>
</html>";
                    var buffer = Encoding.UTF8.GetBytes(html);
                    await context.Response.OutputStream.WriteAsync(buffer, ct);
                    context.Response.Close();
                }
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "接受客户端连接时出错");
            }
        }
    }
    
    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken ct)
    {
        WebSocket? ws = null;
        string? clientId = null;
        
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            ws = wsContext.WebSocket;
            
            // 生成客户端 ID
            // 使用 32 位 UUID，和官方协议推荐长度保持一致。
            clientId = Guid.NewGuid().ToString("N").ToUpperInvariant();
            
            var client = new WebSocketClient(clientId, ws);
            _clients[clientId] = client;
            
            Logger.Information("客户端已连接: {ClientId}", clientId);
            ClientConnected?.Invoke(this, clientId);
            
            // 发送绑定消息
            await client.SendAsync(new DGLabMessage
            {
                Type = "bind",
                ClientId = clientId,
                TargetId = "",
                Message = "targetId"
            });
            
            // 接收消息循环
            await ReceiveLoopAsync(client, ct);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            Logger.Debug("客户端 {ClientId} 连接关闭", clientId);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "处理客户端 {ClientId} 时出错", clientId);
        }
        finally
        {
            if (clientId != null)
            {
                _clients.TryRemove(clientId, out _);
                _clientBindings.TryRemove(clientId, out _);
                ClientDisconnected?.Invoke(this, clientId);
                Logger.Information("客户端已断开: {ClientId}", clientId);
            }
            
            ws?.Dispose();
        }
    }
    
    private async Task ReceiveLoopAsync(WebSocketClient client, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var messageBuilder = new StringBuilder();
        
        while (client.WebSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await client.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
            
            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            
            if (result.EndOfMessage)
            {
                var messageText = messageBuilder.ToString();
                messageBuilder.Clear();
                
                await HandleMessageAsync(client, messageText);
            }
        }
    }
    
    private async Task HandleMessageAsync(WebSocketClient client, string messageText)
    {
        try
        {
            if (messageText.Length > MaxMessageLength)
            {
                await client.SendAsync(new DGLabMessage
                {
                    Type = "error",
                    ClientId = client.Id,
                    TargetId = "",
                    Message = "405"
                });
                Logger.Warning("消息长度超限，拒绝处理: client={ClientId}, length={Length}", client.Id, messageText.Length);
                return;
            }

            Logger.Debug("收到消息 from {ClientId}: {Message}", client.Id, messageText[..Math.Min(200, messageText.Length)]);
            
            var message = JsonSerializer.Deserialize<DGLabMessage>(messageText);
            if (message == null) return;
            
            MessageReceived?.Invoke(this, (client.Id, message));
            
            switch (message.Type)
            {
                case "bind":
                    await HandleBindAsync(client, message);
                    break;
                    
                case "msg":
                    await HandleMsgAsync(client, message);
                    break;
                    
                case "heartbeat":
                    // 心跳，直接回复
                    // 官方协议: 心跳响应 message 为 "DGLAB"
                    await client.SendAsync(new DGLabMessage
                    {
                        Type = "heartbeat",
                        ClientId = client.Id,
                        TargetId = message.TargetId ?? "",
                        Message = "DGLAB"
                    });
                    break;
                    
                case "break":
                    // 断开绑定
                    _clientBindings.TryRemove(client.Id, out _);
                    if (!string.IsNullOrEmpty(message.TargetId) && _clients.TryGetValue(message.TargetId, out var target))
                    {
                        await target.SendAsync(new DGLabMessage
                        {
                            Type = "break",
                            ClientId = client.Id,
                            TargetId = message.TargetId,
                            Message = "1"
                        });
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "处理消息失败: {Message}", messageText[..Math.Min(100, messageText.Length)]);
        }
    }
    
    private async Task HandleBindAsync(WebSocketClient client, DGLabMessage message)
    {
        var targetId = message.TargetId;
        
        if (string.IsNullOrEmpty(targetId))
        {
            // 请求绑定，返回 clientId
            await client.SendAsync(new DGLabMessage
            {
                Type = "bind",
                ClientId = client.Id,
                TargetId = "",
                Message = "targetId"
            });
            return;
        }
        
        // 官方协议: APP 发送 message = "DGLAB" 表示请求绑定
        if (message.Message != "DGLAB")
        {
            Logger.Warning("绑定消息格式错误: message={Message}, 期望 DGLAB", message.Message);
        }
        
        // 尝试绑定到目标
        if (_clients.TryGetValue(targetId, out var targetClient))
        {
            // 检查目标是否已被绑定
            if (_clientBindings.ContainsKey(targetId))
            {
                // 错误码 400: 此 id 已被其他客户端绑定关系
                await client.SendAsync(new DGLabMessage
                {
                    Type = "bind",
                    ClientId = client.Id,
                    TargetId = targetId,
                    Message = "400"
                });
                Logger.Warning("绑定失败: 目标 {TargetId} 已被绑定", targetId);
                return;
            }
            
            // 建立双向绑定
            _clientBindings[client.Id] = targetId;
            _clientBindings[targetId] = client.Id;
            
            // 通知双方绑定成功 (200)
            await client.SendAsync(new DGLabMessage
            {
                Type = "bind",
                ClientId = client.Id,
                TargetId = targetId,
                Message = "200"
            });
            
            await targetClient.SendAsync(new DGLabMessage
            {
                Type = "bind",
                ClientId = targetId,
                TargetId = client.Id,
                Message = "200"
            });
            
            Logger.Information("客户端绑定成功: {Client1} <-> {Client2}", client.Id, targetId);
        }
        else
        {
            // 错误码 401: 要绑定的目标客户端不存在
            await client.SendAsync(new DGLabMessage
            {
                Type = "bind",
                ClientId = client.Id,
                TargetId = targetId,
                Message = "401"
            });
            Logger.Warning("绑定失败: 目标 {TargetId} 不存在", targetId);
        }
    }
    
    private async Task HandleMsgAsync(WebSocketClient client, DGLabMessage message)
    {
        if ((message.Message?.Length ?? 0) > MaxMessageLength)
        {
            await client.SendAsync(new DGLabMessage
            {
                Type = "error",
                ClientId = client.Id,
                TargetId = message.TargetId ?? "",
                Message = "405"
            });
            return;
        }

        var targetId = message.TargetId;
        
        if (string.IsNullOrEmpty(targetId))
        {
            // 使用绑定的目标
            if (!_clientBindings.TryGetValue(client.Id, out targetId))
            {
                Logger.Warning("客户端 {ClientId} 未绑定目标", client.Id);
                // 错误码 402: 收信方和寄信方不是绑定关系
                await client.SendAsync(new DGLabMessage
                {
                    Type = "error",
                    ClientId = client.Id,
                    TargetId = "",
                    Message = "402"
                });
                return;
            }
        }
        
        // 验证绑定关系
        if (!_clientBindings.TryGetValue(client.Id, out var boundTarget) || boundTarget != targetId)
        {
            Logger.Warning("客户端 {ClientId} 与目标 {TargetId} 不是绑定关系", client.Id, targetId);
            await client.SendAsync(new DGLabMessage
            {
                Type = "error",
                ClientId = client.Id,
                TargetId = targetId ?? "",
                Message = "402"
            });
            return;
        }
        
        if (_clients.TryGetValue(targetId, out var targetClient))
        {
            // 转发消息
            await targetClient.SendAsync(new DGLabMessage
            {
                Type = "msg",
                ClientId = client.Id,
                TargetId = targetId,
                Message = message.Message ?? string.Empty
            });
        }
        else
        {
            // 错误码 404: 未找到收信人（离线）
            await client.SendAsync(new DGLabMessage
            {
                Type = "error",
                ClientId = client.Id,
                TargetId = targetId,
                Message = "404"
            });
        }
    }
    
    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
    
    private class WebSocketClient
    {
        public string Id { get; }
        public WebSocket WebSocket { get; }
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        
        public WebSocketClient(string id, WebSocket ws)
        {
            Id = id;
            WebSocket = ws;
        }
        
        public async Task SendAsync(DGLabMessage message)
        {
            if (WebSocket.State != WebSocketState.Open) return;
            
            await _sendLock.WaitAsync();
            try
            {
                var json = JsonSerializer.Serialize(message);
                if (json.Length > MaxMessageLength)
                {
                    Logger.Warning("拒绝发送超长消息: client={ClientId}, length={Length}", Id, json.Length);
                    return;
                }
                var buffer = Encoding.UTF8.GetBytes(json);
                await WebSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        
        public async Task CloseAsync()
        {
            if (WebSocket.State == WebSocketState.Open)
            {
                await WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutdown", CancellationToken.None);
            }
        }
    }
}

// DGLabMessage 类定义在 DGLabWebSocketAdapter.cs 中
