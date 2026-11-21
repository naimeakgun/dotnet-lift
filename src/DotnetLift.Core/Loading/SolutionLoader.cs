using System.Text.RegularExpressions;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Loading;

/// <summary>
/// Loads a solution from a .sln file, a .csproj file or a directory containing one.
/// Deliberately does not depend on MSBuild so it can read .NET Framework projects on any OS.
/// </summary>
public static partial class SolutionLoader
{
    private const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    [GeneratedRegex("""^Project\("\{(?<type>[^}]+)\}"\)\s*=\s*"(?<name>[^"]+)",\s*"(?<path>[^"]+)",""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();

    public static SolutionInfo Load(string inputPath)
    {
        var full = Path.GetFullPath(inputPath);

        if (Directory.Exists(full))
        {
            var sln = Directory.GetFiles(full, "*.sln").OrderBy(f => f).FirstOrDefault();
            if (sln is not null)
            {
                return LoadSolution(sln);
            }

            var projects = Directory.GetFiles(full, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !PathUtil.IsBuildOutput(p) && !PathUtil.IsBackup(p))
                .OrderBy(p => p)
                .ToList();
            if (projects.Count == 0)
            {
                throw new FileNotFoundException($"No .sln or .csproj file found under '{full}'.");
            }

            var info = new SolutionInfo { Path = full };
            foreach (var p in projects)
            {
                info.Projects.Add(ProjectLoader.Load(p));
            }
            return info;
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"'{full}' does not exist.");
        }

        return Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".sln" => LoadSolution(full),
            ".csproj" => LoadSingleProject(full),
            var ext => throw new NotSupportedException($"Unsupported input type '{ext}'. Pass a .sln, .csproj or folder."),
        };
    }

    private static SolutionInfo LoadSolution(string slnPath)
    {
        var info = new SolutionInfo { Path = slnPath };
        var dir = Path.GetDirectoryName(slnPath)!;
        var text = File.ReadAllText(slnPath);

        foreach (Match m in SlnProjectLine().Matches(text))
        {
            if (m.Groups["type"].Value.Equals(SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relPath = m.Groups["path"].Value;
            if (!relPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue; // VB, F#, database projects etc. are out of scope for now.
            }

            var projPath = PathUtil.Combine(dir, relPath);
            if (File.Exists(projPath))
            {
                info.Projects.Add(ProjectLoader.Load(projPath));
            }
        }

        return info;
    }

    private static SolutionInfo LoadSingleProject(string projPath)
    {
        var info = new SolutionInfo { Path = projPath };
        info.Projects.Add(ProjectLoader.Load(projPath));
        return info;
    }
}
