using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Upgrade;

/// <param name="Project">The project the tests are for.</param>
/// <param name="Name">The test project's name, e.g. Acme.Core.Tests.</param>
/// <param name="ProjectPath">Full path of the new .csproj.</param>
/// <param name="TargetFrameworks">What the test project targets: the library's own targets, so the tests run wherever the library does.</param>
/// <param name="Changes">The new project file, a first test, the edited .sln and, under CPM, Directory.Packages.props.</param>
/// <param name="Notes">What the user should know before writing tests.</param>
public sealed record TestScaffoldPlan(
    ProjectInfo Project,
    string Name,
    string ProjectPath,
    string TargetFrameworks,
    IReadOnlyList<FileChange> Changes,
    IReadOnlyList<string> Notes);

/// <summary>
/// Creates an MSTest project for a library, next to it, and adds it to the .sln: the first step of the test-first
/// workflow. Tests are written while the library is still on .NET Framework; the test project targets what the
/// library targets, so `upgrade` retargets it after the library and the same tests then run on modern .NET.
/// </summary>
public static partial class TestProjectScaffold
{
    /// <summary>The project type GUID Visual Studio writes into the .sln for SDK-style C# projects.</summary>
    public const string CSharpSdkProjectTypeGuid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";

    public const string FirstTestFileName = "LibraryLoadTests.cs";

    [GeneratedRegex("""^\s*Project\("\{[^}]+\}"\)\s*=\s*"(?<name>[^"]+)",\s*"(?<path>[^"]+)",\s*"(?<guid>\{[^}]+\})"\s*$""")]
    private static partial Regex ProjectLine();

    public static TestScaffoldPlan Plan(SolutionInfo solution, string projectName, string targetFramework)
    {
        if (!solution.Path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || !File.Exists(solution.Path))
        {
            throw new InvalidOperationException("Adding a test project needs a .sln file; pass the solution (or its folder).");
        }

        var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No project named '{projectName}' in {Path.GetFileName(solution.Path)}. Projects: {string.Join(", ", solution.Projects.Select(p => p.Name))}.");
        if (project.IsTest)
        {
            throw new InvalidOperationException($"{project.Name} is a test project itself.");
        }

        var name = project.Name + ".Tests";
        var slnDir = Path.GetDirectoryName(Path.GetFullPath(solution.Path))!;
        if (solution.Projects.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The solution already has a project named {name}.");
        }

        var directory = Path.Combine(FolderFor(slnDir, project), name);
        if (Directory.Exists(directory))
        {
            throw new InvalidOperationException($"The folder {PathUtil.Relative(slnDir, directory)} already exists; move it away or add its project to the solution yourself.");
        }

        var projectPath = Path.Combine(directory, name + ".csproj");
        var targets = TestTargets(project, targetFramework);
        var central = CentralPackageFile.Find(directory);
        var reference = Path.GetRelativePath(directory, project.FullPath).Replace('/', '\\');

        var changes = new List<FileChange>
        {
            new(projectPath, ProjectFile(targets, Packages(solution, central), reference, central)),
            new(Path.Combine(directory, FirstTestFileName), FirstTest(name, ProjectRemoval.AssemblyName(project))),
            new(solution.Path, AddToSolution(File.ReadAllText(solution.Path), solution.Path, project, name, projectPath, GuidFor(name))),
        };
        PackageReferences.AddIfChanged(changes, central);

        return new TestScaffoldPlan(project, name, projectPath, targets, changes, Notes(solution, project, targets, targetFramework));
    }

