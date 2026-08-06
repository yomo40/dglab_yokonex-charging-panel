using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Devices.Sessions;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// Endpoint 选择测试：只决定连接候选，绝不把不同 Endpoint 合并为同一实例。
/// </summary>
public sealed class EndpointSelectorTests
{
    [Fact]
    public void SelectCandidates_DisabledEndpoint_IsNeverSelected()
    {
        var selector = new EndpointSelector(TimeProvider.System);
        var disabled = Candidate("a", TransportKind.BluetoothLowEnergy, priority: 0, enabled: false);
        var enabled = Candidate("b", TransportKind.BluetoothLowEnergy, priority: 5, enabled: true);

        var result = selector.SelectCandidates([disabled, enabled]);

        Assert.Equal([enabled.EndpointId], result.Select(item => item.EndpointId));
    }

    [Fact]
    public void SelectCandidates_LowerPriorityValue_ComesFirst()
    {
        var selector = new EndpointSelector(TimeProvider.System);
        var low = Candidate("a", TransportKind.BluetoothLowEnergy, priority: 9);
        var high = Candidate("b", TransportKind.BluetoothLowEnergy, priority: 1);

        var result = selector.SelectCandidates([low, high]);

        Assert.Equal([high.EndpointId, low.EndpointId], result.Select(item => item.EndpointId));
    }

    [Fact]
    public void SelectCandidates_UnsupportedTransport_IsExcluded()
    {
        var selector = new EndpointSelector(TimeProvider.System, [TransportKind.BluetoothLowEnergy]);
        var ble = Candidate("a", TransportKind.BluetoothLowEnergy, priority: 1);
        var im = Candidate("b", TransportKind.InstantMessaging, priority: 0);

        var result = selector.SelectCandidates([ble, im]);

        // 平台不支持的传输不得成为候选，即便用户优先级更高。
        Assert.Equal([ble.EndpointId], result.Select(item => item.EndpointId));
    }

    [Fact]
    public void SelectCandidates_RecentFailure_IsDeprioritizedButNotDropped()
    {
        var selector = new EndpointSelector(TimeProvider.System);
        var healthy = Candidate("a", TransportKind.BluetoothLowEnergy, priority: 5);
        var failed = Candidate("b", TransportKind.BluetoothLowEnergy, priority: 1);
        selector.RecordFailure(failed.EndpointId);

        var result = selector.SelectCandidates([healthy, failed]);

        // 近期失败的 Endpoint 降级但仍保留为候选，避免唯一通路被永久排除。
        Assert.Equal([healthy.EndpointId, failed.EndpointId], result.Select(item => item.EndpointId));
    }

    [Fact]
    public void SelectCandidates_AllDisabled_ReturnsEmptyWithoutThrowing()
    {
        var selector = new EndpointSelector(TimeProvider.System);
        var only = Candidate("a", TransportKind.BluetoothLowEnergy, enabled: false);

        var result = selector.SelectCandidates([only]);

        Assert.Empty(result);
    }

    private static EndpointCandidate Candidate(
        string seed,
        TransportKind transportKind,
        int priority = 0,
        bool enabled = true) =>
        new(
            EndpointId.New(),
            new ProtocolProfileId($"dglab.coyote.v3.{seed}"),
            transportKind,
            priority,
            enabled);
}
