using System.Xml.Linq;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Loading;

/// <summary>Reads both legacy (ToolsVersion) and SDK-style project files without MSBuild.</summary>
public static class ProjectLoader
{
    // {349c5851-65df-11da-9384-00065b846f21} = ASP.NET Web Application
    private const string WebApplicationTypeGuid = "349c5851-65df-11da-9384-00065b846f21";
    // {E3E379DF-F4C6-4180-9B81-6769533ABE47} = ASP.NET MVC 4, {E53F8FEA-EAE0-44A6-8774-FFD645390401} = MVC 3
    private static readonly string[] MvcTypeGuids =
        ["e3e379df-f4c6-4180-9b81-6769533abe47", "e53f8fea-eae0-44a6-8774-ffd645390401"];

    public static ProjectInfo Load(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var dir = Path.GetDirectoryName(fullPath)!;
        var doc = XDocument.Load(fullPath);
        var root = doc.Root ?? throw new InvalidDataException($"'{fullPath}' is not a valid project file.");

        var isSdk = root.Attribute("Sdk") is not null || Elements(root, "Sdk").Any();
        var tfm = isSdk
            ? FirstValue(root, "TargetFramework") ?? FirstValue(root, "TargetFrameworks")
            : FirstValue(root, "TargetFrameworkVersion");

        var packagesConfig = Path.Combine(dir, "packages.config");
        var webConfig = FindFile(dir, "Web.config");
        var appConfig = FindFile(dir, "App.config");

        var project = new ProjectInfo
        {
            Name = Path.GetFileNameWithoutExtension(fullPath),
            FullPath = fullPath,
            IsSdkStyle = isSdk,
            TargetFramework = tfm,
            OutputType = FirstValue(root, "OutputType"),
            HasPackagesConfig = File.Exists(packagesConfig),
            WebConfigPath = webConfig,
            AppConfigPath = appConfig,
            HasGlobalAsax = File.Exists(Path.Combine(dir, "Global.asax")),
        };

        foreach (var pr in Elements(root, "ProjectReference"))
        {
            var include = pr.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                project.ProjectReferences.Add(PathUtil.Combine(dir, include));
            }
        }

        foreach (var r in Elements(root, "Reference"))
        {
            var include = r.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                // "System.Web.Mvc, Version=5.2.9.0, Culture=..." -> "System.Web.Mvc"
                var name = include.Split(',')[0].Trim();
                project.AssemblyReferences.Add(name);

                var hintPath = ChildValue(r, "HintPath");
                if (hintPath is not null && !IsPackagesFolder(hintPath))
                {
                    project.FileReferences.Add(new FileReference(name, AssemblyVersion(include), hintPath, PathUtil.Combine(dir, hintPath)));
                }
            }
        }

        foreach (var name in new[] { "PackageVersion", "Version" })
        {
            // Properties only (PackageReference has a <Version> child too).
            var value = root.Elements().Where(e => e.Name.LocalName == "PropertyGroup")
                .SelectMany(g => g.Elements()).Where(e => e.Name.LocalName == name)
                .Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
            if (value is not null)
            {
                project.DeclaredVersion = value;
                project.DeclaredVersionSource = $"<{name}> in {Path.GetFileName(fullPath)}";
                break;
            }
        }
        if (project.DeclaredVersion is null)
        {
            foreach (var nuspec in Directory.EnumerateFiles(dir, "*.nuspec"))
            {
                try
                {
                    var version = XDocument.Load(nuspec).Descendants().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim();
                    if (!string.IsNullOrEmpty(version))
                    {
                        project.DeclaredVersion = version;
                        project.DeclaredVersionSource = Path.GetFileName(nuspec);
                        break;
                    }
                }
                catch (System.Xml.XmlException)
                {
                    // A broken .nuspec is not this loader's problem.
                }
            }
        }

        foreach (var p in Elements(root, "PackageReference"))
        {
            var id = p.Attribute("Include")?.Value;
            var version = p.Attribute("Version")?.Value ?? ChildValue(p, "Version") ?? "";
            if (!string.IsNullOrWhiteSpace(id))
            {
                project.Packages.Add(new PackageRef(id, version));
            }
        }

        if (project.HasPackagesConfig)
        {
            project.Packages.AddRange(ReadPackagesConfig(packagesConfig));
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            if (PathUtil.IsBuildOutput(file) || PathUtil.IsBackup(file))
            {
                continue;
            }

            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".cs")
            {
                project.SourceFiles.Add(file);
            }
            else if (ext is ".cshtml" or ".aspx" or ".ascx" or ".master")
            {
                project.ViewFiles.Add(file);
            }
        }

        project.SourceFiles.Sort(StringComparer.Ordinal);
        project.ViewFiles.Sort(StringComparer.Ordinal);
        project.Kind = DetectKind(project, FirstValue(root, "ProjectTypeGuids"));
        return project;
    }

    public static IEnumerable<PackageRef> ReadPackagesConfig(string path)
    {
        var doc = XDocument.Load(path);
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "package"))
        {
            var id = p.Attribute("id")?.Value;
            if (!string.IsNullOrWhiteSpace(id))
            {
                yield return new PackageRef(id, p.Attribute("version")?.Value ?? "");
            }
        }
    }

    private static ProjectKind DetectKind(ProjectInfo p, string? projectTypeGuids)
    {
        var guids = (projectTypeGuids ?? "").ToLowerInvariant();
        var isWebApp = guids.Contains(WebApplicationTypeGuid) || MvcTypeGuids.Any(g => guids.Contains(g))
            || p.WebConfigPath is not null && p.HasGlobalAsax;

        var hasMvc = p.References("System.Web.Mvc") || p.HasPackage("Microsoft.AspNet.Mvc");
        var hasWebApi = p.References("System.Web.Http") || p.HasPackage("Microsoft.AspNet.WebApi.Core");
        var hasWebForms = p.ViewFiles.Any(f => f.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase));

        if (isWebApp || hasMvc || hasWebApi)
        {
            if (hasMvc && hasWebApi) return ProjectKind.AspNetMvcAndWebApi;
            if (hasMvc) return ProjectKind.AspNetMvc;
            if (hasWebApi) return ProjectKind.AspNetWebApi;
            if (hasWebForms) return ProjectKind.AspNetWebForms;
            return ProjectKind.AspNetMvc;
        }

        return string.Equals(p.OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase)
            ? ProjectKind.Console
            : ProjectKind.ClassLibrary;
    }

    internal static bool IsPackagesFolder(string path)
    {
        var p = path.Replace('/', '\\');
        return p.StartsWith("packages\\", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\packages\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Acme.Logging, Version=2.3.0.0, Culture=neutral" → "2.3.0.0".</summary>
    internal static string? AssemblyVersion(string include)
    {
        var part = include.Split(',').Skip(1).Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
        return part is null ? null : part.Substring("Version=".Length).Trim();
    }

    private static string? FindFile(string dir, string name) =>
        Directory.EnumerateFiles(dir, "*.config")
            .FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));

    // Legacy projects use the msbuild/2003 namespace and SDK-style ones use none; match on local names.
    private static IEnumerable<XElement> Elements(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    private static string? FirstValue(XElement root, string localName) =>
        Elements(root, localName).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);

    private static string? ChildValue(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName)?.Value.Trim();
}
