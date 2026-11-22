using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// A legacy library that projects often keep referencing after the code that used it is gone.
/// Its assemblies and packages are removed together, and only when none of its namespaces is used.
/// </summary>
/// <param name="Name">Shown to the user.</param>
/// <param name="Assemblies">&lt;Reference Include="..."&gt; names.</param>
/// <param name="Packages">Package ids (packages.config and PackageReference); language packs such as "Microsoft.AspNet.Mvc.tr" go with them.</param>
/// <param name="Namespaces">Any use of one of these (or a namespace below it) keeps the whole library.</param>
/// <param name="DependentToken">Another package or assembly whose name contains this probably depends on the library, so it stays.</param>
/// <param name="ViewMarkers">Text in views that uses the library without naming its namespace.</param>
/// <param name="UsedByRazorViews">Any .cshtml view keeps the library (Razor views compile against it).</param>
public sealed record LegacyLibrary(
    string Name,
    string[] Assemblies,
    string[] Packages,
    string[] Namespaces,
    string? DependentToken = null,
    string[]? ViewMarkers = null,
    bool UsedByRazorViews = false);

/// <summary>A library one project references: removed (<see cref="KeptBecause"/> null) or kept, and why.</summary>
public sealed record CleanupItem(string Project, string Library, IReadOnlyList<string> References, string? KeptBecause);

public sealed record CleanupPlan(
    IReadOnlyList<CleanupItem> Removed,
    IReadOnlyList<CleanupItem> Kept,
    IReadOnlyList<FileChange> ReferenceChanges,
    IReadOnlyList<string> BackupFolders,
    IReadOnlyList<FileChange> BackupChanges,
    IReadOnlyList<string> Notes);

/// <summary>
/// Removes what does not have to be migrated: references to legacy libraries nothing uses any more
/// (checked with Roslyn over the C# code, plus views and config files), and the backup folders Visual Studio
/// leaves behind when it migrates or upgrades a project. Conservative: anything that looks used stays.
/// </summary>
public static partial class Cleanup
{
    public static IReadOnlyList<LegacyLibrary> Catalog { get; } =
    [
        new("ASP.NET MVC",
            ["System.Web.Mvc", "System.Web.Razor", "System.Web.WebPages", "System.Web.WebPages.Razor", "System.Web.WebPages.Deployment", "System.Web.Helpers"],
            ["Microsoft.AspNet.Mvc", "Microsoft.AspNet.Razor", "Microsoft.AspNet.WebPages"],
            ["System.Web.Mvc", "System.Web.Razor", "System.Web.WebPages", "System.Web.Helpers"],
            DependentToken: "Mvc",
            UsedByRazorViews: true),
        new("ASP.NET Web Optimization (bundling)",
            ["System.Web.Optimization", "WebGrease", "Antlr3.Runtime"],
            ["Microsoft.AspNet.Web.Optimization", "WebGrease", "Antlr"],
            ["System.Web.Optimization", "WebGrease", "Antlr"],
            DependentToken: "Optimization",
            ViewMarkers: ["Scripts.Render", "Styles.Render", "Scripts.Url", "Styles.Url", "BundleTable"]),
        new("OWIN / Katana",
            ["Owin", "Microsoft.Owin", "Microsoft.Owin.Host.SystemWeb", "Microsoft.Owin.Security", "Microsoft.Owin.Security.Cookies", "Microsoft.Owin.Security.OAuth"],
            ["Owin", "Microsoft.Owin", "Microsoft.Owin.Host.SystemWeb", "Microsoft.Owin.Security", "Microsoft.Owin.Security.Cookies", "Microsoft.Owin.Security.OAuth"],
            ["Owin", "Microsoft.Owin"],
            DependentToken: "Owin"),
        new("Unity (dependency injection)",
            ["Unity.Abstractions", "Unity.Container", "Unity.Configuration", "Unity.ServiceLocation", "Unity.Mvc", "Unity.Mvc5", "Unity.WebApi",
             "Microsoft.Practices.Unity", "Microsoft.Practices.Unity.Configuration", "Microsoft.Practices.Unity.Mvc"],
            ["Unity", "Unity.Abstractions", "Unity.Container", "Unity.Configuration", "Unity.ServiceLocation", "Unity.Mvc", "Unity.Mvc5", "Unity.WebAPI", "Unity.AspNet.WebApi"],
            ["Unity", "Microsoft.Practices.Unity"],
            DependentToken: "Unity"),
        new("System.EnterpriseServices (COM+)",
            ["System.EnterpriseServices"],
            [],
            ["System.EnterpriseServices"]),
    ];

