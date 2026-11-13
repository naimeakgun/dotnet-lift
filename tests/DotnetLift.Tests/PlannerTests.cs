using DotnetLift.Core;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Reporting;

namespace DotnetLift.Tests;

public class PlannerTests
{
    private static UpgradePlan Plan(string target = "net10.0") =>
        UpgradePlanner.Create(new SolutionAnalyzer().Analyze(Samples.LegacyShopSln, target));

    [Fact]
    public void Upgrades_dependencies_before_the_projects_that_use_them()
    {
        Assert.Equal(
            new[] { "LegacyShop.Core", "LegacyShop.Data", "LegacyShop.Web" },
            Plan().Order.Select(p => p.Name));
    }

    [Fact]
    public void Steps_are_numbered_and_grouped_in_project_order()
    {
        var plan = Plan();

        Assert.Equal(Enumerable.Range(1, plan.Steps.Count), plan.Steps.Select(s => s.Number));

        var projectSequence = plan.Steps.Select(s => s.Project).Distinct();
        Assert.Equal(plan.Order.Select(p => p.Name), projectSequence);
    }

    [Fact]
    public void Libraries_multi_target_while_the_web_app_is_still_on_net_framework()
    {
        var retarget = Plan().Steps.Single(s => s.Project == "LegacyShop.Core" && s.Id == "retarget");

        Assert.Equal("Retarget to net48;net10.0", retarget.Title);
    }

    [Fact]
    public void Data_library_gets_a_high_risk_System_Web_decoupling_step()
    {
        var step = Plan().Steps.Single(s => s.Project == "LegacyShop.Data" && s.Id == "decouple-system-web");

        Assert.Equal(StepMode.AgentAssisted, step.Mode);
        Assert.Equal(Risk.High, step.Risk);
        Assert.NotEmpty(step.Findings);
    }

    [Fact]
    public void Web_project_plan_covers_startup_config_pipeline_and_views()
    {
        var ids = Plan().Steps.Where(s => s.Project == "LegacyShop.Web").Select(s => s.Id).ToList();

        Assert.Equal(
            new[] { "aspnetcore-project", "program-cs", "appsettings", "pipeline", "controllers", "views", "static-files", "build" },
            ids);
    }

    [Fact]
    public void Target_is_used_in_step_titles()
    {
        var step = Plan("net11.0").Steps.Single(s => s.Id == "aspnetcore-project");

        Assert.Contains("net11.0", step.Title);
    }

    [Fact]
    public void Report_contains_projects_plan_and_findings()
    {
        var analysis = new SolutionAnalyzer().Analyze(Samples.LegacyShopSln);
        var markdown = MarkdownReport.Render(analysis, UpgradePlanner.Create(analysis), DateTimeOffset.UnixEpoch);

        Assert.Contains("# dotnet-lift upgrade report", markdown);
        Assert.Contains("| 1 | LegacyShop.Core |", markdown);
        Assert.Contains("## Upgrade plan", markdown);
        Assert.Contains("Remove the System.Web dependency", markdown);
        Assert.Contains("`Repositories/ProductRepository.cs:", markdown);
    }
}
