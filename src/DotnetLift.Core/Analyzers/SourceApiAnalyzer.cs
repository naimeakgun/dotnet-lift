using System.Text.RegularExpressions;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;

namespace DotnetLift.Core.Analyzers;

/// <summary>
/// Line-based scan of C# and Razor files for APIs that need rewriting.
/// It is intentionally a heuristic: it finds places to look at, the agent (or a human) decides how to change them.
/// </summary>
public sealed class SourceApiAnalyzer : IAnalyzer
{
    private sealed record Pattern(Regex Regex, Severity Severity, string Category, string Message, string Suggestion, bool ViewsOnly = false);

    private static Pattern P(string regex, Severity s, string category, string message, string suggestion, bool viewsOnly = false) =>
        new(new Regex(regex, RegexOptions.Compiled), s, category, message, suggestion, viewsOnly);

    private static readonly Pattern[] Patterns =
    [
        P(@"HttpContext\.Current\b", Severity.Warning, "System.Web API",
            "HttpContext.Current (static request access).",
            "Inject IHttpContextAccessor, or better, pass the data you need into the method."),
        P(@"\bHttpRuntime\.Cache\b|\bSystem\.Web\.Caching\b", Severity.Warning, "System.Web API",
            "HttpRuntime.Cache / System.Web.Caching.",
            "Use IMemoryCache (or IDistributedCache) from Microsoft.Extensions.Caching."),
        P(@"\bSession\[", Severity.Warning, "System.Web API",
            "Session[...] indexer.",
            "ASP.NET Core session stores byte[]/string only: add AddSession()/UseSession() and serialize objects (e.g. to JSON)."),
        P(@"\bServer\.MapPath\(", Severity.Warning, "System.Web API",
            "Server.MapPath.",
            "Use IWebHostEnvironment.WebRootPath / ContentRootPath."),
        P(@"\bRequest\.(QueryString|Form)\[", Severity.Info, "System.Web API",
            "Request.QueryString / Request.Form indexer.",
            "Prefer model binding; otherwise Request.Query[...] / Request.Form[...] (StringValues)."),
        P(@"\bnew HttpCookie\(|Response\.Cookies\.Add\(", Severity.Info, "System.Web API",
            "System.Web HttpCookie.",
            "Use Response.Cookies.Append(name, value, new CookieOptions { ... })."),
        P(@"\bFormsAuthentication\.", Severity.Warning, "Authentication",
            "FormsAuthentication.",
            "Use cookie authentication: AddAuthentication().AddCookie() and HttpContext.SignInAsync/SignOutAsync."),
        P(@"\bConfigurationManager\.(AppSettings|ConnectionStrings)\b", Severity.Warning, "Configuration",
            "ConfigurationManager.AppSettings / ConnectionStrings.",
            "Bind to IConfiguration / IOptions<T>; values move to appsettings.json."),
        P(@"\bBinaryFormatter\b", Severity.Blocker, "Removed API",
            "BinaryFormatter is removed from modern .NET (it always throws since .NET 9).",
            "Switch to System.Text.Json, or a typed serializer such as MessagePack/protobuf. Data already stored in this format needs a migration plan."),
        P(@"\[ChildActionOnly\]", Severity.Blocker, "MVC",
            "[ChildActionOnly] child actions do not exist in ASP.NET Core.",
            "Convert the action into a View Component."),
        P(@"@Html\.(Action|RenderAction)\(", Severity.Blocker, "MVC",
            "Html.Action / Html.RenderAction (child action call).",
            "Replace with a View Component: @await Component.InvokeAsync(\"Name\").", viewsOnly: true),
        P(@"@(Scripts|Styles)\.Render\(", Severity.Warning, "Bundling",
            "Scripts.Render / Styles.Render (System.Web.Optimization).",
            "Use <script>/<link> tags with asp-append-version, or a bundler such as WebOptimizer.", viewsOnly: true),
        P(@":\s*ApiController\b", Severity.Warning, "Web API",
            "Web API 2 controller (ApiController).",
            "Derive from ControllerBase, add [ApiController]. IHttpActionResult becomes IActionResult."),
        P(@"\bIHttpActionResult\b", Severity.Info, "Web API",
            "IHttpActionResult.",
            "Use IActionResult or ActionResult<T>."),
        P(@"\bGlobalConfiguration\.Configure\(|\bRouteTable\.Routes\b|\bBundleTable\.Bundles\b|\bGlobalFilters\.Filters\b", Severity.Warning, "Startup",
            "Global.asax / App_Start registration.",
            "Moves to Program.cs: builder.Services.AddControllersWithViews(), app.MapControllerRoute(...), builder.Services.AddControllers(o => o.Filters.Add(...))."),
        P(@"\bAreaRegistration\.RegisterAllAreas\(", Severity.Info, "Startup",
            "AreaRegistration.",
            "Use [Area] attributes and an area route in Program.cs."),
        P(@"\bIHttpModule\b|\bIHttpHandler\b", Severity.Warning, "Pipeline",
            "HTTP module / handler.",
            "Rewrite as ASP.NET Core middleware or an endpoint."),
        P(@"\bAppDomain\.CreateDomain\(", Severity.Blocker, "Removed API",
            "AppDomain.CreateDomain is not supported on modern .NET.",
            "Use AssemblyLoadContext or separate processes for isolation."),
        P(@"\bThread\.Abort\(", Severity.Blocker, "Removed API",
            "Thread.Abort throws PlatformNotSupportedException on modern .NET.",
            "Use cooperative cancellation (CancellationToken)."),
        P(@"\bnew WebClient\(", Severity.Info, "Obsolete API",
            "WebClient is obsolete.",
            "Use HttpClient via IHttpClientFactory."),
    ];

    public string Name => "source-api";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        foreach (var file in project.SourceFiles.Concat(project.ViewFiles))
        {
            var isView = !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            var relative = PathUtil.Relative(project.Directory, file);
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsComment(line))
                {
                    continue;
                }

                foreach (var pattern in Patterns)
                {
                    if (pattern.ViewsOnly && !isView)
                    {
                        continue;
                    }

                    if (pattern.Regex.IsMatch(line))
                    {
                        yield return new Finding(project.Name, pattern.Severity, pattern.Category,
                            pattern.Message, pattern.Suggestion, relative, i + 1);
                    }
                }
            }
        }
    }

    private static bool IsComment(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("@*");
    }
}
