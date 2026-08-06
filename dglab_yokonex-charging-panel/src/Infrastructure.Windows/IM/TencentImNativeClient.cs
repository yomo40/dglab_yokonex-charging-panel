using System.Runtime.InteropServices;
using System.Text.Json;

namespace ChargingPanel.Infrastructure.Windows.IM;

/// <summary>
/// 腾讯 IM 原生客户端（ImSDK.dll P/Invoke 门面）。
///
/// 只暴露账户池需要的窄操作：初始化/登录/登出、发送 C2C 文本、
/// 接收回调与网络状态。DLL 未加载时所有操作诚实失败，绝不伪报已就绪。
/// 全局登出由账户池独占调用，Driver 不得直接接触本类型。
/// </summary>
public sealed class TencentImNativeClient : ITencentImNativeClient
{
    private const string DllName = "ImSDK.dll";
    private const int TimConvC2C = 1;          // kTIMConv_C2C
    private const int TimErrSucc = 0;          // ERR_SUCC
    private const int TimLoginStatusLogined = 1;
    private const int KickedOfflineNetworkStatus = 6; // TIMNetworkStatus 中的断开/被踢标识按 SDK 定义展开

    private static readonly TimeSpan NativeCallbackTimeout = TimeSpan.FromSeconds(15);

    private readonly string? _resolvedDllPath;
    private bool _isLoggedIn;

    public TencentImNativeClient(string? sdkDirectory = null)
    {
        _resolvedDllPath = ResolveSdkDll(sdkDirectory);
    }

    public bool IsLoggedIn => _isLoggedIn;

    public event Action<ImReceivedMessage>? MessageReceived;

    public event Action<ImNativeNetworkStatus>? NetworkStatusChanged;

    public async Task InitializeAndLoginAsync(
        ImAccountKey identity,
        string userSig,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSig);

        EnsureSdkAvailable();
        cancellationToken.ThrowIfCancellationRequested();

        var config = JsonSerializer.Serialize(new { is_logger_console_off = true });
        var initCode = TimInit((ulong)identity.SdkAppId, config);
        if (initCode != TimErrSucc)
        {
            throw new InvalidOperationException($"TIMInit 失败（code={initCode}）。");
        }

        try
        {
            var completion = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var callback = new TimCommCallback((code, desc, json, data) =>
                completion.TrySetResult(code));
            // 保持委托存活：回调可能在方法返回后才触发。
            _loginCallback = callback;
            var loginCode = TimLogin(identity.LoginUserId, userSig, callback, IntPtr.Zero);
            if (loginCode != TimErrSucc)
            {
                throw new InvalidOperationException($"TIMLogin 调用失败（code={loginCode}）。");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(NativeCallbackTimeout);
            var resultCode = await completion.Task.WaitAsync(timeoutCts.Token);
            if (resultCode != TimErrSucc)
            {
                throw new InvalidOperationException($"TIMLogin 登录失败（code={resultCode}）。");
            }

            // 登录成功后注册消息与网络状态回调；委托字段保持存活。
            _recvCallback = new TimRecvNewMsgCallback((jsonArray, _) =>
                OnMessageReceived(Marshal.PtrToStringUTF8(jsonArray)));
            _networkCallback = new TimNetworkStatusListenerCallback((status, code, desc, _) =>
                OnNetworkStatusChanged(status));
            TimAddRecvNewMsgCallback(_recvCallback, IntPtr.Zero);
            TimSetNetworkStatusListenerCallback(_networkCallback, IntPtr.Zero);

            _isLoggedIn = true;
        }
        catch
        {
            TimUninit();
            _isLoggedIn = false;
            throw;
        }
    }

    public async Task<ImSendResult> SendC2CTextAsync(
        string targetUserId,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        EnsureSdkAvailable();
        cancellationToken.ThrowIfCancellationRequested();

        var messageJson = JsonSerializer.Serialize(new
        {
            message_elem_array = new[]
            {
                new
                {
                    elem_type = 0,              // kTIMElem_Text
                    text_elem_content = text
                }
            }
        });

        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TimCommCallback((code, desc, json, data) =>
            completion.TrySetResult(code));
        _sendCallback = callback;
        var callCode = TimMsgSendNewMsg(targetUserId, TimConvC2C, messageJson, callback, IntPtr.Zero);
        if (callCode != TimErrSucc)
        {
            return new ImSendResult(ImSendStatus.Failed, $"TIM 发送调用失败（code={callCode}）。");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(NativeCallbackTimeout);
            var resultCode = await completion.Task.WaitAsync(timeoutCts.Token);
            return resultCode == TimErrSucc
                ? new ImSendResult(ImSendStatus.AcceptedByTransport, null)
                : new ImSendResult(ImSendStatus.Failed, $"TIM 发送失败（code={resultCode}）。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 发送超时：结果未知，是否重发由 Dispatcher 依据交付语义决定。
            return new ImSendResult(ImSendStatus.OutcomeUnknown, "发送超时");
        }
    }

    public Task LogoutAsync(CancellationToken cancellationToken)
    {
        EnsureSdkAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isLoggedIn)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TimCommCallback((code, desc, json, data) =>
            completion.TrySetResult(code));
        _logoutCallback = callback;
        var code = TimLogout(callback, IntPtr.Zero);
        _isLoggedIn = false;
        if (code != TimErrSucc)
        {
            return Task.CompletedTask; // 登出失败不阻止释放，SDK 自身会清理。
        }

        return completion.Task.WaitAsync(NativeCallbackTimeout);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isLoggedIn)
        {
            try
            {
                await LogoutAsync(CancellationToken.None);
            }
            catch
            {
                // 释放阶段不掩盖调用方流程。
            }
        }

