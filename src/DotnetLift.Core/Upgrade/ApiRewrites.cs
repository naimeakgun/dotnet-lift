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
        if (!current.IsSdkStyle)
        {
            return changes; // the rewritten code needs references only an SDK-style project file can scope per target
        }

        // The profile's "cache" templates (step argument = profile path) replace MemoryCache.Default with the team's cache API.
        var custom = step.Argument is { } profilePath ? MigrationProfile.Load(profilePath).Cache : null;

        foreach (var file in current.SourceFiles)
        {
            var source = File.ReadAllText(file);
            var rewrite = custom is null ? HttpRuntimeCacheRewrite.Rewrite(source) : CustomCacheRewrite.Rewrite(source, custom);
            if (rewrite is { Replaced: > 0 } && rewrite.NewText != source)
            {
                changes.Add(new FileChange(file, rewrite.NewText));
            }
        }

        if (changes.Count > 0 && custom is null)
        {
            var central = CentralPackageFile.Find(current.Directory);
            var csproj = AddRuntimeCaching(current, central);
            if (csproj is not null)
            {
                changes.Add(new FileChange(current.FullPath, csproj));
            }
            PackageReferences.AddIfChanged(changes, central);
        }

        return changes;
    }

    /// <summary>
    /// System.Runtime.Caching is a framework assembly on .NET Framework and a NuGet package on modern .NET:
    /// add each where it belongs. Returns null when the project already has both.
    /// </summary>
    internal static string? AddRuntimeCaching(ProjectInfo project, CentralPackageFile? central = null)
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
            ProjectXml.AddItem(root, multi ? framework : null,
                new XElement(ns + "Reference", new XAttribute("Include", "System.Runtime.Caching")));
            changed = true;
        }

        var modern = targets.FirstOrDefault(t => !ProjectFormatAnalyzer.IsNetFramework(t));
        if (modern is not null && !project.HasPackage("System.Runtime.Caching"))
        {
            ProjectXml.AddItem(root, multi ? modern : null,
                PackageReferences.Create(ns, "System.Runtime.Caching", RuntimeCachingPackageVersion, central));
            changed = true;
        }

        return changed ? ProjectXml.Serialize(doc) : null;
    }
}
