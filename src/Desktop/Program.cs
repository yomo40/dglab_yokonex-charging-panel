using Avalonia;
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
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
    /// 构建 Avalonia 应用
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
