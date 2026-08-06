using System.Xml.Linq;
using Xunit;

namespace ChargingPanel.Architecture.Tests;

public sealed class ContractsIsolationTests
{
    // 捕获 Contracts 改成 Windows TFM 后把 WinRT 平台面带入公共合同的问题。
    [Fact]
    public void ContractsProject_TargetFramework_RemainsPlatformNeutralNet8()
    {
        var project = LoadContractsProject();
        var targetFramework = project.Descendants("TargetFramework").Single().Value;

        Assert.Equal("net8.0", targetFramework);
    }

    // 捕获 Contracts 新增项目、NuGet 或框架依赖而不再是 BCL-only 的问题。
    [Fact]
    public void ContractsProject_References_ContainNoExternalDependencies()
    {
        var project = LoadContractsProject();
        var forbiddenReferenceKinds = new[]
        {
            "ProjectReference",
            "PackageReference",
            "FrameworkReference"
        };

        var references = forbiddenReferenceKinds
            .SelectMany(kind => project.Descendants(kind))
            .Select(element => element.Attribute("Include")?.Value ?? element.Value)
            .ToArray();

        Assert.Empty(references);
    }

    private static XDocument LoadContractsProject()
    {
        var solutionRoot = FindSolutionRoot();
        var projectPath = Path.Combine(
            solutionRoot,
            "src",
            "Contracts",
            "ChargingPanel.Contracts.csproj");
        return XDocument.Load(projectPath);
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
