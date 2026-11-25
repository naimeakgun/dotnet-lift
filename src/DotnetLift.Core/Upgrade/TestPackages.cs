using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// The packages an SDK-style test project needs for `dotnet test`: the test SDK, the adapter for its framework,
/// and for MSTest v1 the MSTest packages that replace the Visual Studio reference.
/// </summary>
public static class TestPackages
{
    public const string TestSdkVersion = "17.14.1";

    // MSTest 3 keeps what MSTest v1 code uses ([ExpectedException], Assert.ThrowsException); MSTest 4 removed them.
    public const string MSTestVersion = "3.9.3";

    public const string XUnitAdapterVersion = "3.1.0";

    public const string NUnitAdapterVersion = "5.0.0";

    /// <summary>Assembly references the conversion drops because a package replaces them.</summary>
    public static bool IsReplacedReference(ProjectInfo project, string assemblyName) =>
        project.Test is { VisualStudioMSTest: true }
        && assemblyName.Equals(TestProjectDetector.VisualStudioMSTestAssembly, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Packages to add, with their versions; empty for projects that are not test projects, already complete,
    /// or on a framework version (NUnit 2, xUnit 1) that has to be updated first.
    /// </summary>
    public static IReadOnlyList<PackageRef> ToAdd(ProjectInfo project, CentralPackageFile? central = null)
    {
        if (project.Test is not { } test)
        {
            return [];
        }
        if ((test.Framework == TestFramework.NUnit && TestProjectAnalyzer.Major(test.Version) is < 3)
            || (test.Framework == TestFramework.XUnit && TestProjectAnalyzer.Major(test.Version) is < 2))
        {
            return []; // the current adapters do not run these; the analyze warning says to update first
        }
        if (test.Framework == TestFramework.MSTest && project.HasPackage("MSTest"))
        {
            return []; // the meta-package brings the framework, the adapter and the test SDK
        }

        var result = new List<PackageRef>();
        void Add(string id, string version)
        {
            if (!project.HasPackage(id))
            {
                result.Add(new PackageRef(id, central?.VersionOf(id) ?? version));
            }
        }

        Add("Microsoft.NET.Test.Sdk", TestSdkVersion);
        switch (test.Framework)
        {
            case TestFramework.MSTest:
                // The adapter has to match the framework version the project already uses.
                var framework = project.FindPackage("MSTest.TestFramework")?.Version;
                if (framework is null && !test.VisualStudioMSTest)
                {
                    break; // the framework is a copied DLL of unknown version: the file reference step decides its package first
                }
                var mstest = string.IsNullOrWhiteSpace(framework) ? MSTestVersion : framework;
                if (test.VisualStudioMSTest)
                {
                    Add("MSTest.TestFramework", mstest);
                }
                Add("MSTest.TestAdapter", mstest);
                break;
            case TestFramework.XUnit:
                Add("xunit.runner.visualstudio", XUnitAdapterVersion);
                break;
            default:
                Add("NUnit3TestAdapter", NUnitAdapterVersion);
                break;
        }
        return result;
    }
}
