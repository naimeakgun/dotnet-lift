using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Takes HttpContext.Current.User out of a library: the library gets a small CurrentUser provider,
/// its code reads CurrentUser.Principal, and the web app that uses the library sets the provider at startup.
/// When nothing in the library uses System.Web any more, the System.Web reference is removed too.
/// The step argument lists the Global.asax.cs files of the web projects that reference the library.
/// </summary>
public sealed class CurrentUserProvider : IStepExecutor
{
    public string StepId => "current-user";

    public bool CanExecute(ProjectInfo project) => !project.IsWeb;

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        var changes = new List<FileChange>();
        if (!current.IsSdkStyle)
        {
            return changes;
        }

        var providerNamespace = $"{RootNamespace(current)}.Infrastructure";
        var newTexts = new Dictionary<string, string>();
        foreach (var file in current.SourceFiles)
        {
            newTexts[file] = File.ReadAllText(file);
            var rewrite = CurrentUserRewrite.Rewrite(newTexts[file], providerNamespace);
            if (rewrite is { Replaced: > 0 })
            {
                newTexts[file] = rewrite.NewText;
                changes.Add(new FileChange(file, rewrite.NewText));
            }
        }

        if (changes.Count == 0)
        {
            return changes;
        }

        var providerFile = Path.Combine(current.Directory, "Infrastructure", "CurrentUser.cs");
        if (!File.Exists(providerFile))
        {
            changes.Add(new FileChange(providerFile, CurrentUserRewrite.ProviderSource(providerNamespace)));
        }

        // No source file needs System.Web any more: drop the reference, so the coupling cannot come back unnoticed.
        if (current.References("System.Web") && !newTexts.Values.Any(CurrentUserRewrite.UsesSystemWeb))
        {
            var doc = ProjectXml.Load(current.FullPath);
            foreach (var reference in ProjectXml.Elements(doc.Root!, "Reference")
                         .Where(r => r.Attribute("Include")?.Value.Split(',')[0].Trim() == "System.Web").ToList())
            {
                ProjectXml.Remove(reference);
            }
            ProjectXml.RemoveEmptyItemGroups(doc.Root!);
            changes.Add(new FileChange(current.FullPath, ProjectXml.Serialize(doc)));
        }

        // Keep the web app behaving the same: tell the provider where the user comes from.
        foreach (var globalAsax in (step.Argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(globalAsax)
                && CurrentUserRewrite.WireIntoGlobalAsax(File.ReadAllText(globalAsax), providerNamespace) is { } wired)
            {
                changes.Add(new FileChange(globalAsax, wired));
            }
        }

        return changes;
    }

    private static string RootNamespace(ProjectInfo project)
    {
        var doc = System.Xml.Linq.XDocument.Load(project.FullPath);
        return ProjectXml.Elements(doc.Root!, "RootNamespace").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0)
               ?? project.Name;
    }
}
