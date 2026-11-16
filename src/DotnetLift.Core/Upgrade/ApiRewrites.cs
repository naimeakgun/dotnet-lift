using System.Xml.Linq;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Rule-based C# rewrites (Roslyn) for APIs that have an exact equivalent on both .NET Framework and modern .NET.
/// Today: HttpRuntime.Cache → MemoryCache (System.Runtime.Caching).
/// </summary>
public sealed class ApiRewrites : IStepExecutor
{
    public const string RuntimeCachingPackageVersion = "10.0.0";

    public string StepId => "api-rewrites";

    public bool CanExecute(ProjectInfo project) => !project.IsWeb;

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        var changes = new List<FileChange>();

        foreach (var file in current.SourceFiles)
        {
            var source = File.ReadAllText(file);
            var rewrite = HttpRuntimeCacheRewrite.Rewrite(source);
            if (rewrite is { Replaced: > 0 } && rewrite.NewText != source)
            {
                changes.Add(new FileChange(file, rewrite.NewText));
            }
        }

        if (changes.Count > 0 && current.IsSdkStyle)
        {
            var csproj = AddRuntimeCaching(current);
            if (csproj is not null)
            {
                changes.Add(new FileChange(current.FullPath, csproj));
            }
        }

        return changes;
    }

    /// <summary>
    /// System.Runtime.Caching is a framework assembly on .NET Framework and a NuGet package on modern .NET:
    /// add each where it belongs. Returns null when the project already has both.
    /// </summary>
    internal static string? AddRuntimeCaching(ProjectInfo project)
    {
        var doc = ProjectXml.Load(project.FullPath);
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        var targets = (project.TargetFramework ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var multi = targets.Length > 1;
        var changed = false;

        var framework = targets.FirstOrDefault(ProjectFormatAnalyzer.IsNetFramework);
        if (framework is not null && !project.References("System.Runtime.Caching"))
        {
            Add(root, multi ? framework : null,
                new XElement(ns + "Reference", new XAttribute("Include", "System.Runtime.Caching")));
            changed = true;
        }

        var modern = targets.FirstOrDefault(t => !ProjectFormatAnalyzer.IsNetFramework(t));
        if (modern is not null && !project.HasPackage("System.Runtime.Caching"))
        {
            Add(root, multi ? modern : null,
                new XElement(ns + "PackageReference",
                    new XAttribute("Include", "System.Runtime.Caching"),
                    new XAttribute("Version", RuntimeCachingPackageVersion)));
            changed = true;
        }

        return changed ? ProjectXml.Serialize(doc) : null;
    }

    private static void Add(XElement root, string? conditionTfm, XElement item)
    {
        var condition = conditionTfm is null ? null : ProjectXml.TargetFrameworkCondition(conditionTfm);
        var group = ProjectXml.Elements(root, "ItemGroup").FirstOrDefault(g =>
            condition is null
                ? g.Attribute("Condition") is null && g.Elements().Any(e => e.Name.LocalName == item.Name.LocalName)
                : g.Attribute("Condition")?.Value == condition);

        if (group is null)
        {
            group = condition is null
                ? new XElement(root.Name.Namespace + "ItemGroup")
                : new XElement(root.Name.Namespace + "ItemGroup", new XAttribute("Condition", condition));
            ProjectXml.AddItemGroup(root, group, [item]);
        }
        else
        {
            ProjectXml.AddToItemGroup(root, group, item);
        }
    }
}
