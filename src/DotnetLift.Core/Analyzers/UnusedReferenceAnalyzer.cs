using DotnetLift.Core.Model;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Core.Analyzers;

/// <summary>
/// References to legacy libraries (ASP.NET MVC, bundling, OWIN, Unity, COM+) that nothing in the project uses any more.
/// Same check as <c>dotnet-lift cleanup</c> (<see cref="Cleanup.UnusedOrKept"/>), reported without changing anything,
/// so the report shows up front what does not have to be migrated at all.
/// </summary>
public sealed class UnusedReferenceAnalyzer : IAnalyzer
{
    public const string Category = "Cleanup";

    public string Name => "unused-references";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        foreach (var item in Cleanup.UnusedOrKept(project).Where(i => i.KeptBecause is null))
        {
            yield return new Finding(project.Name, Severity.Info, Category,
                $"{item.Library} is referenced but not used ({string.Join(", ", item.References)}).",
                "Run `dotnet-lift cleanup` to remove it before the upgrade; findings about these packages and assemblies then go away too.",
                File: Path.GetFileName(project.FullPath));
        }
    }
}
