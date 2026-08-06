using System.Collections.ObjectModel;
using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Application.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChargingPanel.Desktop.ViewModels.Devices;

/// <summary>
/// 设备控制视图模型。仅通过 <see cref="IDeviceApplicationService"/> 下发
/// 标准能力命令，并把结果如实渲染为 Executed / Rejected / OutcomeUnknown。
/// 绝不把"消息已发送"当作"设备已执行"。
/// </summary>
public sealed partial class DeviceControlViewModel : ObservableObject
{
    private const string SetOperationDefault = "set";

    private readonly IDeviceApplicationService _appService;
    private readonly IDeviceQueryService _query;

    public DeviceControlViewModel(IDeviceApplicationService appService, IDeviceQueryService query)
    {
        _appService = appService ?? throw new ArgumentNullException(nameof(appService));
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public ObservableCollection<DeviceControlSlot> Controls { get; } = new();

    [ObservableProperty]
    private string _deviceTitle = string.Empty;

    [ObservableProperty]
    private string _lastResultText = string.Empty;

    [ObservableProperty]
    private bool _hasActiveDevice;

    [ObservableProperty]
    private bool _lastCommandRejected;

    [ObservableProperty]
    private bool _lastCommandOutcomeUnknown;

    /// <summary>刷新活动设备列表，并重新生成选中设备的控制项。</summary>
    public void Refresh()
    {
        var active = _query.GetActiveDevices();
        if (active.Count == 0)
        {
            ClearControls();
            return;
        }

        var first = active[0];
        Load(first);
    }

    public void Load(ActiveDeviceView device)
    {
        ArgumentNullException.ThrowIfNull(device);
        DeviceTitle = ResolveDeviceTitle(device);
        HasActiveDevice = true;
        LastResultText = string.Empty;
        LastCommandRejected = false;
        LastCommandOutcomeUnknown = false;

        Controls.Clear();
        foreach (var capability in device.Capabilities)
        {
            AddControlsForCapability(capability, device.InstanceId);
        }
    }

    public void ClearControls()
    {
        DeviceTitle = string.Empty;
        HasActiveDevice = false;
        LastResultText = string.Empty;
        LastCommandRejected = false;
        LastCommandOutcomeUnknown = false;
        Controls.Clear();
    }

    /// <summary>
    /// 执行一个控制槽位的能力命令。结果按 DeviceCommandStatus 如实呈现，
    /// Rejected/OutcomeUnknown 绝不显示为"已执行"。
    /// </summary>
    public async Task<DeviceCommandResult> ExecuteAsync(
        DeviceControlSlot slot,
        double level,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);

        using var document = JsonDocument.Parse(slot.BuildPayloadJson(level));
        var request = new CapabilityCommandRequest(
            slot.DeviceInstanceId,
            slot.CapabilityId,
            slot.OperationId,
            document.RootElement.Clone(),
            new CommandSource(CommandSourceKind.UserInterface, "desktop.control"),
            slot.Delivery);

        var result = await _appService.ExecuteCapabilityAsync(request, cancellationToken);
        RenderResult(result);
        return result;
    }

    private void AddControlsForCapability(CapabilityDescriptor capability, DeviceInstanceId instanceId)
    {
        // 纯遥测能力没有可执行命令，跳过。
        if (string.Equals(capability.SafeStopPlan.OperationId, "none", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var actuator in capability.Actuators)
        {
            if (!string.Equals(actuator.Unit, "level", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var channel in actuator.Channels)
            {
                Controls.Add(new DeviceControlSlot(
                    capability,
                    actuator,
                    channel,
                    ResolveSetOperation(capability.Id.Value))
                {
                    DeviceInstanceId = instanceId
                });
            }
        }
    }

    private void RenderResult(DeviceCommandResult result)
    {
        LastCommandRejected = result.Status is DeviceCommandStatus.Rejected
            or DeviceCommandStatus.Expired
            or DeviceCommandStatus.Superseded
            or DeviceCommandStatus.TransportFailed;
        LastCommandOutcomeUnknown = result.Status == DeviceCommandStatus.OutcomeUnknown;

        LastResultText = result.Status switch
        {
            DeviceCommandStatus.Executed => "已执行",
            DeviceCommandStatus.AcceptedByTransport => "已送达设备，等待设备确认",
            DeviceCommandStatus.Rejected => $"已拒绝: {result.Message ?? result.ReasonCode ?? "未知原因"}",
            DeviceCommandStatus.Expired => $"已过期: {result.Message ?? result.ReasonCode ?? "未知原因"}",
            DeviceCommandStatus.Superseded => $"已被新命令取代: {result.Message ?? result.ReasonCode ?? "未知原因"}",
            DeviceCommandStatus.TransportFailed => $"传输失败: {result.Message ?? result.ReasonCode ?? "未知原因"}",
            DeviceCommandStatus.OutcomeUnknown => $"结果未知: {result.Message ?? result.ReasonCode ?? "设备未确认"}",
            _ => "未知状态"
        };
    }

    private static string ResolveDeviceTitle(ActiveDeviceView device)
    {
        var modelId = device.InstanceId.Value.ToString("D");
        return $"活动设备 {modelId}";
    }

    private static string ResolveSetOperation(string capabilityId) => capabilityId switch
    {
        "intensity.output" => "set.level",
        "waveform.stream" => "send",
        "toy.speed.control" => "set.speed",
        "toy.motor.control" => "set.mode",
        "ems.channel.output" => "set.output",
        "enema.pump.control" => "set.pump",
        _ => SetOperationDefault
    };
}

/// <summary>
/// 由能力描述生成的控制槽位。展示与下发所需的最小稳定 DTO，
/// 不包含任何厂商协议字节。
/// </summary>
public sealed class DeviceControlSlot
{
    public DeviceControlSlot(
        CapabilityDescriptor capability,
        CapabilityActuatorDescriptor actuator,
        CapabilityChannelDescriptor channel,
        string operationId)
    {
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
        Actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
        ChannelDescriptor = channel ?? throw new ArgumentNullException(nameof(channel));
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
    }

    public CapabilityDescriptor Capability { get; }

    public CapabilityActuatorDescriptor Actuator { get; }

    public CapabilityChannelDescriptor ChannelDescriptor { get; }

    public string OperationId { get; }

    public DeviceInstanceId DeviceInstanceId { get; init; }

    public CapabilityId CapabilityId => Capability.Id;

    public string Title => $"{Capability.DisplayName} · {ChannelDescriptor.DisplayName}";

    public NumericRange Range => Actuator.DefaultSafetyRange;

    public CommandDelivery Delivery => Capability.CommandPolicy.Delivery;

    /// <summary>构建厂商中立、版本化的 JSON 载荷。</summary>
    public string BuildPayloadJson(double level) =>
        $"{{\"actuatorId\":\"{Actuator.ActuatorId}\",\"channel\":\"{ChannelDescriptor.ChannelId}\",\"level\":{level.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
}
