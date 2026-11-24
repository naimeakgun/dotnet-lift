using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Analyzers;

/// <summary>
/// Test projects: which framework they use, which packages `dotnet test` needs once they are SDK-style,
/// and framework versions that do not run on modern .NET at all.
/// </summary>
public sealed class TestProjectAnalyzer : IAnalyzer
{
    public const string Category = "Tests";

    public string Name => "test-projects";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        if (project.Test is not { } test)
        {
            yield break;
        }

        var framework = TestProjectDetector.Describe(test.Framework);
        var required = RequiredPackages(project, test);
        var missing = required.Where(id => !project.HasPackage(id)).ToList();
        var targets = project.TargetFramework is { } tfm && ProjectFormatAnalyzer.IsNetFramework(tfm)
            ? $"on .NET Framework and {context.TargetFramework}"
            : $"on {context.TargetFramework}";
        yield return new Finding(project.Name, Severity.Info, Category,
            $"{framework} test project ({test.Evidence}).",
            missing.Count == 0
                ? $"Has the packages `dotnet test` needs ({string.Join(", ", required)}), so it can run {targets} after the build."
                : $"`dotnet test` needs {string.Join(", ", required)}; missing: {string.Join(", ", missing)}. " +
                  $"With them the tests run {targets} after the build and prove the upgrade.",
            File: Path.GetFileName(project.FullPath));

        if (test.VisualStudioMSTest)
        {
            yield return new Finding(project.Name, Severity.Warning, Category,
                $"MSTest v1 ({test.Evidence}): the test framework comes from the Visual Studio installation, not from NuGet, and does not run on modern .NET.",
                $"Replace it with the MSTest.TestFramework and MSTest.TestAdapter packages. The namespace (Microsoft.VisualStudio.TestTools.UnitTesting), " +
                "the attributes and Assert stay the same; check [DeploymentItem] paths, and replace .testsettings files with .runsettings.",
                File: Path.GetFileName(project.FullPath));
        }

        if (test.Framework == TestFramework.NUnit && Major(test.Version) is < 3)
        {
            yield return new Finding(project.Name, Severity.Warning, Category,
                $"NUnit {test.Version}: NUnit 2 tests cannot run on modern .NET (the current test adapter only runs NUnit 3 and later).",
                "Update to NUnit 3 or later first, on .NET Framework, and fix what changed there " +
                "([ExpectedException] → Assert.Throws, [TestCase(Result = ...)] → ExpectedResult, [TestFixtureSetUp] → [OneTimeSetUp]).",
                File: Path.GetFileName(project.FullPath));
        }

        if (test.Framework == TestFramework.XUnit && Major(test.Version) is < 2)
        {
            yield return new Finding(project.Name, Severity.Warning, Category,
                $"xUnit {test.Version}: xUnit 1 does not run on modern .NET.",
                "Update to xUnit 2 or xunit.v3 first ([Fact] and [Theory] stay; Assert.Throws<T> and data attributes changed).",
                File: Path.GetFileName(project.FullPath));
        }
    }

    /// <summary>The packages a test project needs for `dotnet test` once it is SDK-style.</summary>
    public static IReadOnlyList<string> RequiredPackages(ProjectInfo project, TestProjectInfo test)
    {
        if (test.Framework == TestFramework.MSTest)
        {
            // The MSTest meta-package brings the framework, the adapter and Microsoft.NET.Test.Sdk.
            if (project.HasPackage("MSTest"))
            {
                return ["MSTest"];
            }
            return ["Microsoft.NET.Test.Sdk", "MSTest.TestFramework", "MSTest.TestAdapter"];
        }
        if (test.Framework == TestFramework.XUnit)
        {
            var xunit = project.HasPackage("xunit.v3") ? "xunit.v3" : "xunit";
            return ["Microsoft.NET.Test.Sdk", xunit, "xunit.runner.visualstudio"];
        }
        return ["Microsoft.NET.Test.Sdk", "NUnit", "NUnit3TestAdapter"];
    }

    /// <summary>"2.6.4" → 2; null or unreadable → null.</summary>
    public static int? Major(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }
        var head = version.Trim().TrimStart('[', '(').Split('.', ',', '-')[0];
        return int.TryParse(head, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            ? major
            : null;
    }
}