    /// <summary>
    /// The library's own targets, so a project reference to it always resolves: net48 while it is a .NET Framework
    /// project, net48;net10.0 once it multi-targets. A .NET Standard library is tested on .NET Framework and the target.
    /// </summary>
    public static string TestTargets(ProjectInfo project, string targetFramework)
    {
        var library = SdkStyleConversion.ToTfm(project.TargetFramework)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = library
            .Where(t => ProjectFormatAnalyzer.IsNetFramework(t) || IsModernNet(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (result.Count < library.Length || result.Count == 0)
        {
            // netstandard2.0 (or something unknown): run where it is consumed today and where it is going.
            if (!result.Any(ProjectFormatAnalyzer.IsNetFramework))
            {
                result.Insert(0, "net48");
            }
            if (!result.Any(IsModernNet))
            {
                result.Add(targetFramework);
            }
        }
        return string.Join(";", result);
    }

    private static bool IsModernNet(string tfm) =>
        tfm.Length > 3 && tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase) && char.IsDigit(tfm[3]) && tfm.Contains('.');

    /// <summary>Next to the library's folder; under tests/ when the solution keeps libraries in src/ and has a tests/ folder.</summary>
    private static string FolderFor(string slnDir, ProjectInfo project)
    {
        var relative = Path.GetRelativePath(slnDir, project.Directory);
        var testsDir = Path.Combine(slnDir, "tests");
        if (relative.StartsWith("src" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(testsDir))
        {
            return testsDir;
        }

        var parent = Path.GetDirectoryName(project.Directory);
        return parent is null || relative == "." || Path.GetRelativePath(slnDir, parent).StartsWith("..", StringComparison.Ordinal)
            ? slnDir
            : parent;
    }

    /// <summary>
    /// The MSTest packages with the versions the repository already uses: Directory.Packages.props first, then another
    /// MSTest project in the solution, then the versions dotnet-lift knows to run MSTest v1-style code on both frameworks.
    /// </summary>
    private static List<PackageRef> Packages(SolutionInfo solution, CentralPackageFile? central)
    {
        string VersionOf(string id, string fallback) =>
            central?.VersionOf(id)
            ?? solution.Projects
                .Select(p => p.FindPackage(id)?.Version)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault()
            ?? fallback;

        var framework = VersionOf("MSTest.TestFramework", TestPackages.MSTestVersion);
        return
        [
            new("Microsoft.NET.Test.Sdk", VersionOf("Microsoft.NET.Test.Sdk", TestPackages.TestSdkVersion)),
            new("MSTest.TestAdapter", central?.VersionOf("MSTest.TestAdapter") ?? framework), // the adapter matches the framework
            new("MSTest.TestFramework", framework),
        ];
    }

    private static string ProjectFile(string targets, List<PackageRef> packages, string reference, CentralPackageFile? central)
    {
        var element = targets.Contains(';') ? "TargetFrameworks" : "TargetFramework";
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <{element}>{targets}</{element}>");
        sb.AppendLine("    <IsPackable>false</IsPackable>");
        sb.AppendLine("    <IsTestProject>true</IsTestProject>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("  <ItemGroup>");
        foreach (var package in packages)
        {
            sb.AppendLine($"    {PackageReferences.CreateText(package.Id, package.Version, central)}");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine();
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <ProjectReference Include=\"{reference}\" />");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine();
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    /// <summary>A first test that proves the setup on every target. Written as C# 7.3, the default for .NET Framework projects.</summary>
    private static string FirstTest(string testProjectName, string assemblyName)
    {
        var nl = Environment.NewLine;
        string[] lines =
        [
            "using System.Reflection;",
            "using Microsoft.VisualStudio.TestTools.UnitTesting;",
            "",
            $"namespace {Namespace(testProjectName)}",
            "{",
            "    // Test-first migration: write the tests here while the library still runs on .NET Framework,",
            "    // run them there (`dotnet test`), and keep them unchanged while the library moves. When it targets",
            "    // modern .NET too, the same tests run on both; anything that passes on one and fails on the other",
            "    // is a difference to look at before shipping.",
            "    [TestClass]",
            "    public class LibraryLoadTests",
            "    {",
            "        [TestMethod]",
            "        public void Library_loads_on_this_framework()",
            "        {",
            $"            var assembly = Assembly.Load(new AssemblyName(\"{assemblyName}\"));",
            "",
            $"            Assert.AreEqual(\"{assemblyName}\", assembly.GetName().Name);",
            "        }",
            "    }",
            "}",
            "",
        ];
        return string.Join(nl, lines);
    }

    /// <summary>The project name as a C# namespace: every part a valid identifier.</summary>
    private static string Namespace(string name) =>
        string.Join(".", name.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var chars = part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            var id = new string(chars);
            return char.IsDigit(id[0]) ? "_" + id : id;
        }));

    /// <summary>A GUID derived from the name, so planning twice (dry run, then for real) writes the same .sln.</summary>
    private static string GuidFor(string name) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant()))[..16]).ToString("B").ToUpperInvariant();

    /// <summary>
    /// The .sln with the new project: its Project block before Global, configuration lines that follow the library's
    /// (same configurations, built where the library is built), and the library's solution folder. Line endings are kept.
    /// </summary>
    public static string AddToSolution(string text, string slnPath, ProjectInfo library, string name, string projectPath, string guid)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var slnDir = Path.GetDirectoryName(Path.GetFullPath(slnPath))!;
        var libraryFull = Path.GetFullPath(library.FullPath);

        string? libraryGuid = null;
        foreach (var line in lines)
        {
            var m = ProjectLine().Match(line);
            if (m.Success && Path.GetFullPath(PathUtil.Combine(slnDir, m.Groups["path"].Value)).Equals(libraryFull, StringComparison.OrdinalIgnoreCase))
            {
                libraryGuid = m.Groups["guid"].Value;
                break;
            }
        }

        var global = lines.FindIndex(l => l.Trim() == "Global");
        if (global < 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(slnPath)} has no Global section.");
        }
        var relative = Path.GetRelativePath(slnDir, projectPath).Replace('/', '\\');
        lines.InsertRange(global,
        [
            $"Project(\"{CSharpSdkProjectTypeGuid}\") = \"{name}\", \"{relative}\", \"{guid}\"",
            "EndProject",
        ]);

        var configurations = SectionLines(lines, "SolutionConfigurationPlatforms")
            .Select(l => l.Split('=')[0].Trim())
            .Where(c => c.Length > 0)
            .ToList();
        if (configurations.Count > 0)
        {
            var libraryLines = libraryGuid is null ? new List<string>() : SectionLines(lines, "ProjectConfigurationPlatforms")
                .Where(l => l.StartsWith(libraryGuid, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var added = new List<string>();
            foreach (var configuration in configurations)
            {
                var prefix = $"{libraryGuid}.{configuration}.";
                var active = libraryLines.FirstOrDefault(l => l.StartsWith(prefix + "ActiveCfg", StringComparison.OrdinalIgnoreCase));
                var projectConfiguration = (active?.Split('=', 2)[1].Trim() ?? configuration).Split('|')[0] + "|Any CPU";
                added.Add($"\t\t{guid}.{configuration}.ActiveCfg = {projectConfiguration}");
                if (libraryLines.Count == 0 || libraryLines.Any(l => l.StartsWith(prefix + "Build.0", StringComparison.OrdinalIgnoreCase)))
                {
                    added.Add($"\t\t{guid}.{configuration}.Build.0 = {projectConfiguration}");
                }
            }

            var end = SectionEnd(lines, "ProjectConfigurationPlatforms");
            if (end >= 0)
            {
                lines.InsertRange(end, added);
            }
            else
            {
                lines.InsertRange(SectionEnd(lines, "SolutionConfigurationPlatforms") + 1,
                    ["\tGlobalSection(ProjectConfigurationPlatforms) = postSolution", .. added, "\tEndGlobalSection"]);
            }
        }

        if (libraryGuid is not null)
        {
            var nested = SectionLines(lines, "NestedProjects")
                .FirstOrDefault(l => l.StartsWith(libraryGuid, StringComparison.OrdinalIgnoreCase));
            if (nested is not null)
            {
                lines.Insert(SectionEnd(lines, "NestedProjects"), $"\t\t{guid} = {nested.Split('=', 2)[1].Trim()}");
            }
        }

        return string.Join(nl, lines);
    }

    private static int SectionStart(List<string> lines, string section) =>
        lines.FindIndex(l => l.TrimStart().StartsWith($"GlobalSection({section})", StringComparison.Ordinal));

    /// <summary>Index of the section's EndGlobalSection line, or -1.</summary>
    private static int SectionEnd(List<string> lines, string section)
    {
        var start = SectionStart(lines, section);
        return start < 0 ? -1 : lines.FindIndex(start, l => l.Trim() == "EndGlobalSection");
    }

    /// <summary>The trimmed lines inside a GlobalSection.</summary>
    private static List<string> SectionLines(List<string> lines, string section)
    {
        var start = SectionStart(lines, section);
        var end = SectionEnd(lines, section);
        return start < 0 || end < 0 ? [] : lines.Skip(start + 1).Take(end - start - 1).Select(l => l.Trim()).ToList();
    }

    private static List<string> Notes(SolutionInfo solution, ProjectInfo project, string targets, string targetFramework)
    {
        var notes = new List<string>();
        var existing = solution.Projects
            .Where(p => p.IsTest && p.ProjectReferences.Any(r =>
                Path.GetFullPath(r).Equals(Path.GetFullPath(project.FullPath), StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();
        if (existing.Count > 0)
        {
            notes.Add($"{project.Name} already has tests in {string.Join(", ", existing)}; this adds another project next to them.");
        }

        var all = targets.Split(';');
        if (all.Any(t => t.Equals(targetFramework, StringComparison.OrdinalIgnoreCase)))
        {
            notes.Add($"The tests target {targets.Replace(";", " and ")}: `dotnet test` runs them on each, so a difference between the frameworks shows up as a test that fails on one of them.");
        }
        else
        {
            notes.Add($"{project.Name} targets {targets} today, so the tests do too: write them now and run them on .NET Framework. `dotnet-lift upgrade` retargets the test project after the library, and the same tests then run on {targetFramework}.");
        }

        if (all.Any(ProjectFormatAnalyzer.IsNetFramework))
        {
            notes.Add("Running tests on .NET Framework needs Windows (or Mono); on Linux and macOS build that target and run it on Windows CI.");
        }
        return notes;
    }
}
