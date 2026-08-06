using ChargingPanel.Contracts.Transport;
using ChargingPanel.Core.Devices.Discovery;

namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;

/// <summary>DG-LAB 设备发现广播构造助手。</summary>
public static class DGLabAdvertisements
{
    public static DiscoveryAdvertisement CoyoteV3Ble(
        string address = "AA:BB:CC:DD:EE:FF",
        string serviceUuids = "0000180c-0000-1000-8000-00805f9b34fb") =>
        new(
            TransportKind.BluetoothLowEnergy,
            "47L121000",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["address"] = address,
                ["serviceUuids"] = serviceUuids
            });

    public static DiscoveryAdvertisement CoyoteV3WebSocket(string target = "ws://127.0.0.1:39001") =>
        new(
            TransportKind.WebSocket,
            "郊狼 V3 桥接",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["serviceIdentity"] = "47L121000",
                ["target"] = target
            });

    public static DiscoveryAdvertisement NamedBle(
        string name,
        string address = "AA:BB:CC:DD:EE:FF",
        string serviceUuids = "0000180c-0000-1000-8000-00805f9b34fb") =>
        new(
            TransportKind.BluetoothLowEnergy,
            name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["address"] = address,
                ["serviceUuids"] = serviceUuids
            });
}