using DotnetLift.Core.Model;

namespace DotnetLift.Core.Analyzers;

public sealed class ProjectFormatAnalyzer : IAnalyzer
{
    public string Name => "project-format";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        if (!project.IsSdkStyle)
        {
            yield return new Finding(project.Name, Severity.Warning, "Project file",
                "Legacy (non SDK-style) project file.",
                "Convert to an SDK-style project. Explicit <Compile Include> lists, HintPaths and MSBuild imports go away.",
                File: Path.GetFileName(project.FullPath));
        }

        if (project.TargetFramework is { } tfm && IsNetFramework(tfm))
        {
            yield return new Finding(project.Name, Severity.Info, "Target framework",
                $"Targets .NET Framework ({tfm}).",
                $"Retarget to {context.TargetFramework}.");
        }

        if (project.HasPackagesConfig)
        {
            yield return new Finding(project.Name, Severity.Warning, "Packages",
                "Uses packages.config.",
                "Migrate to <PackageReference> items; transitive dependencies no longer need to be listed.",
                File: "packages.config");
        }

        if (project.FileReferences.Count > 0)
        {
            var names = string.Join(", ", project.FileReferences.Select(r => r.AssemblyName).Distinct(StringComparer.OrdinalIgnoreCase));
            yield return new Finding(project.Name, Severity.Warning, "Packages",
                $"{project.FileReferences.Count} DLL reference(s) by file path outside packages/: {names}.",
                "Reference them as NuGet packages from your feed: `dotnet-lift refs` converts them " +
                "(assembly → package in dotnet-lift.packages.json, or --by-name).",
                File: Path.GetFileName(project.FullPath));
        }

        if (project.DeclaredVersion is "1.0.0" or "1.0.0.0" or "1.0")
        {
            yield return new Finding(project.Name, Severity.Warning, "Packaging",
                $"Version fixed at {project.DeclaredVersion} ({project.DeclaredVersionSource}): every build produces a package with the same version.",
                "Let the build set the version (for example GitVersion, or `dotnet pack -p:Version=...`) and remove the fixed value.",
                File: project.DeclaredVersionSource?.EndsWith(".nuspec") == true ? project.DeclaredVersionSource : Path.GetFileName(project.FullPath));
        }

        if (project.AppConfigPath is not null && !project.IsWeb)
        {
            yield return new Finding(project.Name, Severity.Info, "Configuration",
                "Has App.config. Class libraries do not read their own App.config at runtime.",
                "Move any settings to the host application's configuration and delete the file if it only holds binding redirects or EF providers.",
                File: Path.GetFileName(project.AppConfigPath));
        }
    }

    public static bool IsNetFramework(string tfm)
    {
        var t = tfm.Trim().ToLowerInvariant();
        return t.StartsWith("v4") || t.StartsWith("v3") || t.StartsWith("v2")
            || t is "net45" or "net451" or "net452" or "net46" or "net461" or "net462"
                or "net47" or "net471" or "net472" or "net48" or "net481";
    }
}
