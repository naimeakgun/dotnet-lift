using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// HttpContext.Current members → the team's abstractions named in the profile's "requestContext" (the step argument is the
/// profile's path). Files that use a member without a mapping are left alone. When no file needs System.Web any more,
/// the System.Web reference goes too. Unlike the CurrentUser step nothing is generated: the abstractions are the team's own.
/// </summary>
public sealed class RequestContextMigration : IStepExecutor
{
    public string StepId => "request-context";

    public bool CanExecute(ProjectInfo project) => !project.IsWeb;

    public string? NotReadyReason(ProjectInfo project) =>
        ProjectLoader.Load(project.FullPath).IsSdkStyle ? null : "the project is not SDK-style yet (apply the SDK-style step first)";

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        var changes = new List<FileChange>();
        if (!current.IsSdkStyle || step.Argument is null || MigrationProfile.Load(step.Argument).RequestContext is not { } map)
        {
            return changes;
        }

        var texts = new Dictionary<string, string>();
        foreach (var file in current.SourceFiles)
        {
            texts[file] = File.ReadAllText(file);
            if (RequestContextRewrite.Rewrite(texts[file], map) is { Replaced: > 0 } rewrite)
            {
                texts[file] = rewrite.NewText;
                changes.Add(new FileChange(file, rewrite.NewText));
            }
        }

        if (changes.Count > 0 && current.References("System.Web") && !texts.Values.Any(CurrentUserRewrite.UsesSystemWeb))
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
        return changes;
    }
}
