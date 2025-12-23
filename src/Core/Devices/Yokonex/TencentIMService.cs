using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ChargingPanel.Core.Devices.Yokonex;

/// <summary>
/// 腾讯云 IM SDK 服务封装
/// 提供高级 API 封装，支持异步操作
/// </summary>
public class TencentIMService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<TencentIMService>();
    
    private static TencentIMService? _instance;
    private static readonly object _lock = new();
    
    /// <summary>单例实例</summary>
    public static TencentIMService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new TencentIMService();
                }
            }
            return _instance;
        }
    }
    
    private bool _initialized;
    private bool _loggedIn;
    private string? _currentUserId;
    private ulong _sdkAppId;
    
    // 回调保持引用防止 GC
    private TencentIMNative.TIMCommCallback? _loginCallback;
    private TencentIMNative.TIMCommCallback? _logoutCallback;
    private TencentIMNative.TIMCommCallback? _sendMsgCallback;
    private TencentIMNative.TIMNetworkStatusListenerCallback? _networkCallback;
    private TencentIMNative.TIMKickedOfflineCallback? _kickedCallback;
    private TencentIMNative.TIMUserSigExpiredCallback? _sigExpiredCallback;
    private TencentIMNative.TIMRecvNewMsgCallback? _recvMsgCallback;
    
    // 异步操作完成源
    private readonly ConcurrentDictionary<int, TaskCompletionSource<(int code, string? desc, string? json)>> _pendingCallbacks = new();
    private int _callbackId;
    
    /// <summary>网络状态变化事件</summary>
    public event EventHandler<TencentIMNative.TIMNetworkStatus>? NetworkStatusChanged;
    
    /// <summary>被踢下线事件</summary>
    public event EventHandler? KickedOffline;
    
    /// <summary>票据过期事件</summary>
    public event EventHandler? UserSigExpired;
    
    /// <summary>收到新消息事件</summary>
    public event EventHandler<string>? MessageReceived;
    
    /// <summary>是否已初始化</summary>
    public bool IsInitialized => _initialized;
    
    /// <summary>是否已登录</summary>
    public bool IsLoggedIn => _loggedIn;
    
    /// <summary>当前用户 ID</summary>
    public string? CurrentUserId => _currentUserId;
    
    private TencentIMService() { }
    
    /// <summary>
    /// 初始化 SDK
    /// </summary>
    /// <param name="sdkAppId">腾讯云 IM SDKAppID</param>
    /// <param name="logPath">日志路径 (可选)</param>
    public bool Initialize(ulong sdkAppId, string? logPath = null)
    {
        if (_initialized)
        {
            Logger.Warning("腾讯 IM SDK 已初始化");
            return true;
        }
        
        if (!TencentIMNative.IsSdkAvailable())
        {
            Logger.Error("腾讯 IM SDK 不可用，请确保 ImSDK.dll 在程序目录");
            return false;
        }
        
        try
        {
            _sdkAppId = sdkAppId;
            
            // 构建配置 JSON
            var config = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(logPath))
            {
                config["sdk_config_log_file_path"] = logPath;
                config["sdk_config_config_file_path"] = logPath;
            }
            
            var configJson = config.Count > 0 ? JsonSerializer.Serialize(config) : "";
            
            var result = TencentIMNative.TIMInit(sdkAppId, configJson);
            if (result != (int)TencentIMNative.TIMResult.TIM_SUCC)
            {
                Logger.Error("腾讯 IM SDK 初始化失败: {Result}", result);
                return false;
            }
            
            // 设置回调
            SetupCallbacks();
            
            _initialized = true;
            Logger.Information("腾讯 IM SDK 初始化成功: SDKAppID={AppId}", sdkAppId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "腾讯 IM SDK 初始化异常");
            return false;
        }
    }
    
    /// <summary>
    /// 登录
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="userSig">用户签名</param>
    public async Task<bool> LoginAsync(string userId, string userSig)
    {
        if (!_initialized)
        {
            Logger.Error("腾讯 IM SDK 未初始化");
            return false;
        }
        
        if (_loggedIn)
        {
            if (_currentUserId == userId)
            {
                Logger.Warning("用户 {UserId} 已登录", userId);
                return true;
            }
            
            // 先登出
            await LogoutAsync();
        }
        
        var tcs = new TaskCompletionSource<(int code, string? desc, string? json)>();
        var callbackId = Interlocked.Increment(ref _callbackId);
        _pendingCallbacks[callbackId] = tcs;
        
        _loginCallback = (code, desc, json, userData) =>
        {
            var descStr = TencentIMNative.PtrToStringUtf8(desc);
            var jsonStr = TencentIMNative.PtrToStringUtf8(json);
            
            if (_pendingCallbacks.TryRemove((int)userData, out var pendingTcs))
            {
                pendingTcs.TrySetResult((code, descStr, jsonStr));
            }
        };
        
        var result = TencentIMNative.TIMLogin(userId, userSig, _loginCallback, (IntPtr)callbackId);
        if (result != (int)TencentIMNative.TIMResult.TIM_SUCC)
        {
            _pendingCallbacks.TryRemove(callbackId, out _);
            Logger.Error("登录接口调用失败: {Result}", result);
            return false;
        }
        
        try
        {
            var (code, descStr, _) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            
            if (code == 0)
            {
                _loggedIn = true;
                _currentUserId = userId;
                Logger.Information("腾讯 IM 登录成功: {UserId}", userId);
                return true;
            }
            else
            {
                Logger.Error("腾讯 IM 登录失败: {Code} - {Desc}", code, descStr);
                return false;
            }
        }
        catch (TimeoutException)
        {
            _pendingCallbacks.TryRemove(callbackId, out _);
            Logger.Error("腾讯 IM 登录超时");
            return false;
        }
    }
    
    /// <summary>
    /// 登出
    /// </summary>
    public async Task<bool> LogoutAsync()
    {
        if (!_loggedIn)
        {
            return true;
        }
        
        var tcs = new TaskCompletionSource<(int code, string? desc, string? json)>();
        var callbackId = Interlocked.Increment(ref _callbackId);
        _pendingCallbacks[callbackId] = tcs;
        
        _logoutCallback = (code, desc, json, userData) =>
        {
            var descStr = TencentIMNative.PtrToStringUtf8(desc);
            var jsonStr = TencentIMNative.PtrToStringUtf8(json);
            
            if (_pendingCallbacks.TryRemove((int)userData, out var pendingTcs))
            {
                pendingTcs.TrySetResult((code, descStr, jsonStr));
            }
        };
        
        var result = TencentIMNative.TIMLogout(_logoutCallback, (IntPtr)callbackId);
        if (result != (int)TencentIMNative.TIMResult.TIM_SUCC)
        {
            _pendingCallbacks.TryRemove(callbackId, out _);
            Logger.Error("登出接口调用失败: {Result}", result);
            return false;
        }
        
        try
        {
            var (code, _, _) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            
            _loggedIn = false;
            _currentUserId = null;
            
            if (code == 0)
            {
                Logger.Information("腾讯 IM 登出成功");
                return true;
            }
            else
            {
                Logger.Warning("腾讯 IM 登出返回: {Code}", code);
                return true; // 即使返回非0也认为登出成功
            }
        }
        catch (TimeoutException)
        {
            _pendingCallbacks.TryRemove(callbackId, out _);
            _loggedIn = false;
            _currentUserId = null;
            Logger.Warning("腾讯 IM 登出超时，强制清理状态");
            return true;
        }
    }
    
    /// <summary>
    /// 发送 C2C 消息
    /// </summary>
    /// <param name="toUserId">目标用户 ID</param>
    /// <param name="textContent">文本内容</param>
    public async Task<bool> SendC2CMessageAsync(string toUserId, string textContent)
    {
        if (!_loggedIn)
        {
            Logger.Error("未登录，无法发送消息");
            return false;
        }
        
        // 构建消息 JSON
        var msgJson = JsonSerializer.Serialize(new
        {
            message_elem_array = new[]
            {
                new
                {
                    elem_type = 0, // kTIMElem_Text
                    text_elem_content = textContent
                }
            }
        });
        
        var tcs = new TaskCompletionSource<(int code, string? desc, string? json)>();
        var callbackId = Interlocked.Increment(ref _callbackId);
        _pendingCallbacks[callbackId] = tcs;
        
        _sendMsgCallback = (code, desc, json, userData) =>
        {
            var descStr = TencentIMNative.PtrToStringUtf8(desc);
            var jsonStr = TencentIMNative.PtrToStringUtf8(json);
            
            if (_pendingCallbacks.TryRemove((int)userData, out var pendingTcs))
            {
                pendingTcs.TrySetResult((code, descStr, jsonStr));
            }
        };
        
        var messageIdBuffer = new StringBuilder(128);
        var result = TencentIMNative.TIMMsgSendMessage(
            toUserId,
            TencentIMNative.TIMConvType.kTIMConv_C2C,
            msgJson,
            messageIdBuffer,
            _sendMsgCallback,
            (IntPtr)callbackId);
        
        if (result != (int)TencentIMNative.TIMResult.TIM_SUCC)
        {
            _pendingCallbacks.TryRemove(callbackId, out _);
            Logger.Error("发送消息接口调用失败: {Result}", result);
            return false;
        }
        
        try
        {
            var (code, descStr, _) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            
            if (code == 0)
            {
                Logger.Debug("消息发送成功: to={To}, msgId={MsgId}", toUserId, messageIdBuffer.ToString());
                return true;
            }
            else
            {
                Logger.Error("消息发送失败: {Code} - {Desc}", code, descStr);
                return false;
            }
        }
        catch (TimeoutException)
        {
            _pendingCallbacks.TryRemove(callbackId, out _);
            Logger.Error("消息发送超时");
            return false;
        }
    }
    
    private void SetupCallbacks()
    {
        // 网络状态回调
        _networkCallback = (status, code, desc, userData) =>
        {
            var descStr = TencentIMNative.PtrToStringUtf8(desc);
            Logger.Information("网络状态变化: {Status}, code={Code}, desc={Desc}", status, code, descStr);
            NetworkStatusChanged?.Invoke(this, status);
        };
        TencentIMNative.TIMSetNetworkStatusListenerCallback(_networkCallback, IntPtr.Zero);
        
        // 被踢下线回调
        _kickedCallback = (userData) =>
        {
            Logger.Warning("被踢下线");
            _loggedIn = false;
            KickedOffline?.Invoke(this, EventArgs.Empty);
        };
        TencentIMNative.TIMSetKickedOfflineCallback(_kickedCallback, IntPtr.Zero);
        
        // 票据过期回调
        _sigExpiredCallback = (userData) =>
        {
            Logger.Warning("用户票据过期");
            UserSigExpired?.Invoke(this, EventArgs.Empty);
        };
        TencentIMNative.TIMSetUserSigExpiredCallback(_sigExpiredCallback, IntPtr.Zero);
        
        // 新消息回调
        _recvMsgCallback = (jsonMsgArray, userData) =>
        {
            var msgJson = TencentIMNative.PtrToStringUtf8(jsonMsgArray);
            if (!string.IsNullOrEmpty(msgJson))
            {
                Logger.Debug("收到新消息: {Json}", msgJson[..Math.Min(200, msgJson.Length)]);
                MessageReceived?.Invoke(this, msgJson);
            }
        };
        TencentIMNative.TIMAddRecvNewMsgCallback(_recvMsgCallback, IntPtr.Zero);
    }
    
    /// <summary>
    /// 反初始化 SDK
    /// </summary>
    public void Uninitialize()
    {
        if (!_initialized) return;
        
        try
        {
            if (_recvMsgCallback != null)
            {
                TencentIMNative.TIMRemoveRecvNewMsgCallback(_recvMsgCallback);
            }
            
            TencentIMNative.TIMUninit();
            _initialized = false;
            _loggedIn = false;
            _currentUserId = null;
            
            Logger.Information("腾讯 IM SDK 已反初始化");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "腾讯 IM SDK 反初始化异常");
        }
    }
    
    public void Dispose()
    {
        Uninitialize();
        GC.SuppressFinalize(this);
    }
}
