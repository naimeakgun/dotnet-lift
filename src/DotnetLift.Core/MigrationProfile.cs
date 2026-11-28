using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotnetLift.Core;

/// <summary>How a library is retargeted.</summary>
public enum MultiTargetMode
{
    /// <summary>Multi-target (old framework + new) only while a .NET Framework web project in the solution still uses the library.</summary>
    Auto,

    /// <summary>Always build for the old framework and the new one.</summary>
    Always,

    /// <summary>Only the new framework; whatever stays on .NET Framework uses the library's last .NET Framework build (e.g. its last package).</summary>
    Never,
}

/// <summary>How `upgrade` records its changes in git.</summary>
public enum CommitMode
{
    /// <summary>One commit per applied step (default): easy to review and to revert step by step.</summary>
    Step,

    /// <summary>One commit for the whole conversion.</summary>
    Single,
}

/// <summary>What happens to BinaryFormatter code.</summary>
public enum BinaryFormatterStrategy
{
    /// <summary>Rewrite to System.Text.Json (default; needs explicit approval because the serialized format changes). Fits caches and data that can be dropped.</summary>
    Json,

    /// <summary>
    /// Keep BinaryFormatter: reference the System.Runtime.Serialization.Formatters compatibility package on modern .NET
    /// and enable it, so data written by the old code stays readable until a planned data migration.
    /// </summary>
    Compatibility,
}

/// <summary>What happens to code that uses the legacy System.Data.SqlClient driver.</summary>
public enum SqlClientStrategy
{
    /// <summary>Move to Microsoft.Data.SqlClient after explicit approval (default): it encrypts connections by default, which can break a connection at runtime.</summary>
    Ask,

    /// <summary>Move to Microsoft.Data.SqlClient without asking: the team has decided, and checked Encrypt / TrustServerCertificate in its connection strings.</summary>
    Microsoft,

    /// <summary>Keep System.Data.SqlClient (on modern .NET through its deprecated package).</summary>
    Keep,
}

/// <summary>
/// dotnet-lift.json: the team's decisions for a repository, so every developer (and every run) converts the same way.
/// Without the file the tool keeps its defaults.
/// <code>
/// {
///   "target": "net10.0",
///   "multiTarget": "never",              // auto | always | never
///   "commits": "single",                 // step | single
///   "codePage": 1254,                    // what Encoding.Default meant on your servers
///   "binaryFormatter": "compatibility",  // json | compatibility
///   "sqlClient": "microsoft",            // ask | microsoft | keep
///   "projects": {
///     "MyCompany.Shared": { "multiTarget": "always" },
///     "MyCompany.Caching": { "binaryFormatter": "json" },
///     "*.Tests": { "multiTarget": "never" }
///   }
/// }
/// </code>
/// </summary>
public sealed class MigrationProfile
{
    public const string FileName = "dotnet-lift.json";

    private static readonly string[] SupportedTargets = ["net10.0", "net11.0"];
    private static readonly string[] TopLevelKeys = ["target", "multiTarget", "commits", "codePage", "binaryFormatter", "sqlClient", "projects"];
    private static readonly string[] ProjectKeys = ["multiTarget", "binaryFormatter"];

    /// <summary>One entry under "projects": a name pattern and the settings it overrides (null = not set there).</summary>
    private sealed record ProjectRule(Regex Pattern, MultiTargetMode? MultiTarget, BinaryFormatterStrategy? BinaryFormatter);

    private readonly List<ProjectRule> _projects;

    private MigrationProfile(string? path, string? target, MultiTargetMode multiTarget, CommitMode commits, int? codePage,
        BinaryFormatterStrategy binaryFormatter, SqlClientStrategy sqlClient, List<ProjectRule> projects)
    {
        SqlClient = sqlClient;
        Path = path;
        Target = target;
        MultiTarget = multiTarget;
        Commits = commits;
        CodePage = codePage;
        BinaryFormatter = binaryFormatter;
        _projects = projects;
    }

    public static MigrationProfile Default { get; } =
        new(null, null, MultiTargetMode.Auto, CommitMode.Step, null, BinaryFormatterStrategy.Json, SqlClientStrategy.Ask, []);

