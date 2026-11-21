namespace DotnetLift.Core.Loading;

internal static class PathUtil
{
    /// <summary>Legacy .sln/.csproj files use Windows separators; normalize for the current OS.</summary>
    public static string Normalize(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    public static string Combine(string baseDir, string relative) =>
        Path.GetFullPath(Path.Combine(baseDir, Normalize(relative)));

    public static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}packages{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}node_modules{sep}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The backup copies Visual Studio leaves when it migrates a project (MigrationBackup, _UpgradeReport_Files):
    /// the loader skips them, so an old copy of a project file is never read as a project.
    /// Not part of <see cref="IsBuildOutput"/>: the SDK would still compile .cs files in them.
    /// </summary>
    public static bool IsBackup(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}MigrationBackup{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}_UpgradeReport_Files{sep}", StringComparison.OrdinalIgnoreCase);
    }

    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
