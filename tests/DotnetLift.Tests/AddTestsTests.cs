using DotnetLift.Cli;
using DotnetLift.Core;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class AddTestsTests
{
    private const string LibGuid = "{11111111-0000-0000-0000-000000000001}";
    private const string FolderGuid = "{22222222-0000-0000-0000-000000000001}";

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// "S.sln" (CRLF) with one SDK-style project Lib in a solution folder; Lib is built in Debug but not in Release.
    /// </summary>
    private static string SdkSolution(string targetFrameworks, string? centralPackages = null)
    {
        var dir = NewDir();
        Directory.CreateDirectory(Path.Combine(dir, "Lib"));
        File.WriteAllText(Path.Combine(dir, "Lib", "Lib.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFrameworks>{targetFrameworks}</TargetFrameworks>\n  </PropertyGroup>\n</Project>\n");
        if (centralPackages is not null)
        {
            File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"),
                "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n" +
                $"  <ItemGroup>\n{centralPackages}\n  </ItemGroup>\n</Project>\n");
        }

        string[] lines =
        [
            "",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"Lib\", \"Lib\\Lib.csproj\", \"{LibGuid}\"",
            "EndProject",
            $"Project(\"{{2150E333-8FDC-42A3-9474-1A3956D46DE8}}\") = \"Shared\", \"Shared\", \"{FolderGuid}\"",
            "EndProject",
            "Global",
            "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
            "\t\tDebug|Any CPU = Debug|Any CPU",
            "\t\tRelease|Any CPU = Release|Any CPU",
            "\tEndGlobalSection",
            "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
            $"\t\t{LibGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
            $"\t\t{LibGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU",
            $"\t\t{LibGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU",
            "\tEndGlobalSection",
            "\tGlobalSection(NestedProjects) = preSolution",
            $"\t\t{LibGuid} = {FolderGuid}",
            "\tEndGlobalSection",
            "EndGlobal",
            "",
        ];
        File.WriteAllText(Path.Combine(dir, "S.sln"), string.Join("\r\n", lines));
        return dir;
    }

    private static string GuidOf(string sln, string name)
    {
        var line = sln.Split('\n').Single(l => l.Contains($"= \"{name}\","));
        return line.TrimEnd('\r')[^39..^1];
    }

    [Fact]
    public void Sample_library_gets_an_MSTest_project_for_net48_next_to_it()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var sln = Path.Combine(dir, "LegacyShop.sln");
        var original = File.ReadAllText(sln);

        var plan = TestProjectScaffold.Plan(SolutionLoader.Load(sln), "legacyshop.core", "net10.0");

        Assert.Equal("LegacyShop.Core.Tests", plan.Name);
        Assert.Equal(Path.Combine(dir, "LegacyShop.Core.Tests", "LegacyShop.Core.Tests.csproj"), plan.ProjectPath);
        Assert.Equal("net48", plan.TargetFrameworks); // the library is still a .NET Framework 4.8 project
        Assert.Contains(plan.Notes, n => n.Contains("`dotnet-lift upgrade` retargets the test project after the library"));
        Assert.Equal(3, plan.Changes.Count);

        var project = plan.Changes[0].NewContent!;
        Assert.Contains("<TargetFramework>net48</TargetFramework>", project);
        Assert.Contains("<IsTestProject>true</IsTestProject>", project);
        Assert.Contains($"<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdkVersion}\" />", project);
        Assert.Contains($"<PackageReference Include=\"MSTest.TestAdapter\" Version=\"{TestPackages.MSTestVersion}\" />", project);
        Assert.Contains($"<PackageReference Include=\"MSTest.TestFramework\" Version=\"{TestPackages.MSTestVersion}\" />", project);
        Assert.Contains("<ProjectReference Include=\"..\\LegacyShop.Core\\LegacyShop.Core.csproj\" />", project);

        var test = plan.Changes[1].NewContent!;
        Assert.Contains("namespace LegacyShop.Core.Tests", test);
        Assert.Contains("Assembly.Load(new AssemblyName(\"LegacyShop.Core\"))", test);
        Assert.DoesNotContain("namespace LegacyShop.Core.Tests;", test); // block namespace: C# 7.3 for net48

        FileChange.ApplyAll(plan.Changes);

        var text = File.ReadAllText(sln);
        var guid = GuidOf(text, "LegacyShop.Core.Tests");
        Assert.Contains($"Project(\"{TestProjectScaffold.CSharpSdkProjectTypeGuid}\") = \"LegacyShop.Core.Tests\", \"LegacyShop.Core.Tests\\LegacyShop.Core.Tests.csproj\", \"{guid}\"", text);
        Assert.Contains($"{guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU", text);
        Assert.Contains($"{guid}.Debug|Any CPU.Build.0 = Debug|Any CPU", text);
        Assert.Contains($"{guid}.Release|Any CPU.Build.0 = Release|Any CPU", text);
        Assert.Equal(original.Contains("\r\n"), text.Contains("\r\n"));
        Assert.Equal(original.Split('\n').Length + 6, text.Split('\n').Length); // 2 project lines, 4 configuration lines

        var loaded = SolutionLoader.Load(sln);
        var tests = Assert.Single(loaded.Projects, p => p.Name == "LegacyShop.Core.Tests");
        Assert.True(tests.IsSdkStyle);
        Assert.Equal(TestFramework.MSTest, tests.Test?.Framework);
        Assert.Equal(4, loaded.Projects.Count);
    }

    [Fact]
    public void The_upgrade_plan_retargets_the_new_test_project_after_its_library()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var sln = Path.Combine(dir, "LegacyShop.sln");
        FileChange.ApplyAll(TestProjectScaffold.Plan(SolutionLoader.Load(sln), "LegacyShop.Core", "net10.0").Changes);

        var plan = UpgradePlanner.Create(new SolutionAnalyzer().Analyze(sln, "net10.0"));

        var names = plan.Order.Select(p => p.Name).ToList();
        Assert.True(names.IndexOf("LegacyShop.Core.Tests") > names.IndexOf("LegacyShop.Core"));
        var retarget = Assert.Single(plan.Steps, s => s.Project == "LegacyShop.Core.Tests" && s.Id == "retarget");
        Assert.Equal("net48;net10.0", retarget.Argument); // the web app is still on .NET Framework, so the library multi-targets too
    }

    [Theory]
    [InlineData("v4.8", "net48")]
    [InlineData("v4.7.2", "net472")]
    [InlineData("net48;net10.0", "net48;net10.0")]
    [InlineData("net10.0", "net10.0")]
    [InlineData("netstandard2.0", "net48;net10.0")]
    [InlineData("netstandard2.0;net10.0", "net48;net10.0")]
    public void Test_targets_follow_the_library(string library, string expected)
    {
        var project = new ProjectInfo { Name = "Lib", FullPath = Path.Combine(NewDir(), "Lib.csproj"), TargetFramework = library };

        Assert.Equal(expected, TestProjectScaffold.TestTargets(project, "net10.0"));
    }

    [Fact]
    public void Multi_targeted_library_gets_tests_on_both_frameworks_in_its_solution_folder()
    {
        var dir = SdkSolution("net48;net10.0");

        var plan = TestProjectScaffold.Plan(SolutionLoader.Load(Path.Combine(dir, "S.sln")), "Lib", "net10.0");

        Assert.Contains("<TargetFrameworks>net48;net10.0</TargetFrameworks>", plan.Changes[0].NewContent);
        Assert.Contains(plan.Notes, n => n.Contains("The tests target net48 and net10.0"));

        FileChange.ApplyAll(plan.Changes);
        var text = File.ReadAllText(Path.Combine(dir, "S.sln"));
        var guid = GuidOf(text, "Lib.Tests");
        Assert.DoesNotContain("\n", text.Replace("\r\n", "")); // CRLF kept everywhere
        Assert.Contains($"\t\t{guid}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n", text);
        Assert.Contains($"\t\t{guid}.Release|Any CPU.ActiveCfg = Release|Any CPU\r\n", text);
        Assert.DoesNotContain($"{guid}.Release|Any CPU.Build.0", text); // the library is not built in Release either
        Assert.Contains($"\t\t{guid} = {FolderGuid}\r\n", text);
        Assert.True(text.IndexOf("\"Lib.Tests\"", StringComparison.Ordinal) < text.IndexOf("\r\nGlobal\r\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Planning_twice_gives_the_same_solution()
    {
        var dir = SdkSolution("net48");
        var solution = SolutionLoader.Load(Path.Combine(dir, "S.sln"));

        var first = TestProjectScaffold.Plan(solution, "Lib", "net10.0");
        var second = TestProjectScaffold.Plan(solution, "Lib", "net10.0");

        Assert.Equal(first.Changes[2].NewContent, second.Changes[2].NewContent); // the dry run shows what is written
    }

    [Fact]
    public void Under_central_package_management_versions_go_to_Directory_Packages_props()
    {
        var dir = SdkSolution("net48", "    <PackageVersion Include=\"MSTest.TestFramework\" Version=\"3.8.0\" />");

        var plan = TestProjectScaffold.Plan(SolutionLoader.Load(Path.Combine(dir, "S.sln")), "Lib", "net10.0");

        var project = plan.Changes[0].NewContent!;
        Assert.Contains("<PackageReference Include=\"MSTest.TestFramework\" />", project);
        Assert.Contains("<PackageReference Include=\"MSTest.TestAdapter\" />", project);
        Assert.Contains("<PackageReference Include=\"Microsoft.NET.Test.Sdk\" />", project);
        Assert.DoesNotContain("Version=", project);

        var props = Assert.Single(plan.Changes, c => c.Path.EndsWith(CentralPackageFile.FileName, StringComparison.Ordinal)).NewContent!;
        Assert.Contains("<PackageVersion Include=\"MSTest.TestAdapter\" Version=\"3.8.0\" />", props); // matches the framework
        Assert.Contains($"<PackageVersion Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdkVersion}\" />", props);
    }

    [Fact]
    public void Refuses_a_test_project_an_existing_folder_and_a_project_without_solution()
    {
        var dir = SdkSolution("net48");
        var sln = Path.Combine(dir, "S.sln");

        Directory.CreateDirectory(Path.Combine(dir, "Lib.Tests"));
        var folder = Assert.Throws<InvalidOperationException>(() => TestProjectScaffold.Plan(SolutionLoader.Load(sln), "Lib", "net10.0"));
        Assert.Contains("Lib.Tests already exists", folder.Message);
        Directory.Delete(Path.Combine(dir, "Lib.Tests"));

        var single = Assert.Throws<InvalidOperationException>(() =>
            TestProjectScaffold.Plan(SolutionLoader.Load(Path.Combine(dir, "Lib", "Lib.csproj")), "Lib", "net10.0"));
        Assert.Contains("needs a .sln file", single.Message);

        FileChange.ApplyAll(TestProjectScaffold.Plan(SolutionLoader.Load(sln), "Lib", "net10.0").Changes);
        var self = Assert.Throws<InvalidOperationException>(() => TestProjectScaffold.Plan(SolutionLoader.Load(sln), "Lib.Tests", "net10.0"));
        Assert.Contains("is a test project itself", self.Message);
        var again = Assert.Throws<InvalidOperationException>(() => TestProjectScaffold.Plan(SolutionLoader.Load(sln), "Lib", "net10.0"));
        Assert.Contains("already has a project named Lib.Tests", again.Message);
    }

    [Fact]
    public async Task Cli_adds_the_test_project_in_one_commit()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        Git.InitWithCommit(dir);
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["add-tests", dir, "-p", "LegacyShop.Data", "--yes"]);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("LegacyShop.Data.Tests: MSTest, net48, references LegacyShop.Data", text);
        Assert.Contains("+ LegacyShop.Data.Tests/LegacyShop.Data.Tests.csproj (new)", text);
        Assert.Contains("~ LegacyShop.sln (+6 −0)", text);
        Assert.Equal(new[] { "dotnet-lift: Add test project LegacyShop.Data.Tests", "initial" }, Git.LogSubjects(dir));
        Assert.Equal("", Git.Status(dir));
        Assert.True(File.Exists(Path.Combine(dir, "LegacyShop.Data.Tests", TestProjectScaffold.FirstTestFileName)));
    }

    [Fact]
    public async Task Cli_dry_run_writes_nothing_and_needs_a_project()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var before = File.ReadAllText(Path.Combine(dir, "LegacyShop.sln"));
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["add-tests", dir, "--project", "LegacyShop.Core", "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.Contains("+Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"LegacyShop.Core.Tests\"", output.ToString());
        Assert.Contains("Dry run: nothing was written.", output.ToString());
        Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "LegacyShop.sln")));
        Assert.False(Directory.Exists(Path.Combine(dir, "LegacyShop.Core.Tests")));

        var missing = new StringWriter();
        Assert.Equal(2, await new App(new StringReader(""), missing, interactive: false).RunAsync(["add-tests", dir]));
        Assert.Contains("add-tests needs the project", missing.ToString());
    }
}
