using DotnetLift.Core;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class CurrentUserTests
{
    private const string Ns = "LegacyShop.Data.Infrastructure";

    [Fact]
    public void Rewrites_the_user_lookup_and_collapses_the_duplicate_null_check()
    {
        var source = File.ReadAllText(Path.Combine(Samples.LegacyShopDir, "LegacyShop.Data", "Repositories", "ProductRepository.cs"));
        var withoutCache = HttpRuntimeCacheRewrite.Rewrite(source)!.NewText;

        var result = CurrentUserRewrite.Rewrite(withoutCache, Ns)!;

        Assert.Equal(3, result.Replaced);
        Assert.Contains("""
                        return CurrentUser.Principal != null
                            ? CurrentUser.Principal.Identity.Name
                            : "anonymous";
            """.Replace("\r\n", "\n"), result.NewText.Replace("\r\n", "\n"));
        Assert.Contains($"using {Ns};", result.NewText);
        Assert.DoesNotContain("using System.Web;", result.NewText);
        Assert.DoesNotContain("HttpContext", result.NewText);
    }

    [Fact]
    public void Keeps_using_System_Web_while_other_System_Web_types_remain()
    {
        var source = File.ReadAllText(Path.Combine(Samples.LegacyShopDir, "LegacyShop.Data", "Repositories", "ProductRepository.cs"));

        var result = CurrentUserRewrite.Rewrite(source, Ns)!; // HttpRuntime.Cache is still there

        Assert.Contains("using System.Web;", result.NewText);
        Assert.Contains($"using {Ns};", result.NewText);
        Assert.Contains("HttpRuntime.Cache", result.NewText);
    }

    [Fact]
    public void A_file_that_uses_HttpContext_for_other_things_is_left_alone()
    {
        const string source = """
            using System.Web;

            class C
            {
                string M() => HttpContext.Current.User.Identity.Name + HttpContext.Current.Items["x"];
            }
            """;

        var result = CurrentUserRewrite.Rewrite(source, Ns)!;

        Assert.Equal(0, result.Replaced);
        Assert.Equal(source, result.NewText);
    }

    [Fact]
    public void Wires_the_provider_into_Application_Start_once()
    {
        var source = File.ReadAllText(Path.Combine(Samples.LegacyShopDir, "LegacyShop.Web", "Global.asax.cs"));

        var wired = CurrentUserRewrite.WireIntoGlobalAsax(source, Ns)!;

        Assert.Contains("""
                        BundleConfig.RegisterBundles(BundleTable.Bundles);
                        // dotnet-lift: libraries read the signed-in user through CurrentUser instead of HttpContext.
                        LegacyShop.Data.Infrastructure.CurrentUser.Use(() => HttpContext.Current?.User);
                    }
            """.Replace("\r\n", "\n"), wired.Replace("\r\n", "\n"));
        Assert.Null(CurrentUserRewrite.WireIntoGlobalAsax(wired, Ns));
    }

    [Fact]
    public void Generated_provider_is_plain_CSharp_7_3()
    {
        var code = CurrentUserRewrite.ProviderSource(Ns);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code,
            new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp7_3));

        Assert.Empty(tree.GetDiagnostics()); // net48 projects compile with C# 7.3 by default
        Assert.Contains($"namespace {Ns}", code);
    }

    [Fact]
    public void Step_removes_System_Web_from_the_library_and_wires_the_web_app()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var analysis = new SolutionAnalyzer().Analyze(dir);
        var plan = UpgradePlanner.Create(analysis);
        foreach (var id in new[] { "package-reference", "sdk-style", "retarget", "api-rewrites" })
        {
            var s = plan.Steps.Single(x => x.Project == "LegacyShop.Data" && x.Id == id);
            var executor = StepExecutors.All.Single(e => e.StepId == id);
            FileChange.ApplyAll(executor.Prepare(ProjectLoader.Load(analysis.Solution.Projects.Single(p => p.Name == "LegacyShop.Data").FullPath), s, "net10.0"));
        }
        var step = plan.Steps.Single(x => x.Id == "current-user");
        var data = ProjectLoader.Load(Path.Combine(dir, "LegacyShop.Data", "LegacyShop.Data.csproj"));

        var changes = new CurrentUserProvider().Prepare(data, step, "net10.0");
        FileChange.ApplyAll(changes);

        Assert.Equal(4, changes.Count);
        Assert.Contains(changes, c => c.Path.EndsWith(Path.Combine("Infrastructure", "CurrentUser.cs")));
        Assert.DoesNotContain("System.Web", ProjectLoader.Load(data.FullPath).AssemblyReferences);
        Assert.Contains("CurrentUser.Use(", File.ReadAllText(Path.Combine(dir, "LegacyShop.Web", "Global.asax.cs")));
        Assert.Empty(new CurrentUserProvider().Prepare(ProjectLoader.Load(data.FullPath), step, "net10.0"));
    }
}
