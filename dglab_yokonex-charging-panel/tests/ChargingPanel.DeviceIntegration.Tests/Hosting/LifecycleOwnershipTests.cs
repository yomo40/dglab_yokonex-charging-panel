using System.Reflection;
using ChargingPanel.Desktop;
using ChargingPanel.Desktop.Bootstrap;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Hosting;

/// <summary>
/// 生命周期归属测试。
///
/// UI 层不得持有生命周期并在 UI 线程上阻塞等待它：
/// Avalonia 在 UI 线程安装了 SynchronizationContext，启动链中的真实异步
/// I/O（SQLite 加载、服务端绑定）完成后续体会被 post 回 UI 线程；
/// 若此时 UI 线程正阻塞在 GetAwaiter().GetResult()，续体永远得不到执行——死锁。
/// 因此生命周期必须由进程入口拥有，在 Avalonia 同步上下文之外启动与停止。
/// </summary>
public sealed class LifecycleOwnershipTests
{
    [Fact]
    public void App_Type_DoesNotOwnApplicationLifecycle()
    {
        var lifecycleFields = typeof(App)
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(DesktopApplicationLifecycle))
            .ToArray();

        Assert.Empty(lifecycleFields);
    }

    [Fact]
    public void Program_Type_OwnsApplicationLifecycle()
    {
        var programType = typeof(App).Assembly.GetType("ChargingPanel.Desktop.Program", throwOnError: true)!;

        // 入口负责创建与停止：该线程没有 Avalonia 同步上下文，阻塞等待是安全的。
        var runMethod = programType.GetMethod(
            "RunWithLifecycle",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(runMethod);
    }
}
