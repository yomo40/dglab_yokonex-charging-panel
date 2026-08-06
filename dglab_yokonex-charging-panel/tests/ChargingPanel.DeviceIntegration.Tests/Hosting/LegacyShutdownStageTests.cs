using ChargingPanel.Desktop.Bootstrap;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Hosting;

/// <summary>
/// 分阶段关闭顺序测试。
///
/// 关键点：外部接单（MOD 桥接/房间发现）必须先停止，
/// 存储与日志必须最后执行，否则后续阶段（安全锁闭、Session、连接池）
/// 的失败就再也无法被记录——关闭故障会静默消失。
/// Task 22 后业务服务同样作为 ExternalIntake 参与者纳入该顺序。
/// </summary>
public sealed class LegacyShutdownStageTests
{
    [Fact]
    public async Task AddParticipant_ExternalIntakeBeforeSessionsBeforeStorage_RunInStagedOrder()
    {
        var order = new List<string>();
        var coordinator = new ApplicationLifetimeCoordinator([], TimeProvider.System);

        // 模拟外部接单（停止对外服务）与最后的存储/日志关闭。
        coordinator.AddParticipant(new DelegateShutdownParticipant(
            ApplicationShutdownStage.StorageAndLogging,
            _ =>
            {
                order.Add("storage-dispose");
                return Task.CompletedTask;
            }));
        coordinator.AddParticipant(new DelegateShutdownParticipant(
            ApplicationShutdownStage.ExternalIntake,
            _ =>
            {
                order.Add("external-intake-stop");
                return Task.CompletedTask;
            }));
        coordinator.AddParticipant(new DelegateShutdownParticipant(
            ApplicationShutdownStage.Sessions,
            _ =>
            {
                order.Add("sessions");
                return Task.CompletedTask;
            }));

        var result = await coordinator.StopAsync();

        // 注册顺序与阶段顺序无关：存储日志必须最后执行，
        // 否则 Session 阶段的失败已无处记录。
        Assert.Equal(["external-intake-stop", "sessions", "storage-dispose"], order);
        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public async Task AddParticipant_AfterStop_IsRejected()
    {
        var coordinator = new ApplicationLifetimeCoordinator([], TimeProvider.System);
        await coordinator.StopAsync();

        // 关闭已完成后再注册会永不被执行，静默忽略等于资源泄漏。
        Assert.Throws<InvalidOperationException>(() => coordinator.AddParticipant(
            new DelegateShutdownParticipant(
                ApplicationShutdownStage.Sessions,
                _ => Task.CompletedTask)));
    }
}
