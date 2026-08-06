using System.Text.RegularExpressions;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Desktop;

/// <summary>
/// Desktop 设备边界测试：扫描 src/Desktop 全部源码（排除 obj/bin 与
/// Avalonia 自动生成的 .g.cs/.g.i.cs），断言 UI 层绝不引用 legacy 设备实现。
///
/// 硬约束：
/// 1) 不引用任何具体厂商适配器 / IDevice / DeviceManager / WindowsBluetoothTransport / .GetDevice(；
/// 2) 不使用 legacy 厂商协议枚举（DGLabVersion、YokonexDeviceType、StrengthMode、
///    EventAction、ChannelTarget，以及页面内的 Channel 成员访问）；
/// 3) 页面不得通过 AppServices.Instance.DeviceManager 直接操作设备。
/// </summary>
public sealed class DesktopDeviceBoundaryTests
{
    private static readonly string[] ForbiddenTypePatterns =
    [
        @"\bDGLabBluetoothAdapter\b",
        @"\bDGLabWebSocketAdapter\b",
        @"\bYokonexEmsBluetoothAdapter\b",
        @"\bYokonexEnemaBluetoothAdapter\b",
        @"\bYokonexToyBluetoothAdapter\b",
        @"\bYokonexIMAdapter\b",
        @"\bYokonexSmartLockPlaceholderAdapter\b",
        @"\bIDevice\b",
        @"\bDeviceManager\b",
        @"\bWindowsBluetoothTransport\b",
        @"\.GetDevice\(",
        @"\bDGLabVersion\b",
        @"\bYokonexDeviceType\b",
        @"\bStrengthMode\b",
        @"\bEventAction\b",
        @"\bChannelTarget\b",
    ];

    /// <summary>页面/ViewModel 内禁止 legacy Channel 枚举成员访问（Channel.A 等）。</summary>
    private static readonly string[] PageChannelPatterns =
    [
        @"\bChannel\s*\.",
    ];

    private static readonly string[] AppServicesDeviceManagerPatterns =
    [
        @"AppServices\.Instance\.DeviceManager",
    ];

    [Fact]
    public void DesktopSource_MustNotReferenceLegacyDeviceAccessTypes()
    {
        var desktopRoot = FindDesktopRoot();
        var violations = new List<string>();

        foreach (var file in EnumerateDesktopSourceFiles(desktopRoot))
        {
            var relative = Path.GetRelativePath(desktopRoot, file);
            var content = File.ReadAllText(file);
            foreach (var pattern in ForbiddenTypePatterns)
            {
                if (Regex.IsMatch(content, pattern))
                {
                    violations.Add($"{relative}: 匹配 {pattern}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Desktop 源码包含 legacy 设备访问模式：\r\n" + string.Join("\r\n", violations));
    }

    [Fact]
    public void DesktopPages_AndViewModels_MustNotUseLegacyChannelEnum()
    {
        var desktopRoot = FindDesktopRoot();
        var violations = new List<string>();
        var scopedRoots = new[] { Path.Combine("Views", "Pages"), "ViewModels" };

        foreach (var scopedRoot in scopedRoots)
        {
            var fullPath = Path.Combine(desktopRoot, scopedRoot);
            if (!Directory.Exists(fullPath))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(fullPath, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGeneratedOrBuildArtifact(file))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(desktopRoot, file);
                var content = File.ReadAllText(file);
                foreach (var pattern in PageChannelPatterns)
                {
                    if (Regex.IsMatch(content, pattern))
                    {
                        violations.Add($"{relative}: 匹配 {pattern}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Desktop 页面/ViewModel 使用 legacy Channel 枚举：\r\n" + string.Join("\r\n", violations));
    }

    [Fact]
    public void DesktopPages_MustNotCallAppServicesDeviceManager()
    {
        var desktopRoot = FindDesktopRoot();
        var pagesRoot = Path.Combine(desktopRoot, "Views", "Pages");
        var violations = new List<string>();

        if (!Directory.Exists(pagesRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(pagesRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGeneratedOrBuildArtifact(file))
            {
                continue;
            }

            var relative = Path.GetRelativePath(desktopRoot, file);
            var content = File.ReadAllText(file);
            foreach (var pattern in AppServicesDeviceManagerPatterns)
            {
                if (Regex.IsMatch(content, pattern))
                {
                    violations.Add($"{relative}: 匹配 {pattern}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Desktop 页面直接调用 AppServices.Instance.DeviceManager：\r\n" + string.Join("\r\n", violations));
    }

    private static IEnumerable<string> EnumerateDesktopSourceFiles(string desktopRoot)
    {
        foreach (var file in Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGeneratedOrBuildArtifact(file))
            {
                continue;
            }

            yield return file;
        }
    }

    private static bool IsGeneratedOrBuildArtifact(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.Ordinal) ||
               normalized.Contains("/bin/", StringComparison.Ordinal) ||
               normalized.EndsWith(".g.cs", StringComparison.Ordinal) ||
               normalized.EndsWith(".g.i.cs", StringComparison.Ordinal);
    }

    private static string FindDesktopRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Desktop");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位 src/Desktop 目录。");
    }
}