    /// <summary>The file this profile came from; null for the defaults.</summary>
    public string? Path { get; }

    public string? Target { get; }

    public MultiTargetMode MultiTarget { get; }

    public CommitMode Commits { get; }

    /// <summary>The code page `Encoding.Default` stood for on the old servers (e.g. 1254), if the team decided on one.</summary>
    public int? CodePage { get; }

    /// <summary>What to do with BinaryFormatter code by default (json unless the team chose otherwise).</summary>
    public BinaryFormatterStrategy BinaryFormatter { get; }

    /// <summary>Whether System.Data.SqlClient code moves to Microsoft.Data.SqlClient, and whether that asks first.</summary>
    public SqlClientStrategy SqlClient { get; }

    public bool IsDefault => Path is null;

    /// <summary>The setting for one project: the first matching entry under "projects" that sets it, else the top-level value.</summary>
    public MultiTargetMode MultiTargetFor(string projectName) =>
        _projects.FirstOrDefault(p => p.MultiTarget is not null && p.Pattern.IsMatch(projectName))?.MultiTarget ?? MultiTarget;

    /// <summary>The BinaryFormatter strategy for one project: the first matching entry under "projects" that sets it, else the top-level value.</summary>
    public BinaryFormatterStrategy BinaryFormatterFor(string projectName) =>
        _projects.FirstOrDefault(p => p.BinaryFormatter is not null && p.Pattern.IsMatch(projectName))?.BinaryFormatter ?? BinaryFormatter;

    /// <summary>The nearest dotnet-lift.json at or above the input (a .sln, .csproj or folder); the defaults when there is none.</summary>
    public static MigrationProfile Find(string inputPath)
    {
        var full = System.IO.Path.GetFullPath(inputPath);
        var start = Directory.Exists(full) ? full : System.IO.Path.GetDirectoryName(full);
        for (var dir = start is null ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
            {
                return Load(candidate);
            }
        }
        return Default;
    }

