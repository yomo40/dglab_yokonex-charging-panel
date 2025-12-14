using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ChargingPanel.Core.Devices.Yokonex;

/// <summary>
/// 役次元设备类型
/// </summary>
public enum YokonexDeviceType
{
    /// <summary>电击器</summary>
    Estim,
    /// <summary>灌肠器</summary>
    Enema,
    /// <summary>跳蛋</summary>
    Vibrator,
    /// <summary>飞机杯</summary>
    Cup
}

/// <summary>
/// 役次元 IM 适配器
/// 通过腾讯云 IM SDK 控制役次元设备
/// </summary>
public class YokonexIMAdapter : IDevice, IDisposable
{
    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type => DeviceType.Yokonex;
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public DeviceState State => GetState();
    public ConnectionConfig? Config { get; private set; }

    /// <summary>役次元设备类型</summary>
    public YokonexDeviceType DeviceSubType { get; }

    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    private readonly ILogger _logger = Log.ForContext<YokonexIMAdapter>();
    private readonly HttpClient _httpClient = new();
    
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    
    private string? _uid;
    private string? _token;
    private string? _targetUserId;
    private string? _appId;
    private string? _userSig;
    
    private int _strengthA = 0;
    private int _strengthB = 0;
    private int _limitA = 276; // Yokonex 最大 276 级
    private int _limitB = 276;

    // API 端点
    private const string API_BASE = "https://suo.jiushu1234.com/api.php";

    public YokonexIMAdapter(YokonexDeviceType deviceType = YokonexDeviceType.Estim, string? id = null, string? name = null)
    {
        DeviceSubType = deviceType;
        Id = id ?? $"yokonex_{Guid.NewGuid():N}".Substring(0, 20);
        Name = name ?? $"役次元 {GetDeviceTypeName()}";
    }

    private string GetDeviceTypeName() => DeviceSubType switch
    {
        YokonexDeviceType.Estim => "电击器",
        YokonexDeviceType.Enema => "灌肠器",
        YokonexDeviceType.Vibrator => "跳蛋",
        YokonexDeviceType.Cup => "飞机杯",
        _ => "设备"
    };

    public async Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        Config = config;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 从配置获取认证信息
        _uid = config.UserId ?? config.Uid;
        _token = config.Token;
        _targetUserId = config.TargetUserId ?? config.TargetId;

        if (string.IsNullOrEmpty(_uid) || string.IsNullOrEmpty(_token))
        {
            throw new ArgumentException("UserId (uid) and Token are required for Yokonex IM connection");
        }

        UpdateStatus(DeviceStatus.Connecting);
        _logger.Information("Connecting to Yokonex IM: uid={Uid}", _uid);

