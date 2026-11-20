using System.Xml;
using System.Xml.Linq;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Moves the static files of a web project (Content/, Scripts/, fonts/, images, favicon.ico, robots.txt) into wwwroot/,
/// keeping their folders: Content/site.css becomes wwwroot/Content/site.css, so UseStaticFiles serves it under the same
/// URL (/Content/site.css) and links in views, CSS and bookmarks keep working.
/// <para>
/// It only runs once the project is an ASP.NET Core project: the .NET Framework app serves these files from their
/// current folders and would lose them. Files are moved byte for byte (git sees renames), files that already exist
/// in wwwroot are left where they are, and explicit project items pointing at moved files are updated.
/// </para>
/// </summary>
public sealed class StaticFilesToWwwroot : IStepExecutor
{
    public const string WebRoot = "wwwroot";

    /// <summary>Top-level folders of the MVC 5 / Web API templates that hold files served as they are.</summary>
    public static readonly IReadOnlyList<string> StaticFolders = ["Content", "Scripts", "fonts", "Images", "img"];

    /// <summary>Files in the project root that browsers and crawlers ask for by name.</summary>
    public static readonly IReadOnlyList<string> StaticRootFiles = ["favicon.ico", "robots.txt"];

    /// <summary>Files in those folders that are code or configuration, not content; they stay where they are.</summary>
    private static readonly HashSet<string> NotStatic = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".config", ".cshtml", ".vbhtml", ".aspx", ".ascx", ".master", ".asax", ".resx", ".tt", ".csproj",
    };

    public string StepId => "static-files";

    public bool CanExecute(ProjectInfo project) => project.IsWeb;

    public string? NotReadyReason(ProjectInfo project) =>
        IsAspNetCoreProject(project.FullPath)
            ? null
            : "the project is still an ASP.NET (.NET Framework) project, which serves these files from their current folders. " +
              "This step runs after the ASP.NET Core project step.";

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        if (!IsAspNetCoreProject(project.FullPath))
        {
            return [];
        }

        var moves = PlanMoves(project.Directory).Moves;
        if (moves.Count == 0)
        {
            return [];
        }

        var changes = new List<FileChange>(moves);
        if (UpdateProjectItems(project.FullPath, project.Directory, moves) is { } csproj)
        {
            changes.Add(new FileChange(project.FullPath, csproj));
        }
        return changes;
    }

    /// <summary>The planned moves, and the files left in place because wwwroot already has a file with that name.</summary>
    public static (IReadOnlyList<FileChange> Moves, IReadOnlyList<string> Conflicts) PlanMoves(string projectDirectory)
    {
        var moves = new List<FileChange>();
        var conflicts = new List<string>();
        foreach (var source in StaticFiles(projectDirectory))
        {
            var target = Path.Combine(projectDirectory, WebRoot, Path.GetRelativePath(projectDirectory, source));
            if (File.Exists(target))
            {
                conflicts.Add(source);
            }
            else
            {
                moves.Add(FileChange.Move(source, target));
            }
        }
        return (moves, conflicts);
    }

    /// <summary>True for an SDK-style project on Microsoft.NET.Sdk.Web (Sdk attribute, &lt;Sdk&gt; element or Sdk import).</summary>
    public static bool IsAspNetCoreProject(string projectPath)
    {
        XElement? root;
        try
        {
            root = XDocument.Load(projectPath).Root;
        }
        catch (Exception e) when (e is XmlException or IOException)
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        static bool IsWebSdk(string? sdk) =>
            sdk is not null && sdk.Split(';').Any(s => s.Trim().Split('/')[0].Trim()
                .Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase));

        return IsWebSdk(root.Attribute("Sdk")?.Value)
            || root.Elements().Any(e => e.Name.LocalName == "Sdk" && IsWebSdk(e.Attribute("Name")?.Value))
            || root.Elements().Any(e => e.Name.LocalName == "Import" && IsWebSdk(e.Attribute("Sdk")?.Value));
    }

    private static IEnumerable<string> StaticFiles(string projectDirectory)
    {
        var found = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(projectDirectory))
        {
            if (!StaticFolders.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            found.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !PathUtil.IsBuildOutput(f) && !NotStatic.Contains(Path.GetExtension(f))));
        }

        found.AddRange(Directory.EnumerateFiles(projectDirectory)
            .Where(f => StaticRootFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)));

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// Items the project lists explicitly for a moved file. Sdk.Web already includes everything under wwwroot as Content,
    /// so a plain Content/None item goes away (keeping it would be a duplicate item); one with metadata becomes a
    /// Content Update on the new path. Other item types, Update and Remove follow the file. Null when nothing changes.
    /// </summary>
    private static string? UpdateProjectItems(string projectPath, string projectDirectory, IReadOnlyList<FileChange> moves)
    {
        var renamed = moves.ToDictionary(
            m => ProjectPath(projectDirectory, m.MovedFrom!),
            m => ProjectPath(projectDirectory, m.Path),
            StringComparer.OrdinalIgnoreCase);

        XDocument doc;
        try
        {
            doc = ProjectXml.Load(projectPath);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = doc.Root!;
        var changed = false;
        var items = ProjectXml.Elements(root, "ItemGroup").SelectMany(g => g.Elements()).ToList();
        foreach (var item in items)
        {
            foreach (var name in new[] { "Update", "Remove" })
            {
                if (item.Attribute(name) is { } attr && renamed.TryGetValue(Normalize(attr.Value), out var moved))
                {
                    attr.Value = moved;
                    changed = true;
                }
            }

            if (item.Attribute("Include") is not { } include || !renamed.TryGetValue(Normalize(include.Value), out var target))
            {
                continue;
            }

            changed = true;
            var isContent = item.Name.LocalName is "Content" or "None";
            var hasMetadata = item.Elements().Any() || item.Attributes().Any(a => a.Name.LocalName != "Include");
            if (isContent && !hasMetadata)
            {
                ProjectXml.Remove(item);
            }
            else if (isContent)
            {
                item.Name = root.Name.Namespace + "Content";
                include.Remove();
                item.SetAttributeValue("Update", target);
            }
            else
            {
                include.Value = target;
            }
        }

        if (!changed)
        {
            return null;
        }

        ProjectXml.RemoveEmptyItemGroups(root);
        return ProjectXml.Serialize(doc);
    }

    /// <summary>A path as project files write it: relative to the project, with backslashes.</summary>
    private static string ProjectPath(string projectDirectory, string path) =>
        Path.GetRelativePath(projectDirectory, path).Replace('/', '\\');

    private static string Normalize(string value)
    {
        var v = value.Trim().Replace('/', '\\');
        return v.StartsWith(".\\", StringComparison.Ordinal) ? v[2..] : v;
    }
}
