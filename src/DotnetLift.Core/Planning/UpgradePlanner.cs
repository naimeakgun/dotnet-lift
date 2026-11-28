using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Core.Planning;

/// <summary>
/// Turns analysis findings into an ordered, step-by-step plan.
/// Libraries go first (leaf dependencies before the projects that use them), the web project last.
/// </summary>
public static class UpgradePlanner
{
    public static UpgradePlan Create(AnalysisResult analysis, MigrationProfile? profile = null)
    {
        profile ??= MigrationProfile.Default;
        var order = DependencyOrder(analysis.Solution);
        var steps = new List<UpgradeStep>();
        var webProjectsStillOnFramework = order.Any(p => p.IsWeb && IsFramework(p));

        foreach (var project in order)
        {
            var findings = analysis.For(project).ToList();
            var builder = new StepBuilder(project, findings, steps);

            if (project.IsWeb)
            {
                AddWebSteps(builder, project, analysis.TargetFramework);
            }
            else
            {
                var hosts = order
                    .Where(p => p.IsWeb && p.HasGlobalAsax && p.ProjectReferences.Any(r =>
                        string.Equals(Path.GetFullPath(r), Path.GetFullPath(project.FullPath), StringComparison.OrdinalIgnoreCase)))
                    .Select(p => Path.Combine(p.Directory, "Global.asax.cs"))
                    .ToList();

                // Applications that stay on packages.config do not get a library's NuGet packages automatically.
                // (Libraries are converted to PackageReference by this tool, so they are not listed.)
                var packagesConfigConsumers = order
                    .Where(p => p != project && (p.IsWeb || p.Kind == ProjectKind.Console) && p.HasPackagesConfig && p.ProjectReferences.Any(r =>
                        string.Equals(Path.GetFullPath(r), Path.GetFullPath(project.FullPath), StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Name)
                    .ToList();
                var multiTarget = profile.MultiTargetFor(project.Name) switch
                {
                    MultiTargetMode.Always => true,
                    MultiTargetMode.Never => false,
                    _ => webProjectsStillOnFramework,
                };
                var consumers = order
                    .Where(p => p != project && p.ProjectReferences.Any(r =>
                        string.Equals(Path.GetFullPath(r), Path.GetFullPath(project.FullPath), StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Name)
                    .ToList();
                AddLibrarySteps(builder, project, analysis.TargetFramework, multiTarget, hosts, packagesConfigConsumers, consumers,
                    profile.CodePage, profile.BinaryFormatterFor(project.Name), profile.SqlClient);
            }
        }

        return new UpgradePlan { TargetFramework = analysis.TargetFramework, Order = order, Steps = steps };
    }

    private static void AddLibrarySteps(StepBuilder b, ProjectInfo p, string target, bool consumedByFrameworkApp,
        IReadOnlyList<string> hostStartupFiles, IReadOnlyList<string> packagesConfigConsumers, IReadOnlyList<string> consumers,
        int? codePage, BinaryFormatterStrategy binaryFormatter, SqlClientStrategy sqlClient = SqlClientStrategy.Ask)
    {
        if (p.HasPackagesConfig)
        {
            b.Add("package-reference", "Migrate packages.config to PackageReference",
                "Move every package from packages.config into <PackageReference> items, remove the HintPath references and NuGet build imports, and delete packages.config. Packages that were only transitive dependencies can be pruned later.",
                StepMode.Automatic, Risk.Low, f => f.Category == "Packages" || f.Category == "Package");
        }

        if (!p.IsSdkStyle)
        {
            var testNote = TestNote(p);
            b.Add("sdk-style", "Convert to SDK-style project",
                "Rewrite the .csproj in the SDK format: <Compile> lists, HintPaths and MSBuild imports go away, files the old project did not compile are excluded explicitly. The project still targets .NET Framework after this step, so the code does not change yet." + testNote,
                StepMode.Automatic, Risk.Low,
                f => f.Category == "Project file" || (testNote.Length > 0 && f.Category == TestProjectAnalyzer.Category));
        }

        if (p.References("System.Web"))
        {
            b.Add("decouple-system-web", "Remove the System.Web dependency",
                "Replace HttpContext.Current, HttpRuntime.Cache and similar calls with injected abstractions (IMemoryCache, an ICurrentUser interface). The web project provides the implementations.",
                StepMode.AgentAssisted, Risk.High,
                f => f.Category == "System.Web API" || (f.Category == "Dependency" && f.Message.Contains("System.Web")));
        }

        static bool IsBinaryFormatter(Finding f) => f.Message.StartsWith("BinaryFormatter");

        if (b.Has(f => f.Category is "Removed API" or "Obsolete API" && !IsBinaryFormatter(f)))
        {
            b.Add("removed-apis", "Replace APIs removed from modern .NET",
                "Rewrite usages of APIs such as Thread.Abort or AppDomain.CreateDomain.",
                StepMode.AgentAssisted, Risk.High, f => f.Category is "Removed API" or "Obsolete API" && !IsBinaryFormatter(f));
        }

        if (b.Has(f => f.Category == "Configuration" && f.Message.Contains("ConfigurationManager")))
        {
            b.Add("configuration", "Decouple from ConfigurationManager",
                "Short term: reference System.Configuration.ConfigurationManager. Better: receive settings through constructor parameters or IOptions<T>.",
                StepMode.AgentAssisted, Risk.Medium,
                f => f.Message.Contains("ConfigurationManager") || f.Message.Contains("System.Configuration"));
        }

        if (b.Has(f => f.Message.StartsWith("EntityFramework ")))
        {
            b.Add("ef6", "Keep EF 6 running on modern .NET",
                "Update to the latest EF 6.x and move provider registration from App.config into a DbConfiguration class. EF Core is a separate project.",
                StepMode.AgentAssisted, Risk.Medium,
                f => f.Message.StartsWith("EntityFramework ") || f.Category == "Entity Framework");
        }

        static bool IsSqlClient(Finding f) => f.Category == SqlClientAnalyzer.Category;

        if (sqlClient != SqlClientStrategy.Keep
            && b.Has(f => IsSqlClient(f) && f.Message.StartsWith(SqlClientAnalyzer.UsagePrefix, StringComparison.Ordinal)))
        {
            var decided = sqlClient == SqlClientStrategy.Microsoft;
            var ef6 = p.HasPackage("EntityFramework")
                ? " EF 6's SQL Server provider stays on System.Data.SqlClient: code that passes a SqlConnection to a DbContext needs Microsoft.EntityFramework.SqlServer too."
                : "";
            b.Add("sqlclient", "Move from System.Data.SqlClient to Microsoft.Data.SqlClient",
                $"Rewrite the System.Data.SqlClient namespace to Microsoft.Data.SqlClient (System.Data.Sql to Microsoft.Data.Sql; type names are the same), " +
                $"reference {SqlClientMigration.PackageId} {SqlClientMigration.PackageVersion} for every target (it supports .NET Framework too, so the change is tested before the retarget) " +
                "and remove the deprecated System.Data.SqlClient package. Connections are encrypted by default afterwards: " +
                "connection strings without Encrypt / TrustServerCertificate can fail where the server certificate is not trusted." + ef6 +
                (decided ? $" Applied without asking: \"sqlClient\": \"microsoft\" in {MigrationProfile.FileName}." : " Asks for explicit approval first."),
                StepMode.Automatic, decided ? Risk.Medium : Risk.High, IsSqlClient,
                argument: decided ? SqlClientMigration.ApprovedArgument : null);
        }

        // The .NET Framework part of the current target(s): "v4.7.2" → net472, "net48;net10.0" → net48.
        var current = SdkStyleConversion.ToTfm(p.TargetFramework)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(ProjectFormatAnalyzer.IsNetFramework) ?? "net48";
        var tfm = consumedByFrameworkApp ? $"{current};{target}" : target;
        b.Add("retarget", $"Retarget to {tfm}",
            consumedByFrameworkApp
                ? $"Multi-target {current} and {target}: code that still runs on .NET Framework keeps working while it is being migrated, and the library is ready for the new one. Framework-only references are limited to {current}."
                : $"Change the target framework to {target} only. Framework-only references are removed; anything that stays on .NET Framework uses the library's last .NET Framework build (for example its last package).",
            StepMode.Automatic, Risk.Medium, f => f.Category == "Target framework", argument: tfm);

        if (b.Has(f => f.Message.StartsWith("HttpRuntime.Cache")))
        {
            b.Add("api-rewrites", "Rewrite well-known APIs (Roslyn)",
                "HttpRuntime.Cache becomes MemoryCache.Default (System.Runtime.Caching), which works on both .NET Framework and modern .NET. Only calls with an exact equivalent are rewritten; anything else is left for the build step to report.",
                StepMode.Automatic, Risk.Low, f => f.Message.StartsWith("HttpRuntime.Cache"));
        }

        if (b.Has(f => f.Message.StartsWith("HttpContext.Current")))
        {
            var wiring = hostStartupFiles.Count > 0
                ? $" {string.Join(", ", hostStartupFiles.Select(f => Path.GetFileName(f)).Distinct())} of the web app sets the provider, so behaviour stays the same."
                : " The host application must call CurrentUser.Use(...) at startup.";
            b.Add("current-user", "Replace HttpContext.Current.User with a CurrentUser provider",
                "The library gets a small CurrentUser class and reads CurrentUser.Principal instead of HttpContext.Current.User; " +
                "the System.Web reference goes away when nothing else needs it." + wiring +
                " Files that use HttpContext.Current for anything else (Items, Request, Session) are left alone.",
                StepMode.Automatic, Risk.Medium,
                f => f.Message.StartsWith("HttpContext.Current") || (f.Category == "Dependency" && f.Message.Contains("System.Web")),
                argument: string.Join(";", hostStartupFiles));
        }

        static bool IsCodePageFinding(Finding f) =>
            f.Category == DataCompatibilityAnalyzer.Category
            && (f.Message.StartsWith("Encoding.Default") || f.Message.StartsWith("Encoding.GetEncoding("));

        if (codePage is { } page && b.Has(IsCodePageFinding))
        {
            b.Add("explicit-code-page", $"Use code page {page} instead of Encoding.Default",
                $"Encoding.Default and Encoding.GetEncoding(0) become LegacyEncoding.Default (code page {page}, as set in {MigrationProfile.FileName}), " +
                "and legacy code pages go through LegacyEncoding.Get, which registers CodePagesEncodingProvider on modern .NET. " +
                "Text that is hashed or stored keeps producing the same bytes; confirm it with golden vectors that contain non-ASCII characters.",
                StepMode.Automatic, Risk.Medium, IsCodePageFinding, argument: page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (binaryFormatter == BinaryFormatterStrategy.Compatibility && b.Has(IsBinaryFormatter))
        {
            var hostNote = consumers.Count == 0
                ? " The application that runs this code on modern .NET must set it too"
                : $" {string.Join(", ", consumers)} must set it too once {(consumers.Count == 1 ? "it targets" : "they target")} modern .NET";
            b.Add("binaryformatter-compat", "Keep BinaryFormatter with the compatibility package",
                $"As chosen in {MigrationProfile.FileName} (\"binaryFormatter\": \"compatibility\"), the code and the stored data stay as they are: " +
                $"the modern target references {BinaryFormatterCompatibility.PackageId} {BinaryFormatterCompatibility.PackageVersion}, which brings back a working BinaryFormatter, " +
                $"and the project sets {BinaryFormatterCompatibility.SwitchProperty} (the {BinaryFormatterCompatibility.ObsoleteWarning} warning is silenced; the decision is recorded in the project file)." +
                hostNote + $" (<{BinaryFormatterCompatibility.SwitchProperty}>true</{BinaryFormatterCompatibility.SwitchProperty}> in the project file): " +
                "the switch is read from the application's runtimeconfig.json, and without it BinaryFormatter throws at runtime. " +
                "The package is unsupported and BinaryFormatter is unsafe with untrusted input: use this for data you wrote yourself, until a planned data migration.",
                StepMode.Automatic, Risk.Medium, IsBinaryFormatter);
        }
        else if (b.Has(IsBinaryFormatter))
        {
            var consumerNote = packagesConfigConsumers.Count == 0
                ? ""
                : $" Note: {string.Join(", ", packagesConfigConsumers)} still use{(packagesConfigConsumers.Count == 1 ? "s" : "")} packages.config, " +
                  "which does not pick up the library's packages: add System.Text.Json there too " +
                  $"(Install-Package System.Text.Json -Version {BinaryFormatterToJson.SystemTextJsonVersion}), otherwise the app fails at runtime with FileNotFoundException.";
            b.Add("binaryformatter-json", "Replace BinaryFormatter with System.Text.Json",
                "new BinaryFormatter().Serialize(stream, value) and (T)new BinaryFormatter().Deserialize(stream) become System.Text.Json calls, which build for both targets. The serialized format changes, so this step only runs after your explicit approval." + consumerNote,
                StepMode.Automatic, Risk.High, IsBinaryFormatter);
        }

        b.Add("build", $"Build for {target} and fix errors",
            $"Build the {target} target, fix known errors automatically (for example a missing package) and rebuild, then list the errors that need a code change with what to do about them.",
            StepMode.Automatic, Risk.Low, _ => false, argument: target);
    }

    /// <summary>What the SDK-style conversion adds to a test project, as a sentence for the step description ("" for other projects).</summary>
    private static string TestNote(ProjectInfo p)
    {
        if (p.Test is not { } test)
        {
            return "";
        }
        var added = TestPackages.ToAdd(p);
        var framework = TestProjectDetector.Describe(test.Framework);
        if (added.Count == 0)
        {
            return $" {framework} test project: its test packages are kept as they are.";
        }
        var replaced = TestPackages.IsReplacedReference(p, TestProjectDetector.VisualStudioMSTestAssembly) && p.References(TestProjectDetector.VisualStudioMSTestAssembly)
            ? $" The {TestProjectDetector.VisualStudioMSTestAssembly} reference is removed; namespaces and attributes stay the same."
            : "";
        return $" {framework} test project: adds {string.Join(", ", added.Select(a => $"{a.Id} {a.Version}"))} so `dotnet test` can run it.{replaced}";
    }

    private static void AddWebSteps(StepBuilder b, ProjectInfo p, string target)
    {
        b.Add("aspnetcore-project", $"Convert to an ASP.NET Core project ({target})",
            "Switch the project to Microsoft.NET.Sdk.Web, remove MVC 5 / Web API 2 / bundling packages, keep the project references.",
            StepMode.Automatic, Risk.Medium,
            f => f.Category is "Project file" or "Packages" or "Package" or "Target framework");

        if (p.HasGlobalAsax || b.Has(f => f.Category == "Startup"))
        {
            b.Add("program-cs", "Move Global.asax and App_Start into Program.cs",
                "Translate route, filter, Web API and DI registrations into the minimal hosting model (WebApplication.CreateBuilder).",
                StepMode.AgentAssisted, Risk.Medium, f => f.Category == "Startup");
        }

        if (p.WebConfigPath is not null)
        {
            b.Add("appsettings", "Move Web.config settings to appsettings.json",
                "Copy appSettings and connection strings into appsettings.json, where IConfiguration reads them under the same keys " +
                "(a ':' in a key becomes a section, provider names are kept as <name>_ProviderName). Framework-only keys and binding redirects are dropped. " +
                "Web.config stays as it is, so the old app keeps working; an existing appsettings.json keeps its values.",
                StepMode.Automatic, Risk.Low,
                f => f.File == Path.GetFileName(p.WebConfigPath) && f.Category is "Configuration" or "Entity Framework");
        }

        if (b.Has(f => f.Category is "Authentication" or "Session" or "Pipeline" or "Errors"))
        {
            b.Add("pipeline", "Rebuild the request pipeline",
                "Authentication, session, HTTP modules/handlers and error pages become middleware registered in Program.cs.",
                StepMode.AgentAssisted, Risk.High, f => f.Category is "Authentication" or "Session" or "Pipeline" or "Errors");
        }

        b.Add("controllers", "Port controllers",
            "MVC and Web API controllers move to Microsoft.AspNetCore.Mvc. ApiController becomes ControllerBase + [ApiController]; System.Web calls are rewritten.",
            StepMode.AgentAssisted, Risk.Medium,
            f => f.Category is "System.Web API" or "Web API" or "Configuration" && f.Line is not null
                 || f.Category == "MVC" && f.File?.EndsWith(".cs") == true);

        if (p.ViewFiles.Count > 0)
        {
            b.Add("views", "Port Razor views",
                "Views/Web.config becomes _ViewImports.cshtml, child actions become View Components, bundling calls become tags.",
                StepMode.AgentAssisted, Risk.Medium,
                f => f.Category is "Bundling" || f.Category == "MVC" && f.File?.EndsWith(".cshtml") == true);
        }

        b.Add("static-files", "Move static files to wwwroot",
            "Content/, Scripts/, fonts/, Images/, img/, favicon.ico and robots.txt move into wwwroot/ with their folders, so every URL stays the same " +
            "(UseStaticFiles serves wwwroot/Content/site.css as /Content/site.css) and views need no change. Files are moved, not rewritten; " +
            "files that already exist in wwwroot stay where they are. Runs once the project is an ASP.NET Core project, because the old app serves them from their current folders. " +
            "Client libraries from NuGet content packages (jQuery, Bootstrap) can later come from LibMan or npm.",
            StepMode.Automatic, Risk.Low, f => f.Message.Contains("content package"));

        if (b.Has(f => f.Category is "Removed API" or "Obsolete API"))
        {
            b.Add("removed-apis", "Replace APIs removed from modern .NET",
                "Rewrite usages of APIs that no longer exist.",
                StepMode.AgentAssisted, Risk.High, f => f.Category is "Removed API" or "Obsolete API");
        }

        b.Add("build", "Build, run and smoke test",
            "dotnet build, start the app and request the main routes. Compare responses with the old application.",
            StepMode.Manual, Risk.Low, _ => false);
    }

    /// <summary>Topological sort over project references; unknown references are ignored.</summary>
    public static IReadOnlyList<ProjectInfo> DependencyOrder(SolutionInfo solution)
    {
        var result = new List<ProjectInfo>();
        var state = new Dictionary<ProjectInfo, bool>(); // false = visiting, true = done

        void Visit(ProjectInfo p)
        {
            if (state.TryGetValue(p, out var done))
            {
                if (!done)
                {
                    throw new InvalidOperationException($"Circular project reference involving '{p.Name}'.");
                }
                return;
            }

            state[p] = false;
            foreach (var reference in p.ProjectReferences)
            {
                var dep = solution.FindByPath(reference);
                if (dep is not null)
                {
                    Visit(dep);
                }
            }
            state[p] = true;
            result.Add(p);
        }

        // Stable: non-web projects first, then by name, so the output does not depend on .sln ordering.
        foreach (var p in solution.Projects.OrderBy(p => p.IsWeb).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            Visit(p);
        }

        return result;
    }

    private static bool IsFramework(ProjectInfo p) =>
        p.TargetFramework is { } tfm && ProjectFormatAnalyzer.IsNetFramework(tfm);

    private sealed class StepBuilder(ProjectInfo project, List<Finding> findings, List<UpgradeStep> steps)
    {
        public bool Has(Func<Finding, bool> predicate) => findings.Any(predicate);

        public void Add(string id, string title, string description, StepMode mode, Risk risk, Func<Finding, bool> related,
            string? argument = null)
        {
            var matched = findings.Where(related).ToList();
            var effectiveRisk = matched.Any(f => f.Severity == Severity.Blocker) ? Risk.High : risk;
            steps.Add(new UpgradeStep(steps.Count + 1, project.Name, id, title, description, mode, effectiveRisk, matched, argument));
        }
    }
}
