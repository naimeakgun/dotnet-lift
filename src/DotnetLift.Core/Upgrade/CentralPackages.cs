using System.Xml.Linq;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// A Directory.Packages.props that turns on Central Package Management (CPM).
/// When it applies to a project, PackageReference items must not carry a Version; the version lives here as a PackageVersion.
/// </summary>
public sealed class CentralPackageFile
{
    public const string FileName = "Directory.Packages.props";

    private readonly XDocument _doc;
    private readonly string? _originalText;

    private CentralPackageFile(string path, XDocument doc, string? originalText)
    {
        Path = path;
        _doc = doc;
        _originalText = originalText;
    }

    public string Path { get; }

    public bool Changed { get; private set; }

    /// <summary>The Directory.Packages.props MSBuild would use for this folder, if it enables CPM.</summary>
    public static CentralPackageFile? Find(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, FileName);
            if (!File.Exists(candidate))
            {
                continue;
            }

            // MSBuild only imports the nearest one, so stop here either way.
            var file = Load(candidate);
            return file.IsEnabled ? file : null;
        }
        return null;
    }

    public static CentralPackageFile Load(string path)
    {
        var text = File.ReadAllText(path);
        return new CentralPackageFile(path, XDocument.Parse(text, LoadOptions.PreserveWhitespace), text);
    }

    /// <summary>A new, empty Directory.Packages.props with CPM turned on.</summary>
    public static CentralPackageFile Create(string path)
    {
        var nl = Environment.NewLine;
        var text =
            $"<Project>{nl}" +
            $"  <PropertyGroup>{nl}" +
            $"    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>{nl}" +
            $"  </PropertyGroup>{nl}" +
            $"  <ItemGroup>{nl}" +
            $"  </ItemGroup>{nl}" +
            $"</Project>{nl}";
        return new CentralPackageFile(path, XDocument.Parse(text, LoadOptions.PreserveWhitespace), null) { Changed = true };
    }

    public bool IsEnabled =>
        ProjectXml.Elements(_doc.Root!, "ManagePackageVersionsCentrally")
            .Any(e => e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    public string? VersionOf(string id) =>
        ProjectXml.Elements(_doc.Root!, "PackageVersion")
            .FirstOrDefault(e => string.Equals(e.Attribute("Include")?.Value, id, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("Version")?.Value;

    /// <summary>Adds a PackageVersion unless the package already has one.</summary>
    public void Ensure(string id, string version)
    {
        if (VersionOf(id) is not null)
        {
            return;
        }

        var root = _doc.Root!;
        var ns = root.Name.Namespace;
        var item = new XElement(ns + "PackageVersion", new XAttribute("Include", id), new XAttribute("Version", version));

        // Keep the list sorted: insert before the first PackageVersion that sorts after this id.
        var versions = ProjectXml.Elements(root, "PackageVersion").ToList();
        var next = versions.FirstOrDefault(v => string.Compare(v.Attribute("Include")?.Value, id, StringComparison.OrdinalIgnoreCase) > 0);
        if (next is not null)
        {
            var indent = next.PreviousNode is XText t && string.IsNullOrWhiteSpace(t.Value) ? t.Value : Environment.NewLine + "    ";
            next.AddBeforeSelf(item, new XText(indent));
        }
        else
        {
            var group = versions.LastOrDefault()?.Parent
                        ?? ProjectXml.Elements(root, "ItemGroup").FirstOrDefault(g => g.Attribute("Condition") is null);
            if (group is null)
            {
                group = new XElement(ns + "ItemGroup");
                ProjectXml.AddItemGroup(root, group, [item]);
            }
            else
            {
                ProjectXml.AddToItemGroup(root, group, item);
            }
        }

        Changed = true;
    }

    public FileChange ToChange()
    {
        var text = ProjectXml.Serialize(_doc);
        return new FileChange(Path, text);
    }

    public bool DiffersFromDisk => _originalText is null || ProjectXml.Serialize(_doc) != _originalText;
}

/// <summary>Creates PackageReference items the way the project's package management expects.</summary>
public static class PackageReferences
{
    /// <summary>
    /// Without CPM: Include + Version. With CPM: Include only, the version goes to Directory.Packages.props
    /// (or VersionOverride when the central version is a different one).
    /// </summary>
    public static XElement Create(XNamespace ns, string id, string version, CentralPackageFile? central)
    {
        if (central is null)
        {
            return new XElement(ns + "PackageReference", new XAttribute("Include", id), new XAttribute("Version", version));
        }

        var centralVersion = central.VersionOf(id);
        if (string.IsNullOrWhiteSpace(version))
        {
            return new XElement(ns + "PackageReference", new XAttribute("Include", id)); // nothing to pin
        }
        if (centralVersion is null)
        {
            central.Ensure(id, version);
            return new XElement(ns + "PackageReference", new XAttribute("Include", id));
        }

        return string.Equals(centralVersion, version, StringComparison.OrdinalIgnoreCase)
            ? new XElement(ns + "PackageReference", new XAttribute("Include", id))
            : new XElement(ns + "PackageReference", new XAttribute("Include", id), new XAttribute("VersionOverride", version));
    }

    /// <summary>The same, as one line of project XML (for project files built as text).</summary>
    public static string CreateText(string id, string version, CentralPackageFile? central) =>
        Create(XNamespace.None, id, version, central).ToString(SaveOptions.DisableFormatting);

    /// <summary>Adds the Directory.Packages.props change to a list of changes when it was modified.</summary>
    public static void AddIfChanged(List<FileChange> changes, CentralPackageFile? central)
    {
        if (central is { Changed: true } && central.DiffersFromDisk)
        {
            changes.Add(central.ToChange());
        }
    }
}
