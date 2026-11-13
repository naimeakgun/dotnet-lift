using DotnetLift.Core.Model;

namespace DotnetLift.Core.Analyzers;

public sealed record AnalysisContext(SolutionInfo Solution, string TargetFramework)
{
    public string RootDirectory => File.Exists(Solution.Path)
        ? Path.GetDirectoryName(Solution.Path)!
        : Solution.Path;
}

/// <summary>One focused check. Analyzers are pure: they read the project and return findings.</summary>
public interface IAnalyzer
{
    string Name { get; }
    IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context);
}
