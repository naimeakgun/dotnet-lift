using DotnetLift.Cli;

namespace DotnetLift.Tests;

/// <summary>
/// Golden-file test: the upgraded sample must match samples/LegacyShop.Expected exactly.
/// Set DOTNET_LIFT_UPDATE_SNAPSHOTS=1 to rewrite the expected files from the actual output.
/// </summary>
public class SnapshotTests
{
    private static string ExpectedDir => Path.Combine(Samples.Root, "samples", "LegacyShop.Expected");

    private static readonly string[] DeletedFiles =
    [
        "LegacyShop.Core/packages.config",
        "LegacyShop.Data/packages.config",
    ];

    [Fact]
    public async Task Upgraded_sample_matches_the_expected_files()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var exit = await new App(new StringReader(""), new StringWriter(), interactive: false)
            .RunAsync(["upgrade", dir, "--yes", "--target", "net10.0"]);
        Assert.Equal(0, exit);

        var update = Environment.GetEnvironmentVariable("DOTNET_LIFT_UPDATE_SNAPSHOTS") == "1";
        var expectedFiles = Directory.EnumerateFiles(ExpectedDir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(expectedFiles);

        foreach (var expectedPath in expectedFiles)
        {
            var relative = Path.GetRelativePath(ExpectedDir, expectedPath);
            var actualPath = Path.Combine(dir, relative);
            Assert.True(File.Exists(actualPath), $"Upgrade did not produce {relative}");

            var actual = Normalize(await File.ReadAllTextAsync(actualPath));
            if (update)
            {
                await File.WriteAllTextAsync(expectedPath, actual);
                continue;
            }

            var expected = Normalize(await File.ReadAllTextAsync(expectedPath));
            Assert.True(expected == actual,
                $"{relative} differs from the snapshot.{Environment.NewLine}--- actual ---{Environment.NewLine}{actual}");
        }

        foreach (var deleted in DeletedFiles)
        {
            Assert.False(File.Exists(Path.Combine(dir, deleted)), $"{deleted} should have been deleted");
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
