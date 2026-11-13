using DotnetLift.Core;
using DotnetLift.Core.Model;

namespace DotnetLift.Tests;

public class AnalyzerTests
{
    private static readonly AnalysisResult Result = new SolutionAnalyzer().Analyze(Samples.LegacyShopSln);

    private static IEnumerable<Finding> For(string project) => Result.Findings.Where(f => f.Project == project);

    [Fact]
    public void Data_library_using_System_Web_is_a_blocker()
    {
        Assert.Contains(For("LegacyShop.Data"), f =>
            f.Severity == Severity.Blocker && f.Message.Contains("System.Web"));
    }

    [Fact]
    public void Finds_HttpContext_Current_with_file_and_line()
    {
        var findings = For("LegacyShop.Data").Where(f => f.Message.StartsWith("HttpContext.Current")).ToList();

        Assert.NotEmpty(findings);
        Assert.All(findings, f =>
        {
            Assert.Equal("Repositories/ProductRepository.cs", f.File);
            Assert.NotNull(f.Line);
        });
    }

    [Fact]
    public void BinaryFormatter_is_a_blocker()
    {
        Assert.Contains(For("LegacyShop.Core"), f =>
            f.Severity == Severity.Blocker && f.Message.Contains("BinaryFormatter"));
    }

    [Fact]
    public void Child_actions_are_found_in_controllers_and_views()
    {
        var web = For("LegacyShop.Web").ToList();

        Assert.Contains(web, f => f.Message.Contains("[ChildActionOnly]") && f.File == "Controllers/HomeController.cs");
        Assert.Contains(web, f => f.Message.Contains("Html.Action") && f.File == "Views/Shared/_Layout.cshtml");
    }

    [Fact]
    public void Bundling_package_is_a_blocker()
    {
        Assert.Contains(For("LegacyShop.Web"), f =>
            f.Severity == Severity.Blocker && f.Message.StartsWith("Microsoft.AspNet.Web.Optimization"));
    }

    [Fact]
    public void WebApi_client_package_gets_its_own_rule()
    {
        var finding = Assert.Single(For("LegacyShop.Web"), f => f.Message.StartsWith("Microsoft.AspNet.WebApi.Client "));

        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void Web_config_settings_are_reported_without_template_keys()
    {
        var settings = Assert.Single(For("LegacyShop.Web"), f => f.Message.Contains("<appSettings>"));

        Assert.Contains("ShopName", settings.Message);
        Assert.Contains("VatRate", settings.Message);
        Assert.DoesNotContain("webpages:Version", settings.Message);
    }

    [Fact]
    public void Forms_authentication_and_modules_are_reported()
    {
        var web = For("LegacyShop.Web").ToList();

        Assert.Contains(web, f => f.Category == "Authentication" && f.File == "Web.config");
        Assert.Contains(web, f => f.Message.Contains("RequestTimingModule"));
    }

    [Fact]
    public void Every_project_gets_a_legacy_format_warning()
    {
        foreach (var project in Result.Solution.Projects)
        {
            Assert.Contains(For(project.Name), f => f.Category == "Project file");
        }
    }
}
