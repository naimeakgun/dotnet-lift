using System.Xml.Linq;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Upgrade;

public sealed record CpmPlan(
    string PropsPath,
    IReadOnlyList<FileChange> Changes,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Notes,
    string? Blocker);

/// <summary>
/// Turns on Central Package Management for a solution: one Directory.Packages.props holds every package version,
/// project files keep only Include. Behaviour does not change: when projects used different versions of a package,
/// the most common one becomes the central version and the others keep theirs with VersionOverride (aligning them is a separate decision).
/// </summary>
public static class CentralPackageManagement
{
    public static CpmPlan Plan(SolutionInfo solution)
    {
        var rootDir = File.Exists(solution.Path) ? Path.GetDirectoryName(solution.Path)! : solution.Path;
        var propsPath = Path.Combine(rootDir, CentralPackageFile.FileName);
        var notes = new List<string>();

        if (File.Exists(propsPath) && !CentralPackageFile.Load(propsPath).IsEnabled)
        {
            return new CpmPlan(propsPath, [], [], notes,
                $"{CentralPackageFile.FileName} exists but does not set ManagePackageVersionsCentrally to true. Decide on that file first.");
        }

        var projects = new List<(ProjectInfo Project, XDocument Doc, List<XElement> Items)>();
        var legacy = new List<string>();
        foreach (var project in solution.Projects)
        {
            if (project.HasPackagesConfig)
            {
                notes.Add($"{project.Name} uses packages.config, which Central Package Management does not affect. " +
                          "Its packages move to the central file when `dotnet-lift upgrade` converts it.");
            }

            var doc = ProjectXml.Load(project.FullPath);
            var items = ProjectXml.Elements(doc.Root!, "PackageReference").Where(e => VersionOf(e) is not null).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            if (!project.IsSdkStyle)
            {
                legacy.Add(project.Name);
            }
            projects.Add((project, doc, items));
        }

        if (legacy.Count > 0)
        {
            return new CpmPlan(propsPath, [], [], notes,
                $"{string.Join(", ", legacy)} use PackageReference in a legacy (non SDK-style) project file. " +
                $"Convert {(legacy.Count == 1 ? "it" : "them")} first (`dotnet-lift upgrade`, step \"Convert to SDK-style project\"), then run this again.");
        }

        if (projects.Count == 0)
        {
            return new CpmPlan(propsPath, [], [], notes, null);
        }

        var central = File.Exists(propsPath) ? CentralPackageFile.Load(propsPath) : CentralPackageFile.Create(propsPath);
        var conflicts = new List<string>();

        // Central version per package: the existing one, else the most used one (ties: the highest).
        var byId = projects
            .SelectMany(p => p.Items.Select(i => (p.Project, Id: IdOf(i), Version: VersionOf(i)!)))
            .Where(x => x.Id is not null)
            .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var package in byId)
        {
            var chosen = central.VersionOf(package.Key)
                         ?? package.GroupBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(v => v.Count())
                             .ThenByDescending(v => v.Key, VersionComparer.Instance)
                             .First().Key;
            central.Ensure(package.Key, chosen);

            var versions = package.GroupBy(x => x.Version, StringComparer.OrdinalIgnoreCase).ToList();
            if (versions.Count > 1 || !versions[0].Key.Equals(chosen, StringComparison.OrdinalIgnoreCase))
            {
                var detail = string.Join("; ", versions.Select(v =>
                    $"{v.Key} in {string.Join(", ", v.Select(x => x.Project.Name).Distinct())}"));
                conflicts.Add($"{package.Key}: {detail}. Central version {chosen}; the others keep their version with VersionOverride.");
            }
        }

        var changes = new List<FileChange>();
        foreach (var (project, doc, items) in projects)
        {
            foreach (var item in items)
            {
                var version = VersionOf(item)!;
                var centralVersion = central.VersionOf(IdOf(item) ?? "");
                RemoveVersion(item);
                if (!string.Equals(version, centralVersion, StringComparison.OrdinalIgnoreCase))
                {
                    item.SetAttributeValue("VersionOverride", version);
                }
            }
            changes.Add(new FileChange(project.FullPath, ProjectXml.Serialize(doc)));
        }

        if (central.Changed)
        {
            changes.Insert(0, central.ToChange());
        }
        return new CpmPlan(propsPath, changes, conflicts, notes, null);
    }

    private static string? IdOf(XElement item) => item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value;

    private static string? VersionOf(XElement item) =>
        item.Attribute("Version")?.Value ?? ProjectXml.Child(item, "Version");

    private static void RemoveVersion(XElement item)
    {
        item.Attribute("Version")?.Remove();
        foreach (var child in item.Elements().Where(e => e.Name.LocalName == "Version").ToList())
        {
            ProjectXml.Remove(child);
        }
        if (!item.Elements().Any())
        {
            item.RemoveNodes(); // only whitespace left: write it as <PackageReference ... />
        }
    }
}

/// <summary>Orders NuGet-style versions: numeric parts compared as numbers, a release above its pre-releases.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var (xRelease, xPre) = Split(x);
        var (yRelease, yPre) = Split(y);
        var length = Math.Max(xRelease.Length, yRelease.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < xRelease.Length ? xRelease[i] : 0;
            var b = i < yRelease.Length ? yRelease[i] : 0;
            if (a != b) return a.CompareTo(b);
        }

        if (xPre is null && yPre is null) return 0;
        if (xPre is null) return 1;
        if (yPre is null) return -1;
        return string.Compare(xPre, yPre, StringComparison.OrdinalIgnoreCase);
    }

    private static (long[] Release, string? Pre) Split(string version)
    {
        var v = version.Split('+')[0];
        var dash = v.IndexOf('-');
        var release = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..] : null;
        var parts = release.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToArray();
        return (parts, pre);
    }
}
