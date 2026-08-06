using System.Text.Json;
using ChargingPanel.Contracts.Plugins;
using ChargingPanel.Contracts.Transport;

namespace ChargingPanel.DeviceIntegration.Tests.Fixtures.DGLab;

/// <summary>测试用插件上下文：时间源可注入，其余存根。</summary>
public sealed class FakePluginContext : IPluginContext
{
    public FakePluginContext(TimeProvider? timeProvider = null)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public IPluginLogger Logger { get; } = new NullPluginLogger();

    public TimeProvider TimeProvider { get; }

    public IPluginStorage Storage { get; } = new MemoryPluginStorage();

    public IEndpointLeaseProvider Endpoints => throw new NotSupportedException("测试上下文不提供端点获取。");
}

/// <summary>空日志器，不输出任何内容。</summary>
public sealed class NullPluginLogger : IPluginLogger
{
    public void Log(
        PluginLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, JsonElement>? properties = null)
    {
    }
}

/// <summary>内存存储，仅在测试生命周期内有效。</summary>
public sealed class MemoryPluginStorage : IPluginStorage
{
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    public ValueTask<JsonElement?> ReadAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_values.TryGetValue(key, out var value) ? value.Clone() : (JsonElement?)null);

    public ValueTask WriteAsync(string key, JsonElement value, CancellationToken cancellationToken)
    {
        _values[key] = value.Clone();
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _values.Remove(key);
        return ValueTask.CompletedTask;
    }
}