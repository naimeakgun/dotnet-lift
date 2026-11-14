using System.Security;
using System.Text;
using System.Xml.Linq;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Rewrites a legacy class library project file in the SDK format, still targeting the same .NET Framework version.
/// Behaviour is preserved: the same files are compiled, the same references and packages are used.
/// </summary>
public sealed class SdkStyleConversion : IStepExecutor
{
    // References the SDK adds by itself for .NET Framework targets.
    private static readonly HashSet<string> ImplicitFrameworkReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "System", "System.Core", "System.Data", "System.Drawing", "System.IO.Compression.FileSystem",
        "System.Numerics", "System.Runtime.Serialization", "System.Xml", "System.Xml.Linq",
    };

    public string StepId => "sdk-style";

    public bool CanExecute(ProjectInfo project) => !project.IsSdkStyle && !project.IsWeb;

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        var current = ProjectLoader.Load(project.FullPath);
        if (current.IsSdkStyle)
        {
            return [];
        }

        var changes = new List<FileChange> { new(current.FullPath, Convert(current)) };

        var packagesConfig = Path.Combine(current.Directory, "packages.config");
        if (File.Exists(packagesConfig))
        {
            changes.Add(new FileChange(packagesConfig, null)); // its packages were carried into the new project file
        }

        return changes;
    }

    internal static string Convert(ProjectInfo project)
    {
        var root = XDocument.Load(project.FullPath).Root!;
        var dir = project.Directory;
        var name = project.Name;

        string? Value(string localName) =>
            ProjectXml.Elements(root, localName).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);

        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{ToTfm(project.TargetFramework)}</TargetFramework>");

        var outputType = Value("OutputType");
        if (outputType is not null && !outputType.Equals("Library", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"    <OutputType>{outputType}</OutputType>");
        }

        var rootNamespace = Value("RootNamespace");
        if (rootNamespace is not null && rootNamespace != name)
        {
            sb.AppendLine($"    <RootNamespace>{Escape(rootNamespace)}</RootNamespace>");
        }

        var assemblyName = Value("AssemblyName");
        if (assemblyName is not null && assemblyName != name)
        {
            sb.AppendLine($"    <AssemblyName>{Escape(assemblyName)}</AssemblyName>");
        }

        if (File.Exists(Path.Combine(dir, "Properties", "AssemblyInfo.cs")))
        {
            // Keep the existing AssemblyInfo.cs; letting the SDK generate one would duplicate the attributes.
            sb.AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        }

        foreach (var carried in new[] { "LangVersion", "AllowUnsafeBlocks", "SignAssembly", "AssemblyOriginatorKeyFile", "DocumentationFile" })
        {
            var v = Value(carried);
            if (v is not null)
            {
                sb.AppendLine($"    <{carried}>{Escape(v)}</{carried}>");
            }
        }

        sb.AppendLine("  </PropertyGroup>");

        // Framework references that are not implicit and do not come from a NuGet package.
        var referenceElements = ProjectXml.Elements(root, "Reference")
            .Where(r => r.Attribute("Include") is not null)
            .ToList();

        var references = referenceElements
            .Where(r => ProjectXml.Child(r, "HintPath") is null)
            .Select(r => r.Attribute("Include")!.Value.Split(',')[0].Trim())
            .Where(r => !ImplicitFrameworkReferences.Contains(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // DLLs referenced by HintPath outside a packages folder (lib/ folders, shared binaries) are kept as-is.
        var fileReferences = referenceElements
            .Select(r => (Include: r.Attribute("Include")!.Value.Split(',')[0].Trim(), HintPath: ProjectXml.Child(r, "HintPath")))
            .Where(r => r.HintPath is not null && !IsPackagesFolder(r.HintPath))
            .ToList();

        Group(sb, references.Select(r => $"<Reference Include=\"{Escape(r)}\" />")
            .Concat(fileReferences.Select(r => $"<Reference Include=\"{Escape(r.Include)}\"><HintPath>{Escape(r.HintPath!)}</HintPath></Reference>")));

        // Packages: PackageReference items already in the file, else packages.config.
        var packages = project.Packages
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"<PackageReference Include=\"{Escape(p.Id)}\" Version=\"{Escape(p.Version)}\" />");
        Group(sb, packages);

        Group(sb, ProjectXml.Elements(root, "ProjectReference")
            .Select(p => p.Attribute("Include")?.Value)
            .Where(p => p is not null)
            .Select(p => $"<ProjectReference Include=\"{Escape(p!)}\" />"));

        Group(sb, CompileItems(root, dir));
        Group(sb, OtherItems(root));

        sb.AppendLine();
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    /// <summary>
    /// The SDK compiles every *.cs under the project folder. Files the old project did not compile get a Compile Remove;
    /// files it compiled from outside the folder (or via a link) are listed explicitly.
    /// </summary>
    private static IEnumerable<string> CompileItems(XElement root, string dir)
    {
        var compiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitItems = new List<string>();

        foreach (var item in ProjectXml.Elements(root, "Compile"))
        {
            var include = item.Attribute("Include")?.Value;
            if (include is null)
            {
                continue;
            }

            if (include.Contains('*'))
            {
                explicitItems.Add($"<Compile Include=\"{Escape(include)}\" />");
                continue;
            }

            var full = PathUtil.Combine(dir, include);
            var link = ProjectXml.Child(item, "Link");
            var insideProject = full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            if (!insideProject || link is not null)
            {
                explicitItems.Add(link is null
                    ? $"<Compile Include=\"{Escape(include)}\" />"
                    : $"<Compile Include=\"{Escape(include)}\" Link=\"{Escape(link)}\" />");
            }
            else
            {
                compiled.Add(full);
            }
        }

        var removes = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !PathUtil.IsBuildOutput(f))
            .Where(f => !compiled.Contains(f))
            .Select(f => PathUtil.Relative(dir, f).Replace('/', '\\'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"<Compile Remove=\"{Escape(f)}\" />");

        return removes.Concat(explicitItems);
    }

    /// <summary>Embedded resources and copied files that the SDK globs would not pick up by themselves.</summary>
    private static IEnumerable<string> OtherItems(XElement root)
    {
        foreach (var item in ProjectXml.Elements(root, "EmbeddedResource"))
        {
            var include = item.Attribute("Include")?.Value;
            if (include is not null && !include.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"<EmbeddedResource Include=\"{Escape(include)}\" />";
            }
        }

        foreach (var item in ProjectXml.Elements(root, "None").Concat(ProjectXml.Elements(root, "Content")))
        {
            var include = item.Attribute("Include")?.Value;
            var copy = ProjectXml.Child(item, "CopyToOutputDirectory");
            if (include is not null && copy is not null)
            {
                yield return $"<None Update=\"{Escape(include)}\" CopyToOutputDirectory=\"{Escape(copy)}\" />";
            }
        }
    }

    private static void Group(StringBuilder sb, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("  <ItemGroup>");
        foreach (var item in list)
        {
            sb.AppendLine($"    {item}");
        }
        sb.AppendLine("  </ItemGroup>");
    }

    /// <summary>"v4.8" → "net48", "v4.7.2" → "net472". Unknown values default to net48.</summary>
    public static string ToTfm(string? targetFrameworkVersion)
    {
        if (string.IsNullOrWhiteSpace(targetFrameworkVersion))
        {
            return "net48";
        }

        var v = targetFrameworkVersion.Trim().TrimStart('v', 'V');
        var digits = v.Replace(".", "");
        return digits.Length > 0 && digits.All(char.IsDigit) ? "net" + digits : "net48";
    }

    private static bool IsPackagesFolder(string path)
    {
        var p = path.Replace('/', '\\');
        return p.StartsWith("packages\\", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\packages\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? value;
}
