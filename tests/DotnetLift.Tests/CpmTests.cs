using DotnetLift.Cli;
using DotnetLift.Core.Build;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class CpmTests
{
    private static readonly UpgradeStep AnyStep =
        new(1, "x", "x", "x", "x", StepMode.Automatic, Risk.Low, [], "net48;net10.0");

    /// <summary>The sample with both libraries converted to SDK-style (the web app still on packages.config).</summary>
    private static string ConvertedSample()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        foreach (var name in new[] { "LegacyShop.Core", "LegacyShop.Data" })
        {
            var path = Path.Combine(dir, name, name + ".csproj");
            FileChange.ApplyAll(new PackageReferenceMigration().Prepare(ProjectLoader.Load(path), AnyStep, "net10.0"));
            FileChange.ApplyAll(new SdkStyleConversion().Prepare(ProjectLoader.Load(path), AnyStep, "net10.0"));
        }
        return dir;
    }

    /// <summary>A folder of SDK-style projects, each with the given package references.</summary>
    private static string Projects(params (string Name, string Packages)[] projects)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        foreach (var (name, packages) in projects)
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
            File.WriteAllText(Path.Combine(dir, name, name + ".csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n{packages}\n  </ItemGroup>\n</Project>\n");
        }
        return dir;
    }

    private static string Csproj(string dir, string name) => File.ReadAllText(Path.Combine(dir, name, name + ".csproj"));

    [Fact]
    public void Moves_the_sample_library_versions_into_Directory_Packages_props()
    {
        var dir = ConvertedSample();

        var plan = CentralPackageManagement.Plan(SolutionLoader.Load(dir));
        FileChange.ApplyAll(plan.Changes);

        Assert.Null(plan.Blocker);
        var props = File.ReadAllText(Path.Combine(dir, "Directory.Packages.props"));
        Assert.Contains("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>", props);
        Assert.Contains("<PackageVersion Include=\"EntityFramework\" Version=\"6.4.4\" />", props);
        Assert.Contains("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", props);
        Assert.True(props.IndexOf("EntityFramework", StringComparison.Ordinal) < props.IndexOf("Newtonsoft.Json", StringComparison.Ordinal));
        Assert.Contains("<PackageReference Include=\"EntityFramework\" />", Csproj(dir, "LegacyShop.Data"));
        Assert.DoesNotContain("Version=", Csproj(dir, "LegacyShop.Core").Replace("PackageVersion", ""));
        Assert.Contains(plan.Notes, n => n.StartsWith("LegacyShop.Web uses packages.config"));
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Different_versions_keep_working_with_VersionOverride()
    {
        var dir = Projects(
            ("A", "    <PackageReference Include=\"Foo\" Version=\"1.0.0\" />"),
            ("B", "    <PackageReference Include=\"Foo\" Version=\"2.0.0\" />"),
            ("C", "    <PackageReference Include=\"Foo\" Version=\"1.0.0\" />"));

        var plan = CentralPackageManagement.Plan(SolutionLoader.Load(dir));
        FileChange.ApplyAll(plan.Changes);

        Assert.Contains("<PackageVersion Include=\"Foo\" Version=\"1.0.0\" />", File.ReadAllText(Path.Combine(dir, "Directory.Packages.props")));
        Assert.Contains("<PackageReference Include=\"Foo\" />", Csproj(dir, "A"));
        Assert.Contains("<PackageReference Include=\"Foo\" VersionOverride=\"2.0.0\" />", Csproj(dir, "B"));
        var conflict = Assert.Single(plan.Conflicts);
        Assert.StartsWith("Foo: 1.0.0 in A, C; 2.0.0 in B.", conflict);
    }

    [Fact]
    public void A_tie_goes_to_the_highest_version()
    {
        var dir = Projects(
            ("A", "    <PackageReference Include=\"Foo\" Version=\"1.10.0\" />"),
            ("B", "    <PackageReference Include=\"Foo\" Version=\"1.9.0\" />"));

        var plan = CentralPackageManagement.Plan(SolutionLoader.Load(dir));

        Assert.Contains("<PackageVersion Include=\"Foo\" Version=\"1.10.0\" />", plan.Changes[0].NewContent);
    }

    [Fact]
    public void Version_child_elements_are_moved_too()
    {
        var dir = Projects(("A", "    <PackageReference Include=\"Foo\">\n      <Version>3.1.0</Version>\n      <PrivateAssets>all</PrivateAssets>\n    </PackageReference>\n    <PackageReference Include=\"Bar\">\n      <Version>1.0.0</Version>\n    </PackageReference>"));

        FileChange.ApplyAll(CentralPackageManagement.Plan(SolutionLoader.Load(dir)).Changes);
        var csproj = Csproj(dir, "A");

        Assert.DoesNotContain("<Version>", csproj);
        Assert.Contains("<PrivateAssets>all</PrivateAssets>", csproj);
        Assert.Contains("<PackageReference Include=\"Bar\" />", csproj);
    }

    [Fact]
    public void Existing_central_versions_are_kept_and_running_again_does_nothing()
    {
        var dir = Projects(("A", "    <PackageReference Include=\"Foo\" Version=\"2.0.0\" />\n    <PackageReference Include=\"Bar\" Version=\"1.0.0\" />"));
        File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"),
            "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageVersion Include=\"Foo\" Version=\"1.5.0\" />\n  </ItemGroup>\n</Project>\n");

        FileChange.ApplyAll(CentralPackageManagement.Plan(SolutionLoader.Load(dir)).Changes);
        var props = File.ReadAllText(Path.Combine(dir, "Directory.Packages.props"));

        Assert.Contains("<PackageVersion Include=\"Foo\" Version=\"1.5.0\" />", props);
        Assert.Contains("<PackageVersion Include=\"Bar\" Version=\"1.0.0\" />", props);
        Assert.True(props.IndexOf("\"Bar\"", StringComparison.Ordinal) < props.IndexOf("\"Foo\"", StringComparison.Ordinal)); // sorted
        Assert.Contains("<PackageReference Include=\"Foo\" VersionOverride=\"2.0.0\" />", Csproj(dir, "A"));
        Assert.Empty(CentralPackageManagement.Plan(SolutionLoader.Load(dir)).Changes);
    }

    [Fact]
    public void Legacy_project_files_with_PackageReference_must_be_converted_first()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var core = Path.Combine(dir, "LegacyShop.Core", "LegacyShop.Core.csproj");
        FileChange.ApplyAll(new PackageReferenceMigration().Prepare(ProjectLoader.Load(core), AnyStep, "net10.0"));

        var plan = CentralPackageManagement.Plan(SolutionLoader.Load(dir));

        Assert.NotNull(plan.Blocker);
        Assert.StartsWith("LegacyShop.Core use PackageReference in a legacy", plan.Blocker);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void Later_steps_add_packages_without_versions_once_CPM_is_on()
    {
        var dir = ConvertedSample();
        FileChange.ApplyAll(CentralPackageManagement.Plan(SolutionLoader.Load(dir)).Changes);
        var core = Path.Combine(dir, "LegacyShop.Core", "LegacyShop.Core.csproj");
        FileChange.ApplyAll(new Retarget().Prepare(ProjectLoader.Load(core), AnyStep, "net10.0"));
        var missing = new Diagnostic("CS0103", "The name 'ConfigurationManager' does not exist in the current context",
            Path.Combine(dir, "LegacyShop.Core", "Services", "AppSettings.cs"), 12, core);

        var fix = Assert.Single(BuildFixCatalog.Fixes([missing], ProjectLoader.Load(core), "net10.0"));
        FileChange.ApplyAll(fix.Changes);

        Assert.Equal(2, fix.Changes.Count);
        Assert.Contains("<PackageReference Include=\"System.Configuration.ConfigurationManager\" />", File.ReadAllText(core));
        Assert.Contains("<PackageVersion Include=\"System.Configuration.ConfigurationManager\" Version=\"10.0.0\" />",
            File.ReadAllText(Path.Combine(dir, "Directory.Packages.props")));
    }

    [Fact]
    public void Sdk_conversion_writes_no_versions_under_CPM()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"),
            "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n  </ItemGroup>\n</Project>\n");
        var data = Path.Combine(dir, "LegacyShop.Data", "LegacyShop.Data.csproj");

        var changes = new SdkStyleConversion().Prepare(ProjectLoader.Load(data), AnyStep, "net10.0");

        Assert.Contains("<PackageReference Include=\"EntityFramework\" />", changes[0].NewContent);
        Assert.Contains(changes, c => c.Path.EndsWith("Directory.Packages.props")
                                      && c.NewContent!.Contains("<PackageVersion Include=\"EntityFramework\" Version=\"6.4.4\" />"));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "2.0.0-beta", 1)]
    [InlineData("2.0.0-alpha", "2.0.0-beta", -1)]
    [InlineData("1.0", "1.0.0", 0)]
    public void Compares_versions_like_NuGet(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(VersionComparer.Instance.Compare(a, b)));
    }

    [Fact]
    public async Task Cpm_command_commits_once_and_dry_run_writes_nothing()
    {
        var dir = ConvertedSample();
        Git.InitWithCommit(dir);

        var dry = new StringWriter();
        await new App(new StringReader(""), dry, interactive: false).RunAsync(["cpm", dir, "--dry-run"]);
        Assert.Contains("+++ Directory.Packages.props", dry.ToString());
        Assert.False(File.Exists(Path.Combine(dir, "Directory.Packages.props")));

        var output = new StringWriter();
        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["cpm", dir, "--yes"]);

        Assert.Equal(0, exit);
        Assert.Contains("+ Directory.Packages.props (new)", output.ToString());
        Assert.Contains("dotnet-lift: Central Package Management (Directory.Packages.props)", Git.LogSubjects(dir));
        Assert.Equal("", Git.Status(dir));
    }
}
