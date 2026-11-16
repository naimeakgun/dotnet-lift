using DotnetLift.Core.Build;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class BuildTests
{
    private static string ConvertedSample()
    {
        // The sample after the packages, SDK-style and retarget steps: the state the build loop starts from.
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        foreach (var name in new[] { "LegacyShop.Core", "LegacyShop.Data" })
        {
            var path = Path.Combine(dir, name, name + ".csproj");
            var step = new UpgradeStep(1, name, "retarget", "", "", StepMode.Automatic, Risk.Low, [], "net48;net10.0");
            FileChange.ApplyAll(new PackageReferenceMigration().Prepare(ProjectLoader.Load(path), step, "net10.0"));
            FileChange.ApplyAll(new SdkStyleConversion().Prepare(ProjectLoader.Load(path), step, "net10.0"));
            FileChange.ApplyAll(new Retarget().Prepare(ProjectLoader.Load(path), step, "net10.0"));
        }
        return dir;
    }

    private static string CoreProject(string dir) => Path.Combine(dir, "LegacyShop.Core", "LegacyShop.Core.csproj");

    private static Diagnostic ConfigurationManagerMissing(string project) => new("CS0103",
        "The name 'ConfigurationManager' does not exist in the current context",
        Path.Combine(Path.GetDirectoryName(project)!, "Services", "AppSettings.cs"), 12, project);

    private static Diagnostic BinaryFormatterObsolete(string project) => new("SYSLIB0011",
        "'BinaryFormatter' is obsolete: 'BinaryFormatter serialization is obsolete and should not be used.'",
        Path.Combine(Path.GetDirectoryName(project)!, "Services", "SnapshotSerializer.cs"), 13, project);

    [Fact]
    public void Parses_compiler_and_nuget_errors_and_drops_duplicates()
    {
        const string output = """
            /src/Core/Services/AppSettings.cs(12,27): error CS0103: The name 'ConfigurationManager' does not exist in the current context [/src/Core/Core.csproj::TargetFramework=net10.0]
            /src/Core/Services/AppSettings.cs(12,27): error CS0103: The name 'ConfigurationManager' does not exist in the current context [/src/Core/Core.csproj::TargetFramework=net10.0]
            /src/Core/Services/SnapshotSerializer.cs(13,21): error SYSLIB0011: 'BinaryFormatter' is obsolete: 'See https://aka.ms/binaryformatter for more information.' [/src/Core/Core.csproj::TargetFramework=net10.0]
            /src/Data/Data.csproj : error NU1202: Package Foo 1.0.0 is not compatible with net10.0 (.NETCoreApp,Version=v10.0). [/src/Data/Data.csproj]
            /src/Core/Services/AppSettings.cs(3,7): warning CS0105: The using directive for 'System' appeared previously in this namespace [/src/Core/Core.csproj]
              Determining projects to restore...
            """;

        var errors = DiagnosticParser.ParseErrors(output);

        Assert.Equal(3, errors.Count);
        Assert.Equal(new Diagnostic("CS0103", "The name 'ConfigurationManager' does not exist in the current context",
            "/src/Core/Services/AppSettings.cs", 12, "/src/Core/Core.csproj"), errors[0]);
        Assert.Equal("SYSLIB0011", errors[1].Code);
        Assert.Contains("https://aka.ms/binaryformatter", errors[1].Message);
        Assert.Equal("NU1202", errors[2].Code);
        Assert.Equal("/src/Data/Data.csproj", errors[2].File);
        Assert.Null(errors[2].Line);
    }

    [Fact]
    public void Symbols_include_the_namespace_for_CS0234()
    {
        var d = new Diagnostic("CS0234", "The type or namespace name 'Caching' does not exist in the namespace 'System.Runtime' (are you missing an assembly reference?)", null, null, null);

        Assert.Equal(new[] { "Caching", "System.Runtime.Caching" }, d.Symbols());
    }

    [Fact]
    public void Retarget_multi_targets_and_keeps_framework_references_for_net48_only()
    {
        var dir = ConvertedSample();
        var data = ProjectLoader.Load(Path.Combine(dir, "LegacyShop.Data", "LegacyShop.Data.csproj"));
        var text = File.ReadAllText(data.FullPath);

        Assert.Equal("net48;net10.0", data.TargetFramework);
        Assert.Contains("<TargetFrameworks>net48;net10.0</TargetFrameworks>", text);
        Assert.Contains("<ItemGroup Condition=\"'$(TargetFramework)' == 'net48'\">", text);
        Assert.Contains("<Reference Include=\"System.Web\" />", text);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, "<Reference ").Count); // moved, not duplicated
    }

    [Fact]
    public void Retarget_twice_changes_nothing()
    {
        var dir = ConvertedSample();
        var core = ProjectLoader.Load(CoreProject(dir));
        var step = new UpgradeStep(1, core.Name, "retarget", "", "", StepMode.Automatic, Risk.Low, [], "net48;net10.0");

        Assert.Empty(new Retarget().Prepare(core, step, "net10.0"));
    }

    [Fact]
    public void Missing_ConfigurationManager_becomes_a_package_for_the_new_target_only()
    {
        var dir = ConvertedSample();
        var core = ProjectLoader.Load(CoreProject(dir));

        var fix = Assert.Single(BuildFixCatalog.Fixes([ConfigurationManagerMissing(core.FullPath), BinaryFormatterObsolete(core.FullPath)], core, "net10.0"));

        Assert.Contains("System.Configuration.ConfigurationManager", fix.Title);
        Assert.Single(fix.Resolves);
        var csproj = fix.Changes.Single().NewContent!;
        Assert.Contains("<ItemGroup Condition=\"'$(TargetFramework)' == 'net10.0'\">", csproj);
        Assert.Contains("<PackageReference Include=\"System.Configuration.ConfigurationManager\" Version=\"10.0.0\" />", csproj);
    }

    [Fact]
    public void No_fix_is_offered_twice()
    {
        var dir = ConvertedSample();
        var core = ProjectLoader.Load(CoreProject(dir));
        FileChange.ApplyAll(BuildFixCatalog.Fixes([ConfigurationManagerMissing(core.FullPath)], core, "net10.0")[0].Changes);

        var again = BuildFixCatalog.Fixes([ConfigurationManagerMissing(core.FullPath)], ProjectLoader.Load(core.FullPath), "net10.0");

        Assert.Empty(again);
    }

    [Fact]
    public void Known_errors_without_a_fix_come_with_guidance()
    {
        Assert.Contains("BinaryFormatter", BuildFixCatalog.Explain(BinaryFormatterObsolete("/x/Core.csproj"))!.Summary);

        var web = new Diagnostic("CS0234", "The type or namespace name 'Web' does not exist in the namespace 'System' (are you missing an assembly reference?)", null, null, null);
        Assert.Contains("System.Web", BuildFixCatalog.Explain(web)!.Summary);

        var mvc = new Diagnostic("CS0246", "The type or namespace name 'ApiController' could not be found", null, null, null);
        Assert.Contains("MVC", BuildFixCatalog.Explain(mvc)!.Summary);

        Assert.Null(BuildFixCatalog.Explain(new Diagnostic("CS1002", "; expected", null, null, null)));
    }

    [Fact]
    public void Loop_applies_the_fix_rebuilds_and_reports_what_is_left()
    {
        var dir = ConvertedSample();
        var core = CoreProject(dir);
        var builder = new FakeBuilder(
            FakeBuilder.Failure(ConfigurationManagerMissing(core), BinaryFormatterObsolete(core)),
            FakeBuilder.Failure(BinaryFormatterObsolete(core)));
        var applied = new List<BuildFix>();

        var result = new BuildFixLoop(builder).Run(core, "net10.0", _ => FixDecision.Apply, applied.Add, _ => { });

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FixesApplied);
        Assert.Equal("SYSLIB0011", Assert.Single(result.Remaining).Code);
        Assert.Equal(2, builder.Calls.Count);
        Assert.All(builder.Calls, c => Assert.Equal("net10.0", c.Tfm));
        Assert.Contains("System.Configuration.ConfigurationManager", File.ReadAllText(core));
    }

    [Fact]
    public void Loop_stops_when_the_user_skips_the_fix()
    {
        var dir = ConvertedSample();
        var core = CoreProject(dir);
        var before = File.ReadAllText(core);
        var builder = new FakeBuilder(FakeBuilder.Failure(ConfigurationManagerMissing(core)));

        var result = new BuildFixLoop(builder).Run(core, "net10.0", _ => FixDecision.Skip, _ => { }, _ => { });

        Assert.Equal(0, result.FixesApplied);
        Assert.Single(result.Remaining);
        Assert.Single(builder.Calls);
        Assert.Equal(before, File.ReadAllText(core));
    }

    [Fact]
    public void Loop_reports_a_failing_dependency_instead_of_guessing()
    {
        var dir = ConvertedSample();
        var core = CoreProject(dir);
        var data = Path.Combine(dir, "LegacyShop.Data", "LegacyShop.Data.csproj");
        var builder = new FakeBuilder(FakeBuilder.Failure(BinaryFormatterObsolete(core)));

        var result = new BuildFixLoop(builder).Run(data, "net10.0", _ => FixDecision.Apply, _ => { }, _ => { });

        Assert.True(result.BlockedByDependency);
        Assert.Equal(core, Assert.Single(result.Remaining).Project);
    }

    [Fact]
    public void Loop_succeeds_when_the_build_does()
    {
        var dir = ConvertedSample();

        var result = new BuildFixLoop(new FakeBuilder()).Run(CoreProject(dir), "net10.0", _ => FixDecision.Apply, _ => { }, _ => { });

        Assert.True(result.Succeeded);
        Assert.Empty(result.Remaining);
    }
}
