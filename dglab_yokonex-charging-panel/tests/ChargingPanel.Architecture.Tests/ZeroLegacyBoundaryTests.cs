using System.Text.RegularExpressions;
using Xunit;

namespace ChargingPanel.Architecture.Tests;

/// <summary>
/// Task 22 硬约束：删除旧运行时后，整个 src/ 树必须达到"旧边界为零"。
/// 扫描 src/ 全部源码（排除 obj/bin 与 Avalonia 生成的 .g.cs/.g.i.cs），
/// 断言以下 legacy 访问模式零出现。
/// </summary>
public sealed class ZeroLegacyBoundaryTests
{
    private static readonly (string Name, string Pattern, string Scope)[] ForbiddenPatterns =
    [
        ("IDevice", @"\bIDevice\b", "src"),
        ("DeviceManager", @"\bDeviceManager\b", "src"),
        ("AppServices.Instance", @"AppServices\.Instance", "src"),
        ("TencentIMService.Instance", @"TencentIMService\.Instance", "src"),
        ("new WindowsBluetoothTransport", @"new\s+WindowsBluetoothTransport\b", "src"),
        ("PlaceholderAdapter", @"PlaceholderAdapter", "src"),
        ("VirtualDevice", @"\bVirtualDevice\b", "src"),
        ("Database.Instance(设备路径)", @"Database\.Instance", "src/Core/Devices"),
        ("控制路径 fire-and-forget Task.Run", @"_\s*=\s*Task\.Run\(", "src/Core/Devices")
    ];

    [Fact]
    public void EntireSourceTree_MustNotContainLegacyDeviceBoundaryPatterns()
    {
        var solutionRoot = FindSolutionRoot();
        var violations = new List<string>();

        foreach (var (name, pattern, scope) in ForbiddenPatterns)
        {
            var scopeRoot = Path.Combine(solutionRoot, scope.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(scopeRoot))
            {
                continue;
            }

            foreach (var file in EnumerateSourceFiles(scopeRoot))
            {
                var content = File.ReadAllText(file);
                if (Regex.IsMatch(content, pattern))
                {
                    violations.Add($"{name}: {Path.GetRelativePath(solutionRoot, file)}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "src/ 树仍包含 legacy 设备边界模式：\r\n" + string.Join("\r\n", violations));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal) ||
                normalized.Contains("/bin/", StringComparison.Ordinal) ||
                normalized.EndsWith(".g.cs", StringComparison.Ordinal) ||
                normalized.EndsWith(".g.i.cs", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string FindSolutionRoot()
    {
        foreach (var startDirectory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "dglab_yokonex-charging-panel.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("无法定位解决方案根目录。");
    }
}
