using System.Xml.Linq;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Analyzers;

/// <summary>Reads Web.config and reports what has to move to appsettings.json / Program.cs.</summary>
public sealed class WebConfigAnalyzer : IAnalyzer
{
    // Keys the ASP.NET MVC 5 template adds; they have no meaning in ASP.NET Core.
    private static readonly HashSet<string> FrameworkAppSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "webpages:Version", "webpages:Enabled", "ClientValidationEnabled", "UnobtrusiveJavaScriptEnabled",
    };

    /// <summary>True for appSettings keys the MVC 5 template adds, which mean nothing to ASP.NET Core.</summary>
    public static bool IsFrameworkAppSetting(string key) => FrameworkAppSettings.Contains(key);

    public string Name => "web-config";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        if (project.WebConfigPath is null)
        {
            yield break;
        }

        var doc = TryLoad(project.WebConfigPath);
        if (doc?.Root is null)
        {
            yield return new Finding(project.Name, Severity.Warning, "Configuration",
                "Web.config could not be parsed as XML.", "Fix the file and run the analysis again.",
                Path.GetFileName(project.WebConfigPath));
            yield break;
        }

        var file = Path.GetFileName(project.WebConfigPath);
        var root = doc.Root!;

        var appKeys = Find(root, "appSettings").Elements().Where(e => e.Name.LocalName == "add")
            .Select(e => e.Attribute("key")?.Value)
            .Where(k => k is not null && !FrameworkAppSettings.Contains(k))
            .ToList();
        if (appKeys.Count > 0)
        {
            yield return new Finding(project.Name, Severity.Warning, "Configuration",
                $"{appKeys.Count} application setting(s) in <appSettings>: {string.Join(", ", appKeys)}.",
                "Move to appsettings.json and read them through IConfiguration / IOptions<T>.", file);
        }

        var connections = Find(root, "connectionStrings").Elements().Where(e => e.Name.LocalName == "add")
            .Select(e => e.Attribute("name")?.Value).Where(n => n is not null).ToList();
        if (connections.Count > 0)
        {
            yield return new Finding(project.Name, Severity.Warning, "Configuration",
                $"{connections.Count} connection string(s): {string.Join(", ", connections)}.",
                "Move to the ConnectionStrings section of appsettings.json (and secrets out of source control).", file);
        }

        var auth = Find(root, "authentication").Attribute("mode")?.Value;
        if (string.Equals(auth, "Forms", StringComparison.OrdinalIgnoreCase))
        {
            yield return new Finding(project.Name, Severity.Warning, "Authentication",
                "Forms authentication configured in Web.config.",
                "Configure cookie authentication in Program.cs (AddAuthentication().AddCookie(o => o.LoginPath = ...)).", file);
        }
        else if (string.Equals(auth, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            yield return new Finding(project.Name, Severity.Warning, "Authentication",
                "Windows authentication configured in Web.config.",
                "Use Microsoft.AspNetCore.Authentication.Negotiate (or IIS integration).", file);
        }

        var session = Find(root, "sessionState");
        if (session.Name.LocalName == "sessionState")
        {
            var mode = session.Attribute("mode")?.Value ?? "InProc";
            yield return new Finding(project.Name, mode.Equals("InProc", StringComparison.OrdinalIgnoreCase) ? Severity.Info : Severity.Warning,
                "Session", $"Session state mode '{mode}'.",
                "Add AddDistributedMemoryCache()/AddSession() (or a Redis/SQL distributed cache) and UseSession().", file);
        }

        var modules = root.Descendants().Where(e => e.Name.LocalName is "httpModules" or "modules")
            .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "add"))
            .Select(e => e.Attribute("name")?.Value).Where(n => n is not null).Distinct().ToList();
        if (modules.Count > 0)
        {
            yield return new Finding(project.Name, Severity.Warning, "Pipeline",
                $"HTTP module(s) registered: {string.Join(", ", modules)}.",
                "Rewrite each module as ASP.NET Core middleware.", file);
        }

        var handlers = root.Descendants().Where(e => e.Name.LocalName is "httpHandlers" or "handlers")
            .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "add"))
            .Select(e => e.Attribute("name")?.Value)
            .Where(n => n is not null && !n.StartsWith("ExtensionlessUrlHandler", StringComparison.OrdinalIgnoreCase))
            .Distinct().ToList();
        if (handlers.Count > 0)
        {
            yield return new Finding(project.Name, Severity.Warning, "Pipeline",
                $"HTTP handler(s) registered: {string.Join(", ", handlers)}.",
                "Rewrite as endpoints (app.MapGet/MapPost) or middleware.", file);
        }

        if (Find(root, "customErrors").Name.LocalName == "customErrors")
        {
            yield return new Finding(project.Name, Severity.Info, "Errors",
                "<customErrors> configured.",
                "Use app.UseExceptionHandler(\"/Error\") and app.UseStatusCodePagesWithReExecute(...).", file);
        }

        if (root.Descendants().Any(e => e.Name.LocalName == "system.serviceModel"))
        {
            yield return new Finding(project.Name, Severity.Warning, "WCF",
                "<system.serviceModel> configuration found.",
                "WCF configuration is not read on modern .NET; configure bindings in code (or CoreWCF).", file);
        }

        var redirects = root.Descendants().Count(e => e.Name.LocalName == "bindingRedirect");
        if (redirects > 0)
        {
            yield return new Finding(project.Name, Severity.Info, "Configuration",
                $"{redirects} assembly binding redirect(s).",
                "Not needed on modern .NET; drop them.", file);
        }

        if (root.Descendants().Any(e => e.Name.LocalName == "entityFramework"))
        {
            yield return new Finding(project.Name, Severity.Info, "Entity Framework",
                "<entityFramework> provider configuration in config file.",
                "EF 6 on modern .NET does not read config files: register providers in a DbConfiguration class.", file);
        }
    }

    private static XDocument? TryLoad(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    // Returns an empty placeholder element instead of null so callers can chain attribute reads.
    private static XElement Find(XElement root, string localName) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName) ?? new XElement("none");
}
