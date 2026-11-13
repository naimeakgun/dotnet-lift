using DotnetLift.Core.Model;

namespace DotnetLift.Core.Planning;

public enum StepMode
{
    /// <summary>Deterministic transformation the tool can do on its own.</summary>
    Automatic,

    /// <summary>Code rewrite where the agent proposes a change and the user approves it.</summary>
    AgentAssisted,

    /// <summary>A decision or change that needs a human (e.g. data migration).</summary>
    Manual,
}

public enum Risk
{
    Low,
    Medium,
    High,
}

public sealed record UpgradeStep(
    int Number,
    string Project,
    string Id,
    string Title,
    string Description,
    StepMode Mode,
    Risk Risk,
    IReadOnlyList<Finding> Findings);

public sealed class UpgradePlan
{
    public required string TargetFramework { get; init; }

    /// <summary>Projects in the order they must be upgraded (dependencies first).</summary>
    public required IReadOnlyList<ProjectInfo> Order { get; init; }

    public required IReadOnlyList<UpgradeStep> Steps { get; init; }

    public IEnumerable<UpgradeStep> For(ProjectInfo project) => Steps.Where(s => s.Project == project.Name);
}
