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
}

public static class StepExecutors
{
    public static IReadOnlyList<IStepExecutor> All { get; } =
    [
        new PackageReferenceMigration(),
        new SdkStyleConversion(),
    ];

    public static IStepExecutor? For(UpgradeStep step, ProjectInfo project) =>
        All.FirstOrDefault(e => e.StepId == step.Id && e.CanExecute(project));
}
