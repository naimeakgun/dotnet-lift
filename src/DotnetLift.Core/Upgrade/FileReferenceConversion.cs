using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Upgrade;

/// <summary>What one DLL reference should become.</summary>
public abstract record PackageTarget
{
    public sealed record Package(string Id, string? Version) : PackageTarget;

    /// <summary>Leave the file reference as it is (e.g. a vendor SDK with no package).</summary>
    public sealed record Keep : PackageTarget;
}

/// <summary>
/// dotnet-lift.packages.json: which package each referenced DLL comes from.
/// <code>
/// {
///   "Acme.Logging": "Acme.Logging@2.3.0",    // package id and version
///   "Acme.Core": "Acme.Core",                // version taken from the DLL reference
///   "Vendor.Sdk": "keep"                     // stays a file reference
/// }
/// </code>
/// </summary>
public sealed class PackageMap
{
    public const string FileName = "dotnet-lift.packages.json";

    private readonly Dictionary<string, PackageTarget> _entries;

    private PackageMap(Dictionary<string, PackageTarget> entries) => _entries = entries;

    public static PackageMap Empty { get; } = new(new Dictionary<string, PackageTarget>(StringComparer.OrdinalIgnoreCase));

    public static PackageMap Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} must be a JSON object: {{ \"AssemblyName\": \"PackageId@Version\" }}.");
        }

        var entries = new Dictionary<string, PackageTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()!.Trim() : "";
            if (value.Length == 0)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}: \"{property.Name}\" needs a string value (\"PackageId@Version\", \"PackageId\" or \"keep\").");
            }

            if (value.Equals("keep", StringComparison.OrdinalIgnoreCase))
            {
                entries[property.Name] = new PackageTarget.Keep();
                continue;
            }

            var at = value.LastIndexOf('@');
            entries[property.Name] = at > 0
                ? new PackageTarget.Package(value[..at].Trim(), value[(at + 1)..].Trim())
                : new PackageTarget.Package(value, null);
        }
        return new PackageMap(entries);
    }

    public PackageTarget? Resolve(string assemblyName) => _entries.GetValueOrDefault(assemblyName);

    /// <summary>A starting point for the map: every DLL found, guessed as "package id = assembly name".</summary>
    public static string Template(IEnumerable<FileReference> references)
    {
        var lines = references
            .GroupBy(r => r.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var version = g.Select(r => FileReferenceConversion.NormalizeVersion(r.AssemblyVersion)).FirstOrDefault(v => v is not null);
                var value = version is null ? g.Key : $"{g.Key}@{version}";
                return $"  {JsonSerializer.Serialize(g.Key)}: {JsonSerializer.Serialize(value)}";
            });

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // Which NuGet package each DLL comes from: \"PackageId@Version\", \"PackageId\" (version from the reference) or \"keep\".");
        sb.AppendLine("  // The entries below are guesses (package id = assembly name). Check them against your feed.");
        sb.AppendLine(string.Join("," + Environment.NewLine, lines));
        sb.AppendLine("}");
        return sb.ToString();
    }
}

public sealed record FileReferencePlan(
    IReadOnlyList<FileChange> Changes,
    IReadOnlyList<string> Converted,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> Notes,
    IReadOnlyList<FileReference> AllReferences);

