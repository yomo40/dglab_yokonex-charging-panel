using System;
using System.Diagnostics;

namespace ChargingPanel.Core.OCR.Recognition;

/// <summary>
/// OCR 截图间隔自适应策略：
/// 根据进程 CPU 占用动态调节截图周期，避免高占用导致卡顿。
/// </summary>
internal sealed class OcrAdaptiveIntervalPolicy
{
    private DateTime _lastSampleAtUtc = DateTime.MinValue;
    private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
    private int _currentIntervalMs;

    public int GetNextDelayMs(int configuredIntervalMs)
    {
        var baseline = Math.Clamp(configuredIntervalMs <= 0 ? 100 : configuredIntervalMs, 30, 200);
        if (_currentIntervalMs <= 0)
        {
            _currentIntervalMs = baseline;
        }

        var now = DateTime.UtcNow;
        if (_lastSampleAtUtc == DateTime.MinValue)
        {
            _lastSampleAtUtc = now;
            _lastTotalProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
            return _currentIntervalMs;
        }

        if ((now - _lastSampleAtUtc).TotalSeconds < 1)
        {
            return _currentIntervalMs;
        }

        var process = Process.GetCurrentProcess();
        var cpuNow = process.TotalProcessorTime;
        var wallClockMs = (now - _lastSampleAtUtc).TotalMilliseconds;
        var cpuMs = (cpuNow - _lastTotalProcessorTime).TotalMilliseconds;
        _lastSampleAtUtc = now;
        _lastTotalProcessorTime = cpuNow;

        if (wallClockMs <= 1)
        {
            return _currentIntervalMs;
        }

        // 归一化为整机百分比（100% = 占满所有逻辑核）
        var cpuPercent = cpuMs / wallClockMs / Environment.ProcessorCount * 100.0;
        const double highThreshold = 50.0;
        const double lowThreshold = 30.0;

        if (cpuPercent > highThreshold)
        {
            _currentIntervalMs = Math.Min(_currentIntervalMs + 20, 200);
        }
        else if (cpuPercent < lowThreshold)
        {
            _currentIntervalMs = Math.Max(_currentIntervalMs - 10, 30);
        }
        else if (_currentIntervalMs != baseline)
        {
            // 占用稳定后平滑回归用户配置值
            _currentIntervalMs += _currentIntervalMs > baseline ? -5 : 5;
            _currentIntervalMs = Math.Clamp(_currentIntervalMs, 30, 200);
        }

        return _currentIntervalMs;
    }
}

