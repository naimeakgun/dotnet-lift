using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Upgrade;

/// <summary>A project that needs the one being removed, and how.</summary>
/// <param name="Project">The dependent project's name.</param>
/// <param name="How">"project reference", "file reference to A.dll" or "through B" for an indirect dependent.</param>
/// <param name="Direct">False when it only depends on it through another dependent.</param>
public sealed record ProjectDependent(string Project, string How, bool Direct);

/// <param name="Project">The project to remove.</param>
/// <param name="Dependents">Direct dependents first, then indirect ones; empty means it can go.</param>
/// <param name="SolutionChange">The edited .sln; null while there are direct dependents.</param>
/// <param name="BuildOrderEntries">Projects whose build-order entry (ProjectDependencies) on it is removed from the .sln with it.</param>
public sealed record ProjectRemovalPlan(
    ProjectInfo Project,
    IReadOnlyList<ProjectDependent> Dependents,
    FileChange? SolutionChange,
    IReadOnlyList<string> BuildOrderEntries)
{
    public bool CanRemove => SolutionChange is not null;
}

/// <summary>
/// Removes one project from the .sln once nothing in the solution depends on it: no project reference and no
/// file reference to its DLL, directly or through another project. The project's folder stays on disk;
/// only the solution entry, its configuration lines and build-order entries pointing at it go.
/// </summary>
public static partial class ProjectRemoval
{
    [GeneratedRegex("""^\s*Project\("\{[^}]+\}"\)\s*=\s*"(?<name>[^"]+)",\s*"(?<path>[^"]+)",\s*"(?<guid>\{[^}]+\})"\s*$""")]
    private static partial Regex ProjectLine();

    public static ProjectRemovalPlan Plan(SolutionInfo solution, string projectName)
    {
        if (!solution.Path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || !File.Exists(solution.Path))
        {
            throw new InvalidOperationException("Removing a project needs a .sln file; pass the solution (or its folder).");
        }

        var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No project named '{projectName}' in {Path.GetFileName(solution.Path)}. Projects: {string.Join(", ", solution.Projects.Select(p => p.Name))}.");

        var dependents = Dependents(solution, project);
        if (dependents.Any(d => d.Direct))
        {
            return new ProjectRemovalPlan(project, dependents, null, []);
        }

        var (content, buildOrder) = RemoveFromSolution(File.ReadAllText(solution.Path), solution.Path, project);
        return new ProjectRemovalPlan(project, dependents, new FileChange(solution.Path, content), buildOrder);
    }

    /// <summary>Every project that needs <paramref name="target"/>, breadth first: direct dependents, then theirs.</summary>
    internal static List<ProjectDependent> Dependents(SolutionInfo solution, ProjectInfo target)
    {
        var result = new List<ProjectDependent>();
        var seen = new HashSet<ProjectInfo> { target };
        var queue = new Queue<ProjectInfo>([target]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var assembly = AssemblyName(current);
            foreach (var other in solution.Projects.Where(p => !seen.Contains(p)))
            {
                var how = HowItUses(other, current, assembly);
                if (how is null)
                {
                    continue;
                }

                var direct = current == target;
                result.Add(new ProjectDependent(other.Name, direct ? how : $"through {current.Name}", direct));
                seen.Add(other);
                queue.Enqueue(other);
            }
        }

        return result;
    }

    private static string? HowItUses(ProjectInfo user, ProjectInfo used, string usedAssembly)
    {
        var full = Path.GetFullPath(used.FullPath);
        if (user.ProjectReferences.Any(r => Path.GetFullPath(r).Equals(full, StringComparison.OrdinalIgnoreCase)))
        {
            return "project reference";
        }

        if (user.FileReferences.FirstOrDefault(f => f.AssemblyName.Equals(usedAssembly, StringComparison.OrdinalIgnoreCase)) is { } file)
        {
            return $"file reference to {Path.GetFileName(file.HintPath.Replace('\\', '/'))}";
        }

        return user.References(usedAssembly) ? $"reference to {usedAssembly}" : null;
    }

    /// <summary>The &lt;AssemblyName&gt; of the project, or its file name when it has none.</summary>
    internal static string AssemblyName(ProjectInfo project)
    {
        try
        {
            var name = XDocument.Load(project.FullPath).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "AssemblyName" && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();
            return string.IsNullOrEmpty(name) || name.Contains("$(", StringComparison.Ordinal) ? project.Name : name;
        }
        catch (XmlException)
        {
            return project.Name;
        }
    }

    /// <summary>
    /// The .sln without the project's Project … EndProject block and without every other line that starts with its
    /// GUID (configuration lines, NestedProjects, other projects' ProjectDependencies). Line endings are kept.
    /// </summary>
    internal static (string Content, List<string> BuildOrderEntries) RemoveFromSolution(string text, string slnPath, ProjectInfo project)
    {
        var lines = text.Split('\n');
        var slnDir = Path.GetDirectoryName(slnPath)!;
        var full = Path.GetFullPath(project.FullPath);

        string? guid = null;
        foreach (var line in lines)
        {
            var m = ProjectLine().Match(line.TrimEnd('\r'));
            if (m.Success && Path.GetFullPath(PathUtil.Combine(slnDir, m.Groups["path"].Value)).Equals(full, StringComparison.OrdinalIgnoreCase))
            {
                guid = m.Groups["guid"].Value;
                break;
            }
        }
        if (guid is null)
        {
            throw new InvalidDataException($"Could not find the entry of {project.Name} in {Path.GetFileName(slnPath)}.");
        }

        var kept = new List<string>(lines.Length);
        var buildOrder = new List<string>();
        var inRemovedBlock = false;
        string? currentProject = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var m = ProjectLine().Match(line.TrimEnd('\r'));
            if (m.Success)
            {
                currentProject = m.Groups["name"].Value;
                if (m.Groups["guid"].Value.Equals(guid, StringComparison.OrdinalIgnoreCase))
                {
                    inRemovedBlock = true;
                    continue;
                }
            }

            if (inRemovedBlock)
            {
                if (trimmed.Equals("EndProject", StringComparison.Ordinal))
                {
                    inRemovedBlock = false;
                    currentProject = null;
                }
                continue;
            }

            if (trimmed.Equals("EndProject", StringComparison.Ordinal))
            {
                currentProject = null;
            }

            if (trimmed.StartsWith(guid, StringComparison.OrdinalIgnoreCase))
            {
                if (currentProject is not null)
                {
                    buildOrder.Add(currentProject);
                }
                continue;
            }

            kept.Add(line);
        }

        return (string.Join('\n', kept), buildOrder);
    }
}
