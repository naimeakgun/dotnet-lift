namespace DotnetLift.Core.Model;

public enum ProjectKind
{
    Unknown,
    ClassLibrary,
    Console,
    AspNetMvc,
    AspNetWebApi,
    AspNetMvcAndWebApi,
    AspNetWebForms,
}

public enum TestFramework
{
    MSTest,
    XUnit,
    NUnit,
}

/// <summary>What makes a project a test project: the framework, the package or reference it was recognised by, and its version.</summary>
/// <param name="VisualStudioMSTest">MSTest v1: Microsoft.VisualStudio.QualityTools.UnitTestFramework from the Visual Studio installation, not a package.</param>
public sealed record TestProjectInfo(TestFramework Framework, string Evidence, string? Version, bool VisualStudioMSTest);

public enum Severity
{
    Info,
    Warning,
    Blocker,
}

/// <summary>A NuGet package entry from packages.config or a PackageReference.</summary>
public sealed record PackageRef(string Id, string Version);

/// <summary>A DLL referenced by file path (HintPath) outside a NuGet packages folder.</summary>
public sealed record FileReference(string AssemblyName, string? AssemblyVersion, string HintPath, string FullPath);

/// <summary>A single thing the upgrade has to deal with.</summary>
public sealed record Finding(
    string Project,
    Severity Severity,
    string Category,
    string Message,
    string? Suggestion = null,
    string? File = null,
    int? Line = null);

public sealed class ProjectInfo
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public string Directory => Path.GetDirectoryName(FullPath)!;

    public bool IsSdkStyle { get; init; }
    public string? TargetFramework { get; init; }
    public string? OutputType { get; init; }
    public ProjectKind Kind { get; set; }

    public List<string> ProjectReferences { get; } = [];
    public List<string> AssemblyReferences { get; } = [];
    public List<PackageRef> Packages { get; } = [];
    public List<FileReference> FileReferences { get; } = [];

    /// <summary>The package/assembly version the project declares itself (Version or PackageVersion property, or a .nuspec), if any.</summary>
    public string? DeclaredVersion { get; set; }
    public string? DeclaredVersionSource { get; set; }

    /// <summary>Set when the project uses MSTest, xUnit or NUnit.</summary>
    public TestProjectInfo? Test { get; set; }
    public bool IsTest => Test is not null;

    public bool HasPackagesConfig { get; init; }
    public string? WebConfigPath { get; init; }
    public string? AppConfigPath { get; init; }
    public bool HasGlobalAsax { get; init; }

    public List<string> SourceFiles { get; } = [];
    public List<string> ViewFiles { get; } = [];

    public bool IsWeb => Kind is ProjectKind.AspNetMvc or ProjectKind.AspNetWebApi
        or ProjectKind.AspNetMvcAndWebApi or ProjectKind.AspNetWebForms;

    public bool References(string assemblyName) =>
        AssemblyReferences.Any(r => r.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));

    public bool HasPackage(string id) =>
        Packages.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public PackageRef? FindPackage(string id) =>
        Packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}

public sealed class SolutionInfo
{
    public required string Path { get; init; }
    public List<ProjectInfo> Projects { get; } = [];

    public ProjectInfo? FindByPath(string fullPath) =>
        Projects.FirstOrDefault(p => string.Equals(
            System.IO.Path.GetFullPath(p.FullPath),
            System.IO.Path.GetFullPath(fullPath),
            StringComparison.OrdinalIgnoreCase));
}

public sealed class AnalysisResult
{
    public required SolutionInfo Solution { get; init; }
    public required string TargetFramework { get; init; }
    public List<Finding> Findings { get; } = [];

    public IEnumerable<Finding> For(ProjectInfo project) =>
        Findings.Where(f => f.Project == project.Name);

    public int Count(ProjectInfo project, Severity severity) =>
        For(project).Count(f => f.Severity == severity);
}
