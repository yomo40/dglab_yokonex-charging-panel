using System.Reflection;
using ChargingPanel.Desktop.Bootstrap;
using ChargingPanel.Desktop.Views;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Hosting;

/// <summary>
/// Desktop 生命周期归属测试。
///
/// 架构要求：核心服务启动完成后才创建主窗口，关闭时统一等待，
/// 禁止窗口构造器 fire-and-forget 初始化与重复 Dispose。
/// 因此窗口不得再声明任何服务启动/停止成员。
/// </summary>
public sealed class DesktopLifecycleWiringTests
{
    [Fact]
    public void MainWindow_Type_DoesNotDeclareServiceLifecycleMembers()
    {
        var declared = typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        // 这两个成员是旧的窗口自持生命周期路径：构造器 fire-and-forget 启动，
        // 关闭时 Task.Run + 超时后仍继续 Dispose，可能与 StopAsync 并发释放。
        Assert.DoesNotContain("InitializeServicesAsync", declared);
        Assert.DoesNotContain("BeginBestEffortCleanup", declared);
    }

    [Fact]
    public void DesktopApplicationLifecycle_ExposesAwaitableStartAndStop()
    {
        var type = typeof(DesktopApplicationLifecycle);

        var start = type.GetMethod(nameof(DesktopApplicationLifecycle.StartAsync));
        var stop = type.GetMethod(nameof(DesktopApplicationLifecycle.StopAsync));

        Assert.NotNull(start);
        Assert.NotNull(stop);
        // 必须可等待：返回 void 就无法在创建窗口前确保启动完成。
        Assert.True(typeof(Task).IsAssignableFrom(start!.ReturnType));
        Assert.True(typeof(Task).IsAssignableFrom(stop!.ReturnType));
    }
}
