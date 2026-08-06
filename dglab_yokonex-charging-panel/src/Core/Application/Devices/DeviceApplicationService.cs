using System.Text.Json;
using ChargingPanel.Contracts.Capabilities;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;
using ChargingPanel.Core.Devices.Runtime;
using ChargingPanel.Core.Devices.Safety;

namespace ChargingPanel.Core.Application.Devices;

/// <summary>
/// 设备应用服务。把厂商中立的业务请求翻译为命令信封，
/// 并交由 CommandDispatcher 统一校验与串行执行。
/// </summary>
public sealed class DeviceApplicationService : IDeviceApplicationService
{
    private static readonly CapabilityId IntensityCapability = new("intensity.output");
    private const string SetLevelOperation = "set.level";

    private readonly CommandDispatcher _dispatcher;
    private readonly SafetySupervisor _safety;
    private readonly TimeProvider _timeProvider;

    public DeviceApplicationService(
        CommandDispatcher dispatcher,
        SafetySupervisor safety,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dispatcher = dispatcher;
        _safety = safety;
        _timeProvider = timeProvider;
    }

    public long CurrentSafetyEpoch => _safety.Current.SafetyEpoch;

    public async Task<DeviceCommandResult> SetIntensityAsync(
        SetIntensityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var commandId = CommandId.New();

        // 安全锁闭优先于一切业务校验：锁闭期间不应暴露任何其他拒绝原因。
        if (_safety.Current.IsLatched)
        {
            return new DeviceCommandResult(
                commandId,
                DeviceCommandStatus.Rejected,
                SafetyRejection.LatchedReasonCode,
                SafetyRejection.LatchedMessage);
        }

        var binding = _dispatcher.ResolveBinding(request.DeviceInstanceId);
        if (binding is null)
        {
            return new DeviceCommandResult(
                commandId,
                DeviceCommandStatus.Rejected,
                "instance.unavailable",
                "设备实例不存在或没有活动 Session。");
        }

        var capability = CommandValidationPipeline.FindCapability(binding, IntensityCapability);
        if (capability is null)
        {
            return new DeviceCommandResult(
                commandId,
                DeviceCommandStatus.Rejected,
                "capability.unsupported",
                "设备未声明强度输出能力。");
        }

        var validation = CommandValidationPipeline.ValidateActuatorValue(
            capability,
            request.ActuatorId,
            request.Value);
        if (!validation.IsValid)
        {
            return new DeviceCommandResult(
                commandId,
                DeviceCommandStatus.Rejected,
                validation.ReasonCode,
                validation.Message);
        }

        using var payload = JsonDocument.Parse(
            $"{{\"actuatorId\":\"{request.ActuatorId}\",\"level\":{request.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");

        var spec = new CapabilityCommandRequest(
            request.DeviceInstanceId,
            IntensityCapability,
            SetLevelOperation,
            payload.RootElement.Clone(),
            request.Source,
            capability.CommandPolicy.Delivery);

        return await ExecuteCapabilityAsync(spec, cancellationToken);
    }

    public Task<DeviceCommandResult> DispatchAsync(
        DeviceCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.DispatchAsync(command, cancellationToken);
    }

    public async Task<DeviceCommandResult> ExecuteCapabilityAsync(
        CapabilityCommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 安全锁闭优先于一切业务校验：锁闭期间不应暴露任何其他拒绝原因。
        if (_safety.Current.IsLatched)
        {
            return new DeviceCommandResult(
                CommandId.New(),
                DeviceCommandStatus.Rejected,
                SafetyRejection.LatchedReasonCode,
                SafetyRejection.LatchedMessage);
        }

        var binding = _dispatcher.ResolveBinding(request.DeviceInstanceId);
        if (binding is null)
        {
            return new DeviceCommandResult(
                CommandId.New(),
                DeviceCommandStatus.Rejected,
                "instance.unavailable",
                "设备实例不存在或没有活动 Session。");
        }

        var capability = binding.Capabilities
            .FirstOrDefault(item => item.Id == request.CapabilityId);
        if (capability is null)
        {
            return new DeviceCommandResult(
                CommandId.New(),
                DeviceCommandStatus.Rejected,
                "capability.unsupported",
                "设备未声明该能力。");
        }

        var now = _timeProvider.GetUtcNow();
        var timeout = request.Timeout ?? capability.CommandPolicy.Timeout;
        var envelope = new DeviceCommandEnvelope(
            CommandId.New(),
            request.DeviceInstanceId,
            request.CapabilityId,
            request.OperationId,
            request.Payload.Clone(),
            request.Source,
            request.Delivery,
            now,
            now + timeout,
            _safety.Current.SafetyEpoch,
            binding.SessionEpoch);

        return await _dispatcher.DispatchAsync(envelope, cancellationToken);
    }

    public Task<EmergencyStopReport> EmergencyStopAsync(string reason, CancellationToken cancellationToken) =>
        _safety.EmergencyStopAllAsync(reason, cancellationToken);

    public Task<bool> ResetSafetyAsync(SafetyResetRequest request, CancellationToken cancellationToken) =>
        _safety.ResetAsync(request, cancellationToken);
}
