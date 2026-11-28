using DotnetLift.Cli;
using DotnetLift.Core;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class BinaryFormatterCompatibilityTests
{
    private static string CorePath(string dir) => Path.Combine(dir, "LegacyShop.Core", "LegacyShop.Core.csproj");

    private static MigrationProfile Profile(string dir, string json)
    {
        var path = Path.Combine(dir, MigrationProfile.FileName);
        File.WriteAllText(path, json);
        return MigrationProfile.Load(path);
    }

    /// <summary>The sample's core library after the steps that come before this one (packages, SDK-style, retarget).</summary>
    private static string CoreRetargetedTo(string dir, string targets)
    {
        var path = CorePath(dir);
        var step = new UpgradeStep(1, "LegacyShop.Core", "x", "", "", StepMode.Automatic, Risk.Medium, [], targets);
        FileChange.ApplyAll(new PackageReferenceMigration().Prepare(ProjectLoader.Load(path), step, "net10.0"));
        FileChange.ApplyAll(new SdkStyleConversion().Prepare(ProjectLoader.Load(path), step, "net10.0"));
        FileChange.ApplyAll(new Retarget().Prepare(ProjectLoader.Load(path), step, "net10.0"));
        return path;
    }

    private static UpgradeStep CompatStep() =>
        new(1, "LegacyShop.Core", "binaryformatter-compat", "", "", StepMode.Automatic, Risk.Medium, []);

    [Fact]
    public void Profile_reads_the_strategy_with_per_project_exceptions()
    {
        var profile = Profile(Samples.CopyToTemp(Samples.LegacyShopDir), """
            {
              "binaryFormatter": "compatibility",
              "projects": {
                "*.Caching": { "binaryFormatter": "json" },
                "Acme.*": { "multiTarget": "never" }
              }
            }
            """);

        Assert.Equal(BinaryFormatterStrategy.Compatibility, profile.BinaryFormatter);
        Assert.Equal(BinaryFormatterStrategy.Json, profile.BinaryFormatterFor("Acme.Caching"));
        // An entry that does not set the key does not reset it.
        Assert.Equal(BinaryFormatterStrategy.Compatibility, profile.BinaryFormatterFor("Acme.Orders"));
        Assert.Equal(MultiTargetMode.Never, profile.MultiTargetFor("Acme.Caching")); // first entry that sets multiTarget
        Assert.Contains("binaryFormatter: compatibility", profile.Describe());
        Assert.Equal(BinaryFormatterStrategy.Json, MigrationProfile.Default.BinaryFormatterFor("Anything"));
    }

    [Theory]
    [InlineData("""{ "binaryFormatter": "keep" }""", "\"binaryFormatter\" must be one of json, compatibility")]
    [InlineData("""{ "projects": { "A": { "binaryFormatter": 1 } } }""", "\"projects.\"A\".binaryFormatter\" must be a string")]
    public void Invalid_strategies_are_reported(string json, string expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, MigrationProfile.FileName);
        File.WriteAllText(path, json);

        var ex = Assert.Throws<InvalidDataException>(() => MigrationProfile.Load(path));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Planner_uses_the_compatibility_step_instead_of_the_json_rewrite()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var analysis = new SolutionAnalyzer().Analyze(dir);

        var plan = UpgradePlanner.Create(analysis, Profile(dir, """{ "binaryFormatter": "compatibility" }"""));

        Assert.DoesNotContain(plan.Steps, s => s.Id == "binaryformatter-json");
        var step = Assert.Single(plan.Steps, s => s.Id == "binaryformatter-compat");
        Assert.Equal("LegacyShop.Core", step.Project);
        Assert.Equal("Keep BinaryFormatter with the compatibility package", step.Title);
        Assert.Contains("System.Runtime.Serialization.Formatters 10.0.0", step.Description);
        // The switch is read by the application, so the projects that use the library are named.
        Assert.Contains("LegacyShop.Data, LegacyShop.Web must set it too", step.Description);
        Assert.NotEmpty(step.Findings);
        Assert.Equal("build", plan.Steps[plan.Steps.ToList().IndexOf(step) + 1].Id);
        Assert.DoesNotContain(plan.Steps, s => s.Project == "LegacyShop.Core" && s.Id == "removed-apis");
    }

    [Fact]
    public void A_project_exception_keeps_the_json_rewrite()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var profile = Profile(dir, """{ "binaryFormatter": "compatibility", "projects": { "LegacyShop.Core": { "binaryFormatter": "json" } } }""");

        var plan = UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir), profile);

        Assert.Contains(plan.Steps, s => s.Id == "binaryformatter-json");
        Assert.DoesNotContain(plan.Steps, s => s.Id == "binaryformatter-compat");
    }

    [Fact]
    public void Multi_targeted_library_gets_the_package_for_the_modern_target_only()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var path = CoreRetargetedTo(dir, "net48;net10.0");
        IStepExecutor executor = new BinaryFormatterCompatibility();

        Assert.Null(executor.NotReadyReason(ProjectLoader.Load(path)));
        Assert.False(executor.RequiresExplicitApproval); // the format does not change
        var change = Assert.Single(executor.Prepare(ProjectLoader.Load(path), CompatStep(), "net10.0"));
        var csproj = change.NewContent!;

        var modernGroup = csproj[csproj.IndexOf("'$(TargetFramework)' == 'net10.0'", StringComparison.Ordinal)..];
        Assert.Contains("<PackageReference Include=\"System.Runtime.Serialization.Formatters\" Version=\"10.0.0\" />",
            modernGroup[..modernGroup.IndexOf("</ItemGroup>", StringComparison.Ordinal)]);
        Assert.Equal(1, Count(csproj, "System.Runtime.Serialization.Formatters\""));
        Assert.Contains("<EnableUnsafeBinaryFormatterSerialization>true</EnableUnsafeBinaryFormatterSerialization>", csproj);
        Assert.Contains("<NoWarn>$(NoWarn);SYSLIB0011</NoWarn>", csproj);
        Assert.Contains("\"binaryFormatter\": \"compatibility\"", csproj);

        // Applied once, there is nothing left to do; the source code is never touched.
        FileChange.ApplyAll([change]);
        Assert.Empty(executor.Prepare(ProjectLoader.Load(path), CompatStep(), "net10.0"));
        Assert.Contains("new BinaryFormatter()", File.ReadAllText(Path.Combine(dir, "LegacyShop.Core", "Services", "SnapshotSerializer.cs")));
        Assert.Contains("net10.0", ProjectLoader.Load(path).TargetFramework); // still a loadable project file
    }

    [Fact]
    public void Single_target_library_gets_an_unconditional_package_reference()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var path = CoreRetargetedTo(dir, "net10.0");

        var csproj = Assert.Single(new BinaryFormatterCompatibility().Prepare(ProjectLoader.Load(path), CompatStep(), "net10.0")).NewContent!;

        var reference = csproj.IndexOf("<PackageReference Include=\"System.Runtime.Serialization.Formatters\"", StringComparison.Ordinal);
        Assert.True(reference > 0);
        var group = csproj.LastIndexOf("<ItemGroup", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("Condition", csproj[group..reference]);
    }

    [Fact]
    public void With_central_package_management_the_version_goes_to_Directory_Packages_props()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        File.WriteAllText(Path.Combine(dir, CentralPackageFile.FileName), """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
              </ItemGroup>
            </Project>
            """);
        var path = CoreRetargetedTo(dir, "net48;net10.0");

        var changes = new BinaryFormatterCompatibility().Prepare(ProjectLoader.Load(path), CompatStep(), "net10.0");

        Assert.Contains("<PackageReference Include=\"System.Runtime.Serialization.Formatters\" />",
            changes.Single(c => c.Path.EndsWith(".csproj", StringComparison.Ordinal)).NewContent);
        Assert.Contains("<PackageVersion Include=\"System.Runtime.Serialization.Formatters\" Version=\"10.0.0\" />",
            changes.Single(c => c.Path.EndsWith(CentralPackageFile.FileName, StringComparison.Ordinal)).NewContent);
    }

    [Fact]
    public void Not_ready_before_the_project_targets_modern_dotnet()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);

        Assert.NotNull(((IStepExecutor)new BinaryFormatterCompatibility()).NotReadyReason(ProjectLoader.Load(CorePath(dir))));
    }

    [Fact]
    public async Task Upgrade_with_the_profile_keeps_the_code_and_needs_no_explicit_approval()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        File.WriteAllText(Path.Combine(dir, MigrationProfile.FileName), """{ "target": "net10.0", "binaryFormatter": "compatibility" }""");
        Git.InitWithCommit(dir);
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false, new FakeBuilder()).RunAsync(["upgrade", dir, "--yes"]);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("needs your explicit approval", output.ToString());
        Assert.Contains("dotnet-lift: Keep BinaryFormatter with the compatibility package (LegacyShop.Core)", Git.LogSubjects(dir));
        Assert.Contains("new BinaryFormatter().Serialize(", File.ReadAllText(Path.Combine(dir, "LegacyShop.Core", "Services", "SnapshotSerializer.cs")));
        var csproj = File.ReadAllText(CorePath(dir));
        Assert.Contains("System.Runtime.Serialization.Formatters", csproj);
        Assert.Contains("<EnableUnsafeBinaryFormatterSerialization>true</EnableUnsafeBinaryFormatterSerialization>", csproj);
        Assert.DoesNotContain("System.Text.Json", csproj);
        Assert.Equal("", Git.Status(dir));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
