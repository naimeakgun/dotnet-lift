using DotnetLift.Cli;
using DotnetLift.Core.Loading;

namespace DotnetLift.Tests;

public class UpgradeCommandTests
{
    private static string RepoWithSample()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        Git.InitWithCommit(dir);
        return dir;
    }

    private static async Task<(int Exit, string Output)> Run(string input, params string[] args)
    {
        var output = new StringWriter();
        var exit = await new App(new StringReader(input), output, interactive: true, new FakeBuilder()).RunAsync(args);
        return (exit, output.ToString());
    }

    private static bool IsSdkStyle(string dir, string name) =>
        ProjectLoader.Load(Path.Combine(dir, name, name + ".csproj")).IsSdkStyle;

    [Fact]
    public async Task Asks_for_each_automatic_step_and_commits_each_one()
    {
        var dir = RepoWithSample();

        // Core: packages yes, SDK-style: show diff then yes, retarget yes. Data: packages skip, SDK-style yes, retarget yes.
        var (exit, output) = await Run("y\nd\ny\ny\nn\ny\ny\n", "upgrade", dir, "--target", "net10.0");

        Assert.Equal(0, exit);
        Assert.Contains("Apply this step?", output);
        Assert.Contains("--- LegacyShop.Core/LegacyShop.Core.csproj", output);
        Assert.Contains("Skipped.", output);
        Assert.Contains("Applied 5, skipped 1", output);
        Assert.Contains("LegacyShop.Core builds for net10.0", output);

        var subjects = Git.LogSubjects(dir);
        Assert.Equal(5, subjects.Count(s => s.StartsWith("dotnet-lift:")));
        Assert.Contains("dotnet-lift: Retarget to net48;net10.0 (LegacyShop.Core)", subjects);
        Assert.Contains("dotnet-lift: Convert to SDK-style project (LegacyShop.Data)", subjects);
        Assert.Equal("", Git.Status(dir)); // everything the tool wrote is committed

        Assert.True(IsSdkStyle(dir, "LegacyShop.Core"));
        Assert.True(IsSdkStyle(dir, "LegacyShop.Data"));
        Assert.False(IsSdkStyle(dir, "LegacyShop.Web"));
        Assert.False(File.Exists(Path.Combine(dir, "LegacyShop.Data", "packages.config")));
    }

    [Fact]
    public async Task Yes_applies_every_automatic_step()
    {
        var dir = RepoWithSample();

        var (exit, output) = await Run("", "upgrade", dir, "--yes");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Apply this step?", output);
        Assert.Equal(6, Git.LogSubjects(dir).Count(s => s.StartsWith("dotnet-lift:")));
        Assert.True(IsSdkStyle(dir, "LegacyShop.Core"));
        Assert.True(IsSdkStyle(dir, "LegacyShop.Data"));
    }

    [Fact]
    public async Task Running_again_finds_nothing_left_to_automate()
    {
        var dir = RepoWithSample();
        await Run("", "upgrade", dir, "--yes");

        var (exit, output) = await Run("", "upgrade", dir, "--yes");

        Assert.Equal(0, exit);
        Assert.Contains("Applied 0", output);
        Assert.Equal(6, Git.LogSubjects(dir).Count(s => s.StartsWith("dotnet-lift:")));
    }

    [Fact]
    public async Task Dry_run_shows_diffs_and_writes_nothing()
    {
        var dir = RepoWithSample();

        var (exit, output) = await Run("", "upgrade", dir, "--dry-run", "--yes");

        Assert.Equal(0, exit);
        Assert.Contains("Dry run", output);
        Assert.Contains("+<Project Sdk=\"Microsoft.NET.Sdk\">", output);
        Assert.Equal("", Git.Status(dir));
        Assert.False(IsSdkStyle(dir, "LegacyShop.Core"));
    }

    [Fact]
    public async Task Quit_stops_without_changes()
    {
        var dir = RepoWithSample();

        var (exit, output) = await Run("q\n", "upgrade", dir, "--target", "net10.0");

        Assert.Equal(0, exit);
        Assert.Contains("Stopped.", output);
        Assert.Single(Git.LogSubjects(dir));
        Assert.Equal("", Git.Status(dir));
    }

    [Fact]
    public async Task Refuses_a_dirty_working_tree_in_unattended_mode()
    {
        var dir = RepoWithSample();
        File.AppendAllText(Path.Combine(dir, "LegacyShop.Core", "Models", "Product.cs"), "// local edit\n");

        var (exit, output) = await Run("", "upgrade", dir, "--yes");

        Assert.Equal(1, exit);
        Assert.Contains("uncommitted changes", output);
        Assert.False(IsSdkStyle(dir, "LegacyShop.Core"));
    }

    [Fact]
    public async Task Works_without_git_when_told_to()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);

        var (exit, output) = await Run("", "upgrade", dir, "--yes");

        Assert.Equal(0, exit);
        Assert.Contains("No git repository found", output);
        Assert.True(IsSdkStyle(dir, "LegacyShop.Core"));
    }

    [Fact]
    public async Task Build_fixes_are_offered_and_committed()
    {
        var dir = RepoWithSample();
        var core = Path.Combine(dir, "LegacyShop.Core", "LegacyShop.Core.csproj");
        var missing = new DotnetLift.Core.Build.Diagnostic("CS0103",
            "The name 'ConfigurationManager' does not exist in the current context",
            Path.Combine(dir, "LegacyShop.Core", "Services", "AppSettings.cs"), 12, core);
        var builder = new FakeBuilder(FakeBuilder.Failure(missing), FakeBuilder.Success);
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: true, builder)
            .RunAsync(["upgrade", dir, "--yes", "--target", "net10.0"]);

        Assert.Equal(0, exit);
        Assert.Contains("Fix: Add package System.Configuration.ConfigurationManager 10.0.0 for net10.0", output.ToString());
        Assert.Contains("dotnet-lift: Add package System.Configuration.ConfigurationManager 10.0.0 for net10.0 (LegacyShop.Core)",
            Git.LogSubjects(dir));
        Assert.Equal("", Git.Status(dir));
    }

    [Fact]
    public async Task Errors_that_need_a_code_change_are_explained()
    {
        var dir = RepoWithSample();
        var core = Path.Combine(dir, "LegacyShop.Core", "LegacyShop.Core.csproj");
        var obsolete = new DotnetLift.Core.Build.Diagnostic("SYSLIB0011", "'BinaryFormatter' is obsolete",
            Path.Combine(dir, "LegacyShop.Core", "Services", "SnapshotSerializer.cs"), 13, core);
        var output = new StringWriter();

        await new App(new StringReader(""), output, interactive: true, new FakeBuilder(FakeBuilder.Failure(obsolete)))
            .RunAsync(["upgrade", dir, "--yes", "--target", "net10.0"]);

        var text = output.ToString();
        Assert.Contains("1 error(s) left that need a code change", text);
        Assert.Contains("BinaryFormatter is removed from modern .NET.", text);
        Assert.Contains("LegacyShop.Core/Services/SnapshotSerializer.cs:13", text);
        Assert.Contains("errors left: LegacyShop.Core", text);
    }
}
