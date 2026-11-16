using System.Xml.Linq;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Changes the target framework(s) of an SDK-style library, e.g. net48 → net48;net10.0.
/// Framework assembly references (System.Web, System.Configuration, …) only exist on .NET Framework,
/// so they move into an item group conditioned on the .NET Framework target.
/// </summary>
public sealed class Retarget : IStepExecutor
{
    public string StepId => "retarget";

    public bool CanExecute(ProjectInfo project) => !project.IsWeb;

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        if (!current.IsSdkStyle)
        {
            return []; // a legacy project file cannot multi-target; the SDK-style step has to run first
        }

        var content = Apply(ProjectXml.Load(current.FullPath), step.Argument ?? targetFramework);
        return content is null ? [] : [new FileChange(current.FullPath, content)];
    }

    /// <summary>Returns the new project text, or null when the project already targets everything wanted.</summary>
    internal static string? Apply(XDocument doc, string wanted)
    {
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        var targets = wanted.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var element = ProjectXml.Elements(root, "TargetFramework").FirstOrDefault()
                      ?? ProjectXml.Elements(root, "TargetFrameworks").FirstOrDefault();
        if (element is null)
        {
            return null;
        }

        var existing = element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targets.All(t => existing.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        element.Name = ns + (targets.Length > 1 ? "TargetFrameworks" : "TargetFramework");
        element.Value = string.Join(";", targets);

        var frameworkReferences = ProjectXml.Elements(root, "Reference")
            .Where(r => r.Attribute("Include") is not null
                        && ProjectXml.Child(r, "HintPath") is null
                        && r.Attribute("Condition") is null
                        && r.Parent?.Attribute("Condition") is null)
            .ToList();

        if (frameworkReferences.Count > 0)
        {
            var includes = frameworkReferences.Select(r => r.Attribute("Include")!.Value).ToList();
            foreach (var reference in frameworkReferences)
            {
                ProjectXml.Remove(reference);
            }
            ProjectXml.RemoveEmptyItemGroups(root);

            // Keep them for the .NET Framework target only; modern .NET has no such assemblies.
            var framework = targets.FirstOrDefault(ProjectFormatAnalyzer.IsNetFramework);
            if (framework is not null)
            {
                var group = new XElement(ns + "ItemGroup",
                    new XAttribute("Condition", ProjectXml.TargetFrameworkCondition(framework)));
                ProjectXml.AddItemGroup(root, group,
                    includes.Select(i => new XElement(ns + "Reference", new XAttribute("Include", i))));
            }
        }

        return ProjectXml.Serialize(doc);
    }
}
