using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Events;
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

#pragma warning disable CS0067 // BatteryChanged 为接口兼容保留
    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;
#pragma warning restore CS0067

    /// <summary>QR 码内容变化事件</summary>
    public event EventHandler<string>? QRCodeChanged;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Timer? _heartbeatTimer;
    private Timer? _reconnectTimer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private string? _clientId;
    private string? _targetId;
    private bool _isBound;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 10;
    
    // 官方协议心跳间隔为 20 秒
    private const int HeartbeatIntervalSeconds = 20;
    // 绑定超时时间
    private const int BindTimeoutSeconds = 20;

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

            // 等待获取 clientId (最多 BindTimeoutSeconds 秒)
            var timeout = DateTime.UtcNow.AddSeconds(BindTimeoutSeconds);
            while (string.IsNullOrEmpty(_clientId) && DateTime.UtcNow < timeout && !_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, _cts.Token);
            }

            if (string.IsNullOrEmpty(_clientId))
            {
                throw new TimeoutException($"未能从服务器获取 ClientId (超时 {BindTimeoutSeconds} 秒)");
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

        // 官方协议: 数组最大长度为 100，每条波形数据代表 100ms
        // 建议波形数据的发送间隔略微小于波形数据的时间长度
        const int batchSize = 100;
        for (int i = 0; i < hexArray.Count; i += batchSize)
        {
            var batch = hexArray.GetRange(i, Math.Min(batchSize, hexArray.Count - i));
            var message = BuildWaveformMessage(channel, batch);
            await SendAsync(message);

            // 如果还有更多数据，等待一段时间再发送
            // 每批 100 条 = 10 秒数据，发送间隔应小于 10 秒
            if (i + batchSize < hexArray.Count)
            {
                await Task.Delay(100); // 100ms 间隔
            }
        }

        Logger.Debug("发送波形: channel={Channel}, length={Length}", channel, hexArray.Count);
    }

    public async Task ClearWaveformQueueAsync(Channel channel)
    {
        EnsureConnected();

        // 协议只支持单通道清空 (1=A, 2=B)，AB 双通道需要分别清空
        if (channel == Channel.AB)
        {
            await SendAsync(BuildClearQueueMessage(Channel.A));
            await SendAsync(BuildClearQueueMessage(Channel.B));
            Logger.Debug("清空波形队列: channel=AB (both)");
        }
        else
        {
            var message = BuildClearQueueMessage(channel);
            await SendAsync(message);
            Logger.Debug("清空波形队列: channel={Channel}", channel);
        }
    }

    public async Task SetLimitsAsync(int limitA, int limitB)
    {
        _limitA = Math.Clamp(limitA, 0, 200);
        _limitB = Math.Clamp(limitB, 0, 200);

        if (_ws?.State == WebSocketState.Open && _isBound)
        {
            // socket 文档：strength-channel+mode+value，mode=2 表示设定绝对值
            await SendAsync(BuildStrengthMessage(Channel.A, _limitA, StrengthMode.Set));
            await SendAsync(BuildStrengthMessage(Channel.B, _limitB, StrengthMode.Set));
            Logger.Information("已下发 WebSocket 强度上限: A={LimitA}, B={LimitB}", _limitA, _limitB);
        }
        else
        {
            Logger.Debug("WebSocket 未绑定，仅更新本地强度上限缓存: A={LimitA}, B={LimitB}", _limitA, _limitB);
        }
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
                    // 心跳响应，官方协议 message 为 "DGLAB" 表示成功
                    if (msg.Message == "DGLAB")
                    {
                        Logger.Debug("心跳响应正常");
                    }
                    else
                    {
                        Logger.Warning("心跳响应异常: {Message}", msg.Message);
                    }
                    break;
                case "break":
                    // 官方协议: 209 表示对方客户端已断开
                    var breakReason = msg.Message switch
                    {
                        "209" => "对方客户端已断开",
                        "1" => "主动断开",
                        _ => $"断开原因: {msg.Message}"
                    };
                    Logger.Warning("连接断开: {Reason}", breakReason);
                    _isBound = false;
                    _targetId = null;
                    UpdateStatus(DeviceStatus.WaitingForBind);
                    // 触发 QR 码变化事件，让用户重新扫码
                    if (!string.IsNullOrEmpty(_clientId))
                    {
                        QRCodeChanged?.Invoke(this, GetQRCodeContent());
                    }
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
        // 服务器返回 clientId
        if (msg.Message == "targetId" && !string.IsNullOrEmpty(msg.ClientId))
        {
            _clientId = msg.ClientId;
            Logger.Information("收到 ClientId: {ClientId}", _clientId);
            QRCodeChanged?.Invoke(this, GetQRCodeContent());
        }
        // 绑定成功 (200)
        else if (msg.Message == "200")
        {
            _isBound = true;
            _targetId = msg.TargetId;
            Logger.Information("已绑定到 APP: TargetId={TargetId}", _targetId);
            UpdateStatus(DeviceStatus.Connected);
        }
        // 处理绑定错误码
        else if (int.TryParse(msg.Message, out int errorCode))
        {
            var errorMsg = errorCode switch
            {
                209 => "对方客户端已断开",
                210 => "二维码中没有有效的 clientID",
                211 => "服务器未下发 APP ID",
                400 => "此 ID 已被其他客户端绑定",
                401 => "要绑定的目标客户端不存在",
                402 => "收信方和寄信方不是绑定关系",
                403 => "发送的内容不是标准 JSON",
                404 => "未找到收信人（离线）",
                405 => "消息长度超过 1950",
                500 => "服务器内部异常",
                _ => $"未知错误码: {errorCode}"
            };
            Logger.Error("绑定失败: {ErrorCode} - {ErrorMsg}", errorCode, errorMsg);
            ErrorOccurred?.Invoke(this, new Exception($"绑定失败: {errorMsg}"));
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
                EventBus.Instance.PublishGameEvent(new GameEvent
                {
                    Type = GameEventType.Custom,
                    EventId = "dglab-feedback",
                    Source = "DGLabWebSocket",
                    TargetDeviceId = Id,
                    OldValue = 0,
                    NewValue = feedbackIndex,
                    Data = new Dictionary<string, object>
                    {
                        ["feedbackIndex"] = feedbackIndex
                    }
                });
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
        // 官方协议: pulse-通道:[波形数据,波形数据,...,波形数据]
        // 通道: A - A 通道；B - B 通道
        // 波形数据必须是 8 字节的 HEX(16 进制)形式
        // 数组最大长度为 100，超出则 APP 会丢弃全部数据
        string channelChar = channel == Channel.A ? "A" : "B";
        
        // 确保不超过 100 条
        var limitedArray = hexArray.Count > 100 ? hexArray.GetRange(0, 100) : hexArray;
        
        // 构建 JSON 数组字符串
        string arrayStr = "[" + string.Join(",", limitedArray.ConvertAll(h => $"\"{h}\"")) + "]";
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
        // 协议: clear-通道，通道: 1 - A 通道；2 - B 通道
        // 注意: 协议没有定义通道 0，AB 双通道需要分别清空
        int channelNum = channel == Channel.A ? 1 : 2;  // 默认 B 通道
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
        // 协议要求: 除初始绑定外，所有消息必须包含 type, clientId, targetId, message 四个字段
        // 心跳消息只在绑定后发送，此时 _targetId 已有值
        // 官方协议: message 应为 "200" 或 "DGLAB"
        return JsonSerializer.Serialize(new
        {
            type = "heartbeat",
            clientId = _clientId,
            targetId = _targetId ?? "",  // 绑定前为空字符串，绑定后为 APP ID
            message = "200"
        });
    }

    #endregion

    #region Helpers

    // 官方协议: json 数据的字符最大长度为 1950
    private const int MaxMessageLength = 1950;
    
    private async Task SendAsync(string message)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket 未连接");
        }

        // 检查消息长度
        if (message.Length > MaxMessageLength)
        {
            Logger.Warning("消息长度 {Length} 超过限制 {Max}，APP 可能丢弃此消息", 
                message.Length, MaxMessageLength);
        }

        var buffer = Encoding.UTF8.GetBytes(message);
        await _sendLock.WaitAsync();
        try
        {
            if (_ws?.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket 未连接");
            }

            await _ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(async _ =>
        {
            try
            {
                // 只在已绑定且连接正常时发送心跳
                // 协议要求心跳消息必须包含 targetId，绑定前无法发送有效心跳
                if (_ws?.State == WebSocketState.Open && _isBound && !string.IsNullOrEmpty(_targetId))
                {
                    await SendAsync(BuildHeartbeatMessage());
                    Logger.Debug("心跳已发送");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "心跳发送失败");
            }
        }, null, TimeSpan.FromSeconds(HeartbeatIntervalSeconds), TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
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
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}

/// <summary>
/// DG-LAB WebSocket 消息
/// </summary>
public class DGLabMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
