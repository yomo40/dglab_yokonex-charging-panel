using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Contracts.Telemetry;
using ChargingPanel.Core.Devices.State;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// SessionEpoch 测试：旧 Epoch、乱序与已关闭 Session 的遥测必须被丢弃，
/// 否则故障转移后会把陈旧状态当作当前设备状态展示。
/// </summary>
public sealed class SessionEpochTests
{
    private static readonly DeviceInstanceId Instance = DeviceInstanceId.New();
    private static readonly ProtocolProfileId Profile = new("dglab.coyote.v3.ble");
    private static readonly CapabilityId Capability = new("battery.telemetry");

    [Fact]
    public void Apply_StaleEpochTelemetry_IsDiscarded()
    {
        var store = new DeviceStateStore();
        store.OpenSession(Instance, sessionEpoch: 5);
        Assert.True(store.Apply(Telemetry(sessionEpoch: 5, sequence: 1, level: 80)));

        // 旧 Epoch 的遥测属于已被取代的连接，必须丢弃。
        var applied = store.Apply(Telemetry(sessionEpoch: 4, sequence: 99, level: 10));

        Assert.False(applied);
        Assert.Equal(80, ReadLevel(store));
    }

    [Fact]
    public void Apply_OutOfOrderSequenceInSameEpoch_IsDiscarded()
    {
        var store = new DeviceStateStore();
        store.OpenSession(Instance, sessionEpoch: 1);
        store.Apply(Telemetry(sessionEpoch: 1, sequence: 10, level: 70));

        var applied = store.Apply(Telemetry(sessionEpoch: 1, sequence: 9, level: 20));

        Assert.False(applied);
        Assert.Equal(70, ReadLevel(store));
    }

    [Fact]
    public void Apply_AfterSessionClosed_IsDiscarded()
    {
        var store = new DeviceStateStore();
        store.OpenSession(Instance, sessionEpoch: 2);
        store.Apply(Telemetry(sessionEpoch: 2, sequence: 1, level: 60));
        store.CloseSession(Instance);

        // Session 已关闭后到达的回调不得再更新状态。
        var applied = store.Apply(Telemetry(sessionEpoch: 2, sequence: 2, level: 5));

        Assert.False(applied);
    }

    [Fact]
    public void Apply_NewerEpochAfterFailover_ResetsSequenceGate()
    {
        var store = new DeviceStateStore();
        store.OpenSession(Instance, sessionEpoch: 1);
        store.Apply(Telemetry(sessionEpoch: 1, sequence: 100, level: 40));

        // 故障转移到新 Endpoint：Epoch 递增，序号从新连接重新开始。
        store.OpenSession(Instance, sessionEpoch: 2);
        var applied = store.Apply(Telemetry(sessionEpoch: 2, sequence: 1, level: 55));

        Assert.True(applied);
        Assert.Equal(55, ReadLevel(store));
    }

    [Fact]
    public void Snapshot_AfterFailover_DoesNotRetainPreviousEpochValues()
    {
        var store = new DeviceStateStore();
        store.OpenSession(Instance, sessionEpoch: 1);
        store.Apply(Telemetry(sessionEpoch: 1, sequence: 1, level: 90));

        store.OpenSession(Instance, sessionEpoch: 2);

        // 新 Session 不得继承旧连接的遥测值：否则 UI 会显示不属于当前连接的状态。
        var snapshot = store.GetSnapshot(Instance);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.SessionEpoch);
        Assert.Empty(snapshot.Telemetry);
    }

    private static DeviceTelemetryEnvelope Telemetry(long sessionEpoch, long sequence, int level)
    {
        using var payload = JsonDocument.Parse($"{{\"level\":{level}}}");
        return new DeviceTelemetryEnvelope(
            Instance,
            Profile,
            Capability,
            "1.0",
            payload.RootElement.Clone(),
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            sessionEpoch,
            sequence,
            TelemetryQuality.Good);
    }

    private static int ReadLevel(DeviceStateStore store)
    {
        var snapshot = store.GetSnapshot(Instance);
        Assert.NotNull(snapshot);
        var envelope = snapshot!.Telemetry[Capability];
        return envelope.Payload.GetProperty("level").GetInt32();
    }
}
