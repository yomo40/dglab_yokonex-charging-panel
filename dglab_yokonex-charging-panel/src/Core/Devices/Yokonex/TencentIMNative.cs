using System;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace ChargingPanel.Core.Devices.Yokonex;

/// <summary>
/// 腾讯云 IM SDK 原生 P/Invoke 封装
/// 基于 ImSDK_Windows_8.7.7201 C 接口
/// </summary>
public static class TencentIMNative
{
    private static readonly ILogger Logger = Log.ForContext(typeof(TencentIMNative));
    
    private const string DllName = "ImSDK.dll";
    
    #region 枚举定义
    
    /// <summary>接口调用返回值</summary>
    public enum TIMResult
    {
        TIM_SUCC = 0,
        TIM_ERR_SDKUNINIT = -1,
        TIM_ERR_NOTLOGIN = -2,
        TIM_ERR_JSON = -3,
        TIM_ERR_PARAM = -4,
        TIM_ERR_CONV = -5,
        TIM_ERR_GROUP = -6,
    }
    
    /// <summary>会话类型</summary>
    public enum TIMConvType
    {
        kTIMConv_Invalid = 0,
        kTIMConv_C2C = 1,
        kTIMConv_Group = 2,
        kTIMConv_System = 3,
    }
    
    /// <summary>网络状态</summary>
    public enum TIMNetworkStatus
    {
        kTIMConnected = 0,
        kTIMDisconnected = 1,
        kTIMConnecting = 2,
        kTIMConnectFailed = 3,
    }
    
    /// <summary>登录状态</summary>
    public enum TIMLoginStatus
    {
        kTIMLoginStatus_Logined = 1,
        kTIMLoginStatus_Logining = 2,
        kTIMLoginStatus_UnLogined = 3,
        kTIMLoginStatus_Logouting = 4,
    }
    
    /// <summary>日志级别</summary>
    public enum TIMLogLevel
    {
        kTIMLog_Off = 0,
        kTIMLog_Test = 1,
        kTIMLog_Verbose = 2,
        kTIMLog_Debug = 3,
        kTIMLog_Info = 4,
        kTIMLog_Warn = 5,
        kTIMLog_Error = 6,
        kTIMLog_Assert = 7,
    }
    
    #endregion
    
    #region 回调委托定义
    
    /// <summary>通用回调</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TIMCommCallback(int code, IntPtr desc, IntPtr jsonParams, IntPtr userData);
    
    /// <summary>网络状态回调</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TIMNetworkStatusListenerCallback(TIMNetworkStatus status, int code, IntPtr desc, IntPtr userData);
    
    /// <summary>被踢下线回调</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TIMKickedOfflineCallback(IntPtr userData);
    
    /// <summary>票据过期回调</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TIMUserSigExpiredCallback(IntPtr userData);
    
    /// <summary>新消息回调</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TIMRecvNewMsgCallback(IntPtr jsonMsgArray, IntPtr userData);
    
    #endregion
    
    #region SDK 初始化 API
    
    /// <summary>初始化 SDK</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int TIMInit(ulong sdkAppId, string jsonSdkConfig);
    
    /// <summary>反初始化 SDK</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIMUninit();
    
    /// <summary>获取 SDK 版本号</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TIMGetSDKVersion();
    
    /// <summary>获取服务器时间</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong TIMGetServerTime();
    
    /// <summary>获取登录状态</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern TIMLoginStatus TIMGetLoginStatus();
    
    #endregion
    
    #region 登录/登出 API
    
    /// <summary>登录</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int TIMLogin(string userId, string userSig, TIMCommCallback cb, IntPtr userData);
    
    /// <summary>登出</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIMLogout(TIMCommCallback cb, IntPtr userData);
    
    /// <summary>获取登录用户 ID</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TIMGetLoginUserID(StringBuilder userIdBuffer);
    
    #endregion
    
    #region 消息 API
    
    /// <summary>发送消息</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int TIMMsgSendMessage(
        string convId, 
        TIMConvType convType, 
        string jsonMsgParam, 
        StringBuilder messageIdBuffer, 
        TIMCommCallback cb, 
        IntPtr userData);
    
    /// <summary>添加新消息回调</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TIMAddRecvNewMsgCallback(TIMRecvNewMsgCallback cb, IntPtr userData);
    
    /// <summary>移除新消息回调</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TIMRemoveRecvNewMsgCallback(TIMRecvNewMsgCallback cb);
    
    #endregion
    
    #region 回调设置 API
    
    /// <summary>设置网络状态回调</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TIMSetNetworkStatusListenerCallback(TIMNetworkStatusListenerCallback cb, IntPtr userData);
    
    /// <summary>设置被踢下线回调</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TIMSetKickedOfflineCallback(TIMKickedOfflineCallback cb, IntPtr userData);
    
    /// <summary>设置票据过期回调</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TIMSetUserSigExpiredCallback(TIMUserSigExpiredCallback cb, IntPtr userData);
    
    #endregion
    
    #region 辅助方法
    
    /// <summary>从 IntPtr 读取 UTF-8 字符串</summary>
    public static string? PtrToStringUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(ptr);
    }
    
    /// <summary>检查 SDK 是否可用</summary>
    public static bool IsSdkAvailable()
    {
        try
        {
            var version = TIMGetSDKVersion();
            return version != IntPtr.Zero;
        }
        catch (DllNotFoundException)
        {
            Logger.Warning("腾讯 IM SDK (ImSDK.dll) 未找到");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "检查腾讯 IM SDK 可用性失败");
            return false;
        }
    }
    
    #endregion
}
