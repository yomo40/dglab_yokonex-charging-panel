using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Events;
using Serilog;

namespace ChargingPanel.Core.Devices.DGLab;

/// <summary>
/// DG-LAB 配件类型（暂无相关协议，预留）
/// </summary>
public enum DGLabAccessoryType
{
    /// <summary>V3 无线传感器（47L120100）</summary>
    WirelessSensor47L120100,
    /// <summary>爪印按钮外部电压配件</summary>
    PawPrintsExternalVoltage
}

/// <summary>
/// DG-LAB 配件设备接口
/// </summary>
public interface IDGLabAccessoryDevice : IDevice
{
    DGLabAccessoryType AccessoryType { get; }
}

/// <summary>
/// DG-LAB 外部电压传感接口
/// </summary>
public interface IDGLabExternalVoltageSensorDevice : IDGLabAccessoryDevice
{
    double LastVoltage { get; }
    event EventHandler<double>? ExternalVoltageChanged;
}

/// <summary>
/// DG-LAB 配件占位适配器
/// 厂商未开放完整协议
/// </summary>
public sealed class DGLabAccessoryPlaceholderAdapter : IDGLabExternalVoltageSensorDevice
{
    private static readonly ILogger Logger = Log.ForContext<DGLabAccessoryPlaceholderAdapter>();

    public string Id { get; }
    public string Name { get; set; }
    public DeviceType Type => DeviceType.DGLab;
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public DeviceState State => new()
    {
        Status = Status,
        Strength = new StrengthInfo(),
        LastUpdate = DateTime.UtcNow
    };
    public ConnectionConfig? Config { get; private set; }
    public DGLabAccessoryType AccessoryType { get; }
    public double LastVoltage { get; private set; }

#pragma warning disable CS0067 // 配件占位适配器保留接口事件
    public event EventHandler<DeviceStatus>? StatusChanged;
    public event EventHandler<StrengthInfo>? StrengthChanged;
    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler<double>? ExternalVoltageChanged;
#pragma warning restore CS0067

    public DGLabAccessoryPlaceholderAdapter(
        DGLabAccessoryType accessoryType,
        string? id = null,
        string? name = null)
    {
        AccessoryType = accessoryType;
        Id = id ?? $"dglab_acc_{Guid.NewGuid():N}"[..20];
        Name = name ?? accessoryType switch
        {
            DGLabAccessoryType.WirelessSensor47L120100 => "DG-LAB 无线传感器（预留）",
            DGLabAccessoryType.PawPrintsExternalVoltage => "DG-LAB 爪印电压传感器（预留）",
            _ => "DG-LAB 配件（预留）"
        };
    }

    public Task ConnectAsync(ConnectionConfig config, CancellationToken cancellationToken = default)
    {
        Config = config;
        UpdateStatus(DeviceStatus.Connected);
        Logger.Warning("DG-LAB 配件 {Accessory} 当前为占位适配器，协议待补齐", AccessoryType);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        UpdateStatus(DeviceStatus.Disconnected);
        return Task.CompletedTask;
    }

    public Task SetStrengthAsync(Channel channel, int value, StrengthMode mode = StrengthMode.Set)
    {
        return Task.CompletedTask;
    }

    public Task SendWaveformAsync(Channel channel, WaveformData waveform)
    {
        return Task.CompletedTask;
    }

    public Task ClearWaveformQueueAsync(Channel channel)
    {
        return Task.CompletedTask;
    }

    public Task SetLimitsAsync(int limitA, int limitB)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 占位输入：用于后续硬件协议接入前联调事件链路。
    /// </summary>
    public void InjectExternalVoltage(double voltage, string? source = null)
    {
        LastVoltage = voltage;
        ExternalVoltageChanged?.Invoke(this, voltage);

        EventBus.Instance.PublishGameEvent(new GameEvent
        {
            Type = GameEventType.ExternalVoltageChanged,
            EventId = "external-voltage-changed",
            Source = source ?? "DGLabAccessoryPlaceholder",
            TargetDeviceId = Id,
            OldValue = 0,
            NewValue = (int)Math.Round(voltage * 1000),
            Data = new Dictionary<string, object>
            {
                ["voltage"] = voltage
            }
        });
    }

    private void UpdateStatus(DeviceStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
