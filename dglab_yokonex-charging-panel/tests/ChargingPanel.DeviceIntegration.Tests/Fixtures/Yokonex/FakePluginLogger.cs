using System.Text.Json;
using ChargingPanel.Contracts.Plugins;

namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.Yokonex;

/// <summary>
/// 合成插件日志器。记录日志级别、事件名与消息；
/// 测试可用它断言"敏感字段不出现在日志中"。
/// </summary>
internal sealed class FakePluginLogger : IPluginLogger
{
    public List<(PluginLogLevel Level, string EventName, string Message)> Entries { get; } = [];

    public void Log(
        PluginLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, JsonElement>? properties = null)
    {
        Entries.Add((level, eventName, message));
    }

    public string AllText => string.Join(Environment.NewLine, Entries.Select(entry => $"{entry.EventName}:{entry.Message}"));
}
