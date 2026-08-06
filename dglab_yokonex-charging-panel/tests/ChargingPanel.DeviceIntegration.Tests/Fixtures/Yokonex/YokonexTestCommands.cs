using System.Text.Json;
using ChargingPanel.Contracts.Commands;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex;

/// <summary>构造 YOKONEX 驱动测试用命令信封。</summary>
internal static class YokonexTestCommands
{
    public static readonly DeviceInstanceId InstanceId =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    public static DeviceCommandEnvelope Create(
        string capabilityId,
        string operationId,
        string payloadJson,
        CommandDelivery delivery = CommandDelivery.AtMostOnce)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return new DeviceCommandEnvelope(
            CommandId.New(),
            InstanceId,
            CapabilityId.Parse(capabilityId),
            operationId,
            document.RootElement.Clone(),
            new CommandSource(CommandSourceKind.UserInterface, "test-user"),
            delivery,
            DateTimeOffset.UtcNow,
            null,
            1,
            1);
    }
}
