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
        var exit = await new App(new StringReader(input), output, interactive: true).RunAsync(args);
        return (exit, output.ToString());
    }

    private static bool IsSdkStyle(string dir, string name) =>
        ProjectLoader.Load(Path.Combine(dir, name, name + ".csproj")).IsSdkStyle;

    [Fact]
    public async Task Asks_for_each_automatic_step_and_commits_each_one()
    {
        var dir = RepoWithSample();

        // Core: packages yes, SDK-style: show diff then yes. Data: packages skip, SDK-style yes.
        var (exit, output) = await Run("y\nd\ny\nn\ny\n", "upgrade", dir, "--target", "net10.0");

        Assert.Equal(0, exit);
        Assert.Contains("Apply this step?", output);
        Assert.Contains("--- LegacyShop.Core/LegacyShop.Core.csproj", output);
        Assert.Contains("Skipped.", output);
        Assert.Contains("Applied 3, skipped 1", output);

        var subjects = Git.LogSubjects(dir);
        Assert.Equal(3, subjects.Count(s => s.StartsWith("dotnet-lift:")));
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
        Assert.Equal(4, Git.LogSubjects(dir).Count(s => s.StartsWith("dotnet-lift:")));
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
        Assert.Equal(4, Git.LogSubjects(dir).Count(s => s.StartsWith("dotnet-lift:")));
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
}
