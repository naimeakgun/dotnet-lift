using DotnetLift.Cli;

namespace DotnetLift.Tests;

public class CliTests
{
    [Fact]
    public async Task Analyze_asks_for_the_target_and_writes_the_report()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var input = new StringReader("2\n\n"); // pick .NET 11, accept the default for "write report?"
        var output = new StringWriter();

        var exit = await new App(input, output, interactive: true).RunAsync(["analyze", dir]);

        Assert.Equal(0, exit);
        Assert.Contains("Which .NET version", output.ToString());
        Assert.Contains("Target: net11.0", output.ToString());
        var report = await File.ReadAllTextAsync(Path.Combine(dir, "lift-report.md"));
        Assert.Contains("`net11.0`", report);
    }

    [Fact]
    public async Task Yes_flag_uses_defaults_without_reading_input()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: true).RunAsync(["analyze", dir, "--yes"]);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Which .NET version", output.ToString());
        Assert.Contains("Target: net10.0", output.ToString());
        Assert.True(File.Exists(Path.Combine(dir, "lift-report.md")));
    }

    [Fact]
    public async Task Summary_lists_blockers()
    {
        var output = new StringWriter();

        await new App(new StringReader(""), output, interactive: false)
            .RunAsync(["analyze", Samples.LegacyShopSln, "--no-report"]);

        Assert.Contains("Blockers:", output.ToString());
        Assert.Contains("BinaryFormatter", output.ToString());
    }

    [Fact]
    public async Task Unknown_target_is_rejected()
    {
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["analyze", "--target", "net5.0"]);

        Assert.Equal(2, exit);
        Assert.Contains("Unsupported target", output.ToString());
    }

    [Fact]
    public async Task Missing_path_is_a_clean_error()
    {
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false)
            .RunAsync(["analyze", "/definitely/not/here", "--yes"]);

        Assert.Equal(1, exit);
        Assert.Contains("error:", output.ToString());
    }
}