    public static MigrationProfile Load(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name} is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{name} must be a JSON object.");
            }
            CheckKeys(root, TopLevelKeys, name);

            string? target = null;
            if (root.TryGetProperty("target", out var t))
            {
                target = ReadString(t, "target", name);
                if (!SupportedTargets.Contains(target))
                {
                    throw new InvalidDataException($"{name}: \"target\" must be one of {string.Join(", ", SupportedTargets)}.");
                }
            }

            var multiTarget = root.TryGetProperty("multiTarget", out var m)
                ? ReadEnum<MultiTargetMode>(m, "multiTarget", name)
                : MultiTargetMode.Auto;
            var commits = root.TryGetProperty("commits", out var c)
                ? ReadEnum<CommitMode>(c, "commits", name)
                : CommitMode.Step;

            int? codePage = null;
            if (root.TryGetProperty("codePage", out var cp))
            {
                if (cp.ValueKind != JsonValueKind.Number || !cp.TryGetInt32(out var page) || page <= 0)
                {
                    throw new InvalidDataException($"{name}: \"codePage\" must be a positive number, e.g. 1254.");
                }
                codePage = page;
            }

            var binaryFormatter = root.TryGetProperty("binaryFormatter", out var bf)
                ? ReadEnum<BinaryFormatterStrategy>(bf, "binaryFormatter", name)
                : BinaryFormatterStrategy.Json;

            var sqlClient = root.TryGetProperty("sqlClient", out var sc)
                ? ReadEnum<SqlClientStrategy>(sc, "sqlClient", name)
                : SqlClientStrategy.Ask;

            var projects = new List<ProjectRule>();
            if (root.TryGetProperty("projects", out var p))
            {
                if (p.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"{name}: \"projects\" must be an object keyed by project name (\"*\" wildcards allowed).");
                }
                foreach (var project in p.EnumerateObject())
                {
                    if (project.Value.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException($"{name}: projects.\"{project.Name}\" must be an object.");
                    }
                    CheckKeys(project.Value, ProjectKeys, $"{name}: projects.\"{project.Name}\"");
                    MultiTargetMode? projectMultiTarget = project.Value.TryGetProperty("multiTarget", out var pm)
                        ? ReadEnum<MultiTargetMode>(pm, $"projects.\"{project.Name}\".multiTarget", name)
                        : null;
                    BinaryFormatterStrategy? projectBinaryFormatter = project.Value.TryGetProperty("binaryFormatter", out var pb)
                        ? ReadEnum<BinaryFormatterStrategy>(pb, $"projects.\"{project.Name}\".binaryFormatter", name)
                        : null;
                    if (projectMultiTarget is not null || projectBinaryFormatter is not null)
                    {
                        projects.Add(new ProjectRule(Wildcard(project.Name), projectMultiTarget, projectBinaryFormatter));
                    }
                }
            }

            return new MigrationProfile(System.IO.Path.GetFullPath(path), target, multiTarget, commits, codePage, binaryFormatter, sqlClient, projects);
        }
    }

    /// <summary>A starting dotnet-lift.json with every setting at its default and a comment explaining it.</summary>
    public static string Template() =>
        """
        {
          // dotnet-lift migration profile: the team's decisions for this repository.

          // Target framework for `analyze` and `upgrade` (net10.0 or net11.0).
          "target": "net10.0",

          // auto:   multi-target (old framework + new) while a .NET Framework web project in this solution still uses the library
          // always: always build for both
          // never:  only the new framework; code that stays on .NET Framework uses the library's last .NET Framework package
          "multiTarget": "auto",

          // step:   one git commit per applied step (easy to review and revert)
          // single: one commit for the whole conversion
          "commits": "step",

          // What Encoding.Default meant on your servers (e.g. 1254 for Turkish Windows); remove if unsure.
          // "codePage": 1254,

          // json:          rewrite BinaryFormatter to System.Text.Json (the stored format changes; asks for approval)
          // compatibility: keep BinaryFormatter through the System.Runtime.Serialization.Formatters package,
          //                so data the old code wrote stays readable until a planned data migration
          "binaryFormatter": "json",

          // ask:       move System.Data.SqlClient code to Microsoft.Data.SqlClient after explicit approval
          //            (it encrypts by default: check Encrypt / TrustServerCertificate in your connection strings)
          // microsoft: move without asking (the team has checked its connection strings)
          // keep:      stay on System.Data.SqlClient
          "sqlClient": "ask",

          // Per-project exceptions, by name; "*" is a wildcard.
          "projects": {
            // "MyCompany.Shared": { "multiTarget": "always" },
            // "MyCompany.Caching": { "binaryFormatter": "json" }
          }
        }

        """;

    public string Describe() =>
        $"multiTarget: {MultiTarget.ToString().ToLowerInvariant()}, commits: {Commits.ToString().ToLowerInvariant()}" +
        (CodePage is { } page ? $", codePage: {page}" : "") +
        (BinaryFormatter != BinaryFormatterStrategy.Json ? $", binaryFormatter: {BinaryFormatter.ToString().ToLowerInvariant()}" : "") +
        (SqlClient != SqlClientStrategy.Ask ? $", sqlClient: {SqlClient.ToString().ToLowerInvariant()}" : "") +
        (_projects.Count > 0 ? $", {_projects.Count} project exception(s)" : "");

    private static void CheckKeys(JsonElement element, string[] allowed, string where)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"{where}: unknown setting \"{property.Name}\". Known settings: {string.Join(", ", allowed)}.");
            }
        }
    }

    private static string ReadString(JsonElement e, string key, string file) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException($"{file}: \"{key}\" must be a string.");

    private static T ReadEnum<T>(JsonElement e, string key, string file) where T : struct, System.Enum
    {
        var text = ReadString(e, key, file);
        return System.Enum.TryParse<T>(text, ignoreCase: true, out var value) && System.Enum.IsDefined(value)
            ? value
            : throw new InvalidDataException(
                $"{file}: \"{key}\" must be one of {string.Join(", ", System.Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");
    }

    private static Regex Wildcard(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
}
