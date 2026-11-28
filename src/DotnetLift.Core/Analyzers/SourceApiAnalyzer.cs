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
        P(@"\bRealProxy\b", Severity.Blocker, "Removed API",
            "RealProxy (remoting proxies) is not supported on modern .NET.",
            "Use System.Reflection.DispatchProxy (interfaces only) or a library such as Castle DynamicProxy."),
        P(@"\bCallContext\.(LogicalGetData|LogicalSetData|GetData|SetData|FreeNamedDataSlot)\(", Severity.Blocker, "Removed API",
            "CallContext (System.Runtime.Remoting.Messaging) does not exist on modern .NET.",
            "Use AsyncLocal<T>: LogicalSetData/LogicalGetData flow with async calls like AsyncLocal does; SetData/GetData did not flow, so check each use."),
        P(@":\s*MarshalByRefObject\b", Severity.Warning, "Removed API",
            "MarshalByRefObject: compiles, but there is no remoting or cross-AppDomain call on modern .NET.",
            "Remove the base class if it was only there for remoting; replace cross-domain calls with AssemblyLoadContext or another process."),
        P(@"\bMarshal\.GetActiveObject\(", Severity.Blocker, "Removed API",
            "Marshal.GetActiveObject (COM) does not exist on modern .NET.",
            "Call GetActiveObject from oleaut32 through P/Invoke, or create the COM object with Type.GetTypeFromProgID + Activator.CreateInstance (Windows only)."),
        P(@"\bServicePointManager\.", Severity.Warning, "Obsolete API",
            "ServicePointManager: its settings (SecurityProtocol, DefaultConnectionLimit, ServerCertificateValidationCallback, Expect100Continue) do not affect HttpClient on modern .NET.",
            "Set them on SocketsHttpHandler / HttpClientHandler (SslOptions, MaxConnectionsPerServer, ServerCertificateCustomValidationCallback); the OS picks the TLS version by default."),
        P(@"\bJavaScriptSerializer\b", Severity.Warning, "Removed API",
            "JavaScriptSerializer (System.Web.Extensions) does not exist on modern .NET.",
            "Use System.Text.Json or Newtonsoft.Json; compare the output with golden vectors (dates were written as \"\\/Date(ms)\\/\", property casing and enum handling differ)."),
        P(@":\s*(ConfigurationSection|ConfigurationElementCollection|ConfigurationElement|IConfigurationSectionHandler)\b", Severity.Warning, "Configuration",
            "Custom configuration section (System.Configuration): the ConfigurationManager package still reads it, but only from App.config / Web.config.",
            "Short term: reference System.Configuration.ConfigurationManager and keep the section in the host's config file. Better: an options class bound from appsettings.json (IOptions<T>)."),
        P(@"\bMicrosoft\.Practices\.EnterpriseLibrary\b", Severity.Warning, "Dependency",
            "Enterprise Library (Microsoft.Practices.EnterpriseLibrary) is not maintained and has no modern .NET version for most blocks.",
            "Replace block by block: Data → ADO.NET/Dapper, Logging → Microsoft.Extensions.Logging, Caching → IMemoryCache, Exception Handling → middleware/filters, Validation → DataAnnotations/FluentValidation."),
        P(@"^\s*#\s*(if|elif)\b.*\b(NET\d+_\d+|NETCOREAPP\d+_\d+|NETSTANDARD\d+_\d+)(?!_OR_GREATER)\b", Severity.Warning, "Conditional compilation",
            "Version-exact preprocessor symbol (e.g. NET6_0): it is defined only for that exact target, so the code under it is silently left out when building for net10.0.",
            "Use the _OR_GREATER form (NET6_0_OR_GREATER) or NETCOREAPP / NET, then check which branch net10.0 should take."),
        P(@"\b(Registry\.(LocalMachine|CurrentUser|ClassesRoot|GetValue|SetValue)|RegistryKey|EventLog|PerformanceCounter|WindowsIdentity|WindowsPrincipal|ManagementObjectSearcher)\b", Severity.Info, "Windows-only",
            "Windows-only API: works when the app runs on Windows, throws PlatformNotSupportedException on Linux (containers, Linux App Service).",
            "Fine for Windows hosting. For Linux hosting, move it behind an interface with a non-Windows implementation, or guard it with OperatingSystem.IsWindows()."),
        P(@"\bDllImport(Attribute)?\(", Severity.Info, "Windows-only",
            "P/Invoke ([DllImport]): the native library must exist on every OS the app runs on (kernel32/user32 only on Windows).",
            "Keep it for Windows hosting; for Linux, check the library and consider [LibraryImport] (source-generated) on modern .NET."),
        P(@"\b(RazorEngine|Engine\.Razor|Razor\.Parse\(|RazorGenerator|RazorTemplateBase)\b", Severity.Warning, "Templating",
            "Razor template engine for e-mails or documents (RazorEngine / RazorGenerator): these packages target .NET Framework only.",
            "Use RazorLight, or render Razor components to a string with HtmlRenderer (.NET 8+). Compare rendered output with golden vectors."),
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
