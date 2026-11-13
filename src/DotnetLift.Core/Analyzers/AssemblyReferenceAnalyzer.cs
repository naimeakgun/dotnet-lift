using DotnetLift.Core.Model;

namespace DotnetLift.Core.Analyzers;

/// <summary>Framework assembly references that do not exist, or behave differently, on modern .NET.</summary>
public sealed class AssemblyReferenceAnalyzer : IAnalyzer
{
    public string Name => "assembly-references";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        if (project.References("System.Web") && !project.IsWeb)
        {
            yield return new Finding(project.Name, Severity.Blocker, "Dependency",
                "Class library references System.Web. It cannot move to modern .NET until this coupling is removed.",
                "Inject what the code needs (current user, cache, settings) through interfaces such as IMemoryCache or a small ICurrentUser abstraction, implemented in the web project.");
        }

        if (project.References("System.Configuration"))
        {
            yield return new Finding(project.Name, Severity.Warning, "Dependency",
                "References System.Configuration (ConfigurationManager).",
                "Short term: add the System.Configuration.ConfigurationManager package. Long term: bind settings with IConfiguration / IOptions<T>.");
        }

        if (project.References("System.ServiceModel"))
        {
            yield return new Finding(project.Name, Severity.Warning, "Dependency",
                "References System.ServiceModel (WCF).",
                "WCF clients: use the System.ServiceModel.* packages. WCF services: use CoreWCF or move to gRPC / HTTP APIs.");
        }

        if (project.References("System.Runtime.Remoting"))
        {
            yield return new Finding(project.Name, Severity.Blocker, "Dependency",
                ".NET Remoting is not supported on modern .NET.",
                "Replace with gRPC, HTTP APIs or named pipes (System.IO.Pipes).");
        }

        if (project.References("System.EnterpriseServices"))
        {
            yield return new Finding(project.Name, Severity.Blocker, "Dependency",
                "System.EnterpriseServices (COM+) is not supported on modern .NET.",
                "Redesign the component; use System.Transactions for distributed transactions on Windows.");
        }

        if (project.References("System.Drawing"))
        {
            yield return new Finding(project.Name, Severity.Warning, "Dependency",
                "System.Drawing is Windows-only on modern .NET.",
                "Use the System.Drawing.Common package on Windows only, or move to ImageSharp / SkiaSharp for cross-platform.");
        }

        if (project.References("System.Data.Entity") || project.References("System.Data.Linq"))
        {
            yield return new Finding(project.Name, Severity.Warning, "Dependency",
                "References EF 4/5 or LINQ to SQL from the GAC.",
                "Move to EF 6.4+ (supports modern .NET) or EF Core.");
        }
    }
}
