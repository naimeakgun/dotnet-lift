using DotnetLift.Cli;
using DotnetLift.Core;
using DotnetLift.Core.Planning;

namespace DotnetLift.Tests;

public class ProfileTests
{
    private static string Write(string dir, string json)
    {
        var path = Path.Combine(dir, MigrationProfile.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Reads_every_setting_with_comments_and_per_project_wildcards()
    {
        var path = Write(TempDir(), """
            {
              // team decisions
              "target": "net10.0",
              "multiTarget": "never",
              "commits": "single",
              "codePage": 1254,
              "projects": {
                "Acme.Shared": { "multiTarget": "always" },
                "*.Tests": { "multiTarget": "auto" },
              },
            }
            """);

        var profile = MigrationProfile.Load(path);

        Assert.Equal("net10.0", profile.Target);
        Assert.Equal(MultiTargetMode.Never, profile.MultiTarget);
        Assert.Equal(CommitMode.Single, profile.Commits);
        Assert.Equal(1254, profile.CodePage);
        Assert.Equal(MultiTargetMode.Always, profile.MultiTargetFor("acme.shared"));
        Assert.Equal(MultiTargetMode.Auto, profile.MultiTargetFor("Acme.Core.Tests"));
        Assert.Equal(MultiTargetMode.Never, profile.MultiTargetFor("Acme.Core"));
    }

    [Theory]
    [InlineData("""{ "multiTargets": "never" }""", "unknown setting \"multiTargets\"")]
    [InlineData("""{ "multiTarget": "sometimes" }""", "\"multiTarget\" must be one of auto, always, never")]
    [InlineData("""{ "target": "net8.0" }""", "\"target\" must be one of net10.0, net11.0")]
    [InlineData("""{ "codePage": "1254" }""", "\"codePage\" must be a positive number")]
    [InlineData("""{ "projects": { "A": { "commits": "single" } } }""", "unknown setting \"commits\"")]
    [InlineData("""{ "target": """, "is not valid JSON")]
    public void Mistakes_are_reported_clearly(string json, string expected)
    {
        var path = Write(TempDir(), json);

        var ex = Assert.Throws<InvalidDataException>(() => MigrationProfile.Load(path));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Find_walks_up_from_the_input_and_falls_back_to_the_defaults()
    {
        var root = TempDir();
        Write(root, """{ "commits": "single" }""");
        var nested = Path.Combine(root, "src", "Lib");
        Directory.CreateDirectory(nested);

        Assert.Equal(CommitMode.Single, MigrationProfile.Find(nested).Commits);
        Assert.True(MigrationProfile.Find(TempDir()).IsDefault);
    }

    [Fact]
    public void The_template_loads_and_means_the_defaults()
    {
        var profile = MigrationProfile.Load(Write(TempDir(), MigrationProfile.Template()));

        Assert.Equal(MultiTargetMode.Auto, profile.MultiTarget);
        Assert.Equal(CommitMode.Step, profile.Commits);
        Assert.Null(profile.CodePage);
        Assert.Equal(BinaryFormatterStrategy.Json, profile.BinaryFormatter);
    }

    [Fact]
    public void Never_retargets_libraries_to_the_new_framework_only()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var profile = MigrationProfile.Load(Write(dir, """{ "multiTarget": "never", "projects": { "LegacyShop.Core": { "multiTarget": "always" } } }"""));

        var plan = UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir), profile);

        Assert.Equal("Retarget to net48;net10.0", plan.Steps.Single(s => s.Project == "LegacyShop.Core" && s.Id == "retarget").Title);
        var data = plan.Steps.Single(s => s.Project == "LegacyShop.Data" && s.Id == "retarget");
        Assert.Equal("Retarget to net10.0", data.Title);
        Assert.Equal("net10.0", data.Argument);
        Assert.Contains("last .NET Framework build", data.Description);
    }

    [Fact]
    public void Without_a_profile_the_plan_is_unchanged()
    {
        var analysis = new SolutionAnalyzer().Analyze(Samples.LegacyShopSln);

        var withDefault = UpgradePlanner.Create(analysis, MigrationProfile.Default);
        var without = UpgradePlanner.Create(analysis);

        Assert.Equal(without.Steps.Select(s => (s.Id, s.Title, s.Argument)), withDefault.Steps.Select(s => (s.Id, s.Title, s.Argument)));
        Assert.Equal("Retarget to net48;net10.0", without.Steps.First(s => s.Id == "retarget").Title);
    }

    [Fact]
    public async Task Single_commit_mode_records_the_whole_upgrade_as_one_commit()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        Write(dir, """{ "target": "net10.0", "commits": "single" }""");
        Git.InitWithCommit(dir);
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: true, new FakeBuilder()).RunAsync(["upgrade", dir, "--yes"]);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Which .NET version", output.ToString()); // the target comes from the profile
        Assert.Contains("Profile: ", output.ToString());
        var commits = Git.LogSubjects(dir).Where(s => s.StartsWith("dotnet-lift:")).ToList();
        Assert.StartsWith("dotnet-lift: upgrade to net10.0 (", Assert.Single(commits));
        Assert.Equal("", Git.Status(dir));
    }

    [Fact]
    public async Task Single_target_upgrade_drops_framework_references()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        Write(dir, """{ "target": "net10.0", "multiTarget": "never" }""");
        Git.InitWithCommit(dir);

        await new App(new StringReader(""), new StringWriter(), interactive: false, new FakeBuilder()).RunAsync(["upgrade", dir, "--yes"]);

        var core = File.ReadAllText(Path.Combine(dir, "LegacyShop.Core", "LegacyShop.Core.csproj"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", core);
        Assert.DoesNotContain("net48", core);
        Assert.DoesNotContain("<Reference Include=\"System.Configuration\" />", core);
    }

    [Fact]
    public async Task Init_writes_the_template_once()
    {
        var dir = TempDir();
        var first = new StringWriter();
        var second = new StringWriter();

        Assert.Equal(0, await new App(new StringReader(""), first, interactive: false).RunAsync(["init", dir]));
        Assert.Equal(1, await new App(new StringReader(""), second, interactive: false).RunAsync(["init", dir]));

        Assert.True(File.Exists(Path.Combine(dir, MigrationProfile.FileName)));
        Assert.Contains("already exists", second.ToString());
    }

    [Fact]
    public async Task A_broken_profile_stops_with_a_clear_error()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        Write(dir, """{ "multiTarget": "maybe" }""");
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["analyze", dir, "--no-report"]);

        Assert.Equal(1, exit);
        Assert.Contains("error: dotnet-lift.json: \"multiTarget\" must be one of auto, always, never", output.ToString());
    }
}
