using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// CallContext (logical data) and FormsAuthentication password hashing → helpers generated into Infrastructure/, which behave
/// the same on .NET Framework and modern .NET. Runs while the project still builds for .NET Framework, so the tests check it there.
/// </summary>
public sealed class ExactRewrites : IStepExecutor
{
    public string StepId => "exact-rewrites";

    public bool CanExecute(ProjectInfo project) => !project.IsWeb;

    public string? NotReadyReason(ProjectInfo project) =>
        ProjectLoader.Load(project.FullPath).IsSdkStyle ? null : "the project is not SDK-style yet (apply the SDK-style step first)";

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        var changes = new List<FileChange>();
        if (!current.IsSdkStyle)
        {
            return changes;
        }

        var ns = RootNamespace(current) + ".Infrastructure";
        var infrastructure = Path.Combine(current.Directory, "Infrastructure");
        var helpers = LegacyHelpers.None;
        foreach (var file in current.SourceFiles)
        {
            if (LegacyApiRewrite.Rewrite(File.ReadAllText(file), ns) is { } rewrite)
            {
                changes.Add(new FileChange(file, rewrite.NewText));
                helpers |= rewrite.Helpers;
            }
        }

        void Helper(LegacyHelpers flag, string name, Func<string, string> source)
        {
            var path = Path.Combine(infrastructure, name + ".cs");
            if (helpers.HasFlag(flag) && !File.Exists(path))
            {
                changes.Add(new FileChange(path, source(ns)));
            }
        }
        Helper(LegacyHelpers.LogicalCallContext, LegacyApiRewrite.CallContextHelper, LegacyApiRewrite.CallContextSource);
        Helper(LegacyHelpers.PasswordHash, LegacyApiRewrite.PasswordHashHelper, LegacyApiRewrite.PasswordHashSource);
        return changes;
    }

    private static string RootNamespace(ProjectInfo project)
    {
        var doc = System.Xml.Linq.XDocument.Load(project.FullPath);
        return ProjectXml.Elements(doc.Root!, "RootNamespace").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0)
               ?? project.Name;
    }
}