        if (_recvCallback is not null)
        {
            try
            {
                TimRemoveRecvNewMsgCallback(_recvCallback);
            }
            catch
            {
                // 同上。
            }
        }

        try
        {
            TimUninit();
        }
        catch
        {
            // 同上。
        }

        _isLoggedIn = false;
    }

    private void EnsureSdkAvailable()
    {
        if (_resolvedDllPath is null)
        {
            throw new InvalidOperationException(
                "原生 IM SDK（ImSDK.dll）未加载，无法执行 IM 操作。请放置 SDK 后重试。");
        }
    }

    private void OnMessageReceived(string? jsonArray)
    {
        // 回调在 SDK 线程触发；解析出 text 后广播给账户池。
        if (string.IsNullOrEmpty(jsonArray))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonArray);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var sender = GetString(item, "sender");
                if (string.IsNullOrWhiteSpace(sender))
                {
                    continue;
                }

                // 文本元素：elem_type=0 时 text_elem_content 为文本。
                var text = ExtractText(item);
                if (text is null)
                {
                    continue;
                }

                MessageReceived?.Invoke(new ImReceivedMessage(sender, text));
            }
        }
        catch (JsonException)
        {
            // 无法解析的回调不阻断其他消息。
        }
    }

    private static string? ExtractText(JsonElement message)
    {
        if (message.TryGetProperty("elem_array", out var elements) ||
            message.TryGetProperty("message_elem_array", out elements))
        {
            foreach (var element in elements.EnumerateArray())
            {
                if (element.TryGetProperty("elem_type", out var type) &&
                    type.ValueKind == JsonValueKind.Number &&
                    type.GetInt32() == 0 &&
                    element.TryGetProperty("text_elem_content", out var text))
                {
                    return text.GetString();
                }
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value))
        {
            return value.GetString();
        }

        return null;
    }

    private void OnNetworkStatusChanged(int status)
    {
        // 按 SDK TIMNetworkStatus 枚举映射：0 已连接、1 断开、2 连接中、3 连接失败。
        NetworkStatusChanged?.Invoke(status switch
        {
            0 => ImNativeNetworkStatus.Connected,
            2 => ImNativeNetworkStatus.Connecting,
            _ => ImNativeNetworkStatus.Disconnected
        });
    }

    private static string? ResolveSdkDll(string? sdkDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(sdkDirectory))
        {
            candidates.Add(Path.Combine(sdkDirectory, DllName));
            candidates.Add(Path.Combine(sdkDirectory, "Win64", DllName));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, DllName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Win64", DllName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "..", "shared_lib", "Win64", DllName));

        return candidates.FirstOrDefault(File.Exists);
    }

    // ---- P/Invoke 面（ImSDK C API）----

    private TimCommCallback? _loginCallback;
    private TimCommCallback? _sendCallback;
    private TimCommCallback? _logoutCallback;
    private TimRecvNewMsgCallback? _recvCallback;
    private TimNetworkStatusListenerCallback? _networkCallback;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TimCommCallback(
        int code,
        IntPtr desc,
        IntPtr jsonParam,
        IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TimRecvNewMsgCallback(IntPtr jsonMsgArray, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TimNetworkStatusListenerCallback(
        int status,
        int code,
        IntPtr desc,
        IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "TIMInit")]
    private static extern int TimInit(
        ulong sdkAppId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string jsonSdkConfig);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "TIMUninit")]
    private static extern int TimUninit();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "TIMLogin")]
    private static extern int TimLogin(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string userId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string userSig,
        TimCommCallback cb,
        IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "TIMLogout")]
    private static extern int TimLogout(TimCommCallback cb, IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "TIMMsgSendNewMsg")]
    private static extern int TimMsgSendNewMsg(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string convId,
        int convType,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string jsonMsgParam,
        TimCommCallback cb,
        IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "TIMAddRecvNewMsgCallback")]
    private static extern void TimAddRecvNewMsgCallback(TimRecvNewMsgCallback cb, IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "TIMRemoveRecvNewMsgCallback")]
    private static extern void TimRemoveRecvNewMsgCallback(TimRecvNewMsgCallback cb);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "TIMSetNetworkStatusListenerCallback")]
    private static extern void TimSetNetworkStatusListenerCallback(
        TimNetworkStatusListenerCallback cb,
        IntPtr userData);
}
