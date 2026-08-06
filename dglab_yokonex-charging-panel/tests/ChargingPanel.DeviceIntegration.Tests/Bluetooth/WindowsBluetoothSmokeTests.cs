using ChargingPanel.Infrastructure.Windows.Bluetooth;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Bluetooth;

/// <summary>
/// Windows 平台 BLE smoke test。
///
/// 只验证真实 WinRT 后端的适配器枚举与扫描启动/取消，绝不连接用户设备。
/// 无蓝牙适配器时跳过（环境缺失视为通过），不把平台限制当成失败；
/// 有适配器时也只做无副作用的枚举与监听生命周期验证。
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class WindowsBluetoothSmokeTests
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task IsAdapterAvailableAsync_Smoke_DoesNotThrow()
    {
        // 适配器枚举路径的真实平台冒烟：有无适配器均可，
        // 关键是调用不得因 WinRT 封装错误而崩溃。
        var backend = new WindowsBluetoothBackend();

        _ = await backend.IsAdapterAvailableAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ScanStartAndCancel_Smoke_CompletesWithoutConnecting()
    {
        var backend = new WindowsBluetoothBackend();
        if (!await backend.IsAdapterAvailableAsync(CancellationToken.None))
        {
            // 无可用蓝牙适配器：环境缺失视为跳过，不把平台限制当成失败。
            return;
        }

        var received = new List<BluetoothAdvertisement>();
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scanLease = backend.StartWatcher(advertisement =>
        {
            lock (received)
            {
                received.Add(advertisement);
                if (received.Count == 1)
                {
                    firstSeen.TrySetResult();
                }
            }
        });

        // 有界观察窗口：等第一个广播或窗口耗尽，之后必须能干净取消。
        // 无论是否扫到设备，扫描都必须在不连接任何设备的前提下正常停止。
        // 蓝牙开着但没有设备在广播是正常环境，不是失败。
        try
        {
            await firstSeen.Task.WaitAsync(ObservationWindow);
        }
        catch (TimeoutException)
        {
            // 窗口内未收到广播：环境安静，视为通过。
        }

        scanLease.Dispose();

        // 若确实收到广播，每条都必须携带合法规范化地址；
        // RSSI 允许 -32768（部分无线电把未知信号强度上报为该值）。
        lock (received)
        {
            foreach (var advertisement in received)
            {
                Assert.Equal(12, BluetoothAddress.Normalize(advertisement.NormalizedAddress).Length);
                Assert.True(advertisement.Rssi is -32768 or >= -128 and <= 20);
            }
        }
    }
}