    /// <summary>Folders Visual Studio leaves when it migrates packages.config or upgrades an old project format.</summary>
    public static bool IsBackupFolderName(string name) =>
        name.Equals("MigrationBackup", StringComparison.OrdinalIgnoreCase)
        || name.Equals("_UpgradeReport_Files", StringComparison.OrdinalIgnoreCase);

    public static CleanupPlan Plan(SolutionInfo solution, IReadOnlyList<LegacyLibrary>? catalog = null)
    {
        catalog ??= Catalog;
        var rootDir = File.Exists(solution.Path) ? Path.GetDirectoryName(solution.Path)! : solution.Path;
        var removed = new List<CleanupItem>();
        var kept = new List<CleanupItem>();
        var referenceChanges = new List<FileChange>();
        var notes = new List<string>();

        foreach (var project in solution.Projects)
        {
            var toRemove = new List<LegacyLibrary>();
            foreach (var (library, item) in Classify(project, catalog))
            {
                if (item.KeptBecause is null)
                {
                    toRemove.Add(library);
                    removed.Add(item);
                }
                else
                {
                    kept.Add(item);
                }
            }

            if (toRemove.Count > 0)
            {
                referenceChanges.AddRange(RemoveReferences(project, toRemove));
                if (project.IsSdkStyle && CentralPackageFile.Find(project.Directory) is { } central)
                {
                    notes.Add($"{project.Name}: the PackageVersion entries in {PathUtil.Relative(rootDir, central.Path)} stay; remove them once no project uses those packages.");
                }
            }
        }

        if (removed.Any(r => solution.Projects.First(p => p.Name == r.Project).IsWeb))
        {
            notes.Add("Binding redirects in Web.config for removed assemblies are left in place; they do no harm and go away with Web.config.");
        }

        var (backupFolders, backupChanges) = Backups(rootDir);
        return new CleanupPlan(removed, kept, referenceChanges, backupFolders, backupChanges, notes);
    }

    /// <summary>
    /// Every catalog library <paramref name="project"/> references, each with whether it is unused
    /// (<see cref="CleanupItem.KeptBecause"/> null) or what keeps it. Reads the project's files; changes nothing.
    /// </summary>
    public static IReadOnlyList<CleanupItem> UnusedOrKept(ProjectInfo project, IReadOnlyList<LegacyLibrary>? catalog = null) =>
        Classify(project, catalog ?? Catalog).Select(x => x.Item).ToList();

    private static List<(LegacyLibrary Library, CleanupItem Item)> Classify(ProjectInfo project, IReadOnlyList<LegacyLibrary> catalog)
    {
        var result = new List<(LegacyLibrary Library, CleanupItem Item)>();
        var referenced = catalog
            .Select(library => (Library: library, References: ReferencesOf(project, library)))
            .Where(x => x.References.Count > 0)
            .ToList();
        if (referenced.Count == 0)
        {
            return result;
        }

        var usage = NamespaceUsage.Collect(project);
        foreach (var (library, references) in referenced)
        {
            result.Add((library, new CleanupItem(project.Name, library.Name, references, WhyKept(project, library, usage))));
        }
        return result;
    }

