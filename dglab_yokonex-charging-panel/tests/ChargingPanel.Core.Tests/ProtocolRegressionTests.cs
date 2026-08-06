using ChargingPanel.Contracts.Commands;
using ChargingPanel.Core.Application.Devices;
using ChargingPanel.Core.Automation;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Protocols;
using Xunit;

namespace ChargingPanel.Core.Tests;

/// <summary>
/// 协议回归测试 — 验证动作 → 命令信封的映射输出与预期一致。
/// 旧翻译器链已删除；这些测试改为断言 <see cref="DeviceActionMapperRegistry"/> 的映射结果。
/// </summary>
public class ProtocolRegressionTests
{
    private static readonly ChargingPanel.Contracts.Capabilities.CapabilityDescriptor DglabIntensity =
        Capabilities.BuildDglabIntensity();

    private static ActiveDeviceView View() =>
        new(Contracts.Identifiers.DeviceInstanceId.New(), 1, new[] { DglabIntensity });

    private static IReadOnlyList<DeviceCommandEnvelope> Map(DeviceActionRequest request) =>
        new DeviceActionMapperRegistry().Map(
            request,
            View(),
            new CommandSource(CommandSourceKind.Automation, "test"),
            0,
            TimeProvider.System);

    [Fact]
    public void GenericActionTranslator_ShouldHandleSetAction()
    {
        var request = new DeviceActionRequest
        {
            EventId = "test",
            ActionType = "set",
            Value = 50,
            Channels = new[] { Channel.A }
        };

        var envelope = Assert.Single(Map(request));
        Assert.Equal("intensity.output", envelope.CapabilityId.Value);
        Assert.Equal("set.level", envelope.OperationId);
        Assert.Contains("\"channelA\":100", envelope.Payload.GetRawText());
    }

    [Fact]
    public void GenericActionTranslator_ShouldHandleIncreaseAction()
    {
        var request = new DeviceActionRequest
        {
            EventId = "test",
            ActionType = "increase",
            Value = 20,
            Channels = new[] { Channel.A }
        };

        var envelope = Assert.Single(Map(request));
        Assert.Equal("intensity.output", envelope.CapabilityId.Value);
        Assert.Equal("adjust.level", envelope.OperationId);
        Assert.Contains("\"channelA\":40", envelope.Payload.GetRawText());
    }

    [Fact]
    public void GenericActionTranslator_ShouldHandleDecreaseAction()
    {
        var request = new DeviceActionRequest
        {
            EventId = "test",
            ActionType = "decrease",
            Value = 30,
            Channels = new[] { Channel.A }
        };

        var envelope = Assert.Single(Map(request));
        Assert.Equal("intensity.output", envelope.CapabilityId.Value);
        Assert.Equal("adjust.level", envelope.OperationId);
        Assert.Contains("\"channelA\":-60", envelope.Payload.GetRawText());
    }

    [Fact]
    public void ActionSemantic_ShouldNormalizeAliases()
    {
        // 验证动作别名规范化
        Assert.Equal("increase", ActionSemantic.Normalize("inc"));
        Assert.Equal("increase", ActionSemantic.Normalize("increase"));
        Assert.Equal("decrease", ActionSemantic.Normalize("dec"));
        Assert.Equal("decrease", ActionSemantic.Normalize("decrease"));
        Assert.Equal("set", ActionSemantic.Normalize("set"));
        Assert.Equal("wave", ActionSemantic.Normalize("wave"));
        Assert.Equal("wave", ActionSemantic.Normalize("waveform"));
    }

    [Fact]
    public void WaveformPresetExchangeService_ShouldRoundTrip()
    {
        var preset = new WaveformPresetRecord
        {
            Id = "test_preset",
            Name = "测试预设",
            Channel = "A",
            WaveformData = "AABBCCDDEEFF0011", // 16 字符 HEX 格式
            Duration = 1000,
            Intensity = 50,
            SortOrder = 0
        };

        var json = WaveformPresetExchangeService.ExportPackage(new[] { preset });
        var success = WaveformPresetExchangeService.TryImportPackage(json, out var imported, out var error);

        Assert.True(success, $"导入失败: {error}");
        Assert.Single(imported);
        Assert.Equal(preset.Name, imported[0].Name);
        Assert.Equal(preset.Duration, imported[0].Duration);
        Assert.Equal(preset.Intensity, imported[0].Intensity);
    }
}
