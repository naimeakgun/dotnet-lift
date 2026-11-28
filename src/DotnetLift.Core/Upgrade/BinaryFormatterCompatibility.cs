using System.Xml.Linq;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Keeps BinaryFormatter working on modern .NET instead of changing the format (profile: "binaryFormatter": "compatibility"):
/// the modern target references the unsupported System.Runtime.Serialization.Formatters package, which brings back a working
/// implementation, and the project sets EnableUnsafeBinaryFormatterSerialization. The code and the stored data stay as they are.
/// The .NET Framework target needs nothing: BinaryFormatter is part of the framework there.
/// </summary>
public sealed class BinaryFormatterCompatibility : IStepExecutor
{
    public const string PackageId = "System.Runtime.Serialization.Formatters";
    public const string PackageVersion = "10.0.0";
    public const string SwitchProperty = "EnableUnsafeBinaryFormatterSerialization";
    public const string ObsoleteWarning = "SYSLIB0011";

    public string StepId => "binaryformatter-compat";

    public bool CanExecute(ProjectInfo project) => !project.IsWeb;

    public string? NotReadyReason(ProjectInfo project)
    {
        var current = ProjectLoader.Load(project.FullPath);
        return current.IsSdkStyle && ModernTargets(current).Count > 0
            ? null
            : "the project does not target modern .NET yet (apply the retarget step first)";
    }

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        var changes = new List<FileChange>();
        var modern = ModernTargets(current);
        if (!current.IsSdkStyle || modern.Count == 0)
        {
            return changes;
        }

        var doc = ProjectXml.Load(current.FullPath);
        var root = doc.Root!;
        var central = CentralPackageFile.Find(current.Directory);
        var changed = false;

        if (!current.HasPackage(PackageId))
        {
            // One target: unconditional. Several: only the modern ones (on .NET Framework the type is in the box).
            var multi = AllTargets(current).Count > 1;
            foreach (var tfm in modern)
            {
                ProjectXml.AddItem(root, multi ? tfm : null, PackageReferences.Create(root.Name.Namespace, PackageId, PackageVersion, central));
            }
            changed = true;
        }

        if (!ProjectXml.Elements(root, SwitchProperty).Any())
        {
            AddSwitchProperties(root);
            changed = true;
        }

        if (changed)
        {
            changes.Add(new FileChange(current.FullPath, ProjectXml.Serialize(doc)));
            PackageReferences.AddIfChanged(changes, central);
        }
        return changes;
    }

    /// <summary>
    /// A PropertyGroup that enables the compatibility package and keeps the obsoletion warning out of the build
    /// (it is a decision recorded in the profile, not something to fix again on every build).
    /// </summary>
    private static void AddSwitchProperties(XElement root)
    {
        var ns = root.Name.Namespace;
        var group = new XElement(ns + "PropertyGroup");
        ProjectXml.AddItemGroup(root, group,
        [
            new XElement(ns + SwitchProperty, "true"),
            new XElement(ns + "NoWarn", $"$(NoWarn);{ObsoleteWarning}"),
        ]);
        group.AddBeforeSelf(
            new XComment($" BinaryFormatter is kept on purpose ({MigrationProfile.FileName}: \"binaryFormatter\": \"compatibility\"), so stored data stays readable. See https://aka.ms/binaryformatter "),
            new XText(Environment.NewLine + ProjectXml.ChildIndent(root)));
    }

    private static List<string> AllTargets(ProjectInfo project) =>
        (project.TargetFramework ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static List<string> ModernTargets(ProjectInfo project) =>
        AllTargets(project).Where(IsModernNet).ToList();

    /// <summary>net5.0 and later (the package does not exist for .NET Framework, .NET Standard or .NET Core 3.x).</summary>
    private static bool IsModernNet(string tfm)
    {
        var t = tfm.ToLowerInvariant();
        return t.StartsWith("net", StringComparison.Ordinal) && !t.StartsWith("netstandard", StringComparison.Ordinal)
               && !t.StartsWith("netcoreapp", StringComparison.Ordinal) && !ProjectFormatAnalyzer.IsNetFramework(t);
    }
}
