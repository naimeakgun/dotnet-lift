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
    private static readonly string[] TopLevelKeys = ["target", "multiTarget", "commits", "codePage", "binaryFormatter", "sqlClient", "requestContext", "cache", "projects"];
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

    /// <summary>The team's replacements for HttpContext.Current members, or null to use the generated CurrentUser provider.</summary>
    public RequestContextMap? RequestContext { get; private set; }

    /// <summary>The team's cache API for HttpRuntime.Cache calls, or null for System.Runtime.Caching.MemoryCache.Default.</summary>
    public CacheMap? Cache { get; private set; }

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

            return new MigrationProfile(System.IO.Path.GetFullPath(path), target, multiTarget, commits, codePage, binaryFormatter, sqlClient, projects)
            {
                RequestContext = root.TryGetProperty("requestContext", out var rc) ? RequestContextMap.Read(rc, name) : null,
                Cache = root.TryGetProperty("cache", out var ca) ? CacheMap.Read(ca, name) : null,
            };
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

          // Your own request abstractions instead of HttpContext.Current (remove to get a generated CurrentUser provider).
          // Each value is the C# expression that replaces HttpContext.Current.<Member>; members you leave out keep a file unchanged.
          // "requestContext": {
          //   "user": "UserContext.Current.Principal",
          //   "items": "RequestContext.Current.Items",
          //   "session": "SessionAccessor.Current",
          //   "context": "RequestContext.Current",          // replaces HttpContext.Current in null checks
          //   "usings": ["MyCompany.Web.Abstractions"]
          // },

          // Your cache API instead of MemoryCache.Default for HttpRuntime.Cache (remove to keep MemoryCache.Default).
          // Templates use {key}, {value}, {absolute} (DateTime) and {sliding} (TimeSpan); calls without a template stay as they are.
          // "cache": {
          //   "get": "CacheManager.Get<object>({key})",
          //   "set": "CacheManager.Set({key}, {value})",
          //   "setAbsolute": "CacheManager.Set({key}, {value}, {absolute})",
          //   "setSliding": "CacheManager.SetSliding({key}, {value}, {sliding})",
          //   "remove": "CacheManager.Remove({key})",
          //   "usings": ["MyCompany.Caching"]
          // },

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
        (RequestContext is not null ? $", requestContext: {string.Join("/", RequestContext.Members.Keys.Select(k => k.ToLowerInvariant()))}" : "") +
        (Cache is not null ? ", cache: custom" : "") +
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

/// <summary>
/// "requestContext" in dotnet-lift.json: what replaces each HttpContext.Current member in libraries, as C# expressions of
/// the team's own abstractions (e.g. "user": "UserContext.Current.Principal"), and the namespaces they need.
/// </summary>
public sealed class RequestContextMap
{
    /// <summary>Profile key → HttpContext member name.</summary>
    public static readonly IReadOnlyDictionary<string, string> MemberKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["user"] = "User",
        ["items"] = "Items",
        ["session"] = "Session",
        ["request"] = "Request",
        ["response"] = "Response",
        ["server"] = "Server",
        ["cache"] = "Cache",
    };

    public RequestContextMap(IReadOnlyDictionary<string, string> members, string? context, IReadOnlyList<string> usings)
    {
        Members = members;
        Context = context;
        Usings = usings;
    }

    /// <summary>HttpContext member name (User, Items, …) → replacement expression.</summary>
    public IReadOnlyDictionary<string, string> Members { get; }

    /// <summary>Replaces HttpContext.Current itself in null checks; null = files with such checks are left alone.</summary>
    public string? Context { get; }

    public IReadOnlyList<string> Usings { get; }

    internal static RequestContextMap Read(JsonElement e, string file)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{file}: \"requestContext\" must be an object, e.g. {{ \"user\": \"UserContext.Current.Principal\" }}.");
        }

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        string? context = null;
        var usings = new List<string>();
        foreach (var property in e.EnumerateObject())
        {
            var where = $"{file}: requestContext.{property.Name}";
            if (property.Name == "usings")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.EnumerateArray().Any(u => u.ValueKind != JsonValueKind.String))
                {
                    throw new InvalidDataException($"{where} must be an array of namespaces.");
                }
                usings.AddRange(property.Value.EnumerateArray().Select(u => u.GetString()!.Trim()).Where(u => u.Length > 0));
                continue;
            }

            if (property.Name != "context" && !MemberKeys.ContainsKey(property.Name))
            {
                throw new InvalidDataException(
                    $"{file}: unknown setting requestContext.{property.Name}. Known: {string.Join(", ", MemberKeys.Keys)}, context, usings.");
            }
            var expression = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()!.Trim() : "";
            var parsed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(expression);
            if (expression.Length == 0 || parsed.ContainsDiagnostics || parsed.ToString() != expression)
            {
                throw new InvalidDataException($"{where} must be a C# expression, e.g. \"UserContext.Current.Principal\".");
            }

            if (property.Name == "context")
            {
                context = expression;
            }
            else
            {
                members[MemberKeys[property.Name]] = expression;
            }
        }

        if (members.Count == 0)
        {
            throw new InvalidDataException($"{file}: \"requestContext\" needs at least one member (user, items, session, request, response, server, cache).");
        }
        return new RequestContextMap(members, context, usings);
    }
}

