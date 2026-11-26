using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Encoding.Default → the code page set in the migration profile (step argument), through a generated LegacyEncoding helper,
/// so text that is hashed or stored produces the same bytes after the move.
/// </summary>
public sealed class ExplicitCodePage : IStepExecutor
{
    public string StepId => "explicit-code-page";

    public bool CanExecute(ProjectInfo project) => !project.IsWeb;

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        var changes = new List<FileChange>();
        if (!current.IsSdkStyle || !int.TryParse(step.Argument, out var codePage))
        {
            return changes; // the generated helper is picked up by SDK-style projects without editing the project file
        }

        var ns = $"{RootNamespace(current)}.Infrastructure";
        var helperPath = Path.Combine(current.Directory, "Infrastructure", $"{CodePageRewrite.HelperName}.cs");
        foreach (var file in current.SourceFiles.Where(f => !string.Equals(f, helperPath, StringComparison.OrdinalIgnoreCase)))
        {
            var source = File.ReadAllText(file);
            if (CodePageRewrite.Rewrite(source, ns) is { Replaced: > 0 } rewrite)
            {
                changes.Add(new FileChange(file, rewrite.NewText));
            }
        }

        if (changes.Count > 0 && !File.Exists(helperPath))
        {
            changes.Add(new FileChange(helperPath, CodePageRewrite.HelperSource(ns, codePage)));
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
