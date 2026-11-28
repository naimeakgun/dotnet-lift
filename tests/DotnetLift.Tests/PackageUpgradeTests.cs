using DotnetLift.Core;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Tests;

public class PackageUpgradeTests
{
    private static string Library(params (string Id, string? Version)[] packages)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Acme.Lib"));
        var items = string.Concat(packages.Select(p => p.Version is null
            ? $"\n    <PackageReference Include=\"{p.Id}\" />"
            : $"\n    <PackageReference Include=\"{p.Id}\" Version=\"{p.Version}\" />"));
        File.WriteAllText(Path.Combine(dir, "Acme.Lib", "Acme.Lib.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net48</TargetFramework>
              </PropertyGroup>
              <ItemGroup>{items}
              </ItemGroup>
            </Project>
            """);
        return dir;
    }

    private static List<Finding> Upgrades(string dir) =>
        new SolutionAnalyzer().Analyze(dir).Findings.Where(f => f.Category == PackageUpgradeAdvisor.Category).ToList();

    [Fact]
    public void Old_versions_get_the_target_and_the_breaking_changes()
    {
        var findings = Upgrades(Library(("Quartz", "2.6.2"), ("Npgsql", "4.1.13"), ("MongoDB.Driver", "2.19.0"), ("RestSharp", "106.15.0")));

        var quartz = Assert.Single(findings, f => f.Message.StartsWith("Quartz ", StringComparison.Ordinal));
        Assert.Equal("Quartz 2.6.2 → Quartz 3.x.", quartz.Message);
        Assert.Contains("IJob.Execute returns Task", quartz.Suggestion);
        Assert.Contains("Kind=Utc", Assert.Single(findings, f => f.Message.StartsWith("Npgsql 4.1.13", StringComparison.Ordinal)).Suggestion);
        Assert.Contains("GuidRepresentation", Assert.Single(findings, f => f.Message.StartsWith("MongoDB.Driver 2.19.0", StringComparison.Ordinal)).Suggestion);
        Assert.Single(findings, f => f.Message.StartsWith("RestSharp 106.15.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Versions_that_already_run_on_modern_net_are_quiet()
    {
        var findings = Upgrades(Library(("Quartz", "3.13.1"), ("Npgsql", "8.0.5"), ("StackExchange.Redis", "2.8.16"), ("RestSharp", "[112.1.0, )"), ("Newtonsoft.Json", "13.0.3")));

        Assert.Empty(findings);
    }

    [Fact]
    public void Replaced_packages_are_reported_at_any_version_and_unknown_versions_say_so()
    {
        var findings = Upgrades(Library(("NEST", "7.17.5"), ("WindowsAzure.Storage", "9.3.3"), ("Quartz", null)));

        Assert.Contains(findings, f => f.Message == "NEST 7.17.5 → Elastic.Clients.Elasticsearch (the 8.x+ client; NEST is deprecated).");
        Assert.Contains(findings, f => f.Message.StartsWith("WindowsAzure.Storage 9.3.3 → Azure.Storage.Blobs", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Message == "Quartz (version not in the project file) → Quartz 3.x.");
    }

    [Fact]
    public void Upgrades_are_one_planned_task_and_EF6_stays_in_its_own_step()
    {
        var dir = Library(("Quartz", "2.6.2"), ("Hangfire.Core", "1.6.30"), ("EntityFramework", "6.2.0"));

        var steps = UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir)).Steps;

        var upgrades = Assert.Single(steps, s => s.Id == "package-upgrades");
        Assert.Equal(StepMode.AgentAssisted, upgrades.Mode);
        Assert.Equal(2, upgrades.Findings.Count);
        Assert.DoesNotContain(upgrades.Findings, f => f.Message.StartsWith("EntityFramework", StringComparison.Ordinal));
        Assert.Contains(Assert.Single(steps, s => s.Id == "ef6").Findings, f => f.Message.StartsWith("EntityFramework 6.2.0 → EntityFramework 6.5.x", StringComparison.Ordinal));
        var ids = steps.Select(s => s.Id).ToList();
        Assert.True(ids.IndexOf("package-upgrades") < ids.IndexOf("retarget"));
    }

    [Fact]
    public void The_sample_needs_no_package_upgrade()
    {
        Assert.DoesNotContain(new SolutionAnalyzer().Analyze(Samples.LegacyShopSln).Findings, f => f.Category == PackageUpgradeAdvisor.Category);
    }
}