        try
        {
            // 1. 获取 IM 签名
            await GetGameSignAsync(_uid, _token);
            
            // 2. 连接 IM (使用 WebSocket 模拟，实际应使用腾讯 IM SDK)
            // 注意: 完整实现需要集成腾讯云 IM SDK
            // 这里提供基于 HTTP 的简化实现
            
            UpdateStatus(DeviceStatus.Connected);
            _logger.Information("Yokonex IM connected: uid={Uid}, appId={AppId}", _uid, _appId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to connect Yokonex IM");
            UpdateStatus(DeviceStatus.Error);
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _ws = null;

        _strengthA = 0;
        _strengthB = 0;
        UpdateStatus(DeviceStatus.Disconnected);
        _logger.Information("Yokonex IM disconnected");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 发送游戏指令
    /// </summary>
    public async Task SendCommandAsync(string eventId, int? strength = null, int? duration = null)
    {
        EnsureConnected();

        var command = new YokonexCommand
        {
            Code = "game_cmd",
            Id = eventId,
            Token = _token!,
            Payload = strength.HasValue ? new { strength = strength.Value, duration = duration ?? 1000 } : null
        };

        await SendMessageAsync(command);
        _logger.Information("Sent Yokonex command: {EventId}", eventId);
    }

    public async Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        EnsureConnected();

        int targetValue = mode switch
        {
            StrengthMode.Increase => Math.Min((channel == Channel.A ? _strengthA : _strengthB) + value, 276),
            StrengthMode.Decrease => Math.Max((channel == Channel.A ? _strengthA : _strengthB) - value, 0),
            _ => Math.Clamp(value, 0, 276)
        };

        if (channel == Channel.A || channel == Channel.AB) _strengthA = targetValue;
        if (channel == Channel.B || channel == Channel.AB) _strengthB = targetValue;

        // 发送强度变化指令
        // 役次元通过事件 ID 触发，强度通常在事件配置中预设
        // 这里发送一个通用的强度变化事件
        var eventId = value > 0 ? "set_strength" : "stop";
        await SendCommandAsync(eventId, targetValue);

        StrengthChanged?.Invoke(this, new StrengthInfo
        {
            ChannelA = _strengthA,
            ChannelB = _strengthB,
            LimitA = _limitA,
            LimitB = _limitB
        });
    }

    public Task SendWaveformAsync(Channel channel, DGLab.WaveformData data)
    {
        // 役次元不支持波形，转换为强度/持续时间
        return SendCommandAsync("waveform", data.Strength, data.Duration);
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        return SendCommandAsync("stop");
    }

    public Task SetLimitsAsync(int limitA, int limitB)
    {
        _limitA = Math.Clamp(limitA, 0, 276);
        _limitB = Math.Clamp(limitB, 0, 276);
        return Task.CompletedTask;
    }

    #region IM API

    private async Task GetGameSignAsync(string uid, string token)
    {
        var requestBody = JsonSerializer.Serialize(new { uid = $"game_{uid}", token });
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync($"{API_BASE}/user/game_sign", content);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GameSignResponse>(responseBody);

        if (result?.Code != 1 || result.Data == null)
        {
            throw new Exception($"Failed to get game sign: {result?.Msg ?? "Unknown error"}");
        }

        _appId = result.Data.AppId;
        _userSig = result.Data.Sign;

        _logger.Debug("Got game sign: appId={AppId}", _appId);
    }

    private async Task SendMessageAsync(YokonexCommand command)
    {
        if (string.IsNullOrEmpty(_targetUserId))
        {
            _logger.Warning("Target user ID not set, message not sent");
            return;
        }

        // 役次元 IM 消息格式：
        // code: "game_info" (固定值)
        // data: 指令编号 (0=miss, 1=hit, 2=bomb, 3+=自定义)
        // token: 登录时使用的 token
        var gameMessage = new
        {
            code = "game_info",
            data = command.Id switch
            {
                "hit" => 1,
                "miss" => 0,
                "bomb" => 2,
                "kill" => 2,
                "death" => 2,
                "lost_hp" => 1,
                "add_hp" => 0,
                "stop" => 0,
                "set_strength" => 1,
                "waveform" => 1,
                _ => 1
            },
            token = _token
        };

        _logger.Information("IM Message to {Target}: {Message}", _targetUserId, 
            JsonSerializer.Serialize(gameMessage));

        // 注意: 完整实现需要集成腾讯云 IM SDK
        // 当前为简化实现，仅记录日志
        // 实际使用时需要：
        // 1. 初始化 TIM SDK: TIMInit(appId, ...)
        // 2. 登录: TIMLogin(uid, userSig, ...)
        // 3. 发送消息: TIMConvSendMessage(...)
        
        // 如果需要完整实现，请参考 ImSDK_Windows_8.7.7201 中的 C/C++ API
        // 或使用 WebSocket 方式连接腾讯 IM
        
        await Task.CompletedTask;
    }

    #endregion

    #region Predefined Events

    /// <summary>
    /// 触发预定义事件
    /// </summary>
    public Task TriggerEventAsync(YokonexEventType eventType)
    {
        var eventId = eventType switch
        {
            YokonexEventType.Hit => "hit",
            YokonexEventType.Miss => "miss",
            YokonexEventType.Bomb => "bomb",
            YokonexEventType.Kill => "kill",
            YokonexEventType.Death => "death",
            YokonexEventType.LostHp => "lost_hp",
            YokonexEventType.AddHp => "add_hp",
            YokonexEventType.Stop => "stop",
            _ => "hit"
        };

        return SendCommandAsync(eventId);
    }

    #endregion

    #region Helpers

    private void EnsureConnected()
    {
        if (Status != DeviceStatus.Connected)
        {
            throw new InvalidOperationException("Device is not connected");
        }
    }

    private void UpdateStatus(DeviceStatus status)
    {
        if (Status != status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
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
        _cts?.Cancel();
        _cts?.Dispose();
        _ws?.Dispose();
        _httpClient.Dispose();
    }

    #endregion
}

/// <summary>
/// 役次元事件类型
/// </summary>
public enum YokonexEventType
{
    /// <summary>命中</summary>
    Hit,
    /// <summary>未命中</summary>
    Miss,
    /// <summary>爆炸/强刺激</summary>
    Bomb,
    /// <summary>击杀</summary>
    Kill,
    /// <summary>死亡</summary>
    Death,
    /// <summary>掉血</summary>
    LostHp,
    /// <summary>回血</summary>
    AddHp,
    /// <summary>停止</summary>
    Stop
}

/// <summary>
/// 役次元指令结构
/// </summary>
public class YokonexCommand
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "game_cmd";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}

/// <summary>
/// game_sign API 响应
/// </summary>
public class GameSignResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public GameSignData? Data { get; set; }
}

public class GameSignData
{
    [JsonPropertyName("appid")]
    public string? AppId { get; set; }

    [JsonPropertyName("sign")]
    public string? Sign { get; set; }
}
