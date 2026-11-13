using DotnetLift.Core.Model;

namespace DotnetLift.Core.Analyzers;

/// <summary>Knows what happens to well-known .NET Framework packages on modern .NET.</summary>
public sealed class PackageAnalyzer : IAnalyzer
{
    private sealed record Rule(string Prefix, Severity Severity, string Message, string Suggestion, bool ExactMatch = false);

    private static readonly Rule[] Rules =
    [
        new("Microsoft.AspNet.Mvc", Severity.Warning, "ASP.NET MVC 5 package.",
            "Remove. MVC is part of ASP.NET Core (Microsoft.NET.Sdk.Web)."),
        new("Microsoft.AspNet.Razor", Severity.Info, "Razor for MVC 5.",
            "Remove. Razor is built into ASP.NET Core."),
        new("Microsoft.AspNet.WebPages", Severity.Info, "ASP.NET Web Pages.",
            "Remove. Not needed in ASP.NET Core."),
        new("Microsoft.AspNet.WebApi.Client", Severity.Info, "Web API client (HttpClient formatting helpers).",
            "Replace with System.Net.Http.Json, or keep Microsoft.AspNet.WebApi.Client which supports .NET.", ExactMatch: true),
        new("Microsoft.AspNet.WebApi", Severity.Warning, "ASP.NET Web API 2 package.",
            "Remove. Web API controllers become ASP.NET Core controllers ([ApiController])."),
        new("Microsoft.AspNet.Web.Optimization", Severity.Blocker, "System.Web.Optimization bundling.",
            "No direct equivalent. Use LigerShark.WebOptimizer, a frontend build step (npm/Vite) or plain <link>/<script> tags."),
        new("Microsoft.AspNet.Identity", Severity.Blocker, "ASP.NET Identity 2 (OWIN).",
            "Migrate to ASP.NET Core Identity. The schema differs, plan a data migration."),
        new("Microsoft.Owin", Severity.Warning, "OWIN / Katana middleware.",
            "Rewrite as ASP.NET Core middleware. Authentication middleware maps to AddAuthentication()."),
        new("Owin", Severity.Info, "OWIN abstractions.", "Remove together with Katana.", ExactMatch: true),
        new("Microsoft.AspNet.SignalR", Severity.Blocker, "ASP.NET SignalR 2.",
            "Migrate to ASP.NET Core SignalR. Client libraries must change too, the protocols are not compatible."),
        new("Microsoft.CodeDom.Providers.DotNetCompilerPlatform", Severity.Info, "Roslyn CodeDOM provider for runtime view compilation.",
            "Remove."),
        new("Microsoft.Web.Infrastructure", Severity.Info, "System.Web infrastructure helper.", "Remove."),
        new("WebGrease", Severity.Info, "Bundling dependency.", "Remove."),
        new("Antlr", Severity.Info, "Bundling dependency (via WebGrease).", "Remove."),
        new("Modernizr", Severity.Info, "Client-side library delivered as a NuGet content package.",
            "Content packages do not work with PackageReference. Use LibMan or npm.", ExactMatch: true),
        new("jQuery", Severity.Info, "Client-side library delivered as a NuGet content package.",
            "Content packages do not work with PackageReference. Use LibMan or npm."),
        new("bootstrap", Severity.Info, "Client-side library delivered as a NuGet content package.",
            "Content packages do not work with PackageReference. Use LibMan or npm.", ExactMatch: true),
        new("Unity.Mvc", Severity.Warning, "Unity MVC integration.",
            "Use the built-in Microsoft.Extensions.DependencyInjection container, or Unity.Microsoft.DependencyInjection."),
        new("Unity.WebAPI", Severity.Warning, "Unity Web API integration.",
            "Use the built-in Microsoft.Extensions.DependencyInjection container."),
        new("Autofac.Mvc5", Severity.Warning, "Autofac MVC 5 integration.",
            "Replace with Autofac.Extensions.DependencyInjection.", ExactMatch: true),
        new("Autofac.WebApi2", Severity.Warning, "Autofac Web API integration.",
            "Replace with Autofac.Extensions.DependencyInjection.", ExactMatch: true),
        new("Ninject.MVC", Severity.Warning, "Ninject MVC integration.",
            "Ninject has no maintained ASP.NET Core integration. Consider the built-in container."),
        new("EntityFramework", Severity.Warning, "Entity Framework 6.",
            "EF 6.4+ runs on modern .NET (config moves to code: DbConfiguration). Migrating to EF Core is a separate, larger decision.", ExactMatch: true),
        new("System.Web.Providers", Severity.Blocker, "ASP.NET universal providers (membership/session).",
            "No equivalent. Replace with ASP.NET Core Identity / distributed session."),
        new("ELMAH", Severity.Warning, "ELMAH error logging.",
            "Use ElmahCore or ASP.NET Core logging (ILogger) with a sink."),
        new("Microsoft.ApplicationInsights.Web", Severity.Info, "Application Insights for ASP.NET.",
            "Replace with Microsoft.ApplicationInsights.AspNetCore or OpenTelemetry.", ExactMatch: true),
    ];

    public string Name => "packages";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        foreach (var package in project.Packages)
        {
            var rule = Match(package.Id);
            if (rule is null)
            {
                continue;
            }

            yield return new Finding(project.Name, rule.Severity, "Package",
                $"{package.Id} {package.Version}: {rule.Message}",
                rule.Suggestion);
        }
    }

    private static Rule? Match(string id) =>
        // Exact rules first, then the longest prefix, so "Microsoft.AspNet.WebApi.Client" beats "Microsoft.AspNet.WebApi".
        Rules.FirstOrDefault(r => r.ExactMatch && r.Prefix.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? Rules.Where(r => !r.ExactMatch && id.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Prefix.Length)
                .FirstOrDefault();
}
