using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;

namespace DotnetLift.Core;

/// <summary>Entry point: load a solution and run every analyzer over every project.</summary>
public sealed class SolutionAnalyzer
{
    public const string DefaultTarget = "net10.0";

    private readonly IReadOnlyList<IAnalyzer> _analyzers;

    public SolutionAnalyzer(IEnumerable<IAnalyzer>? analyzers = null)
    {
        _analyzers = analyzers?.ToList() ?? DefaultAnalyzers();
    }

    public static IReadOnlyList<IAnalyzer> DefaultAnalyzers() =>
    [
        new ProjectFormatAnalyzer(),
        new PackageAnalyzer(),
        new AssemblyReferenceAnalyzer(),
        new WebConfigAnalyzer(),
        new SourceApiAnalyzer(),
        new DataCompatibilityAnalyzer(),
        new SqlClientAnalyzer(),
        new PlatformInventoryAnalyzer(),
        new UnusedReferenceAnalyzer(),
        new TestProjectAnalyzer(),
    ];

    public AnalysisResult Analyze(string inputPath, string targetFramework = DefaultTarget) =>
        Analyze(SolutionLoader.Load(inputPath), targetFramework);

    public AnalysisResult Analyze(SolutionInfo solution, string targetFramework = DefaultTarget)
    {
        var result = new AnalysisResult { Solution = solution, TargetFramework = targetFramework };
        var context = new AnalysisContext(solution, targetFramework);

        foreach (var project in solution.Projects)
        {
            foreach (var analyzer in _analyzers)
            {
                result.Findings.AddRange(analyzer.Analyze(project, context));
            }
        }

        return result;
    }
}
