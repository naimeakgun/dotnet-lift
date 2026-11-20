using System.Xml.Linq;
using DotnetLift.Core.Model;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Core.Build;

/// <summary>An automatic fix for one or more build errors.</summary>
public sealed record BuildFix(string Title, string Explanation, IReadOnlyList<FileChange> Changes, IReadOnlyList<Diagnostic> Resolves);

/// <summary>What an error means and what to do about it, for errors that need a code change.</summary>
public sealed record Guidance(string Summary, string Suggestion);

/// <summary>
/// Known errors after moving a .NET Framework library to modern .NET.
/// Rule-based on purpose: no network, no API key, same result every time.
/// </summary>
public static class BuildFixCatalog
{
    private sealed record PackageRule(string[] Symbols, string PackageId, string Version, string Why)
    {
        public bool Matches(string symbol) =>
            Symbols.Any(s => symbol.Equals(s, StringComparison.Ordinal) || symbol.StartsWith(s + ".", StringComparison.Ordinal));
    }

    private static readonly PackageRule[] PackageRules =
    [
        new(["ConfigurationManager", "ConfigurationSection", "ConfigurationElement", "ConfigurationElementCollection",
             "ConfigurationErrorsException", "System.Configuration"],
            "System.Configuration.ConfigurationManager", "10.0.0",
            "ConfigurationManager is not part of modern .NET. This package provides it (it still reads App.config/Web.config), so the code keeps working; moving to IConfiguration can come later."),
        new(["MemoryCache", "ObjectCache", "CacheItemPolicy", "CacheItemPriority", "System.Runtime.Caching"],
            "System.Runtime.Caching", "10.0.0",
            "System.Runtime.Caching is a separate package on modern .NET."),
        new(["Bitmap", "ImageFormat", "System.Drawing.Imaging"],
            "System.Drawing.Common", "10.0.0",
            "System.Drawing is a Windows-only package on modern .NET; consider ImageSharp or SkiaSharp for cross-platform code."),
    ];

    private static readonly string[] MissingSymbolCodes = ["CS0103", "CS0246", "CS0234"];

    /// <summary>Fixes that can be applied for these errors. All package additions for one project are a single fix.</summary>
    public static IReadOnlyList<BuildFix> Fixes(IReadOnlyList<Diagnostic> errors, ProjectInfo project, string targetFramework)
    {
        var needed = new List<(PackageRule Rule, List<Diagnostic> Errors)>();
        foreach (var rule in PackageRules)
        {
            if (project.HasPackage(rule.PackageId))
            {
                continue; // already there: adding it again would loop forever
            }

            var hits = errors.Where(e => MissingSymbolCodes.Contains(e.Code) && e.Symbols().Any(rule.Matches)).ToList();
            if (hits.Count > 0)
            {
                needed.Add((rule, hits));
            }
        }

        if (needed.Count == 0)
        {
            return [];
        }

        var multiTarget = project.TargetFramework?.Contains(';') == true;
        var central = CentralPackageFile.Find(project.Directory);
        var content = AddPackages(project.FullPath, needed.Select(n => n.Rule).ToList(), multiTarget ? targetFramework : null, central);
        var changes = new List<FileChange> { new(project.FullPath, content) };
        PackageReferences.AddIfChanged(changes, central);
        var names = string.Join(", ", needed.Select(n => $"{n.Rule.PackageId} {n.Rule.Version}"));

        return
        [
            new BuildFix(
                $"Add package{(needed.Count > 1 ? "s" : "")} {names}{(multiTarget ? $" for {targetFramework}" : "")}",
                string.Join(" ", needed.Select(n => n.Rule.Why)),
                changes,
                needed.SelectMany(n => n.Errors).ToList()),
        ];
    }

    private static string AddPackages(string projectPath, IReadOnlyList<PackageRule> rules, string? conditionTfm, CentralPackageFile? central)
    {
        var doc = ProjectXml.Load(projectPath);
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        var items = rules.Select(r => PackageReferences.Create(ns, r.PackageId, r.Version, central)).ToList();

        var condition = conditionTfm is null ? null : ProjectXml.TargetFrameworkCondition(conditionTfm);
        var group = ProjectXml.Elements(root, "ItemGroup").FirstOrDefault(g =>
            condition is null
                ? g.Attribute("Condition") is null && g.Elements().Any(e => e.Name.LocalName == "PackageReference")
                : g.Attribute("Condition")?.Value == condition);

        if (group is null)
        {
            group = condition is null ? new XElement(ns + "ItemGroup") : new XElement(ns + "ItemGroup", new XAttribute("Condition", condition));
            ProjectXml.AddItemGroup(root, group, items);
        }
        else
        {
            foreach (var item in items)
            {
                ProjectXml.AddToItemGroup(root, group, item);
            }
        }

        return ProjectXml.Serialize(doc);
    }

    /// <summary>Explains an error that cannot be fixed automatically, or returns null when it is not a known one.</summary>
    public static Guidance? Explain(Diagnostic d)
    {
        var symbols = d.Symbols();
        bool About(params string[] names) => symbols.Any(s => names.Any(n => s == n || s.StartsWith(n + ".")));

        return d.Code switch
        {
            "SYSLIB0011" => new Guidance(
                "BinaryFormatter is removed from modern .NET.",
                "Serialize with System.Text.Json (or MessagePack/protobuf). If serialized data is stored somewhere (cookies, files, a database), plan how existing data is read once and rewritten."),
            "SYSLIB0014" => new Guidance(
                "WebRequest / WebClient / ServicePoint are obsolete.",
                "Use HttpClient, created through IHttpClientFactory."),
            "CS0006" => new Guidance(
                "A referenced project did not build.",
                "Fix the referenced project first; this error goes away with it."),
            "NU1202" or "NU1701" => new Guidance(
                "A NuGet package does not support the new target framework.",
                "Update the package to a version that supports modern .NET, or replace it."),
            _ when About("System.Web.Mvc", "System.Web.Http", "ApiController", "ActionResult", "IHttpActionResult")
                => new Guidance(
                    "ASP.NET MVC 5 / Web API 2 types are used.",
                    "These belong in the web project, which moves to ASP.NET Core in a later step."),
            _ when About("System.Web", "HttpContext", "HttpRuntime", "HttpContextBase", "HttpCookie", "HttpServerUtility")
                => new Guidance(
                    "The library uses System.Web, which does not exist on modern .NET.",
                    "Do not reach into the web request from a library. Pass in what is needed (user name, settings), or depend on a small interface (ICurrentUser) and an IMemoryCache/ObjectCache instead of HttpRuntime.Cache; the web project provides the implementations."),
            _ when About("AppDomain") || d.Message.Contains("AppDomain")
                => new Guidance(
                    "AppDomain isolation is not available on modern .NET.",
                    "Use AssemblyLoadContext, or separate processes."),
            _ => null,
        };
    }
}
