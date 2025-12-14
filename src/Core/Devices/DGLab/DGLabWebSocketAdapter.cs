using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ChargingPanel.Core.Devices.DGLab;

/// <summary>
/// DG-LAB WebSocket 适配器
/// 通过官方 WebSocket 中转服务连接 DG-LAB APP
/// </summary>
public class DGLabWebSocketAdapter : IDevice, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<DGLabWebSocketAdapter>();

    // 官方 WebSocket 服务器
    public const string OfficialWebSocketUrl = "wss://ws.dungeon-lab.cn";

    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type => DeviceType.DGLab;
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public DeviceState State => GetState();
    public ConnectionConfig? Config { get; private set; }

    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>QR 码内容变化事件</summary>
    public event EventHandler<string>? QRCodeChanged;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Timer? _heartbeatTimer;
    private Timer? _reconnectTimer;

    private string? _clientId;
    private string? _targetId;
    private bool _isBound;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 10;

    private int _strengthA;
    private int _strengthB;
    private int _limitA = 200;
    private int _limitB = 200;

    public DGLabWebSocketAdapter(string? id = null, string? name = null)
    {
        Id = id ?? $"dglab_ws_{Guid.NewGuid():N}"[..20];
        Name = name ?? "郊狼设备";
    }

    /// <summary>客户端 ID（用于生成二维码）</summary>
    public string? ClientId => _clientId;

    /// <summary>目标 ID（APP 端）</summary>
    public string? TargetId => _targetId;

    /// <summary>是否已绑定到 APP</summary>
    public bool IsBound => _isBound;

    /// <summary>
    /// 获取二维码内容
    /// </summary>
    public string GetQRCodeContent()
    {
        if (string.IsNullOrEmpty(_clientId))
            throw new InvalidOperationException("尚未获取到 ClientId，请先连接 WebSocket");

        var wsUrl = Config?.WebSocketUrl ?? OfficialWebSocketUrl;
        // 官方格式: https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#wss://ws.dungeon-lab.cn/{ClientId}
        return $"https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#{wsUrl}/{_clientId}";
    }

    public async Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        Config = config;
        var wsUrl = config.WebSocketUrl ?? OfficialWebSocketUrl;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UpdateStatus(DeviceStatus.Connecting);
        Logger.Information("正在连接 DG-LAB WebSocket: {Url}", wsUrl);

        try
        {
            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
            Logger.Information("WebSocket 连接已建立");

            _reconnectAttempts = 0;

            // 启动消息接收
            _receiveTask = ReceiveLoopAsync(_cts.Token);

            // 等待获取 clientId (最多 10 秒)
            var timeout = DateTime.UtcNow.AddSeconds(10);
            while (string.IsNullOrEmpty(_clientId) && DateTime.UtcNow < timeout && !_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, _cts.Token);
            }

            if (string.IsNullOrEmpty(_clientId))
            {
                throw new TimeoutException("未能从服务器获取 ClientId");
            }

            // 启动心跳
            StartHeartbeat();

            UpdateStatus(DeviceStatus.WaitingForBind);
            Logger.Information("已获取 ClientId: {ClientId}, 等待 APP 扫码绑定", _clientId);

            // 触发 QR 码变化事件
            QRCodeChanged?.Invoke(this, GetQRCodeContent());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "WebSocket 连接失败");
            UpdateStatus(DeviceStatus.Error);
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        StopHeartbeat();
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _cts?.Cancel();

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);
            }
            catch { }
        }

        _ws?.Dispose();
        _ws = null;
        _isBound = false;
        _clientId = null;
        _targetId = null;

        UpdateStatus(DeviceStatus.Disconnected);
        Logger.Information("WebSocket 已断开");
    }

    public async Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        EnsureConnected();

        var safeValue = Math.Clamp(value, 0, 200);
        var message = BuildStrengthMessage(channel, safeValue, mode);
        await SendAsync(message);

        Logger.Debug("设置强度: channel={Channel}, value={Value}, mode={Mode}", channel, safeValue, mode);
    }

    public async Task SendWaveformAsync(Channel channel, WaveformData data)
    {
        EnsureConnected();

        var hexArray = WaveformGenerator.GenerateHexArray(data);

        // 分批发送，每批最多 100 条
        const int batchSize = 100;
        for (int i = 0; i < hexArray.Count; i += batchSize)
        {
            var batch = hexArray.GetRange(i, Math.Min(batchSize, hexArray.Count - i));
            var message = BuildWaveformMessage(channel, batch);
            await SendAsync(message);

            if (i + batchSize < hexArray.Count)
            {
                await Task.Delay(50);
            }
        }

        Logger.Debug("发送波形: channel={Channel}, length={Length}", channel, hexArray.Count);
    }

    public async Task ClearWaveformQueueAsync(Channel channel)
    {
        EnsureConnected();

        var message = BuildClearQueueMessage(channel);
        await SendAsync(message);
        Logger.Debug("清空波形队列: channel={Channel}", channel);
    }

    public Task SetLimitsAsync(int limitA, int limitB)
    {
        _limitA = Math.Clamp(limitA, 0, 200);
        _limitB = Math.Clamp(limitB, 0, 200);
        // WebSocket 模式下，限制通过 APP 设置
        return Task.CompletedTask;
    }

    #region Message Handling

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var messageBuilder = new StringBuilder();

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Warning("收到 WebSocket 关闭消息");
                    break;
                }

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var message = messageBuilder.ToString();
                    messageBuilder.Clear();
                    HandleMessage(message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            Logger.Warning("WebSocket 连接意外关闭");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "接收消息时出错");
            ErrorOccurred?.Invoke(this, ex);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                UpdateStatus(DeviceStatus.Disconnected);
                if (Config?.AutoReconnect == true && _reconnectAttempts < MaxReconnectAttempts)
                {
                    ScheduleReconnect();
                }
            }
        }
    }

    private void HandleMessage(string data)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<DGLabMessage>(data);
            if (msg == null)
            {
                Logger.Warning("无法解析消息: {Data}", data);
                return;
            }

            Logger.Debug("收到消息: type={Type}, message={Message}", msg.Type, msg.Message);

            switch (msg.Type)
            {
                case "bind":
                    HandleBindMessage(msg);
                    break;
                case "msg":
                    HandleDataMessage(msg);
                    break;
                case "heartbeat":
                    // 心跳响应
                    break;
                case "break":
                    Logger.Warning("连接断开: {Message}", msg.Message);
                    _isBound = false;
                    _targetId = null;
                    UpdateStatus(DeviceStatus.WaitingForBind);
                    break;
                case "error":
                    Logger.Error("服务器错误: {Message}", msg.Message);
                    ErrorOccurred?.Invoke(this, new Exception(msg.Message));
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "处理消息失败: {Data}", data);
        }
    }

    private void HandleBindMessage(DGLabMessage msg)
    {
        if (msg.Message == "targetId" && !string.IsNullOrEmpty(msg.ClientId))
        {
            _clientId = msg.ClientId;
            Logger.Information("收到 ClientId: {ClientId}", _clientId);
            QRCodeChanged?.Invoke(this, GetQRCodeContent());
        }
        else if (msg.Message == "200")
        {
            _isBound = true;
            _targetId = msg.TargetId;
            Logger.Information("已绑定到 APP: TargetId={TargetId}", _targetId);
            UpdateStatus(DeviceStatus.Connected);
        }
    }

    private void HandleDataMessage(DGLabMessage msg)
    {
        if (string.IsNullOrEmpty(msg.Message)) return;

        // 解析强度数据: strength-A通道强度+B通道强度+A强度上限+B强度上限
        if (msg.Message.StartsWith("strength-"))
        {
            var parts = msg.Message[9..].Split('+');
            if (parts.Length >= 4 &&
                int.TryParse(parts[0], out int strengthA) &&
                int.TryParse(parts[1], out int strengthB) &&
                int.TryParse(parts[2], out int limitA) &&
                int.TryParse(parts[3], out int limitB))
            {
                _strengthA = strengthA;
                _strengthB = strengthB;
                _limitA = limitA;
                _limitB = limitB;

                Logger.Debug("强度更新: A={A}, B={B}, LimitA={LA}, LimitB={LB}",
                    _strengthA, _strengthB, _limitA, _limitB);

                StrengthChanged?.Invoke(this, new StrengthInfo
                {
                    ChannelA = _strengthA,
                    ChannelB = _strengthB,
                    LimitA = _limitA,
                    LimitB = _limitB
                });
            }
        }
        // 解析 APP 反馈按钮: feedback-index (0-9)
        else if (msg.Message.StartsWith("feedback-"))
        {
            if (int.TryParse(msg.Message[9..], out int feedbackIndex))
            {
                Logger.Information("收到 APP 反馈: button {Index}", feedbackIndex);
            }
        }
    }

    #endregion

    #region Message Building

    private string BuildStrengthMessage(Channel channel, int value, StrengthMode mode)
    {
        // strength-channel+mode+value
        // channel: 1=A, 2=B
        // mode: 0=decrease, 1=increase, 2=set
        int channelNum = channel == Channel.A ? 1 : 2;
        int modeNum = (int)mode;
        string message = $"strength-{channelNum}+{modeNum}+{value}";

        return JsonSerializer.Serialize(new
        {
            type = "msg",
            clientId = _clientId,
            targetId = _targetId,
            message
        });
    }

    private string BuildWaveformMessage(Channel channel, List<string> hexArray)
    {
        // pulse-channel:[hex1,hex2,...] 
        // 官方协议: 通道使用 A/B 而不是 1/2
        string channelChar = channel == Channel.A ? "A" : (channel == Channel.B ? "B" : "A");
        string arrayStr = "[" + string.Join(",", hexArray.ConvertAll(h => $"\"{h}\"")) + "]";
        string message = $"pulse-{channelChar}:{arrayStr}";

        return JsonSerializer.Serialize(new
        {
            type = "msg",
            clientId = _clientId,
            targetId = _targetId,
            message
        });
    }

    private string BuildClearQueueMessage(Channel channel)
    {
        int channelNum = channel == Channel.A ? 1 : (channel == Channel.B ? 2 : 0);
        return JsonSerializer.Serialize(new
        {
            type = "msg",
            clientId = _clientId,
            targetId = _targetId,
            message = $"clear-{channelNum}"
        });
    }

    private string BuildHeartbeatMessage()
    {
        return JsonSerializer.Serialize(new
        {
            type = "heartbeat",
            clientId = _clientId,
            message = "ping"
        });
    }

    #endregion

    #region Helpers

    private async Task SendAsync(string message)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket 未连接");
        }

        var buffer = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(async _ =>
        {
            try
            {
                if (_ws?.State == WebSocketState.Open)
                {
                    await SendAsync(BuildHeartbeatMessage());
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "心跳发送失败");
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    private void ScheduleReconnect()
    {
        _reconnectAttempts++;
        var delay = Math.Min(5000 * _reconnectAttempts, 60000);
        Logger.Information("计划重连: {Delay}ms 后 (尝试 {Attempt}/{Max})",
            delay, _reconnectAttempts, MaxReconnectAttempts);

        _reconnectTimer?.Dispose();
        _reconnectTimer = new Timer(async _ =>
        {
            try
            {
                if (Config != null)
                {
                    await ConnectAsync(Config);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "重连失败");
            }
        }, null, delay, Timeout.Infinite);
    }

    private void UpdateStatus(DeviceStatus status)
    {
        if (Status != status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    private void EnsureConnected()
    {
        if (_ws?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket 未连接");
        }
        if (!_isBound)
        {
            throw new InvalidOperationException("尚未绑定到 APP，请使用郊狼 APP 扫描二维码");
        }
    }

    private DeviceState GetState()
    {
        return new DeviceState
        {
            Status = Status,
            Strength = new StrengthInfo
            {
                ChannelA = _strengthA,
                ChannelB = _strengthB,
                LimitA = _limitA,
                LimitB = _limitB
            },
            LastUpdate = DateTime.UtcNow
        };
    }

    public void Dispose()
    {
        _heartbeatTimer?.Dispose();
        _reconnectTimer?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _ws?.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}

/// <summary>
/// DG-LAB WebSocket 消息
/// </summary>
internal class DGLabMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
