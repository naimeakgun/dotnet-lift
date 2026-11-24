using DotnetLift.Core.Model;

namespace DotnetLift.Core.Loading;

/// <summary>
/// Recognises MSTest, xUnit and NUnit projects from their packages, assembly references or the legacy test project type GUID.
/// </summary>
public static class TestProjectDetector
{
    // {3AC096D0-A1C2-E12C-1390-A8335801FDAB} = Visual Studio unit test project (MSTest).
    internal const string TestProjectTypeGuid = "3ac096d0-a1c2-e12c-1390-a8335801fdab";

    internal const string VisualStudioMSTestAssembly = "Microsoft.VisualStudio.QualityTools.UnitTestFramework";

    private static readonly (TestFramework Framework, string Package)[] FrameworkPackages =
    [
        (TestFramework.MSTest, "MSTest.TestFramework"),
        (TestFramework.MSTest, "MSTest"),
        (TestFramework.XUnit, "xunit.v3"),
        (TestFramework.XUnit, "xunit"),
        (TestFramework.XUnit, "xunit.core"),
        (TestFramework.XUnit, "xunit.v3.core"),
        (TestFramework.XUnit, "xunit.assert"),
        (TestFramework.NUnit, "NUnit"),
    ];

    private static readonly (TestFramework Framework, string Assembly)[] FrameworkAssemblies =
    [
        (TestFramework.MSTest, "Microsoft.VisualStudio.TestPlatform.TestFramework"),
        (TestFramework.XUnit, "xunit.core"),
        (TestFramework.XUnit, "xunit.v3.core"),
        (TestFramework.XUnit, "xunit"),
        (TestFramework.NUnit, "nunit.framework"),
    ];

    public static TestProjectInfo? Detect(ProjectInfo project, string? projectTypeGuids)
    {
        // The Visual Studio MSTest reference wins: it is the one that cannot come along as it is.
        if (project.References(VisualStudioMSTestAssembly))
        {
            return new TestProjectInfo(TestFramework.MSTest, $"reference {VisualStudioMSTestAssembly}", null, VisualStudioMSTest: true);
        }

        foreach (var (framework, id) in FrameworkPackages)
        {
            if (project.FindPackage(id) is { } package)
            {
                var version = package.Version.Length > 0 ? package.Version : null;
                return new TestProjectInfo(framework, version is null ? $"package {package.Id}" : $"package {package.Id} {version}",
                    version, VisualStudioMSTest: false);
            }
        }

        foreach (var (framework, assembly) in FrameworkAssemblies)
        {
            if (project.References(assembly))
            {
                var version = project.FileReferences
                    .FirstOrDefault(r => r.AssemblyName.Equals(assembly, StringComparison.OrdinalIgnoreCase))?.AssemblyVersion;
                return new TestProjectInfo(framework, $"reference {assembly}", version, VisualStudioMSTest: false);
            }
        }

        if ((projectTypeGuids ?? "").ToLowerInvariant().Contains(TestProjectTypeGuid))
        {
            return new TestProjectInfo(TestFramework.MSTest, "test project type GUID", null, VisualStudioMSTest: true);
        }

        return null;
    }

    public static string Describe(TestFramework framework) => framework switch
    {
        TestFramework.MSTest => "MSTest",
        TestFramework.XUnit => "xUnit",
        _ => "NUnit",
    };
}
