using System.Xml.Linq;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Replaces BinaryFormatter with System.Text.Json. High risk by nature (the stored format changes),
/// so the runner only applies it after an explicit yes, never through --yes alone.
/// </summary>
public sealed class BinaryFormatterToJson : IStepExecutor
{
    public const string SystemTextJsonVersion = "10.0.0";

    public string StepId => "binaryformatter-json";

    public bool RequiresExplicitApproval => true;

    public string? ApprovalNote =>
        "This changes the serialized format. Anything the old code wrote with BinaryFormatter (cookies, files, cache entries, " +
        "database columns) cannot be read by the new code. Make sure that data can be dropped, or plan a one-time conversion.";

    public bool CanExecute(ProjectInfo project) => !project.IsWeb;

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        var changes = new List<FileChange>();
        if (!current.IsSdkStyle)
        {
            return changes; // the rewritten code needs references only an SDK-style project file can scope per target
        }

        foreach (var file in current.SourceFiles)
        {
            var source = File.ReadAllText(file);
            var rewrite = BinaryFormatterRewrite.Rewrite(source);
            if (rewrite is { Replaced: > 0 } && rewrite.NewText != source)
            {
                changes.Add(new FileChange(file, rewrite.NewText));
            }
        }

        if (changes.Count > 0 && !current.HasPackage("System.Text.Json"))
        {
            // System.Text.Json is part of modern .NET; .NET Framework needs the package.
            var targets = (current.TargetFramework ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var framework = targets.FirstOrDefault(ProjectFormatAnalyzer.IsNetFramework);
            if (framework is not null)
            {
                var doc = ProjectXml.Load(current.FullPath);
                var root = doc.Root!;
                ProjectXml.AddItem(root, targets.Length > 1 ? framework : null,
                    new XElement(root.Name.Namespace + "PackageReference",
                        new XAttribute("Include", "System.Text.Json"),
                        new XAttribute("Version", SystemTextJsonVersion)));
                changes.Add(new FileChange(current.FullPath, ProjectXml.Serialize(doc)));
            }
        }

        return changes;
    }
}