/// <summary>
/// "cache" in dotnet-lift.json: C# templates for the team's cache API that replace HttpRuntime.Cache calls,
/// e.g. "get": "CacheManager.Get&lt;object&gt;({key})". A call shape without a template is left as it is.
/// </summary>
public sealed class CacheMap
{
    /// <summary>Template key → the placeholders it may use.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Shapes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["get"] = ["key"],
        ["set"] = ["key", "value"],
        ["setAbsolute"] = ["key", "value", "absolute"],
        ["setSliding"] = ["key", "value", "sliding"],
        ["remove"] = ["key"],
    };

    public CacheMap(IReadOnlyDictionary<string, string> templates, IReadOnlyList<string> usings)
    {
        Templates = templates;
        Usings = usings;
    }

    /// <summary>Shape (get, set, setAbsolute, setSliding, remove) → template.</summary>
    public IReadOnlyDictionary<string, string> Templates { get; }

    public IReadOnlyList<string> Usings { get; }

    /// <summary>The template with its placeholders filled in, or null when the shape has no template.</summary>
    public string? Fill(string shape, IReadOnlyDictionary<string, string> values)
    {
        if (!Templates.TryGetValue(shape, out var template))
        {
            return null;
        }
        foreach (var (name, value) in values)
        {
            template = template.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }
        return template;
    }

    internal static CacheMap Read(JsonElement e, string file)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{file}: \"cache\" must be an object, e.g. {{ \"get\": \"CacheManager.Get<object>({{key}})\" }}.");
        }

        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        var usings = new List<string>();
        foreach (var property in e.EnumerateObject())
        {
            var where = $"{file}: cache.{property.Name}";
            if (property.Name == "usings")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.EnumerateArray().Any(u => u.ValueKind != JsonValueKind.String))
                {
                    throw new InvalidDataException($"{where} must be an array of namespaces.");
                }
                usings.AddRange(property.Value.EnumerateArray().Select(u => u.GetString()!.Trim()).Where(u => u.Length > 0));
                continue;
            }

            if (!Shapes.TryGetValue(property.Name, out var allowed))
            {
                throw new InvalidDataException($"{file}: unknown setting cache.{property.Name}. Known: {string.Join(", ", Shapes.Keys)}, usings.");
            }
            var template = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()!.Trim() : "";

            var used = System.Text.RegularExpressions.Regex.Matches(template, @"\{(\w+)\}").Select(m => m.Groups[1].Value).ToList();
            var unknown = used.Where(u => !allowed.Contains(u)).Distinct().ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidDataException($"{where} uses {{{string.Join("}, {", unknown)}}}; it can use {{{string.Join("}, {", allowed)}}}.");
            }
            if (!used.Contains("key"))
            {
                throw new InvalidDataException($"{where} must use {{key}}.");
            }

            var probe = template;
            foreach (var name in allowed)
            {
                probe = probe.Replace("{" + name + "}", "x" + name, StringComparison.Ordinal);
            }
            var parsed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(probe);
            if (parsed.ContainsDiagnostics || parsed.ToString() != probe)
            {
                throw new InvalidDataException($"{where} must be a C# expression with placeholders, e.g. \"CacheManager.Get<object>({{key}})\".");
            }
            templates[property.Name] = template;
        }

        if (templates.Count == 0)
        {
            throw new InvalidDataException($"{file}: \"cache\" needs at least one template ({string.Join(", ", Shapes.Keys)}).");
        }
        return new CacheMap(templates, usings);
    }
}
