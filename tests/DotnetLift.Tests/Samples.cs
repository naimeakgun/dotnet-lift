namespace DotnetLift.Tests;

internal static class Samples
{
    public static string Root { get; } = FindRepoRoot();

    public static string LegacyShopDir => Path.Combine(Root, "samples", "LegacyShop");
    public static string LegacyShopSln => Path.Combine(LegacyShopDir, "LegacyShop.sln");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DotnetLift.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (DotnetLift.slnx).");
    }

    /// <summary>Copies a sample into a temp folder so tests that write files never touch the repo.</summary>
    public static string CopyToTemp(string sourceDir)
    {
        var target = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return target;
    }
}
