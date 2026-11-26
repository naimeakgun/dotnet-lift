using DotnetLift.Core;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Reporting;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class TestProjectTests
{
    private const string LegacyTestGuids = "{3AC096D0-A1C2-E12C-1390-A8335801FDAB};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

    /// <summary>A folder with one project "A.Tests": legacy or SDK-style, extra properties, item group XML and an optional packages.config body.</summary>
    private static string Project(bool sdk, string items, string? packages = null, string properties = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "A.Tests"));
        var xml = sdk
            ? $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net48</TargetFramework>\n{properties}\n  </PropertyGroup>\n  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n"
            : $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n  <PropertyGroup>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n    <OutputType>Library</OutputType>\n{properties}\n  </PropertyGroup>\n  <ItemGroup>\n    <Reference Include=\"System\" />\n{items}\n  </ItemGroup>\n</Project>\n";
        File.WriteAllText(Path.Combine(dir, "A.Tests", "A.Tests.csproj"), xml);
        if (packages is not null)
        {
            File.WriteAllText(Path.Combine(dir, "A.Tests", "packages.config"),
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<packages>\n{packages}\n</packages>\n");
        }
        File.WriteAllText(Path.Combine(dir, "A.Tests", "ThingTests.cs"), "namespace A.Tests { public class ThingTests { } }\n");
        return dir;
    }

    private static string Package(string id, string version) => $"  <package id=\"{id}\" version=\"{version}\" targetFramework=\"net48\" />";

    private static string PackageRef(string id, string version) => $"    <PackageReference Include=\"{id}\" Version=\"{version}\" />";

    private static string Ref(string assembly, string version, string hintPath) =>
        $"    <Reference Include=\"{assembly}, Version={version}, Culture=neutral, processorArchitecture=MSIL\">\n      <HintPath>{hintPath}</HintPath>\n    </Reference>";

    private static ProjectInfo Load(string dir) => SolutionLoader.Load(dir).Projects.Single();

    private static List<Finding> Analyze(string dir)
    {
        var solution = SolutionLoader.Load(dir);
        return new TestProjectAnalyzer().Analyze(solution.Projects.Single(), new AnalysisContext(solution, "net10.0")).ToList();
    }

    [Fact]
    public void Visual_Studio_MSTest_is_recognised_and_needs_packages()
    {
        var dir = Project(sdk: false,
            "    <Reference Include=\"Microsoft.VisualStudio.QualityTools.UnitTestFramework, Version=10.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL\" />",
            properties: $"    <ProjectTypeGuids>{LegacyTestGuids}</ProjectTypeGuids>");

        var project = Load(dir);
        var findings = Analyze(dir);

        Assert.Equal(new TestProjectInfo(TestFramework.MSTest, "reference Microsoft.VisualStudio.QualityTools.UnitTestFramework", null, VisualStudioMSTest: true), project.Test);
        Assert.Equal(ProjectKind.ClassLibrary, project.Kind);
        var info = Assert.Single(findings, f => f.Severity == Severity.Info);
        Assert.Equal("MSTest test project (reference Microsoft.VisualStudio.QualityTools.UnitTestFramework).", info.Message);
        Assert.Contains("missing: Microsoft.NET.Test.Sdk, MSTest.TestFramework, MSTest.TestAdapter.", info.Suggestion);
        Assert.Contains("on .NET Framework and net10.0", info.Suggestion);
        Assert.Equal("A.Tests.csproj", info.File);
        var warning = Assert.Single(findings, f => f.Severity == Severity.Warning);
        Assert.StartsWith("MSTest v1", warning.Message);
        Assert.Contains("MSTest.TestFramework and MSTest.TestAdapter packages", warning.Suggestion);
        Assert.All(findings, f => Assert.Equal(TestProjectAnalyzer.Category, f.Category));
    }

    [Fact]
    public void The_test_project_type_guid_alone_means_MSTest_v1()
    {
        var dir = Project(sdk: false, "", properties: $"    <ProjectTypeGuids>{LegacyTestGuids}</ProjectTypeGuids>");

        var test = Load(dir).Test;

        Assert.NotNull(test);
        Assert.Equal(TestFramework.MSTest, test.Framework);
        Assert.True(test.VisualStudioMSTest);
        Assert.Equal("test project type GUID", test.Evidence);
    }

    [Fact]
    public void MSTest_v2_packages_are_not_flagged()
    {
        var dir = Project(sdk: false,
            Ref("Microsoft.VisualStudio.TestPlatform.TestFramework", "14.0.0.0", "..\\packages\\MSTest.TestFramework.2.2.10\\lib\\net45\\Microsoft.VisualStudio.TestPlatform.TestFramework.dll"),
            Package("MSTest.TestAdapter", "2.2.10") + "\n" + Package("MSTest.TestFramework", "2.2.10"));

        var project = Load(dir);
        var findings = Analyze(dir);

        Assert.Equal(new TestProjectInfo(TestFramework.MSTest, "package MSTest.TestFramework 2.2.10", "2.2.10", VisualStudioMSTest: false), project.Test);
        var finding = Assert.Single(findings);
        Assert.Contains("missing: Microsoft.NET.Test.Sdk.", finding.Suggestion);
    }

    [Fact]
    public void The_MSTest_meta_package_is_enough()
    {
        var dir = Project(sdk: true, PackageRef("MSTest", "3.6.4"));

        var finding = Assert.Single(Analyze(dir));

        Assert.StartsWith("Has the packages `dotnet test` needs (MSTest)", finding.Suggestion);
    }

    [Fact]
    public void XUnit_from_packages_config_lists_the_missing_test_sdk()
    {
        var dir = Project(sdk: false, "",
            Package("xunit", "2.4.2") + "\n" + Package("xunit.core", "2.4.2") + "\n" + Package("xunit.runner.visualstudio", "2.4.5"));

        var project = Load(dir);
        var finding = Assert.Single(Analyze(dir));

        Assert.Equal(TestFramework.XUnit, project.Test?.Framework);
        Assert.Equal("xUnit test project (package xunit 2.4.2).", finding.Message);
        Assert.Contains("`dotnet test` needs Microsoft.NET.Test.Sdk, xunit, xunit.runner.visualstudio; missing: Microsoft.NET.Test.Sdk.", finding.Suggestion);
    }

    [Fact]
    public void A_complete_sdk_style_NUnit_project_gets_only_the_info_finding()
    {
        var dir = Project(sdk: true,
            PackageRef("Microsoft.NET.Test.Sdk", "17.13.0") + "\n" + PackageRef("NUnit", "3.14.0") + "\n" + PackageRef("NUnit3TestAdapter", "4.6.0"));

        var finding = Assert.Single(Analyze(dir));

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("NUnit test project (package NUnit 3.14.0).", finding.Message);
        Assert.Equal("Has the packages `dotnet test` needs (Microsoft.NET.Test.Sdk, NUnit, NUnit3TestAdapter), so it can run on .NET Framework and net10.0 after the build.", finding.Suggestion);
    }

    [Fact]
    public void NUnit_2_cannot_run_on_modern_dotnet()
    {
        var dir = Project(sdk: false,
            Ref("nunit.framework", "2.6.4.14350", "..\\packages\\NUnit.2.6.4\\lib\\nunit.framework.dll"),
            Package("NUnit", "2.6.4"));

        var warning = Assert.Single(Analyze(dir), f => f.Severity == Severity.Warning);

        Assert.StartsWith("NUnit 2.6.4: NUnit 2 tests cannot run on modern .NET", warning.Message);
        Assert.Contains("[OneTimeSetUp]", warning.Suggestion);
    }

    [Fact]
    public void XUnit_1_cannot_run_on_modern_dotnet()
    {
        var dir = Project(sdk: false, "", Package("xunit", "1.9.2"));

        var warning = Assert.Single(Analyze(dir), f => f.Severity == Severity.Warning);

        Assert.StartsWith("xUnit 1.9.2: xUnit 1 does not run on modern .NET.", warning.Message);
    }

    [Fact]
    public void A_framework_dll_outside_packages_is_recognised_with_its_version()
    {
        var dir = Project(sdk: false, Ref("nunit.framework", "3.12.0.0", "..\\lib\\nunit.framework.dll"));

        var project = Load(dir);
        var findings = Analyze(dir);

        Assert.Equal(new TestProjectInfo(TestFramework.NUnit, "reference nunit.framework", "3.12.0.0", VisualStudioMSTest: false), project.Test);
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void This_repositorys_own_test_project_is_complete_xunit()
    {
        var project = ProjectLoader.Load(Path.Combine(Samples.Root, "tests", "DotnetLift.Tests", "DotnetLift.Tests.csproj"));

        Assert.Equal(TestFramework.XUnit, project.Test?.Framework);
        var finding = Assert.Single(new TestProjectAnalyzer().Analyze(project,
            new AnalysisContext(new SolutionInfo { Path = Samples.Root }, "net10.0")));
        Assert.Equal("Has the packages `dotnet test` needs (Microsoft.NET.Test.Sdk, xunit, xunit.runner.visualstudio), so it can run on net10.0 after the build.", finding.Suggestion);
    }

    [Fact]
    public void Libraries_are_not_test_projects()
    {
        var solution = SolutionLoader.Load(Samples.LegacyShopSln);
        var analysis = new SolutionAnalyzer().Analyze(solution);

        Assert.All(solution.Projects, p => Assert.Null(p.Test));
        Assert.DoesNotContain(analysis.Findings, f => f.Category == TestProjectAnalyzer.Category);
    }

    [Fact]
    public void The_report_shows_the_test_framework_as_the_project_type()
    {
        var dir = Project(sdk: false, "", Package("xunit", "2.4.2"));
        var analysis = new SolutionAnalyzer().Analyze(dir);

        var report = MarkdownReport.Render(analysis, UpgradePlanner.Create(analysis));

        Assert.Contains("| 1 | A.Tests | Tests (xUnit) | v4.8 | Legacy |", report);
        Assert.Contains("xUnit test project (package xunit 2.4.2).", report);
    }

    private static readonly UpgradeStep AnyStep =
        new(1, "x", "x", "x", "x", StepMode.Automatic, Risk.Low, Array.Empty<Finding>());

    private const string VisualStudioMSTestReference =
        "    <Reference Include=\"Microsoft.VisualStudio.QualityTools.UnitTestFramework, Version=10.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL\" />";

    private static string CsprojPath(string dir) => Path.Combine(dir, "A.Tests", "A.Tests.csproj");

    /// <summary>Runs the SDK-style conversion on the single project and returns the new project file.</summary>
    private static string ConvertToSdkStyle(string dir)
    {
        var changes = new SdkStyleConversion().Prepare(ProjectLoader.Load(CsprojPath(dir)), AnyStep, "net10.0");
        FileChange.ApplyAll(changes);
        return File.ReadAllText(CsprojPath(dir));
    }

    [Fact]
    public void Sdk_conversion_moves_MSTest_v1_to_the_MSTest_packages()
    {
        var dir = Project(sdk: false, VisualStudioMSTestReference,
            properties: $"    <ProjectTypeGuids>{LegacyTestGuids}</ProjectTypeGuids>");

        var csproj = ConvertToSdkStyle(dir);

        Assert.DoesNotContain("QualityTools", csproj);
        Assert.DoesNotContain("ProjectTypeGuids", csproj);
        Assert.Contains($"<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdkVersion}\" />", csproj);
        Assert.Contains($"<PackageReference Include=\"MSTest.TestAdapter\" Version=\"{TestPackages.MSTestVersion}\" />", csproj);
        Assert.Contains($"<PackageReference Include=\"MSTest.TestFramework\" Version=\"{TestPackages.MSTestVersion}\" />", csproj);

        var reloaded = Load(dir);
        Assert.True(reloaded.IsSdkStyle);
        Assert.Equal(new TestProjectInfo(TestFramework.MSTest, $"package MSTest.TestFramework {TestPackages.MSTestVersion}", TestPackages.MSTestVersion, VisualStudioMSTest: false), reloaded.Test);
        var finding = Assert.Single(Analyze(dir));
        Assert.StartsWith("Has the packages `dotnet test` needs (Microsoft.NET.Test.Sdk, MSTest.TestFramework, MSTest.TestAdapter)", finding.Suggestion);
    }

    [Fact]
    public void Sdk_conversion_adds_an_MSTest_adapter_with_the_framework_version()
    {
        var dir = Project(sdk: false,
            Ref("Microsoft.VisualStudio.TestPlatform.TestFramework", "14.0.0.0", "..\\packages\\MSTest.TestFramework.2.2.10\\lib\\net45\\Microsoft.VisualStudio.TestPlatform.TestFramework.dll"),
            Package("MSTest.TestFramework", "2.2.10"));

        var csproj = ConvertToSdkStyle(dir);

        Assert.Contains("<PackageReference Include=\"MSTest.TestAdapter\" Version=\"2.2.10\" />", csproj);
        Assert.Contains("<PackageReference Include=\"MSTest.TestFramework\" Version=\"2.2.10\" />", csproj);
        Assert.Contains("<PackageReference Include=\"Microsoft.NET.Test.Sdk\"", csproj);
        Assert.DoesNotContain("HintPath", csproj);
        Assert.False(File.Exists(Path.Combine(dir, "A.Tests", "packages.config")));
    }

    [Fact]
    public void Sdk_conversion_keeps_an_existing_xunit_adapter_and_adds_the_test_sdk()
    {
        var dir = Project(sdk: false, "",
            Package("xunit", "2.4.2") + "\n" + Package("xunit.core", "2.4.2") + "\n" + Package("xunit.runner.visualstudio", "2.4.5"));

        var csproj = ConvertToSdkStyle(dir);

        Assert.Contains("<PackageReference Include=\"xunit.runner.visualstudio\" Version=\"2.4.5\" />", csproj);
        Assert.Contains($"<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdkVersion}\" />", csproj);
        Assert.Single(Load(dir).Packages, p => p.Id == "xunit.runner.visualstudio");
    }

    [Fact]
    public void Sdk_conversion_adds_the_NUnit_adapter()
    {
        var dir = Project(sdk: false, "", Package("NUnit", "3.14.0"));

        var csproj = ConvertToSdkStyle(dir);

        Assert.Contains($"<PackageReference Include=\"NUnit3TestAdapter\" Version=\"{TestPackages.NUnitAdapterVersion}\" />", csproj);
        Assert.Contains("<PackageReference Include=\"NUnit\" Version=\"3.14.0\" />", csproj);
    }

    [Theory]
    [InlineData("NUnit", "2.6.4")]
    [InlineData("xunit", "1.9.2")]
    public void Sdk_conversion_adds_nothing_to_frameworks_that_must_be_updated_first(string id, string version)
    {
        var dir = Project(sdk: false, "", Package(id, version));

        var csproj = ConvertToSdkStyle(dir);

        Assert.DoesNotContain("Microsoft.NET.Test.Sdk", csproj);
        Assert.DoesNotContain("Adapter", csproj);
        Assert.DoesNotContain("runner", csproj);
        Assert.Contains($"<PackageReference Include=\"{id}\" Version=\"{version}\" />", csproj);
    }

    [Fact]
    public void Sdk_conversion_puts_the_test_package_versions_in_Directory_Packages_props()
    {
        var dir = Project(sdk: false, VisualStudioMSTestReference);
        File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"),
            "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n" +
            "    <PackageVersion Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.12.0\" />\n  </ItemGroup>\n</Project>\n");

        var csproj = ConvertToSdkStyle(dir);
        var props = File.ReadAllText(Path.Combine(dir, "Directory.Packages.props"));

        Assert.Contains("<PackageReference Include=\"Microsoft.NET.Test.Sdk\" />", csproj); // the central version is used
        Assert.Contains("<PackageReference Include=\"MSTest.TestAdapter\" />", csproj);
        Assert.DoesNotContain("VersionOverride", csproj);
        Assert.Contains("<PackageVersion Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.12.0\" />", props);
        Assert.Contains($"<PackageVersion Include=\"MSTest.TestAdapter\" Version=\"{TestPackages.MSTestVersion}\" />", props);
        Assert.Contains($"<PackageVersion Include=\"MSTest.TestFramework\" Version=\"{TestPackages.MSTestVersion}\" />", props);
    }

    [Fact]
    public void Complete_test_projects_and_libraries_get_no_test_packages()
    {
        var complete = Project(sdk: false, "",
            Package("Microsoft.NET.Test.Sdk", "17.13.0") + "\n" + Package("NUnit", "3.14.0") + "\n" + Package("NUnit3TestAdapter", "4.6.0"));
        var library = ProjectLoader.Load(Path.Combine(Samples.LegacyShopDir, "LegacyShop.Core", "LegacyShop.Core.csproj"));

        Assert.Empty(TestPackages.ToAdd(Load(complete)));
        Assert.Empty(TestPackages.ToAdd(library));
        Assert.Empty(TestPackages.ToAdd(Load(Project(sdk: false, "", Package("MSTest", "3.6.4")))));
    }

    [Fact]
    public void The_plan_says_what_the_sdk_conversion_adds_to_a_test_project()
    {
        var dir = Project(sdk: false, VisualStudioMSTestReference);
        var analysis = new SolutionAnalyzer().Analyze(dir);

        var step = Assert.Single(UpgradePlanner.Create(analysis).Steps, s => s.Id == "sdk-style");

        Assert.Contains($"MSTest test project: adds Microsoft.NET.Test.Sdk {TestPackages.TestSdkVersion}, MSTest.TestFramework {TestPackages.MSTestVersion}, " +
                        $"MSTest.TestAdapter {TestPackages.MSTestVersion} so `dotnet test` can run it.", step.Description);
        Assert.Contains("Microsoft.VisualStudio.QualityTools.UnitTestFramework reference is removed", step.Description);
        Assert.Contains(step.Findings, f => f.Category == TestProjectAnalyzer.Category && f.Severity == Severity.Warning);
    }

    [Theory]
    [InlineData("2.6.4", 2)]
    [InlineData("3.14.0", 3)]
    [InlineData("[3.0,4.0)", 3)]
    [InlineData("1.9.2-beta", 1)]
    [InlineData("", null)]
    [InlineData("latest", null)]
    public void Major_version(string version, int? expected) =>
        Assert.Equal(expected, TestProjectAnalyzer.Major(version));
}