/// <summary>
/// DLLs referenced by HintPath outside packages/ (e.g. a SolutionItems folder) → PackageReference.
/// The DLL files themselves are left alone; the plan notes which ones nothing references any more.
/// </summary>
public static class FileReferenceConversion
{
    public static FileReferencePlan Plan(SolutionInfo solution, PackageMap map, bool byName)
    {
        var changes = new List<FileChange>();
        var converted = new List<string>();
        var unmapped = new List<string>();
        var notes = new List<string>();
        var centralFiles = new Dictionary<string, CentralPackageFile>(StringComparer.OrdinalIgnoreCase);
        var stillReferenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var convertedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = solution.Projects.SelectMany(p => p.FileReferences).ToList();

        foreach (var project in solution.Projects)
        {
            if (project.FileReferences.Count == 0)
            {
                continue;
            }

            if (project.HasPackagesConfig)
            {
                notes.Add($"{project.Name}: {project.FileReferences.Count} DLL reference(s) left as they are, because the project uses packages.config. " +
                          "Convert it to PackageReference first (`dotnet-lift upgrade`, first step), then run this again.");
                foreach (var r in project.FileReferences)
                {
                    stillReferenced.Add(r.FullPath);
                }
                continue;
            }

            var central = project.IsSdkStyle ? CentralFor(project.Directory, centralFiles) : null;
            var doc = ProjectXml.Load(project.FullPath);
            var root = doc.Root!;
            var changed = false;

            foreach (var element in ProjectXml.Elements(root, "Reference").ToList())
            {
                var include = element.Attribute("Include")?.Value;
                var hintPath = ProjectXml.Child(element, "HintPath");
                if (include is null || hintPath is null || Loading.ProjectLoader.IsPackagesFolder(hintPath))
                {
                    continue;
                }

                var reference = project.FileReferences.First(r => r.HintPath == hintPath);
                var target = map.Resolve(reference.AssemblyName)
                             ?? (byName ? new PackageTarget.Package(reference.AssemblyName, null) : null);

                switch (target)
                {
                    case PackageTarget.Keep:
                        stillReferenced.Add(reference.FullPath);
                        continue;
                    case null:
                        unmapped.Add($"{project.Name}: {reference.AssemblyName} ({hintPath})");
                        stillReferenced.Add(reference.FullPath);
                        continue;
                }

                var package = (PackageTarget.Package)target;
                var version = package.Version ?? NormalizeVersion(reference.AssemblyVersion);
                if (version is null)
                {
                    unmapped.Add($"{project.Name}: {reference.AssemblyName} ({hintPath}): no version known; add \"{package.Id}@<version>\" to {PackageMap.FileName}");
                    stillReferenced.Add(reference.FullPath);
                    continue;
                }

                // The package goes where the reference was: same condition (e.g. only for net48).
                var condition = element.Parent?.Attribute("Condition")?.Value;
                ProjectXml.Remove(element);
                AddPackage(root, condition, PackageReferences.Create(root.Name.Namespace, package.Id, version, central));
                converted.Add($"{project.Name}: {reference.AssemblyName} → {package.Id} {version}");
                convertedFiles.Add(reference.FullPath);
                changed = true;
            }

            if (changed)
            {
                ProjectXml.RemoveEmptyItemGroups(root);
                changes.Add(new FileChange(project.FullPath, ProjectXml.Serialize(doc)));
            }
        }

        foreach (var central in centralFiles.Values)
        {
            PackageReferences.AddIfChanged(changes, central);
        }

        foreach (var file in convertedFiles.Where(f => !stillReferenced.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var root = File.Exists(solution.Path) ? Path.GetDirectoryName(solution.Path)! : solution.Path;
            notes.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')} is no longer referenced by any project in this solution. " +
                      "Delete it once nothing else (other solutions, build scripts) uses it.");
        }

        return new FileReferencePlan(changes, converted, unmapped, notes, all);
    }

    /// <summary>2.3.0.0 → 2.3.0, 2.3.1.4 stays; null when there is nothing usable.</summary>
    public static string? NormalizeVersion(string? assemblyVersion)
    {
        if (string.IsNullOrWhiteSpace(assemblyVersion))
        {
            return null;
        }

        var parts = assemblyVersion.Trim().Split('.');
        return parts.Length == 4 && parts[3] == "0" ? string.Join('.', parts.Take(3)) : assemblyVersion.Trim();
    }

    private static CentralPackageFile? CentralFor(string directory, Dictionary<string, CentralPackageFile> cache)
    {
        var found = CentralPackageFile.Find(directory);
        if (found is null)
        {
            return null;
        }
        if (!cache.TryGetValue(found.Path, out var cached))
        {
            cache[found.Path] = cached = found;
        }
        return cached;
    }

    private static void AddPackage(XElement root, string? condition, XElement item)
    {
        var group = ProjectXml.Elements(root, "ItemGroup").FirstOrDefault(g =>
            condition is null
                ? g.Attribute("Condition") is null && g.Elements().Any(e => e.Name.LocalName == "PackageReference")
                : g.Attribute("Condition")?.Value == condition);

        if (group is null)
        {
            group = condition is null
                ? new XElement(root.Name.Namespace + "ItemGroup")
                : new XElement(root.Name.Namespace + "ItemGroup", new XAttribute("Condition", condition));
            ProjectXml.AddItemGroup(root, group, [item]);
        }
        else
        {
            ProjectXml.AddToItemGroup(root, group, item);
        }
    }
}