    /// <summary>The references and packages of <paramref name="library"/> the project has, as "reference X" / "package X".</summary>
    internal static IReadOnlyList<string> ReferencesOf(ProjectInfo project, LegacyLibrary library)
    {
        var result = new List<string>();
        result.AddRange(project.AssemblyReferences
            .Where(a => IsAssembly(library, a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(a => $"reference {a}"));
        result.AddRange(project.Packages
            .Where(p => IsPackage(library, p.Id))
            .Select(p => p.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => $"package {id}"));
        return result;
    }

    private static string? WhyKept(ProjectInfo project, LegacyLibrary library, NamespaceUsage usage)
    {
        foreach (var ns in library.Namespaces)
        {
            if (usage.Find(ns) is { } where)
            {
                return $"{ns} is used in {where}";
            }
        }

        if (library.UsedByRazorViews
            && project.ViewFiles.FirstOrDefault(v => v.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) is { } razorView)
        {
            return $"Razor views use it ({PathUtil.Relative(project.Directory, razorView)})";
        }

        if (library.ViewMarkers is { } markers)
        {
            foreach (var view in project.ViewFiles)
            {
                var text = File.ReadAllText(view);
                var marker = markers.FirstOrDefault(m => text.Contains(m, StringComparison.Ordinal));
                if (marker is not null)
                {
                    return $"\"{marker}\" is used in {PathUtil.Relative(project.Directory, view)}";
                }
            }
        }

        if (library.DependentToken is { } token)
        {
            var dependent = project.Packages.Select(p => p.Id)
                .Concat(project.AssemblyReferences)
                .FirstOrDefault(n => n.Contains(token, StringComparison.OrdinalIgnoreCase)
                                     && !IsPackage(library, n) && !IsAssembly(library, n));
            if (dependent is not null)
            {
                return $"{dependent} probably depends on it";
            }
        }

        return null;
    }

    private static bool IsAssembly(LegacyLibrary library, string name) =>
        library.Assemblies.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The id itself, or one of its language packs ("Microsoft.AspNet.Mvc.tr", "Microsoft.AspNet.Mvc.zh-Hans").</summary>
    private static bool IsPackage(LegacyLibrary library, string id) =>
        library.Packages.Any(p => id.Equals(p, StringComparison.OrdinalIgnoreCase)
                                  || id.Length > p.Length + 1
                                  && id.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)
                                  && LanguagePack().IsMatch(id[(p.Length + 1)..]));

    [GeneratedRegex("^[a-z]{2}(-[A-Za-z]{2,4})?$")]
    private static partial Regex LanguagePack();

    private static IEnumerable<FileChange> RemoveReferences(ProjectInfo project, IReadOnlyList<LegacyLibrary> libraries)
    {
        var doc = ProjectXml.Load(project.FullPath);
        var root = doc.Root!;
        var changed = false;

        foreach (var element in ProjectXml.Elements(root, "Reference").ToList())
        {
            var name = element.Attribute("Include")?.Value.Split(',')[0].Trim();
            if (name is not null && libraries.Any(l => IsAssembly(l, name)))
            {
                ProjectXml.Remove(element);
                changed = true;
            }
        }

        foreach (var element in ProjectXml.Elements(root, "PackageReference").ToList())
        {
            var id = element.Attribute("Include")?.Value;
            if (id is not null && libraries.Any(l => IsPackage(l, id)))
            {
                ProjectXml.Remove(element);
                changed = true;
            }
        }

        if (changed)
        {
            ProjectXml.RemoveEmptyItemGroups(root);
            yield return new FileChange(project.FullPath, ProjectXml.Serialize(doc));
        }

        var packagesConfig = Path.Combine(project.Directory, "packages.config");
        if (project.HasPackagesConfig && File.Exists(packagesConfig))
        {
            var config = ProjectXml.Load(packagesConfig);
            var entries = config.Root!.Elements()
                .Where(e => e.Name.LocalName == "package"
                            && e.Attribute("id")?.Value is { } id
                            && libraries.Any(l => IsPackage(l, id)))
                .ToList();
            if (entries.Count > 0)
            {
                foreach (var entry in entries)
                {
                    ProjectXml.Remove(entry);
                }
                yield return new FileChange(packagesConfig, ProjectXml.Serialize(config));
            }
        }
    }

    /// <summary>Every backup folder under the solution folder (outermost only), and a delete for each file in it.</summary>
    private static (List<string> Folders, List<FileChange> Changes) Backups(string rootDir)
    {
        var folders = new List<string>();
        var changes = new List<FileChange>();
        if (!Directory.Exists(rootDir))
        {
            return (folders, changes);
        }

        var pending = new Stack<string>([rootDir]);
        while (pending.Count > 0)
        {
            foreach (var dir in Directory.EnumerateDirectories(pending.Pop()).OrderByDescending(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                if (IsBackupFolderName(name))
                {
                    folders.Add(dir);
                    changes.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .OrderBy(f => f, StringComparer.Ordinal)
                        .Select(f => new FileChange(f, null)));
                }
                else if (!name.StartsWith('.') && !PathUtil.IsBuildOutput(dir + Path.DirectorySeparatorChar))
                {
                    pending.Push(dir);
                }
            }
        }

        folders.Sort(StringComparer.Ordinal);
        return (folders, changes);
    }

    /// <summary>Deletes the backup folders that are empty once their files are gone (bottom-up).</summary>
    public static void RemoveEmptyFolders(IEnumerable<string> folders)
    {
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(folder))
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
        }
    }

    /// <summary>
    /// Dotted names a project uses: using directives, qualified names and member accesses in C# (via Roslyn),
    /// &lt;Using Include&gt; items in the project file, and namespace or type names in views and config files.
    /// </summary>
    internal sealed partial class NamespaceUsage
    {
        private readonly List<(string Name, string Where)> _names = [];

        public static NamespaceUsage Collect(ProjectInfo project)
        {
            var usage = new NamespaceUsage();
            var options = new CSharpParseOptions(LanguageVersion.Latest);

            foreach (var file in project.SourceFiles)
            {
                var where = PathUtil.Relative(project.Directory, file);
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), options);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    var name = node switch
                    {
                        UsingDirectiveSyntax u => u.NamespaceOrType.ToString(),
                        QualifiedNameSyntax q when q.Parent is not QualifiedNameSyntax => q.ToString(),
                        MemberAccessExpressionSyntax m when m.Parent is not MemberAccessExpressionSyntax => m.ToString(),
                        _ => null,
                    };
                    if (name is not null)
                    {
                        usage.Add(Normalize(name), where);
                    }
                }
            }

            try
            {
                var doc = XDocument.Load(project.FullPath);
                foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "Using"))
                {
                    if (item.Attribute("Include")?.Value is { } include)
                    {
                        usage.Add(include.Trim(), Path.GetFileName(project.FullPath));
                    }
                }
            }
            catch (XmlException)
            {
                // The loader has already read this file; nothing more to learn from a broken one.
            }

            foreach (var file in project.ViewFiles.Concat(ConfigFiles(project)))
            {
                var where = PathUtil.Relative(project.Directory, file);
                foreach (Match m in DottedName().Matches(TextWithoutBindingRedirects(file)))
                {
                    usage.Add(m.Value, where);
                }
            }

            return usage;
        }

        /// <summary>Where <paramref name="ns"/> (or something inside it) is used first, or null.</summary>
        public string? Find(string ns)
        {
            foreach (var (name, where) in _names)
            {
                if (name.Equals(ns, StringComparison.Ordinal) || name.StartsWith(ns + ".", StringComparison.Ordinal))
                {
                    return where;
                }
            }
            return null;
        }

        private void Add(string name, string where) => _names.Add((name, where));

        private static string Normalize(string name)
        {
            var compact = string.Concat(name.Where(c => !char.IsWhiteSpace(c)));
            return compact.StartsWith("global::", StringComparison.Ordinal) ? compact["global::".Length..] : compact;
        }

        private static IEnumerable<string> ConfigFiles(ProjectInfo project) =>
            Directory.EnumerateFiles(project.Directory, "*.config", SearchOption.AllDirectories)
                .Where(f => !PathUtil.IsBuildOutput(f) && !PathUtil.IsBackup(f)
                            && !Path.GetFileName(f).Equals("packages.config", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal);

        /// <summary>Binding redirects name every assembly the app was ever built with; they say nothing about use.</summary>
        private static string TextWithoutBindingRedirects(string file)
        {
            var text = File.ReadAllText(file);
            if (!file.EndsWith(".config", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            try
            {
                var doc = XDocument.Parse(text);
                foreach (var binding in doc.Descendants().Where(e => e.Name.LocalName == "assemblyBinding").ToList())
                {
                    binding.Remove();
                }
                return doc.ToString();
            }
            catch (XmlException)
            {
                return text;
            }
        }

        // A dotted identifier not preceded by another identifier character or dot, e.g. "System.Web.Mvc.WebViewPage".
        [GeneratedRegex(@"(?<![\w.])[A-Za-z_]\w*(\.[A-Za-z_]\w*)*")]
        private static partial Regex DottedName();
    }
}
