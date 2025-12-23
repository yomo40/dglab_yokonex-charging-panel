using System;
using System.Collections.Concurrent;
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
/// IM 连接模式
/// </summary>
public enum IMConnectionMode
{
    /// <summary>WebSocket 模式 (默认，兼容性好)</summary>
    WebSocket,
    /// <summary>原生 SDK 模式 (需要 ImSDK.dll)</summary>
    NativeSDK
}

/// <summary>
/// 役次元 IM 适配器
/// 通过腾讯云 IM 控制役次元设备
/// 
/// 协议流程:
/// 1. 用户从役次元 APP 获取 uid 和 token
/// 2. 调用 /user/game_sign 获取 appId 和 userSig
/// 3. 使用 WebSocket 或原生 SDK 连接腾讯云 IM
/// 4. 登录: userID = "game_" + uid
/// 5. 发送消息: to = uid (不带 game_ 前缀)
/// 6. 消息格式: { code: "game_cmd", id: "事件ID" } 或 { code: "game_info", data: 数值, token: "..." }
/// 
/// 支持两种连接模式:
/// - WebSocket: 默认模式，通过 WebSocket 连接腾讯云 IM
/// - NativeSDK: 原生 SDK 模式，需要 ImSDK.dll，性能更好
/// </summary>
public class YokonexIMAdapter : IDevice, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<YokonexIMAdapter>();

    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type => DeviceType.Yokonex;
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public DeviceState State => GetState();
    public ConnectionConfig? Config { get; private set; }

    /// <summary>役次元设备类型</summary>
    public YokonexDeviceType DeviceSubType { get; }
    
    /// <summary>IM 连接模式</summary>
    public IMConnectionMode ConnectionMode { get; private set; } = IMConnectionMode.WebSocket;
    
    /// <summary>是否原生 SDK 可用</summary>
    public static bool IsNativeSDKAvailable => TencentIMNative.IsSdkAvailable();

    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;
    
    /// <summary>收到消息事件</summary>
    public event EventHandler<YokonexIMReceivedMessage>? MessageReceived;

    private readonly HttpClient _httpClient = new();
    private readonly ConcurrentQueue<Func<Task>> _sendQueue = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // WebSocket 模式
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _sendTask;
    private Timer? _heartbeatTimer;
    
    // 原生 SDK 模式
    private TencentIMService? _nativeService;

    // 认证信息
    private string? _uid;           // 用户 ID (不带 game_ 前缀)
    private string? _token;         // 游戏鉴权令牌
    private string? _appId;         // 腾讯 IM SDKAppID
    private string? _userSig;       // 用户签名

    // 状态
    private bool _isReady;
    private bool _isLoggedIn;
    private int _msgSeq;
    
    // 重连
    private Timer? _reconnectTimer;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 5;
    private const int ReconnectDelayMs = 5000;

    // 强度状态 (本地缓存)
    private int _strengthA;
    private int _strengthB;
    private int _limitA = 276;
    private int _limitB = 276;

    // API 端点
    private const string API_BASE = "https://suo.jiushu1234.com/api.php";
    
    // 腾讯云 IM WebSocket 端点
    private const string TIM_WS_URL = "wss://wss.im.qcloud.com";

    public YokonexIMAdapter(YokonexDeviceType deviceType = YokonexDeviceType.Estim, string? id = null, string? name = null, IMConnectionMode? preferredMode = null)
    {
        DeviceSubType = deviceType;
        Id = id ?? $"yokonex_im_{Guid.NewGuid():N}"[..20];
        Name = name ?? $"役次元 {GetDeviceTypeName()} (IM)";
        
        // 自动选择连接模式
        if (preferredMode.HasValue)
        {
            ConnectionMode = preferredMode.Value;
        }
        else
        {
            // 如果原生 SDK 可用，优先使用
            ConnectionMode = IsNativeSDKAvailable ? IMConnectionMode.NativeSDK : IMConnectionMode.WebSocket;
        }
        
        Logger.Information("役次元 IM 适配器创建: 模式={Mode}, SDK可用={SdkAvailable}", ConnectionMode, IsNativeSDKAvailable);
    }

    private string GetDeviceTypeName() => DeviceSubType switch
    {
        YokonexDeviceType.Estim => "电击器",
        YokonexDeviceType.Enema => "灌肠器",
        YokonexDeviceType.Vibrator => "跳蛋",
        YokonexDeviceType.Cup => "飞机杯",
        _ => "设备"
    };

    /// <summary>
    /// 连接到役次元 IM
    /// </summary>
    /// <param name="config">连接配置，需要包含 UserId/Uid 和 Token</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        Config = config;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 从配置获取认证信息
        _uid = config.UserId ?? config.Uid;
        _token = config.Token;

        // 移除 game_ 前缀 (如果有)
        if (_uid?.StartsWith("game_") == true)
        {
            _uid = _uid[5..];
        }

        if (string.IsNullOrEmpty(_uid) || string.IsNullOrEmpty(_token))
        {
            throw new ArgumentException("UserId (uid) 和 Token 是必需的");
        }

        UpdateStatus(DeviceStatus.Connecting);
        Logger.Information("正在连接役次元 IM: uid={Uid}, 模式={Mode}", _uid, ConnectionMode);

        try
        {
            // 1. 获取 IM 签名
            // 注意: 请求时 uid 需要带 game_ 前缀
            await GetGameSignAsync($"game_{_uid}", _token);

            // 根据连接模式选择不同的连接方式
            if (ConnectionMode == IMConnectionMode.NativeSDK)
            {
                await ConnectNativeSDKAsync(_cts.Token);
            }
            else
            {
                await ConnectWebSocketModeAsync(_cts.Token);
            }

            UpdateStatus(DeviceStatus.Connected);
            Logger.Information("役次元 IM 已连接: uid={Uid}, appId={AppId}, 模式={Mode}", _uid, _appId, ConnectionMode);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "连接役次元 IM 失败");
            UpdateStatus(DeviceStatus.Error);
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }
    
    /// <summary>
    /// 使用原生 SDK 连接
    /// </summary>
    private async Task ConnectNativeSDKAsync(CancellationToken ct)
    {
        _nativeService = TencentIMService.Instance;
        
        // 初始化 SDK
        if (!_nativeService.IsInitialized)
        {
            var appIdLong = ulong.Parse(_appId!);
            if (!_nativeService.Initialize(appIdLong))
            {
                Logger.Warning("原生 SDK 初始化失败，回退到 WebSocket 模式");
                ConnectionMode = IMConnectionMode.WebSocket;
                await ConnectWebSocketModeAsync(ct);
                return;
            }
        }
        
        // 订阅消息事件
        _nativeService.MessageReceived += OnNativeMessageReceived;
        _nativeService.KickedOffline += OnNativeKickedOffline;
        
        // 登录
        var loginSuccess = await _nativeService.LoginAsync($"game_{_uid}", _userSig!);
        if (!loginSuccess)
        {
            Logger.Warning("原生 SDK 登录失败，回退到 WebSocket 模式");
            ConnectionMode = IMConnectionMode.WebSocket;
            await ConnectWebSocketModeAsync(ct);
            return;
        }
        
        _isLoggedIn = true;
        _isReady = true;
    }
    
    /// <summary>
    /// 使用 WebSocket 模式连接
    /// </summary>
    private async Task ConnectWebSocketModeAsync(CancellationToken ct)
    {
        // 2. 连接 WebSocket
        await ConnectWebSocketAsync(ct);

        // 3. 登录 IM
        await LoginAsync(ct);

        // 4. 启动心跳
        StartHeartbeat();

        // 5. 启动发送队列处理
        _sendTask = ProcessSendQueueAsync(ct);
    }
    
    private void OnNativeMessageReceived(object? sender, string msgJson)
    {
        try
        {
            // 解析消息 JSON
            using var doc = JsonDocument.Parse(msgJson);
            var root = doc.RootElement;
            
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var msg in root.EnumerateArray())
                {
                    ProcessNativeMessage(msg);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "处理原生 SDK 消息失败");
        }
    }
    
    private void ProcessNativeMessage(JsonElement msg)
    {
        try
        {
            var from = msg.TryGetProperty("message_sender", out var sender) ? sender.GetString() : null;
            
            if (msg.TryGetProperty("message_elem_array", out var elemArray))
            {
                foreach (var elem in elemArray.EnumerateArray())
                {
                    var elemType = elem.TryGetProperty("elem_type", out var et) ? et.GetInt32() : -1;
                    
                    if (elemType == 0 && elem.TryGetProperty("text_elem_content", out var textContent))
                    {
                        var text = textContent.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            try
                            {
                                var parsed = JsonSerializer.Deserialize<YokonexIMReceivedMessage>(text);
                                if (parsed != null)
                                {
                                    parsed.From = from;
                                    MessageReceived?.Invoke(this, parsed);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "解析原生消息失败");
        }
    }
    
    private void OnNativeKickedOffline(object? sender, EventArgs e)
    {
        Logger.Warning("役次元 IM 被踢下线");
        _isLoggedIn = false;
        _isReady = false;
        UpdateStatus(DeviceStatus.Disconnected);
        
        // 触发重连
        StartReconnectTimer();
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync()
    {
        StopHeartbeat();
        StopReconnectTimer();
        _cts?.Cancel();

        // 原生 SDK 模式
        if (ConnectionMode == IMConnectionMode.NativeSDK && _nativeService != null)
        {
            _nativeService.MessageReceived -= OnNativeMessageReceived;
            _nativeService.KickedOffline -= OnNativeKickedOffline;
            await _nativeService.LogoutAsync();
            _nativeService = null;
        }
        
        // WebSocket 模式
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
        _isReady = false;
        _isLoggedIn = false;
        _strengthA = 0;
        _strengthB = 0;

        UpdateStatus(DeviceStatus.Disconnected);
        Logger.Information("役次元 IM 已断开");
    }
    
    /// <summary>
    /// 手动触发重连
    /// </summary>
    public async Task ReconnectAsync()
    {
        if (Config == null)
        {
            throw new InvalidOperationException("没有保存的连接配置");
        }
        
        Logger.Information("手动触发 IM 重连");
        _reconnectAttempts = 0;
        
        // 先断开现有连接
        await DisconnectAsync();
        
        // 重新连接
        await ConnectAsync(Config, CancellationToken.None);
    }

    #region 公开方法

    /// <summary>
    /// 发送游戏指令 (game_cmd 格式)
    /// </summary>
    /// <param name="eventId">事件 ID，如 "hurt", "kill", "death" 等</param>
    public Task SendGameCommandAsync(string eventId)
    {
        // game_cmd 格式: { code: "game_cmd", id: "事件ID" }
        // 注意: token 会在发送时自动添加
        var message = new
        {
            code = "game_cmd",
            id = eventId
        };

        return EnqueueMessageAsync(message);
    }

    /// <summary>
    /// 发送游戏信息 (game_info 格式)
    /// </summary>
    /// <param name="data">指令编号: 0=miss, 1=hit, 2=bomb, 3+=自定义</param>
    public Task SendGameInfoAsync(int data)
    {
        // game_info 格式: { code: "game_info", data: 数值, token: "..." }
        var message = new
        {
            code = "game_info",
            data,
            token = _token
        };

        return EnqueueMessageAsync(message);
    }

    /// <summary>
    /// 触发预定义事件
    /// </summary>
    public Task TriggerEventAsync(YokonexEventType eventType)
    {
        return eventType switch
        {
            YokonexEventType.Miss => SendGameInfoAsync(0),
            YokonexEventType.Hit => SendGameInfoAsync(1),
            YokonexEventType.Bomb => SendGameInfoAsync(2),
            YokonexEventType.Kill => SendGameInfoAsync(2),
            YokonexEventType.Death => SendGameInfoAsync(2),
            YokonexEventType.LostHp => SendGameInfoAsync(1),
            YokonexEventType.AddHp => SendGameInfoAsync(0),
            YokonexEventType.Stop => SendGameInfoAsync(0),
            _ => SendGameInfoAsync(1)
        };
    }

    /// <summary>
    /// 发送自定义事件 ID
    /// </summary>
    public Task SendCommandAsync(string eventId, int? strength = null, int? duration = null)
    {
        // 优先使用 game_cmd 格式
        return SendGameCommandAsync(eventId);
    }

    public async Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        int targetValue = mode switch
        {
            StrengthMode.Increase => Math.Min((channel == Channel.A ? _strengthA : _strengthB) + value, 276),
            StrengthMode.Decrease => Math.Max((channel == Channel.A ? _strengthA : _strengthB) - value, 0),
            _ => Math.Clamp(value, 0, 276)
        };

        if (channel == Channel.A || channel == Channel.AB) _strengthA = targetValue;
        if (channel == Channel.B || channel == Channel.AB) _strengthB = targetValue;

        // 役次元通过事件触发，这里发送一个通用事件
        if (targetValue > 0)
            await SendGameInfoAsync(1);  // hit
        else
            await SendGameInfoAsync(0);  // miss/stop

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
        // 役次元不支持波形，转换为事件
        return SendGameInfoAsync(1);
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        return SendGameInfoAsync(0);
    }

    public Task SetLimitsAsync(int limitA, int limitB)
    {
        _limitA = Math.Clamp(limitA, 0, 276);
        _limitB = Math.Clamp(limitB, 0, 276);
        return Task.CompletedTask;
    }

    #endregion

    #region IM API

    /// <summary>
    /// 获取游戏签名
    /// </summary>
    private async Task GetGameSignAsync(string uid, string token)
    {
        var requestBody = JsonSerializer.Serialize(new { uid, token });
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        Logger.Debug("请求游戏签名: uid={Uid}", uid);

        var response = await _httpClient.PostAsync($"{API_BASE}/user/game_sign", content);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GameSignResponse>(responseBody);

        if (result?.Code != 1 || result.Data == null)
        {
            throw new Exception($"获取游戏签名失败: {result?.Msg ?? "未知错误"}");
        }

        _appId = result.Data.AppId;
        _userSig = result.Data.Sign;

        Logger.Information("获取游戏签名成功: appId={AppId}", _appId);
    }

    #endregion

    #region WebSocket 连接

    /// <summary>
    /// 连接 WebSocket
    /// </summary>
    private async Task ConnectWebSocketAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        // 腾讯云 IM WebSocket URL
        // 格式: wss://wss.im.qcloud.com/v4/ws?sdkappid={appId}&identifier={userId}&usersig={userSig}
        var wsUrl = $"{TIM_WS_URL}/v4/ws?sdkappid={_appId}&identifier=game_{_uid}&usersig={Uri.EscapeDataString(_userSig!)}";

        Logger.Debug("连接 WebSocket: {Url}", wsUrl[..Math.Min(100, wsUrl.Length)] + "...");

        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        Logger.Information("WebSocket 已连接");

        // 启动接收循环
        _receiveTask = ReceiveLoopAsync(ct);
    }

    /// <summary>
    /// 登录 IM
    /// </summary>
    private async Task LoginAsync(CancellationToken ct)
    {
        // 腾讯云 IM 登录请求
        var loginRequest = new
        {
            ReqHead = new
            {
                Seq = Interlocked.Increment(ref _msgSeq),
                Cmd = "Login"
            },
            ReqBody = new
            {
                SDKAppID = int.Parse(_appId!),
                UserID = $"game_{_uid}",
                UserSig = _userSig
            }
        };

        await SendRawAsync(loginRequest, ct);

        // 等待登录完成 (最多 10 秒)
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!_isLoggedIn && DateTime.UtcNow < timeout && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct);
        }

        if (!_isLoggedIn)
        {
            throw new TimeoutException("IM 登录超时");
        }

        Logger.Information("IM 登录成功: userID=game_{Uid}", _uid);
    }

    /// <summary>
    /// 接收消息循环
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Warning("WebSocket 收到关闭消息");
                    break;
                }

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var message = messageBuilder.ToString();
                    messageBuilder.Clear();
                    HandleReceivedMessage(message);
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
                // 触发自动重连
                StartReconnectTimer();
            }
        }
    }

    /// <summary>
    /// 处理收到的消息
    /// </summary>
    private void HandleReceivedMessage(string data)
    {
        try
        {
            Logger.Debug("收到消息: {Data}", data[..Math.Min(200, data.Length)]);

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            // 检查响应头
            if (root.TryGetProperty("RspHead", out var rspHead))
            {
                var cmd = rspHead.GetProperty("Cmd").GetString();

                switch (cmd)
                {
                    case "Login":
                        HandleLoginResponse(root);
                        break;
                    case "SendMsg":
                        HandleSendMsgResponse(root);
                        break;
                }
            }

            // 检查推送消息
            if (root.TryGetProperty("PushHead", out var pushHead))
            {
                var cmd = pushHead.GetProperty("Cmd").GetString();

                if (cmd == "MsgNotify")
                {
                    HandleMsgNotify(root);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "处理消息失败: {Data}", data[..Math.Min(100, data.Length)]);
        }
    }

    private void HandleLoginResponse(JsonElement root)
    {
        if (root.TryGetProperty("RspBody", out var rspBody))
        {
            if (rspBody.TryGetProperty("ErrorCode", out var errorCode) && errorCode.GetInt32() == 0)
            {
                _isLoggedIn = true;
                _isReady = true;
                Logger.Information("IM 登录响应: 成功");
            }
            else
            {
                var errorInfo = rspBody.TryGetProperty("ErrorInfo", out var info) ? info.GetString() : "未知错误";
                Logger.Error("IM 登录失败: {Error}", errorInfo);
            }
        }
    }

    private void HandleSendMsgResponse(JsonElement root)
    {
        if (root.TryGetProperty("RspBody", out var rspBody))
        {
            var errorCode = rspBody.TryGetProperty("ErrorCode", out var code) ? code.GetInt32() : -1;
            if (errorCode == 0)
            {
                Logger.Debug("消息发送成功");
            }
            else
            {
                var errorInfo = rspBody.TryGetProperty("ErrorInfo", out var info) ? info.GetString() : "未知错误";
                Logger.Warning("消息发送失败: {Code} - {Info}", errorCode, errorInfo);
            }
        }
    }

    private void HandleMsgNotify(JsonElement root)
    {
        if (root.TryGetProperty("PushBody", out var pushBody) &&
            pushBody.TryGetProperty("MsgList", out var msgList))
        {
            foreach (var msg in msgList.EnumerateArray())
            {
                try
                {
                    var from = msg.TryGetProperty("From_Account", out var f) ? f.GetString() : null;
                    var msgBody = msg.GetProperty("MsgBody");

                    foreach (var elem in msgBody.EnumerateArray())
                    {
                        if (elem.TryGetProperty("MsgType", out var msgType) &&
                            msgType.GetString() == "TIMTextElem" &&
                            elem.TryGetProperty("MsgContent", out var content) &&
                            content.TryGetProperty("Text", out var text))
                        {
                            var textContent = text.GetString();
                            Logger.Information("收到消息 from={From}: {Text}", from, textContent);

                            // 尝试解析 JSON
                            try
                            {
                                var parsed = JsonSerializer.Deserialize<YokonexIMReceivedMessage>(textContent!);
                                if (parsed != null)
                                {
                                    parsed.From = from;
                                    MessageReceived?.Invoke(this, parsed);
                                }
                            }
                            catch
                            {
                                // 非 JSON 消息，忽略
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "解析消息失败");
                }
            }
        }
    }

    #endregion

    #region 消息发送

    /// <summary>
    /// 将消息加入发送队列
    /// </summary>
    private Task EnqueueMessageAsync(object payload)
    {
        // 原生 SDK 模式直接发送
        if (ConnectionMode == IMConnectionMode.NativeSDK && _nativeService != null)
        {
            return SendViaNativeSDKAsync(payload);
        }
        
        // WebSocket 模式使用队列
        var tcs = new TaskCompletionSource();

        _sendQueue.Enqueue(async () =>
        {
            try
            {
                await SendTextMessageAsync(payload);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }
    
    /// <summary>
    /// 通过原生 SDK 发送消息
    /// </summary>
    private async Task SendViaNativeSDKAsync(object payload)
    {
        if (_nativeService == null || !_nativeService.IsLoggedIn)
        {
            Logger.Warning("原生 SDK 未就绪，消息丢弃");
            return;
        }
        
        var payloadJson = JsonSerializer.Serialize(payload);
        
        // 添加 token (如果是 game_info 格式且没有 token)
        if (payloadJson.Contains("\"code\":\"game_info\"") && !payloadJson.Contains("\"token\""))
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson)!;
            dict["token"] = _token!;
            payloadJson = JsonSerializer.Serialize(dict);
        }
        
        // 目标用户 ID: 不带 game_ 前缀
        var toUserId = _uid!;
        
        Logger.Information("通过原生 SDK 发送消息到 {To}: {Payload}", toUserId, payloadJson);
        
        var success = await _nativeService.SendC2CMessageAsync(toUserId, payloadJson);
        if (!success)
        {
            Logger.Warning("原生 SDK 发送消息失败");
        }
    }

    /// <summary>
    /// 处理发送队列
    /// </summary>
    private async Task ProcessSendQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_sendQueue.TryDequeue(out var task))
            {
                await _sendLock.WaitAsync(ct);
                try
                {
                    await task();
                    // 发送间隔，避免频率过高
                    await Task.Delay(50, ct);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }

    /// <summary>
    /// 发送文本消息
    /// </summary>
    private async Task SendTextMessageAsync(object payload)
    {
        if (!_isReady || _ws?.State != WebSocketState.Open)
        {
            Logger.Warning("IM 未就绪，消息丢弃");
            return;
        }

        // 添加 token 到 payload (如果是 game_info 格式且没有 token)
        var payloadJson = JsonSerializer.Serialize(payload);
        if (payloadJson.Contains("\"code\":\"game_info\"") && !payloadJson.Contains("\"token\""))
        {
            // 重新构建带 token 的消息
            using var doc = JsonDocument.Parse(payloadJson);
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson)!;
            dict["token"] = _token!;
            payloadJson = JsonSerializer.Serialize(dict);
        }

        // 目标用户 ID: 不带 game_ 前缀
        var toUserId = _uid!;

        // 构建发送消息请求
        var sendRequest = new
        {
            ReqHead = new
            {
                Seq = Interlocked.Increment(ref _msgSeq),
                Cmd = "SendMsg"
            },
            ReqBody = new
            {
                SyncOtherMachine = 1,
                From_Account = $"game_{_uid}",
                To_Account = toUserId,
                MsgRandom = new Random().Next(100000000, 999999999),
                MsgTimeStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MsgBody = new[]
                {
                    new
                    {
                        MsgType = "TIMTextElem",
                        MsgContent = new { Text = payloadJson }
                    }
                }
            }
        };

        Logger.Information("发送消息到 {To}: {Payload}", toUserId, payloadJson);
        await SendRawAsync(sendRequest, CancellationToken.None);
    }

    /// <summary>
    /// 发送原始 WebSocket 消息
    /// </summary>
    private async Task SendRawAsync(object message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket 未连接");
        }

        var json = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, ct);
    }

    #endregion

    #region 心跳

    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(async _ =>
        {
            try
            {
                if (_ws?.State == WebSocketState.Open && _isLoggedIn)
                {
                    var heartbeat = new
                    {
                        ReqHead = new
                        {
                            Seq = Interlocked.Increment(ref _msgSeq),
                            Cmd = "Heartbeat"
                        },
                        ReqBody = new { }
                    };
                    await SendRawAsync(heartbeat, CancellationToken.None);
                    Logger.Debug("心跳已发送");
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
    
    private void StartReconnectTimer()
    {
        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            Logger.Warning("IM 已达到最大重连次数 ({Max})，停止重连", MaxReconnectAttempts);
            return;
        }
        
        StopReconnectTimer();
        
        _reconnectTimer = new Timer(async _ =>
        {
            await TryReconnectAsync();
        }, null, TimeSpan.FromMilliseconds(ReconnectDelayMs), Timeout.InfiniteTimeSpan);
    }
    
    private void StopReconnectTimer()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
    }
    
    private async Task TryReconnectAsync()
    {
        if (Config == null || Status == DeviceStatus.Connected)
        {
            return;
        }
        
        _reconnectAttempts++;
        Logger.Information("尝试 IM 重连 (第 {Attempt}/{Max} 次)", _reconnectAttempts, MaxReconnectAttempts);
        
        try
        {
            await ConnectAsync(Config, CancellationToken.None);
            Logger.Information("IM 重连成功");
            _reconnectAttempts = 0;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "IM 重连失败");
            
            if (_reconnectAttempts < MaxReconnectAttempts)
            {
                StartReconnectTimer();
            }
            else
            {
                Logger.Error("IM 重连失败，已达到最大重连次数");
                UpdateStatus(DeviceStatus.Error);
            }
        }
    }

    #endregion

    #region Helpers

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
        StopHeartbeat();
        StopReconnectTimer();
        _cts?.Cancel();
        _cts?.Dispose();
        _ws?.Dispose();
        _httpClient.Dispose();
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}


#region 数据类型

/// <summary>
/// 役次元事件类型
/// </summary>
public enum YokonexEventType
{
    /// <summary>命中 (data=1)</summary>
    Hit,
    /// <summary>未命中 (data=0)</summary>
    Miss,
    /// <summary>爆炸/强刺激 (data=2)</summary>
    Bomb,
    /// <summary>击杀 (data=2)</summary>
    Kill,
    /// <summary>死亡 (data=2)</summary>
    Death,
    /// <summary>掉血 (data=1)</summary>
    LostHp,
    /// <summary>回血 (data=0)</summary>
    AddHp,
    /// <summary>停止 (data=0)</summary>
    Stop
}

/// <summary>
/// 役次元 IM 收到的消息
/// </summary>
public class YokonexIMReceivedMessage
{
    /// <summary>发送者 ID</summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }

    /// <summary>消息类型: game_info 或 game_cmd</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>指令编号 (game_info 格式)</summary>
    [JsonPropertyName("data")]
    public int? Data { get; set; }

    /// <summary>事件 ID (game_cmd 格式)</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>鉴权令牌</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }
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

/// <summary>
/// game_sign 数据
/// </summary>
public class GameSignData
{
    [JsonPropertyName("appid")]
    public string? AppId { get; set; }

    [JsonPropertyName("sign")]
    public string? Sign { get; set; }
}

#endregion
