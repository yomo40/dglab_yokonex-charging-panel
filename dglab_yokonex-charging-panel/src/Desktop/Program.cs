using Avalonia;
using ChargingPanel.Desktop.Bootstrap;
using Serilog;
using System;

namespace ChargingPanel.Desktop;

class Program
{
    /// <summary>
    /// 应用程序入口点
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // 配置日志
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Log.Information("郊狼&役次元游戏适配面板 启动中...");
            RunWithLifecycle(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "应用程序崩溃");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// 进程入口拥有应用生命周期。
    ///
    /// 必须在此处启动与停止，而不是在 App/窗口内：Avalonia 会在 UI 线程安装
    /// SynchronizationContext，启动链里的真实异步 I/O（SQLite 加载、服务端绑定）
    /// 完成后续体会被 post 回 UI 线程；若那时 UI 线程正阻塞等待启动完成，
    /// 续体永远得不到执行，进程直接死锁。
    /// 入口线程没有该同步上下文，因此同步等待是安全的。
    /// </summary>
    internal static void RunWithLifecycle(string[] args)
    {
        var lifecycle = DesktopApplicationLifecycle.Create(new ApplicationHostOptions());

        // 架构要求：核心服务启动完成后才创建主窗口。
        lifecycle.StartAsync().GetAwaiter().GetResult();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Avalonia 消息循环已退出，此处同样没有 UI 同步上下文。
            // 关闭失败只记录，不覆盖上面可能正在传播的原始异常。
            try
            {
                lifecycle.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "应用关闭期间停止服务失败");
            }
        }
    }

    /// <summary>
    /// 构建 Avalonia 应用
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
