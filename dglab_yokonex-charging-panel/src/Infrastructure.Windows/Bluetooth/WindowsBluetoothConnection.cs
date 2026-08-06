using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Foundation;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ChargingPanel.Infrastructure.Windows.Bluetooth;

/// <summary>
/// 单条物理 BLE 连接（WinRT 后端实现）。
///
/// 每个方法只做一次可取消 I/O：connect/discover/read/write/subscribe 各一次，
/// 不包含重连与语义重试——那是 SessionCoordinator 与 Dispatcher 的职责。
/// 关键不变量：WithoutResponse 写入失败时绝不自动改成 WithResponse 重发同一物理命令，
/// 否则一次电击或泵动作可能被执行两次。
/// </summary>
internal sealed class WindowsBluetoothConnection : IWindowsBluetoothConnection
{
    private static readonly TimeSpan GattOperationTimeout = TimeSpan.FromMilliseconds(5000);

    private readonly BluetoothLEDevice _device;
    private readonly IReadOnlyDictionary<Guid, GattDeviceService> _services;
    private readonly GattSession _session;
    private readonly ConcurrentDictionary<(Guid ServiceUuid, Guid CharacteristicUuid), GattCharacteristic> _characteristics = new();
    private readonly object _subscriptionGate = new();
    private readonly List<Subscription> _subscriptions = [];
    private bool _disposed;

    public WindowsBluetoothConnection(
        BluetoothLEDevice device,
        IReadOnlyDictionary<Guid, GattDeviceService> services,
        GattSession session)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public ValueTask WriteAsync(
        Guid serviceUuid,
        Guid characteristicUuid,
        ReadOnlyMemory<byte> payload,
        bool withResponse,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 一次写入，显式声明响应方式；失败原样上抛，绝不私自换方式重发。
        return CoreAsync();
        async ValueTask CoreAsync()
        {
            var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid, cancellationToken);
            var option = withResponse
                ? GattWriteOption.WriteWithResponse
                : GattWriteOption.WriteWithoutResponse;
            var status = await characteristic
                .WriteValueAsync(payload.ToArray().AsBuffer(), option)
                .AsTask()
                .WaitAsync(GattOperationTimeout, cancellationToken);
            if (status != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException($"GATT 写入失败（{status}），方式={(withResponse ? "WithResponse" : "WithoutResponse")}。");
            }
        }
    }

    public async ValueTask SubscribeAsync(
        Guid serviceUuid,
        Guid characteristicUuid,
        Action<byte[]> onNotified,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onNotified);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var characteristic = await GetCharacteristicAsync(serviceUuid, characteristicUuid, cancellationToken);

        // 先挂处理器再写 CCCD：设备可能在确认描述符写入的瞬间就开始上报，
        // 若在写入之后才挂接，会丢掉第一条通知（BLE 订阅时序的经典坑）。
        // 每个订阅一个闭包处理器，卸载时按订阅记录精确移除；
        // 同一特征被多个调用方订阅时各自独立投递。
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler =
            (sender, args) => OnNotified(sender, args, onNotified);
        lock (_subscriptionGate)
        {
            characteristic.ValueChanged += handler;
            _subscriptions.Add(new Subscription(characteristic, handler));
        }

        var descriptorValue = ChooseDescriptorValue(characteristic);
        var status = await characteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(descriptorValue)
            .AsTask()
            .WaitAsync(GattOperationTimeout, cancellationToken);
        if (status != GattCommunicationStatus.Success)
        {
            // 启用失败必须回滚订阅，否则会留下永不摘除的处理器。
            lock (_subscriptionGate)
            {
                _subscriptions.RemoveAll(subscription => ReferenceEquals(subscription.Handler, handler));
            }

            try
            {
                characteristic.ValueChanged -= handler;
            }
            catch
            {
                // 摘除失败不掩盖原始订阅失败。
            }

            throw new InvalidOperationException($"启用 GATT 通知失败（{status}）。");
        }
    }

    public ValueTask UnsubscribeAllAsync(CancellationToken cancellationToken)
    {
        List<Subscription> subscriptions;
        lock (_subscriptionGate)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        return CoreAsync();
        async ValueTask CoreAsync()
        {
            foreach (var subscription in subscriptions)
            {
                try
                {
                    subscription.Characteristic.ValueChanged -= subscription.Handler;
                }
                catch
                {
                    // 摘除失败不阻止其余订阅与整体释放。
                }
            }

            // 每个被订阅的特征只写一次关闭通知；单个失败不阻止其他特征关闭。
            foreach (var characteristic in subscriptions.Select(subscription => subscription.Characteristic).Distinct())
            {
                try
                {
                    await characteristic
                        .WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None)
                        .AsTask()
                        .WaitAsync(GattOperationTimeout, cancellationToken);
                }
                catch
                {
                    // 同上：不阻止整体取消订阅。
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            foreach (var service in _services.Values)
            {
                try
                {
                    service.Dispose();
                }
                catch
                {
                    // 单个服务释放失败不阻止设备释放。
                }
            }

            try
            {
                _session.Dispose();
            }
            catch
            {
                // 会话释放失败不阻止设备释放。
            }

            try
            {
                _device.Dispose();
            }
            catch
            {
                // 释放阶段不再上抛，避免掩盖调用方的正常流程。
            }
        }
        catch
        {
            // 兜底：任何未预期的释放异常都不掩盖调用方流程。
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<GattCharacteristic> GetCharacteristicAsync(
        Guid serviceUuid,
        Guid characteristicUuid,
        CancellationToken cancellationToken)
    {
        var key = (serviceUuid, characteristicUuid);
        if (_characteristics.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!_services.TryGetValue(serviceUuid, out var service))
        {
            throw new InvalidOperationException($"未发现 GATT 服务 {serviceUuid}。");
        }

        var result = await service
            .GetCharacteristicsForUuidAsync(characteristicUuid)
            .AsTask()
            .WaitAsync(GattOperationTimeout, cancellationToken);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            throw new InvalidOperationException($"未发现 GATT 特征 {characteristicUuid}（{result.Status}）。");
        }

        var characteristic = result.Characteristics[0];
        _characteristics[key] = characteristic;
        return characteristic;
    }

    private static GattClientCharacteristicConfigurationDescriptorValue ChooseDescriptorValue(
        GattCharacteristic characteristic) =>
        characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

    private static void OnNotified(
        GattCharacteristic sender,
        GattValueChangedEventArgs args,
        Action<byte[]> onNotified)
    {
        using var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var data = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        try
        {
            onNotified(data);
        }
        catch
        {
            // 回调异常不得终结 WinRT 通知线程。
        }
    }

    private sealed record Subscription(
        GattCharacteristic Characteristic,
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> Handler);
}
