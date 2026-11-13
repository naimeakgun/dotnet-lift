using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;

namespace DotnetLift.Tests;

public class LoaderTests
{
    [Fact]
    public void Loads_all_three_projects_from_the_solution()
    {
        var solution = SolutionLoader.Load(Samples.LegacyShopSln);

        Assert.Equal(
            new[] { "LegacyShop.Core", "LegacyShop.Data", "LegacyShop.Web" },
            solution.Projects.Select(p => p.Name).OrderBy(n => n));
    }

    [Fact]
    public void Loading_a_folder_finds_the_solution()
    {
        var solution = SolutionLoader.Load(Samples.LegacyShopDir);

        Assert.Equal(3, solution.Projects.Count);
    }

    [Fact]
    public void Reads_legacy_project_details()
    {
        var web = Project("LegacyShop.Web");

        Assert.False(web.IsSdkStyle);
        Assert.Equal("v4.8", web.TargetFramework);
        Assert.Equal(ProjectKind.AspNetMvcAndWebApi, web.Kind);
        Assert.True(web.HasPackagesConfig);
        Assert.True(web.HasGlobalAsax);
        Assert.NotNull(web.WebConfigPath);
        Assert.Contains("System.Web.Mvc", web.AssemblyReferences);
        Assert.Contains(web.Packages, p => p.Id == "Microsoft.AspNet.Mvc" && p.Version == "5.2.9");
        Assert.Equal(2, web.ProjectReferences.Count);
        Assert.Contains(web.ViewFiles, f => f.EndsWith("_Layout.cshtml"));
    }

    [Fact]
    public void Class_libraries_are_detected()
    {
        Assert.Equal(ProjectKind.ClassLibrary, Project("LegacyShop.Core").Kind);
        Assert.Equal(ProjectKind.ClassLibrary, Project("LegacyShop.Data").Kind);
    }

    [Fact]
    public void Reads_sdk_style_projects()
    {
        var path = Path.Combine(Samples.Root, "src", "DotnetLift.Core", "DotnetLift.Core.csproj");

        var project = ProjectLoader.Load(path);

        Assert.True(project.IsSdkStyle);
        Assert.Equal("net10.0", project.TargetFramework);
    }

    private static ProjectInfo Project(string name) =>
        SolutionLoader.Load(Samples.LegacyShopSln).Projects.Single(p => p.Name == name);
}
