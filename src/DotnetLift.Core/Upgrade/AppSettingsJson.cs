using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using DotnetLift.Core.Analyzers;

namespace DotnetLift.Core.Upgrade;

/// <summary>A connection string from Web.config.</summary>
public sealed record ConnectionStringSetting(string Name, string Value, string? ProviderName);

/// <summary>The effective &lt;appSettings&gt; and &lt;connectionStrings&gt; of a Web.config, after add/remove/clear.</summary>
public sealed record WebConfigSettings(
    IReadOnlyList<KeyValuePair<string, string>> AppSettings,
    IReadOnlyList<ConnectionStringSetting> ConnectionStrings);

/// <summary>
/// Turns Web.config settings into appsettings.json, the way ASP.NET Core's configuration reads them:
/// connection strings go to the ConnectionStrings section (the provider name as "&lt;name&gt;_ProviderName", the same
/// convention the environment-variable provider uses), app settings become top-level keys, and a ':' in a key
/// becomes a nested section, so <c>configuration["Key"]</c> returns the same value <c>ConfigurationManager.AppSettings["Key"]</c> did.
/// </summary>
public static class AppSettingsJson
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Keep URLs and connection strings readable ('&', '+', non-ASCII); this is a config file, not HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Reads the effective settings, or null when the file is not valid XML.</summary>
    public static WebConfigSettings? ReadWebConfig(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (XmlException)
        {
            return null;
        }

        if (doc.Root is not { } root)
        {
            return null;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        var app = new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries(root, "appSettings", dir))
        {
            switch (e.Name.LocalName)
            {
                case "add" when Attr(e, "key") is { } key && !string.IsNullOrWhiteSpace(key):
                    app[key] = Attr(e, "value") ?? "";
                    break;
                case "remove" when Attr(e, "key") is { } key:
                    app.Remove(key);
                    break;
                case "clear":
                    app.Clear();
                    break;
            }
        }

        var connections = new OrderedDictionary<string, ConnectionStringSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries(root, "connectionStrings", dir))
        {
            switch (e.Name.LocalName)
            {
                case "add" when Attr(e, "name") is { } name && !string.IsNullOrWhiteSpace(name):
                    connections[name] = new ConnectionStringSetting(name, Attr(e, "connectionString") ?? "",
                        Attr(e, "providerName") is { Length: > 0 } provider ? provider : null);
                    break;
                case "remove" when Attr(e, "name") is { } name:
                    connections.Remove(name);
                    break;
                case "clear":
                    connections.Clear();
                    break;
            }
        }

        return new WebConfigSettings(
            app.Where(kv => !WebConfigAnalyzer.IsFrameworkAppSetting(kv.Key)).ToList(),
            connections.Values.ToList());
    }

    /// <summary>
    /// The new content of appsettings.json, or null when there is nothing to add.
    /// An existing file wins: its values are never changed, only missing keys are added.
    /// </summary>
    public static string? Render(string? existingJson, WebConfigSettings settings)
    {
        var generated = new JsonObject(NodeOptions);
        if (settings.ConnectionStrings.Count > 0)
        {
            var section = new JsonObject(NodeOptions);
            foreach (var c in settings.ConnectionStrings)
            {
                section[c.Name] = c.Value;
                if (c.ProviderName is not null)
                {
                    section[c.Name + "_ProviderName"] = c.ProviderName;
                }
            }
            generated["ConnectionStrings"] = section;
        }

        var notMoved = new List<string>();
        foreach (var (key, value) in settings.AppSettings)
        {
            if (!TrySet(generated, key.Split(':'), value))
            {
                notMoved.Add(key);
            }
        }

        string json;
        if (existingJson is null)
        {
            // A new file looks like the one `dotnet new web` creates.
            AddMissing(generated, TemplateDefaults());
            json = generated.ToJsonString(WriteOptions);
        }
        else
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(existingJson, NodeOptions, DocumentOptions);
            }
            catch (JsonException)
            {
                return null; // not ours to repair; leave the file alone
            }

            if (parsed is not JsonObject existing || !AddMissing(existing, generated))
            {
                return null;
            }
            json = existing.ToJsonString(WriteOptions);
        }

        // The JSON configuration provider skips comments, so the note does not break the app.
        var note = notMoved.Count == 0
            ? ""
            : "// dotnet-lift: not moved, because a key cannot be both a value and a section in JSON: " +
              string.Join(", ", notMoved) + ". Move these by hand." + Environment.NewLine;
        return note + json + Environment.NewLine;
    }

    private static JsonObject TemplateDefaults() => new(NodeOptions)
    {
        ["Logging"] = new JsonObject(NodeOptions)
        {
            ["LogLevel"] = new JsonObject(NodeOptions)
            {
                ["Default"] = "Information",
                ["Microsoft.AspNetCore"] = "Warning",
            },
        },
        ["AllowedHosts"] = "*",
    };

    /// <summary>Sets a value at a ':'-separated path; false when the path collides with an existing value or section.</summary>
    private static bool TrySet(JsonObject root, string[] segments, string value)
    {
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!current.TryGetPropertyValue(segments[i], out var child))
            {
                var next = new JsonObject(NodeOptions);
                current[segments[i]] = next;
                current = next;
            }
            else if (child is JsonObject section)
            {
                current = section;
            }
            else
            {
                return false;
            }
        }

        if (current.TryGetPropertyValue(segments[^1], out var existing) && existing is JsonObject)
        {
            return false;
        }

        current[segments[^1]] = value;
        return true;
    }

    /// <summary>Copies keys from <paramref name="source"/> that <paramref name="target"/> does not have; true if any were added.</summary>
    private static bool AddMissing(JsonObject target, JsonObject source)
    {
        var added = false;
        foreach (var (name, value) in source.ToList())
        {
            if (!target.TryGetPropertyValue(name, out var existing))
            {
                target[name] = value?.DeepClone();
                added = true;
            }
            else if (existing is JsonObject targetSection && value is JsonObject sourceSection)
            {
                added |= AddMissing(targetSection, sourceSection);
            }
        }
        return added;
    }

    /// <summary>
    /// The child elements of a top-level section, following configSource (replaces the section) and,
    /// for appSettings, file (merged after the section, so its values win), like ASP.NET does.
    /// A missing external file contributes nothing.
    /// </summary>
    private static IEnumerable<XElement> Entries(XElement root, string sectionName, string dir)
    {
        var section = root.Elements().FirstOrDefault(e => e.Name.LocalName == sectionName);
        if (section is null)
        {
            yield break;
        }

        if (Attr(section, "configSource") is { Length: > 0 } source)
        {
            section = LoadExternal(dir, source, sectionName);
            if (section is null)
            {
                yield break;
            }
        }

        foreach (var e in section.Elements())
        {
            yield return e;
        }

        if (sectionName == "appSettings" && Attr(section, "file") is { Length: > 0 } file
            && LoadExternal(dir, file, sectionName) is { } extra)
        {
            foreach (var e in extra.Elements())
            {
                yield return e;
            }
        }
    }

    private static XElement? LoadExternal(string dir, string relativePath, string sectionName)
    {
        var path = Path.Combine(dir, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var root = XDocument.Load(path).Root;
            return root?.Name.LocalName == sectionName ? root : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;
}
