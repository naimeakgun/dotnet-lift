using System.Xml.Linq;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// packages.config → &lt;PackageReference&gt; inside the existing (legacy) project file.
/// Removes the HintPath references, NuGet build imports and the packages.config file itself.
/// </summary>
public sealed class PackageReferenceMigration : IStepExecutor
{
    public string StepId => "package-reference";

    public bool CanExecute(ProjectInfo project) => project.HasPackagesConfig;

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        var packagesConfig = Path.Combine(current.Directory, "packages.config");
        if (!File.Exists(packagesConfig))
        {
            return [];
        }

        var doc = ProjectXml.Load(current.FullPath);
        var newContent = Migrate(doc, ProjectLoader.ReadPackagesConfig(packagesConfig).ToList());
        return [new FileChange(current.FullPath, newContent), new FileChange(packagesConfig, null)];
    }

    internal static string Migrate(XDocument doc, IReadOnlyList<PackageRef> packages)
    {
        var root = doc.Root!;
        var ns = root.Name.Namespace;

        foreach (var reference in ProjectXml.Elements(root, "Reference").ToList())
        {
            var hintPath = ProjectXml.Child(reference, "HintPath");
            if (hintPath is not null && IsPackagesFolder(hintPath))
            {
                ProjectXml.Remove(reference);
            }
        }

        foreach (var item in ProjectXml.Elements(root, "None").Concat(ProjectXml.Elements(root, "Content")).ToList())
        {
            if (string.Equals(item.Attribute("Include")?.Value, "packages.config", StringComparison.OrdinalIgnoreCase))
            {
                ProjectXml.Remove(item);
            }
        }

        foreach (var import in ProjectXml.Elements(root, "Import").ToList())
        {
            if (IsPackagesFolder(import.Attribute("Project")?.Value ?? ""))
            {
                ProjectXml.Remove(import);
            }
        }

        foreach (var target in ProjectXml.Elements(root, "Target")
                     .Where(t => t.Attribute("Name")?.Value == "EnsureNuGetPackageBuildImports").ToList())
        {
            ProjectXml.Remove(target);
        }

        ProjectXml.RemoveEmptyItemGroups(root);

        if (packages.Count > 0)
        {
            var indent = ProjectXml.ChildIndent(root);
            var group = new XElement(ns + "ItemGroup");
            foreach (var p in packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                group.Add(new XText(Environment.NewLine + indent + indent));
                group.Add(new XElement(ns + "PackageReference",
                    new XAttribute("Include", p.Id),
                    new XAttribute("Version", p.Version)));
            }
            group.Add(new XText(Environment.NewLine + indent));

            // Put the packages next to the other item groups: before the first <Import> that follows them.
            var anchor = root.Elements().LastOrDefault(e => e.Name.LocalName == "ItemGroup");
            if (anchor is not null)
            {
                anchor.AddAfterSelf(new XText(Environment.NewLine + indent), group);
            }
            else
            {
                root.Add(new XText(indent), group, new XText(Environment.NewLine));
            }
        }

        return ProjectXml.Serialize(doc);
    }

    private static bool IsPackagesFolder(string path)
    {
        var p = path.Replace('/', '\\');
        return p.StartsWith("packages\\", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\packages\\", StringComparison.OrdinalIgnoreCase);
    }
}
