using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// System.Data.SqlClient → Microsoft.Data.SqlClient: namespaces in the code (Roslyn), the Microsoft.Data.SqlClient package for
/// every target (it supports .NET Framework 4.6.2+ and modern .NET, so the step can run before or after the retarget), and the
/// deprecated System.Data.SqlClient package out of the project file. Connections are encrypted by default after this step, which
/// can fail at runtime where the server certificate is not trusted, so the step asks first unless the profile records the decision.
/// </summary>
public sealed class SqlClientMigration : IStepExecutor
{
    public const string PackageId = "Microsoft.Data.SqlClient";
    public const string PackageVersion = "7.1.2";

    /// <summary>The step argument when the profile says "sqlClient": "microsoft": the team has decided, no question.</summary>
    public const string ApprovedArgument = "approved";

    public string StepId => "sqlclient";

    public bool CanExecute(ProjectInfo project) => true;

    public bool RequiresExplicitApproval => true;

    public bool NeedsApproval(UpgradeStep step) => step.Argument != ApprovedArgument;

    public string? ApprovalNote =>
        "Microsoft.Data.SqlClient encrypts connections by default (Encrypt=True) and validates the server certificate: " +
        "a connection string without Encrypt / TrustServerCertificate can fail at runtime where it works today. " +
        $"Check every environment's connection strings first; \"sqlClient\": \"microsoft\" in {MigrationProfile.FileName} records the decision.";

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

        foreach (var file in current.SourceFiles)
        {
            if (SqlClientRewrite.Rewrite(File.ReadAllText(file)) is { } rewrite)
            {
                changes.Add(new FileChange(file, rewrite.NewText));
            }
        }

        var legacy = current.HasPackage(SqlClientAnalyzer.LegacyPackage);
        if (changes.Count == 0 && !legacy)
        {
            return changes;
        }

        var doc = ProjectXml.Load(current.FullPath);
        var root = doc.Root!;
        var central = CentralPackageFile.Find(current.Directory);
        var changed = false;

        foreach (var reference in ProjectXml.Elements(root, "PackageReference")
                     .Where(e => string.Equals(e.Attribute("Include")?.Value, SqlClientAnalyzer.LegacyPackage, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            ProjectXml.Remove(reference);
            changed = true;
        }
        ProjectXml.RemoveEmptyItemGroups(root);

        if (!current.HasPackage(PackageId))
        {
            // Every target: the driver is the same on .NET Framework and modern .NET. Under CPM a version already pinned wins.
            var version = central?.VersionOf(PackageId) ?? PackageVersion;
            ProjectXml.AddItem(root, null, PackageReferences.Create(root.Name.Namespace, PackageId, version, central));
            changed = true;
        }

        if (changed)
        {
            changes.Add(new FileChange(current.FullPath, ProjectXml.Serialize(doc)));
            PackageReferences.AddIfChanged(changes, central);
        }
        return changes;
    }
}
