using System.Text;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Core.Reporting;

/// <summary>Renders the analysis and the plan as a Markdown document meant to be read in a PR or wiki.</summary>
public static class MarkdownReport
{
    public static string Render(AnalysisResult analysis, UpgradePlan plan, DateTimeOffset? generatedAt = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# dotnet-lift upgrade report");
        sb.AppendLine();
        sb.AppendLine($"- **Input:** `{Path.GetFileName(analysis.Solution.Path)}`");
        sb.AppendLine($"- **Target:** `{analysis.TargetFramework}`");
        sb.AppendLine($"- **Generated:** {(generatedAt ?? DateTimeOffset.Now):yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        var blockers = analysis.Findings.Count(f => f.Severity == Severity.Blocker);
        var warnings = analysis.Findings.Count(f => f.Severity == Severity.Warning);
        var infos = analysis.Findings.Count(f => f.Severity == Severity.Info);
        sb.AppendLine($"**{analysis.Solution.Projects.Count} project(s), {plan.Steps.Count} step(s).** " +
                      $"Findings: {blockers} blocker, {warnings} warning, {infos} info.");
        sb.AppendLine();

        sb.AppendLine("## Projects");
        sb.AppendLine();
        sb.AppendLine("| Order | Project | Type | Current | Format | Blockers | Warnings |");
        sb.AppendLine("|---:|---|---|---|---|---:|---:|");
        var i = 1;
        foreach (var p in plan.Order)
        {
            sb.AppendLine($"| {i++} | {p.Name} | {Describe(p)} | {p.TargetFramework ?? "?"} | " +
                          $"{(p.IsSdkStyle ? "SDK-style" : "Legacy")} | {analysis.Count(p, Severity.Blocker)} | {analysis.Count(p, Severity.Warning)} |");
        }
        sb.AppendLine();

        if (plan.Order.Count > 1)
        {
            sb.AppendLine("Projects are upgraded in dependency order: a project is only touched after everything it references.");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var p in plan.Order)
            {
                var deps = p.ProjectReferences
                    .Select(r => analysis.Solution.FindByPath(r)?.Name)
                    .Where(n => n is not null)
                    .ToList();
                sb.AppendLine(deps.Count == 0 ? p.Name : $"{p.Name}  ->  {string.Join(", ", deps)}");
            }
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## Upgrade plan");
        sb.AppendLine();
        sb.AppendLine("| # | Project | Step | Mode | Risk | Findings |");
        sb.AppendLine("|---:|---|---|---|---|---:|");
        foreach (var s in plan.Steps)
        {
            sb.AppendLine($"| {s.Number} | {s.Project} | {s.Title} | {Describe(s.Mode)} | {s.Risk} | {s.Findings.Count} |");
        }
        sb.AppendLine();
        sb.AppendLine("*Automatic*: deterministic change. *Agent-assisted*: code rewrite proposed for your approval. *Manual*: needs a human decision.");
        sb.AppendLine();

        foreach (var p in plan.Order)
        {
            sb.AppendLine($"## {p.Name}");
            sb.AppendLine();

            foreach (var s in plan.For(p))
            {
                sb.AppendLine($"### {s.Number}. {s.Title}");
                sb.AppendLine();
                sb.AppendLine($"_{Describe(s.Mode)} · risk {s.Risk.ToString().ToLowerInvariant()}_");
                sb.AppendLine();
                sb.AppendLine(s.Description);
                sb.AppendLine();
            }

            var findings = analysis.For(p)
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.File, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ToList();
            if (findings.Count == 0)
            {
                continue;
            }

            sb.AppendLine("#### Findings");
            sb.AppendLine();
            sb.AppendLine("| Severity | Where | Issue | Suggested fix |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var f in findings)
            {
                var where = f.File is null ? "" : f.Line is null ? $"`{f.File}`" : $"`{f.File}:{f.Line}`";
                sb.AppendLine($"| {Icon(f.Severity)} {f.Severity} | {where} | {Escape(f.Message)} | {Escape(f.Suggestion ?? "")} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>The project's kind, or "Tests (xUnit)" for a test project.</summary>
    public static string Describe(ProjectInfo project) =>
        project.Test is { } test ? $"Tests ({TestProjectDetector.Describe(test.Framework)})" : Describe(project.Kind);

    public static string Describe(ProjectKind kind) => kind switch
    {
        ProjectKind.AspNetMvc => "ASP.NET MVC",
        ProjectKind.AspNetWebApi => "ASP.NET Web API",
        ProjectKind.AspNetMvcAndWebApi => "ASP.NET MVC + Web API",
        ProjectKind.AspNetWebForms => "ASP.NET Web Forms",
        ProjectKind.ClassLibrary => "Class library",
        ProjectKind.Console => "Console app",
        _ => "Unknown",
    };

    public static string Describe(StepMode mode) => mode switch
    {
        StepMode.Automatic => "Automatic",
        StepMode.AgentAssisted => "Agent-assisted",
        _ => "Manual",
    };

    private static string Icon(Severity s) => s switch
    {
        Severity.Blocker => "🛑",
        Severity.Warning => "⚠️",
        _ => "ℹ️",
    };

    private static string Escape(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}
