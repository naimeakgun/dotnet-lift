using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Knows how to carry out one kind of plan step. Executors only compute changes; the runner decides whether to apply them.
/// They reload the project from disk because an earlier step may already have changed it.
/// </summary>
public interface IStepExecutor
{
    string StepId { get; }

    bool CanExecute(ProjectInfo project);

    IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework);

    /// <summary>
    /// Steps that change behaviour in a way the user must accept (e.g. a data format) are never applied by --yes alone,
    /// and the interactive question defaults to "no".
    /// </summary>
    bool RequiresExplicitApproval => false;

    /// <summary>Shown before asking, when <see cref="RequiresExplicitApproval"/> is true.</summary>
    string? ApprovalNote => null;
}

public static class StepExecutors
{
    public static IReadOnlyList<IStepExecutor> All { get; } =
    [
        new PackageReferenceMigration(),
        new SdkStyleConversion(),
        new Retarget(),
        new ApiRewrites(),
        new CurrentUserProvider(),
        new BinaryFormatterToJson(),
        new WebConfigToAppSettings(),
    ];

    public static IStepExecutor? For(UpgradeStep step, ProjectInfo project) =>
        All.FirstOrDefault(e => e.StepId == step.Id && e.CanExecute(project));
}
