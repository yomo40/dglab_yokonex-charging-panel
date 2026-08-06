using System.Xml.Linq;
using Xunit;

namespace ChargingPanel.Architecture.Tests;

public sealed class ProjectDependencyTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedProjectReferences =
        new Dictionary<string, string[]>
        {
            ["src/Contracts/ChargingPanel.Contracts.csproj"] = [],
            ["src/Core/ChargingPanel.Core.csproj"] = ["src/Contracts/ChargingPanel.Contracts.csproj"],
            ["src/Infrastructure.Windows/ChargingPanel.Infrastructure.Windows.csproj"] =
            ["src/Contracts/ChargingPanel.Contracts.csproj", "src/Core/ChargingPanel.Core.csproj"],
            ["src/Vendor.DGLab/ChargingPanel.Vendor.DGLab.csproj"] = ["src/Contracts/ChargingPanel.Contracts.csproj"],
            ["src/Vendor.Yokonex/ChargingPanel.Vendor.Yokonex.csproj"] = ["src/Contracts/ChargingPanel.Contracts.csproj"],
            ["src/Desktop/ChargingPanel.Desktop.csproj"] =
            [
                "src/Contracts/ChargingPanel.Contracts.csproj",
                "src/Core/ChargingPanel.Core.csproj",
                "src/Infrastructure.Windows/ChargingPanel.Infrastructure.Windows.csproj",
                "src/Vendor.DGLab/ChargingPanel.Vendor.DGLab.csproj",
                "src/Vendor.Yokonex/ChargingPanel.Vendor.Yokonex.csproj"
            ]
        };

    [Fact]
    public void ProductionProjects_WhenConfigured_HaveOnlyApprovedProjectReferences()
    {
        var solutionRoot = FindSolutionRoot();

        foreach (var (relativeProjectPath, expectedReferences) in ExpectedProjectReferences)
        {
            var projectPath = Path.Combine(solutionRoot, relativeProjectPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(projectPath), $"缺少生产项目：{relativeProjectPath}");

            AssertProjectReferences(solutionRoot, projectPath, expectedReferences);
        }
    }

    [Fact]
    public void ProjectReferences_WhenSameFileNameTargetsWrongDirectory_RejectsReference()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var coreProjectPath = Path.Combine(fixtureRoot, "src", "Core", "ChargingPanel.Core.csproj");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(coreProjectPath)!);
            Directory.CreateDirectory(Path.Combine(fixtureRoot, "src", "Contracts"));
            Directory.CreateDirectory(Path.Combine(fixtureRoot, "src", "Other"));
            File.WriteAllText(Path.Combine(fixtureRoot, "src", "Contracts", "ChargingPanel.Contracts.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(fixtureRoot, "src", "Other", "ChargingPanel.Contracts.csproj"), "<Project />");
            File.WriteAllText(
                coreProjectPath,
                "<Project><ItemGroup><ProjectReference Include=\"..\\Other\\ChargingPanel.Contracts.csproj\" /></ItemGroup></Project>");

            Assert.Throws<Xunit.Sdk.EqualException>(() => AssertProjectReferences(
                fixtureRoot,
                coreProjectPath,
                ["src/Contracts/ChargingPanel.Contracts.csproj"]));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void VendorProjects_WhenConfigured_DoNotReferenceUiWindowsOrPersistencePackages()
    {
        var solutionRoot = FindSolutionRoot();
        var vendorProjectPaths = new[]
        {
            "src/Vendor.DGLab/ChargingPanel.Vendor.DGLab.csproj",
            "src/Vendor.Yokonex/ChargingPanel.Vendor.Yokonex.csproj"
        };
        var forbiddenFragments = new[]
        {
            "Avalonia",
            "WinRT",
            "SQLite",
            "Microsoft.Extensions.DependencyInjection"
        };

        foreach (var relativeProjectPath in vendorProjectPaths)
        {
            var projectPath = Path.Combine(solutionRoot, relativeProjectPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(projectPath), $"缺少厂商项目：{relativeProjectPath}");

            var references = ReadItemIncludes(projectPath, "PackageReference")
                .Concat(ReadItemIncludes(projectPath, "FrameworkReference"))
                .Concat(ReadItemIncludes(projectPath, "ProjectReference"));

            Assert.DoesNotContain(
                references,
                reference => forbiddenFragments.Any(fragment =>
                    reference.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static IEnumerable<string> ReadItemIncludes(string projectPath, string itemName)
    {
        return XDocument.Load(projectPath)
            .Descendants(itemName)
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>();
    }

    private static void AssertProjectReferences(
        string solutionRoot,
        string projectPath,
        IEnumerable<string> expectedRelativeProjectPaths)
    {
        var actualReferences = ReadItemIncludes(projectPath, "ProjectReference")
            .Select(reference => NormalizeProjectPath(Path.GetDirectoryName(projectPath)!, reference))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedReferences = expectedRelativeProjectPaths
            .Select(reference => NormalizeProjectPath(solutionRoot, reference))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedReferences, actualReferences);
    }

    private static string NormalizeProjectPath(string basePath, string projectReference)
    {
        return Path.GetFullPath(Path.Combine(basePath, projectReference));
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
